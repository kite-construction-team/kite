using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Kite.Core.Models;

namespace Kite.Server.Transport;

/// <summary>
/// Manages Layer-4 TCP &amp; TLS SNI passthrough stream listeners (<see cref="L4StreamProxyConfig"/>)
/// without terminating TLS at the edge.
/// </summary>
public sealed class L4StreamListenerManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ActiveStreamListener> _listeners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reconciles active Layer-4 TCP/SNI listeners against the latest <see cref="ServerConfig.TcpStreams"/>.
    /// </summary>
    public async ValueTask ReconcileAsync(IReadOnlyList<L4StreamProxyConfig> configs, CancellationToken ct)
    {
        var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (L4StreamProxyConfig cfg in configs)
        {
            if (!cfg.Enabled || cfg.ListenPort <= 0 || string.IsNullOrWhiteSpace(cfg.Id))
            {
                continue;
            }

            string key = $"{cfg.Id}:{cfg.ListenPort}";
            activeIds.Add(key);
            if (_listeners.TryGetValue(key, out ActiveStreamListener? existing))
            {
                existing.UpdateConfig(cfg);
            }
            else
            {
                var created = new ActiveStreamListener(cfg);
                if (_listeners.TryAdd(key, created))
                {
                    created.Start();
                }
            }
        }

        foreach (string key in _listeners.Keys.ToList())
        {
            if (!activeIds.Contains(key) && _listeners.TryRemove(key, out ActiveStreamListener? removed))
            {
                await removed.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (string key in _listeners.Keys.ToList())
        {
            if (_listeners.TryRemove(key, out ActiveStreamListener? listener))
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Zero-allocation parser for TLS ClientHello SNI extension (<c>0x0000</c>).
    /// Returns <see langword="false"/> if the packet is not a TLS Handshake ClientHello or needs more bytes.
    /// </summary>
    public static bool TryParseTlsClientHelloSni(ReadOnlySpan<byte> buffer, out string? sniHostname)
    {
        sniHostname = null;
        // Minimum TLS record header (5) + handshake header (4) + version (2) + random (32) = 43 bytes
        if (buffer.Length < 44)
        {
            return false;
        }

        // TLS Handshake record type = 0x16, ClientHello handshake type = 0x01
        if (buffer[0] != 0x16 || buffer[5] != 0x01)
        {
            return false;
        }

        int recordLen = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(3, 2));
        if (buffer.Length < 5 + recordLen)
        {
            return false;
        }

        ReadOnlySpan<byte> hello = buffer.Slice(5, recordLen);
        int offset = 4 + 2 + 32; // handshake header + legacy_version + random
        if (offset >= hello.Length)
        {
            return false;
        }

        int sessionIdLen = hello[offset];
        offset += 1 + sessionIdLen;
        if (offset + 2 > hello.Length)
        {
            return false;
        }

        int cipherSuitesLen = BinaryPrimitives.ReadUInt16BigEndian(hello.Slice(offset, 2));
        offset += 2 + cipherSuitesLen;
        if (offset + 1 > hello.Length)
        {
            return false;
        }

        int compressionMethodsLen = hello[offset];
        offset += 1 + compressionMethodsLen;
        if (offset + 2 > hello.Length)
        {
            return false;
        }

        int extensionsLen = BinaryPrimitives.ReadUInt16BigEndian(hello.Slice(offset, 2));
        offset += 2;
        int extensionsEnd = Math.Min(hello.Length, offset + extensionsLen);

        while (offset + 4 <= extensionsEnd)
        {
            ushort extType = BinaryPrimitives.ReadUInt16BigEndian(hello.Slice(offset, 2));
            ushort extLen = BinaryPrimitives.ReadUInt16BigEndian(hello.Slice(offset + 2, 2));
            offset += 4;
            if (offset + extLen > extensionsEnd)
            {
                break;
            }

            if (extType == 0x0000 && extLen >= 5) // server_name extension
            {
                ReadOnlySpan<byte> ext = hello.Slice(offset, extLen);
                int listLen = BinaryPrimitives.ReadUInt16BigEndian(ext.Slice(0, 2));
                int pos = 2;
                int listEnd = Math.Min(ext.Length, 2 + listLen);
                while (pos + 3 <= listEnd)
                {
                    byte nameType = ext[pos];
                    int nameLen = BinaryPrimitives.ReadUInt16BigEndian(ext.Slice(pos + 1, 2));
                    pos += 3;
                    if (pos + nameLen > listEnd)
                    {
                        break;
                    }

                    if (nameType == 0x00 && nameLen > 0)
                    {
                        sniHostname = Encoding.ASCII.GetString(ext.Slice(pos, nameLen));
                        return true;
                    }
                    pos += nameLen;
                }
            }

            offset += extLen;
        }

        return false;
    }

    public static (string Host, int Port) ResolveUpstream(L4StreamProxyConfig cfg, string? sniHostname)
    {
        if (!string.IsNullOrWhiteSpace(sniHostname) && cfg.SniRoutes.Count > 0)
        {
            foreach (L4SniRouteConfig route in cfg.SniRoutes)
            {
                if (string.Equals(route.SniHostname, sniHostname, StringComparison.OrdinalIgnoreCase) ||
                    (route.SniHostname.StartsWith("*.", StringComparison.Ordinal) &&
                     sniHostname.EndsWith(route.SniHostname[1..], StringComparison.OrdinalIgnoreCase)))
                {
                    return (route.UpstreamHost, route.UpstreamPort);
                }
            }
        }

        return (cfg.DefaultUpstreamHost, cfg.DefaultUpstreamPort);
    }

    private sealed class ActiveStreamListener : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private L4StreamProxyConfig _config;
        private Task? _acceptLoop;

        public ActiveStreamListener(L4StreamProxyConfig config)
        {
            _config = config;
            _listener = new TcpListener(IPAddress.Any, config.ListenPort);
        }

        public void UpdateConfig(L4StreamProxyConfig config) => _config = config;

        public void Start()
        {
            _listener.Start();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    continue;
                }

                _ = Task.Run(() => HandleConnectionAsync(client, ct), ct);
            }
        }

        private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
        {
            using TcpClient ownedClient = client;
            ownedClient.NoDelay = true;
            L4StreamProxyConfig cfg = _config;

            try
            {
                await using NetworkStream clientStream = ownedClient.GetStream();
                byte[] peekBuffer = new byte[4096];
                int peeked = 0;
                string? sni = null;

                if (cfg.EnableTlsSniRouting)
                {
                    using var peekCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    peekCts.CancelAfter(500);
                    try
                    {
                        peeked = await clientStream.ReadAsync(peekBuffer, peekCts.Token).ConfigureAwait(false);
                        if (peeked > 0)
                        {
                            TryParseTlsClientHelloSni(peekBuffer.AsSpan(0, peeked), out sni);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Server-first protocols (e.g. non-TLS before greeting) may not send initial bytes
                    }
                }

                (string host, int port) = ResolveUpstream(cfg, sni);
                using var upstream = new TcpClient { NoDelay = true };
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(Math.Clamp(cfg.ConnectTimeoutMilliseconds, 250, 30_000));
                await upstream.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);

                await using NetworkStream upstreamStream = upstream.GetStream();
                if (peeked > 0)
                {
                    await upstreamStream.WriteAsync(peekBuffer.AsMemory(0, peeked), ct).ConfigureAwait(false);
                    await upstreamStream.FlushAsync(ct).ConfigureAwait(false);
                }

                Task c2u = clientStream.CopyToAsync(upstreamStream, ct);
                Task u2c = upstreamStream.CopyToAsync(clientStream, ct);
                await Task.WhenAny(c2u, u2c).ConfigureAwait(false);
            }
            catch
            {
                // Close connection cleanly on upstream error or reset
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                _listener.Stop();
            }
            catch
            {
            }

            if (_acceptLoop is not null)
            {
                try
                {
                    await _acceptLoop.ConfigureAwait(false);
                }
                catch
                {
                }
            }
            _cts.Dispose();
        }
    }
}
