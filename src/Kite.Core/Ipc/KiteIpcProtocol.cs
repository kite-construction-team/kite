using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Kite.Core.Ipc;

public enum KiteIpcMessageType : byte
{
    Ping = 1,
    Pong = 2,
    HttpRequest = 3,
    HttpResponse = 4
}

public sealed class KiteIpcRequestHeader
{
    public string Method { get; set; } = "GET";
    public string Scheme { get; set; } = "http";
    public string Host { get; set; } = "localhost";
    public string PathBase { get; set; } = "";
    public string Path { get; set; } = "/";
    public string QueryString { get; set; } = "";
    public string RemoteIp { get; set; } = "127.0.0.1";
    public int RemotePort { get; set; }
    public List<KeyValuePair<string, string>> Headers { get; set; } = new(16);

    public void Reset()
    {
        Method = "GET";
        Scheme = "http";
        Host = "localhost";
        PathBase = "";
        Path = "/";
        QueryString = "";
        RemoteIp = "127.0.0.1";
        RemotePort = 0;
        Headers.Clear();
    }
}

public sealed class KiteIpcResponseHeader
{
    public int StatusCode { get; set; } = 200;
    public List<KeyValuePair<string, string>> Headers { get; set; } = new(16);

    public void Reset()
    {
        StatusCode = 200;
        Headers.Clear();
    }
}

/// <summary>
/// High-speed, single-write batch-framed binary IPC protocol between <c>Kite.Server</c> and <c>Kite.Worker</c>
/// over Named Pipes or Unix Domain Sockets. Rents all working buffers from <see cref="ArrayPool{T}.Shared"/>.
/// </summary>
public static class KiteIpcProtocol
{
    private const int BufferSize = 32 * 1024;

