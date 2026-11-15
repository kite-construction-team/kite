using System.Buffers;
using System.Net;
using System.Net.Http;
using Kite.Core.Models;
using Kite.Server.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Kite.Server.Pools;

/// <summary>
/// Shared HTTP reverse proxy forwarder handling HTTP/1.1 & HTTP/2 upgrades, RFC 7239 headers,
/// unbuffered streaming pass-through (SSE/gRPC), and trailing headers.
/// </summary>
internal static class HttpProxyForwarder
{
    private static HttpMethod ResolveHttpMethod(string method)
    {
        if (HttpMethods.IsGet(method)) return HttpMethod.Get;
        if (HttpMethods.IsPost(method)) return HttpMethod.Post;
        if (HttpMethods.IsPut(method)) return HttpMethod.Put;
        if (HttpMethods.IsDelete(method)) return HttpMethod.Delete;
        if (HttpMethods.IsHead(method)) return HttpMethod.Head;
        if (HttpMethods.IsOptions(method)) return HttpMethod.Options;
        if (HttpMethods.IsPatch(method)) return HttpMethod.Patch;
        return HttpMethod.Parse(method);
    }

    private static string ResolveRemoteIp(IPAddress? address)
    {
        if (address is null || IPAddress.IsLoopback(address))
        {
            return address?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? "::1"
                : "127.0.0.1";
        }
        return address.ToString();
    }

    public static async ValueTask<int> ForwardAsync(
        HttpContext context,
        HttpMessageInvoker invoker,
        string baseAddress,
        ReadOnlyMemory<char> subPath,
        bool enableStreamingPassThrough,
        CancellationToken ct,
        bool preserveHostHeader = false)
    {
        string trimmedBase = baseAddress.EndsWith('/') ? baseAddress[..^1] : baseAddress;
        ReadOnlySpan<char> querySpan = context.Request.QueryString.Value.AsSpan();
        string targetUri = querySpan.IsEmpty
            ? string.Concat(trimmedBase.AsSpan(), subPath.Span)
            : string.Concat(trimmedBase.AsSpan(), subPath.Span, querySpan);

        using var proxyRequest = new HttpRequestMessage(ResolveHttpMethod(context.Request.Method), targetUri);

        if (preserveHostHeader && context.Request.Host.HasValue)
        {
            proxyRequest.Headers.Host = context.Request.Host.Value;
            int prevHop = int.TryParse(context.Request.Headers["X-Kite-Mesh-Hop"], out int h) ? h : 0;
            proxyRequest.Headers.TryAddWithoutValidation("X-Kite-Mesh-Hop", (prevHop + 1).ToString());
        }

        string? reqContentType = context.Request.ContentType;
        bool isGrpc = reqContentType is not null && reqContentType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase);
        if (isGrpc || context.Request.Protocol.Equals("HTTP/2", StringComparison.OrdinalIgnoreCase))
        {
            proxyRequest.Version = HttpVersion.Version20;
            proxyRequest.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }

        bool hasBody = context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0;
        if (hasBody)
        {
            proxyRequest.Content = new StreamContent(context.Request.Body);
        }

        foreach (KeyValuePair<string, StringValues> header in context.Request.Headers)
        {
            string key = header.Key;
            if (string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Connection", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            StringValues vals = header.Value;
            if (vals.Count == 1)
            {
                string singleVal = vals[0] ?? string.Empty;
                if (!proxyRequest.Headers.TryAddWithoutValidation(key, singleVal))
                {
                    proxyRequest.Content?.Headers.TryAddWithoutValidation(key, singleVal);
                }
            }
            else if (vals.Count > 1)
            {
                string[] arr = vals.ToArray()!;
                if (!proxyRequest.Headers.TryAddWithoutValidation(key, arr))
                {
                    proxyRequest.Content?.Headers.TryAddWithoutValidation(key, arr);
                }
            }
        }

        string remoteIp = ResolveRemoteIp(context.Connection.RemoteIpAddress);
        string host = context.Request.Host.Value ?? "localhost";
        string proto = context.Request.Scheme;
        proxyRequest.Headers.TryAddWithoutValidation("Forwarded", $"for=\"{remoteIp}\";proto={proto};host=\"{host}\"");
        proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", remoteIp);
        proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", proto);
        proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", host);
        KiteMetricsAndTracing.InjectTraceContext(proxyRequest);

        using HttpResponseMessage upstreamResponse = await invoker
            .SendAsync(proxyRequest, ct)
            .ConfigureAwait(false);

        int statusCode = (int)upstreamResponse.StatusCode;
        context.Response.StatusCode = statusCode;
        IHeaderDictionary respHeaders = context.Response.Headers;

        foreach (var header in upstreamResponse.Headers.NonValidated)
        {
            string key = header.Key;
            if (!string.Equals(key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "Trailer", StringComparison.OrdinalIgnoreCase))
            {
                respHeaders[key] = header.Value.Count == 1 ? header.Value.ToString() : header.Value.ToArray();
            }
        }

        foreach (var header in upstreamResponse.Content.Headers.NonValidated)
        {
            respHeaders[header.Key] = header.Value.Count == 1 ? header.Value.ToString() : header.Value.ToArray();
        }

        bool isStreaming = enableStreamingPassThrough || isGrpc;
        if (!isStreaming)
        {
            StringValues acceptValues = context.Request.Headers.Accept;
            for (int i = 0; i < acceptValues.Count; i++)
            {
                if (acceptValues[i]?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
                {
                    isStreaming = true;
                    break;
                }
            }

            if (!isStreaming &&
                upstreamResponse.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            {
                isStreaming = true;
            }
        }

        if (isStreaming)
        {
            await using Stream upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                int bytesRead;
                while ((bytesRead = await upstreamStream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        else
        {
            await upstreamResponse.Content.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
        }

        IHttpResponseTrailersFeature? trailersFeature = context.Features.Get<IHttpResponseTrailersFeature>();
        if (trailersFeature is not null)
        {
            foreach (var trailer in upstreamResponse.TrailingHeaders)
            {
                trailersFeature.Trailers[trailer.Key] = trailer.Value.ToArray();
            }
        }

        return statusCode;
    }
}
