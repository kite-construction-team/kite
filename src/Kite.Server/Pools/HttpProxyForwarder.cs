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
    public static async ValueTask<int> ForwardAsync(
        HttpContext context,
        HttpMessageInvoker invoker,
        string baseAddress,
        ReadOnlyMemory<char> subPath,
        bool enableStreamingPassThrough,
        CancellationToken ct,
        bool preserveHostHeader = false)
    {
        string trimmedBase = baseAddress.TrimEnd('/');
        string targetUri = $"{trimmedBase}{subPath.Span}{context.Request.QueryString.Value}";
        using var proxyRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

        if (preserveHostHeader && context.Request.Host.HasValue)
        {
            proxyRequest.Headers.Host = context.Request.Host.Value;
            int prevHop = int.TryParse(context.Request.Headers["X-Kite-Mesh-Hop"], out int h) ? h : 0;
            proxyRequest.Headers.TryAddWithoutValidation("X-Kite-Mesh-Hop", (prevHop + 1).ToString());
        }

        bool isGrpc = context.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true;
        if (isGrpc || context.Request.Protocol.Equals("HTTP/2", StringComparison.OrdinalIgnoreCase))
        {
            proxyRequest.Version = HttpVersion.Version20;
            proxyRequest.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }

        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            proxyRequest.Content = new StreamContent(context.Request.Body);
        }

        foreach (KeyValuePair<string, StringValues> header in context.Request.Headers)
        {
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "Connection", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!proxyRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                proxyRequest.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        string remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
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

        foreach (var header in upstreamResponse.Headers)
        {
            if (!string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(header.Key, "Trailer", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        foreach (var header in upstreamResponse.Content.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        bool isStreaming = enableStreamingPassThrough ||
                           isGrpc ||
                           context.Request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) ||
                           upstreamResponse.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;

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
