using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Kite.Server.HttpModules;

/// <summary>
/// Zero-reflection structured NDJSON access log formatter built on <see cref="Utf8JsonWriter"/>
/// and a thread-local reusable buffer.
/// </summary>
public static class StructuredJsonLogFormatter
{
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_buffer;

    public static string FormatLinePooled(
        DateTimeOffset utcTimestamp,
        string siteId,
        ReadOnlySpan<char> clientIp,
        ReadOnlySpan<char> method,
        ReadOnlySpan<char> path,
        ReadOnlySpan<char> query,
        int port,
        ReadOnlySpan<char> host,
        ReadOnlySpan<char> protocol,
        int statusCode,
        long bytesSent,
        long durationMs,
        ReadOnlySpan<char> traceId)
    {
        ArrayBufferWriter<byte> buffer = t_buffer ??= new ArrayBufferWriter<byte>(512);
        buffer.Clear();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp", utcTimestamp);
            writer.WriteString("siteId", siteId);
            writer.WriteString("clientIp", clientIp);
            writer.WriteString("method", method);
            writer.WriteString("path", path);
            if (!query.IsEmpty)
            {
                writer.WriteString("query", query);
            }
            writer.WriteString("host", host);
            writer.WriteNumber("port", port);
            writer.WriteString("protocol", protocol);
            writer.WriteNumber("statusCode", statusCode);
            writer.WriteNumber("bytesSent", bytesSent);
            writer.WriteNumber("durationMs", durationMs);
            if (!traceId.IsEmpty)
            {
                writer.WriteString("traceId", traceId);
            }
            writer.WriteEndObject();
            writer.Flush();
        }

        ReadOnlySpan<byte> written = buffer.WrittenSpan;
        return string.Concat(Encoding.UTF8.GetString(written), "\n");
    }
}
