using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Kite.Core.Models;

namespace Kite.Server.State;

/// <summary>
/// Abstraction for shared distributed edge state (rate-limit counters, cache flush epochs).
/// </summary>
public interface IDistributedEdgeState
{
    ValueTask<bool> PingAsync(CancellationToken ct = default);
    ValueTask<long> IncrementWindowAsync(string key, int windowSeconds, CancellationToken ct = default);
    ValueTask<string?> GetStringAsync(string key, CancellationToken ct = default);
    ValueTask SetStringAsync(string key, string value, int ttlSeconds, CancellationToken ct = default);
}

/// <summary>
/// Dependency-free Redis RESP2 TCP client supporting <c>AUTH</c>, <c>PING</c>, <c>GET</c>, <c>SET</c>,
/// <c>INCR</c>, and <c>EXPIRE</c> with automatic fallback on transient connectivity loss.
/// </summary>
public sealed class RedisRespClient : IDistributedEdgeState, IDisposable
{
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private DistributedStateConfig _config;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private StreamReader? _reader;

    public RedisRespClient(DistributedStateConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    public void UpdateConfig(DistributedStateConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        Disconnect();
    }

    public async ValueTask<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            string? resp = await ExecuteCommandAsync(["PING"], ct).ConfigureAwait(false);
            return string.Equals(resp, "PONG", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask<long> IncrementWindowAsync(string key, int windowSeconds, CancellationToken ct = default)
    {
        string prefixedKey = $"{_config.KeyPrefix}{key}";
        string? countStr = await ExecuteCommandAsync(["INCR", prefixedKey], ct).ConfigureAwait(false);
        if (!long.TryParse(countStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long count))
        {
            return 0;
        }

        if (count == 1 && windowSeconds > 0)
        {
            await ExecuteCommandAsync(
                ["EXPIRE", prefixedKey, windowSeconds.ToString(CultureInfo.InvariantCulture)],
                ct).ConfigureAwait(false);
        }

        return count;
    }

    public Task<string?> GetStringAsync(string key, CancellationToken ct = default) =>
        ExecuteCommandAsync(["GET", $"{_config.KeyPrefix}{key}"], ct).AsTask();

    ValueTask<string?> IDistributedEdgeState.GetStringAsync(string key, CancellationToken ct) =>
        ExecuteCommandAsync(["GET", $"{_config.KeyPrefix}{key}"], ct);

    public async ValueTask SetStringAsync(string key, string value, int ttlSeconds, CancellationToken ct = default)
    {
        string prefixedKey = $"{_config.KeyPrefix}{key}";
        if (ttlSeconds > 0)
        {
            await ExecuteCommandAsync(
                ["SET", prefixedKey, value, "EX", ttlSeconds.ToString(CultureInfo.InvariantCulture)],
                ct).ConfigureAwait(false);
        }
        else
        {
            await ExecuteCommandAsync(["SET", prefixedKey, value], ct).ConfigureAwait(false);
        }
    }

    private async ValueTask<string?> ExecuteCommandAsync(string[] args, CancellationToken ct)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            byte[] payload = EncodeRespArray(args);
            await _stream!.WriteAsync(payload, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            return await ReadRespReplyAsync(_reader!, ct).ConfigureAwait(false);
        }
        catch
        {
            Disconnect();
            throw;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_tcpClient is { Connected: true } && _stream is not null && _reader is not null)
        {
            return;
        }

        Disconnect();
        int timeoutMs = Math.Clamp(_config.ConnectTimeoutMilliseconds, 200, 15000);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(timeoutMs);

        var client = new TcpClient();
        await client.ConnectAsync(_config.RedisHost, Math.Clamp(_config.RedisPort, 1, 65535), connectCts.Token)
            .ConfigureAwait(false);
        NetworkStream stream = client.GetStream();
        stream.ReadTimeout = timeoutMs;
        stream.WriteTimeout = timeoutMs;
        var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        _tcpClient = client;
        _stream = stream;
        _reader = reader;

        if (!string.IsNullOrEmpty(_config.Password))
        {
            byte[] authBytes = EncodeRespArray(["AUTH", _config.Password]);
            await _stream.WriteAsync(authBytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            await ReadRespReplyAsync(_reader, ct).ConfigureAwait(false);
        }
    }

    public static byte[] EncodeRespArray(IReadOnlyList<string> args)
    {
        var sb = new StringBuilder(64);
        sb.Append('*').Append(args.Count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        for (int i = 0; i < args.Count; i++)
        {
            int byteLen = Encoding.UTF8.GetByteCount(args[i]);
            sb.Append('$').Append(byteLen.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
              .Append(args[i]).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static async ValueTask<string?> ReadRespReplyAsync(StreamReader reader, CancellationToken ct)
    {
        string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        char prefix = line[0];
        string body = line[1..];
        switch (prefix)
        {
            case '+':
            case ':':
                return body;
            case '-':
                throw new InvalidOperationException($"Redis RESP error: {body}");
            case '$':
                if (!int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bulkLen) || bulkLen < 0)
                {
                    return null;
                }

                char[] chars = new char[bulkLen];
                int read = 0;
                while (read < bulkLen)
                {
                    int chunk = await reader.ReadAsync(chars.AsMemory(read, bulkLen - read), ct).ConfigureAwait(false);
                    if (chunk <= 0)
                    {
                        break;
                    }
                    read += chunk;
                }
                await reader.ReadLineAsync(ct).ConfigureAwait(false); // trailing CRLF
                return new string(chars, 0, read);
            default:
                return line;
        }
    }

    private void Disconnect()
    {
        _reader?.Dispose();
        _reader = null;
        _stream?.Dispose();
        _stream = null;
        _tcpClient?.Dispose();
        _tcpClient = null;
    }

    public void Dispose()
    {
        Disconnect();
        _ioLock.Dispose();
    }
}