    public static async ValueTask WriteMessageTypeAsync(Stream stream, KiteIpcMessageType type, CancellationToken ct = default)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(1);
        try
        {
            buf[0] = (byte)type;
            await stream.WriteAsync(buf.AsMemory(0, 1), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public static async ValueTask<KiteIpcMessageType?> ReadMessageTypeAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(1);
        try
        {
            int read = await stream.ReadAsync(buf.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }
            return (KiteIpcMessageType)buf[0];
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public static async ValueTask WriteRequestHeaderAsync(
        Stream stream,
        KiteIpcRequestHeader request,
        CancellationToken ct = default)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            Span<byte> span = buf.AsSpan();
            span[0] = (byte)KiteIpcMessageType.HttpRequest;
            int offset = 5; // reserve bytes 1..4 for payload length

            WriteSpanString(span, ref offset, request.Method);
            WriteSpanString(span, ref offset, request.Scheme);
            WriteSpanString(span, ref offset, request.Host);
            WriteSpanString(span, ref offset, request.PathBase);
            WriteSpanString(span, ref offset, request.Path);
            WriteSpanString(span, ref offset, request.QueryString);
            WriteSpanString(span, ref offset, request.RemoteIp);
            WriteSpanInt32(span, ref offset, request.RemotePort);

            WriteSpanInt32(span, ref offset, request.Headers.Count);
            for (int i = 0; i < request.Headers.Count; i++)
            {
                KeyValuePair<string, string> kvp = request.Headers[i];
                WriteSpanString(span, ref offset, kvp.Key);
                WriteSpanString(span, ref offset, kvp.Value);
            }

            int payloadLength = offset - 5;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(1, 4), payloadLength);
            await stream.WriteAsync(buf.AsMemory(0, offset), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>
    /// Writes the response header, small body payload, and 0-length terminator in a single coalesced pipe write.
    /// </summary>
    public static async ValueTask WriteResponseMessageAsync(
        Stream stream,
        KiteIpcResponseHeader response,
        Stream bodyStream,
        CancellationToken ct = default)
    {
        if (bodyStream.Length > BufferSize / 2)
        {
            await WriteResponseHeaderAsync(stream, response, ct).ConfigureAwait(false);
            await WriteBodyFramesAsync(stream, bodyStream, ct).ConfigureAwait(false);
            return;
        }

        byte[] buf = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            Span<byte> span = buf.AsSpan();
            span[0] = (byte)KiteIpcMessageType.HttpResponse;
            int offset = 5;

            WriteSpanInt32(span, ref offset, response.StatusCode);
            WriteSpanInt32(span, ref offset, response.Headers.Count);
            for (int i = 0; i < response.Headers.Count; i++)
            {
                KeyValuePair<string, string> kvp = response.Headers[i];
                WriteSpanString(span, ref offset, kvp.Key);
                WriteSpanString(span, ref offset, kvp.Value);
            }

            int headerPayloadLength = offset - 5;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(1, 4), headerPayloadLength);

            int bodyLen = (int)bodyStream.Length;
            if (bodyLen > 0)
            {
                WriteSpanInt32(span, ref offset, bodyLen);
                int read = bodyStream.Read(span.Slice(offset, bodyLen));
                offset += read;
            }

            // 0-length terminator frame
            WriteSpanInt32(span, ref offset, 0);

            await stream.WriteAsync(buf.AsMemory(0, offset), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>
    /// Writes the request header and (when there is no request body) the 0-length body terminator in a single pipe write.
    /// </summary>
    public static async ValueTask WriteRequestMessageAsync(
        Stream stream,
        KiteIpcRequestHeader request,
        Stream? bodyStream,
        CancellationToken ct = default)
    {
        if (bodyStream is not null)
        {
            await WriteRequestHeaderAsync(stream, request, ct).ConfigureAwait(false);
            await WriteBodyFramesAsync(stream, bodyStream, ct).ConfigureAwait(false);
            return;
        }

        byte[] buf = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            Span<byte> span = buf.AsSpan();
            span[0] = (byte)KiteIpcMessageType.HttpRequest;
            int offset = 5;

            WriteSpanString(span, ref offset, request.Method);
            WriteSpanString(span, ref offset, request.Scheme);
            WriteSpanString(span, ref offset, request.Host);
            WriteSpanString(span, ref offset, request.PathBase);
            WriteSpanString(span, ref offset, request.Path);
            WriteSpanString(span, ref offset, request.QueryString);
            WriteSpanString(span, ref offset, request.RemoteIp);
            WriteSpanInt32(span, ref offset, request.RemotePort);

            WriteSpanInt32(span, ref offset, request.Headers.Count);
            for (int i = 0; i < request.Headers.Count; i++)
            {
                KeyValuePair<string, string> kvp = request.Headers[i];
                WriteSpanString(span, ref offset, kvp.Key);
                WriteSpanString(span, ref offset, kvp.Value);
            }

            int payloadLength = offset - 5;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(1, 4), payloadLength);

            // Append 0-length body terminator frame in the same write!
            WriteSpanInt32(span, ref offset, 0);

            await stream.WriteAsync(buf.AsMemory(0, offset), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public static async ValueTask ReadRequestHeaderAsync(
        Stream stream,
        KiteIpcRequestHeader target,
        CancellationToken ct = default)
    {
        target.Reset();
        int payloadLength = await ReadInt32Async(stream, ct).ConfigureAwait(false);
        byte[] buf = ArrayPool<byte>.Shared.Rent(payloadLength);
        try
        {
            await ReadExactlyAsync(stream, buf.AsMemory(0, payloadLength), ct).ConfigureAwait(false);
            ReadOnlySpan<byte> span = buf.AsSpan(0, payloadLength);
            int offset = 0;

            target.Method = ReadSpanString(span, ref offset);
            target.Scheme = ReadSpanString(span, ref offset);
            target.Host = ReadSpanString(span, ref offset);
            target.PathBase = ReadSpanString(span, ref offset);
            target.Path = ReadSpanString(span, ref offset);
            target.QueryString = ReadSpanString(span, ref offset);
            target.RemoteIp = ReadSpanString(span, ref offset);
            target.RemotePort = ReadSpanInt32(span, ref offset);

            int headerCount = ReadSpanInt32(span, ref offset);
            for (int i = 0; i < headerCount; i++)
            {
                string key = ReadSpanString(span, ref offset);
                string val = ReadSpanString(span, ref offset);
                target.Headers.Add(new KeyValuePair<string, string>(key, val));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public static async ValueTask WriteResponseHeaderAsync(
        Stream stream,
        KiteIpcResponseHeader response,
        CancellationToken ct = default)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            Span<byte> span = buf.AsSpan();
            span[0] = (byte)KiteIpcMessageType.HttpResponse;
            int offset = 5; // reserve bytes 1..4 for payload length

            WriteSpanInt32(span, ref offset, response.StatusCode);
            WriteSpanInt32(span, ref offset, response.Headers.Count);
            for (int i = 0; i < response.Headers.Count; i++)
            {
                KeyValuePair<string, string> kvp = response.Headers[i];
                WriteSpanString(span, ref offset, kvp.Key);
                WriteSpanString(span, ref offset, kvp.Value);
            }

            int payloadLength = offset - 5;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(1, 4), payloadLength);
            await stream.WriteAsync(buf.AsMemory(0, offset), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public static async ValueTask ReadResponseHeaderAsync(
        Stream stream,
        KiteIpcResponseHeader target,
        CancellationToken ct = default)
    {
        target.Reset();
        KiteIpcMessageType? type = await ReadMessageTypeAsync(stream, ct).ConfigureAwait(false);
        if (type != KiteIpcMessageType.HttpResponse)
        {
            throw new InvalidDataException($"Expected HttpResponse IPC message, got '{type}'.");
        }

        int payloadLength = await ReadInt32Async(stream, ct).ConfigureAwait(false);
        byte[] buf = ArrayPool<byte>.Shared.Rent(payloadLength);
        try
        {
            await ReadExactlyAsync(stream, buf.AsMemory(0, payloadLength), ct).ConfigureAwait(false);
            ReadOnlySpan<byte> span = buf.AsSpan(0, payloadLength);
            int offset = 0;

            target.StatusCode = ReadSpanInt32(span, ref offset);
            int headerCount = ReadSpanInt32(span, ref offset);
            for (int i = 0; i < headerCount; i++)
            {
                string key = ReadSpanString(span, ref offset);
                string val = ReadSpanString(span, ref offset);
                target.Headers.Add(new KeyValuePair<string, string>(key, val));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public static async ValueTask WriteBodyFramesAsync(
        Stream ipcStream,
        Stream? bodyStream,
        CancellationToken ct = default)
    {
        if (bodyStream is not null)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                int bytesRead;
                while ((bytesRead = await bodyStream.ReadAsync(buffer.AsMemory(4, BufferSize - 4), ct).ConfigureAwait(false)) > 0)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), bytesRead);
                    await ipcStream.WriteAsync(buffer.AsMemory(0, 4 + bytesRead), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Write 0-length terminator frame
        await WriteInt32Async(ipcStream, 0, ct).ConfigureAwait(false);
        await ipcStream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async ValueTask ReadBodyFramesToAsync(
        Stream ipcStream,
        Stream destination,
        CancellationToken ct = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                int chunkLength = await ReadInt32Async(ipcStream, ct).ConfigureAwait(false);
                if (chunkLength <= 0)
                {
                    break;
                }

                int remaining = chunkLength;
                while (remaining > 0)
                {
                    int toRead = Math.Min(remaining, BufferSize);
                    await ReadExactlyAsync(ipcStream, buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                    await destination.WriteAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                    remaining -= toRead;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void WriteSpanInt32(Span<byte> dest, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(offset, 4), value);
        offset += 4;
    }

    private static int ReadSpanInt32(ReadOnlySpan<byte> src, ref int offset)
    {
        int val = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(offset, 4));
        offset += 4;
        return val;
    }

    private static void WriteSpanString(Span<byte> dest, ref int offset, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            WriteSpanInt32(dest, ref offset, 0);
            return;
        }

        int byteCount = Encoding.UTF8.GetBytes(value.AsSpan(), dest[(offset + 4)..]);
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(offset, 4), byteCount);
        offset += 4 + byteCount;
    }

    private static string ReadSpanString(ReadOnlySpan<byte> src, ref int offset)
    {
        int len = ReadSpanInt32(src, ref offset);
        if (len <= 0)
        {
            return string.Empty;
        }

        string str = Encoding.UTF8.GetString(src.Slice(offset, len));
        offset += len;
        return str;
    }

    private static async ValueTask WriteInt32Async(Stream stream, int value, CancellationToken ct)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), value);
            await stream.WriteAsync(buf.AsMemory(0, 4), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static async ValueTask<int> ReadInt32Async(Stream stream, CancellationToken ct)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            await ReadExactlyAsync(stream, buf.AsMemory(0, 4), ct).ConfigureAwait(false);
            return BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected EOF while reading IPC frame.");
            }
            offset += read;
        }
    }
}
