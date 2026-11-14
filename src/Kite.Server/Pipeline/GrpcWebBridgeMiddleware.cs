using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Kite.Server.Pipeline;

/// <summary>
/// Translates browser <c>application/grpc-web</c> / <c>application/grpc-web-text</c> HTTP/1.1 or HTTP/2 requests
/// into backend HTTP/2 <c>application/grpc</c> frames and encodes trailing <c>grpc-status</c>/<c>grpc-message</c>
/// headers into gRPC-Web <c>0x80</c> trailer frames.
/// </summary>
public static class GrpcWebBridgeMiddleware
{
    public static bool IsGrpcWebRequest(HttpRequest request)
    {
        string? ct = request.ContentType;
        return !string.IsNullOrEmpty(ct) &&
               ct.StartsWith("application/grpc-web", StringComparison.OrdinalIgnoreCase);
    }

    public static async ValueTask ForwardGrpcWebAsync(
        HttpContext context,
        HttpMessageInvoker invoker,
        string upstreamBaseAddress,
        ReadOnlyMemory<char> subPath,
        CancellationToken ct)
    {
        bool isBase64Text = context.Request.ContentType?
            .Contains("grpc-web-text", StringComparison.OrdinalIgnoreCase) == true;

        byte[] requestBytes = await ReadRequestPayloadAsync(context.Request.Body, isBase64Text, ct).ConfigureAwait(false);
        string targetUri = $"{upstreamBaseAddress.TrimEnd('/')}{subPath.Span}{context.Request.QueryString.Value}";

        using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, targetUri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new ByteArrayContent(requestBytes)
        };
        upstreamReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        upstreamReq.Headers.TryAddWithoutValidation("TE", "trailers");

        foreach (KeyValuePair<string, StringValues> header in context.Request.Headers)
        {
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            upstreamReq.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        using HttpResponseMessage upstreamResp = await invoker.SendAsync(upstreamReq, ct).ConfigureAwait(false);
        byte[] dataBytes = await upstreamResp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        byte[] trailerFrame = BuildTrailerFrame(upstreamResp.TrailingHeaders);

        byte[] combined = new byte[dataBytes.Length + trailerFrame.Length];
        dataBytes.CopyTo(combined, 0);
        trailerFrame.CopyTo(combined, dataBytes.Length);

        byte[] responseBody = isBase64Text
            ? Encoding.ASCII.GetBytes(Convert.ToBase64String(combined))
            : combined;

        context.Response.StatusCode = (int)upstreamResp.StatusCode;
        foreach (var h in upstreamResp.Headers)
        {
            if (!string.Equals(h.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers[h.Key] = h.Value.ToArray();
            }
        }

        context.Response.ContentType = isBase64Text
            ? "application/grpc-web-text+proto"
            : "application/grpc-web+proto";
        context.Response.Headers["Access-Control-Expose-Headers"] = "grpc-status, grpc-message";
        context.Response.ContentLength = responseBody.Length;

        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(responseBody, ct).ConfigureAwait(false);
        }
    }

    public static byte[] BuildTrailerFrame(HttpResponseHeaders trailers)
    {
        var sb = new StringBuilder();
        bool hasStatus = false;
        foreach (var h in trailers)
        {
            if (string.Equals(h.Key, "grpc-status", StringComparison.OrdinalIgnoreCase))
            {
                hasStatus = true;
            }

            foreach (string v in h.Value)
            {
                sb.Append(h.Key.ToLowerInvariant()).Append(": ").Append(v).Append("\r\n");
            }
        }

        if (!hasStatus && sb.Length == 0)
        {
            sb.Append("grpc-status: 0\r\n");
        }

        byte[] payload = Encoding.ASCII.GetBytes(sb.ToString());
        byte[] frame = new byte[5 + payload.Length];
        frame[0] = 0x80; // gRPC-Web uncompressed trailer frame flag
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(frame, 5);
        return frame;
    }

    public static async ValueTask<byte[]> ReadRequestPayloadAsync(Stream body, bool isBase64Text, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await body.CopyToAsync(ms, ct).ConfigureAwait(false);
        byte[] raw = ms.ToArray();
        if (!isBase64Text || raw.Length == 0)
        {
            return raw;
        }

        string text = Encoding.ASCII.GetString(raw).Trim();
        return Convert.FromBase64String(text);
    }
}
