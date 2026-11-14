using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using Kite.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Kite.Server.Pools;

/// <summary>
/// Manages load-balanced reverse proxy upstream clusters with active /healthz probing,
/// passive circuit breaking, weighted canary splits, and bounded idempotent retries.
/// </summary>
public sealed class UpstreamClusterManager : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly SocketsHttpHandler _socketsHandler;
    private readonly HttpMessageInvoker _invoker;
    private readonly HttpClient _healthClient;
    private readonly ConcurrentDictionary<string, ManagedCluster> _clusters = new(StringComparer.OrdinalIgnoreCase);

    public UpstreamClusterManager(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _socketsHandler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 2048
        };
        _invoker = new HttpMessageInvoker(_socketsHandler, disposeHandler: false);
        _healthClient = new HttpClient(_socketsHandler, disposeHandler: false);
    }

    public void ReconcileClusters(IReadOnlyList<UpstreamClusterConfig> configs)
    {
        var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (UpstreamClusterConfig cfg in configs)
        {
            if (string.IsNullOrWhiteSpace(cfg.Id))
            {
                continue;
            }

            activeIds.Add(cfg.Id);
            _clusters.AddOrUpdate(
                cfg.Id,
                _ => new ManagedCluster(cfg, _timeProvider),
                (_, existing) =>
                {
                    existing.Update(cfg);
                    return existing;
                });
        }

        foreach (string id in _clusters.Keys.ToList())
        {
            if (!activeIds.Contains(id))
            {
                _clusters.TryRemove(id, out _);
            }
        }
    }

    public bool HasCluster(string? clusterId) =>
        !string.IsNullOrWhiteSpace(clusterId) && _clusters.ContainsKey(clusterId);

    public HttpMessageInvoker ProxyInvoker => _invoker;

    public string? ResolveAddressForContext(string? clusterId, HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(clusterId) || !_clusters.TryGetValue(clusterId, out ManagedCluster? cluster))
        {
            return null;
        }
        return cluster.SelectDestination(context, excluded: null)?.Config.Address;
    }

    public string? ResolveTargetClusterId(
        SiteApplicationConfig app,
        string? overrideClusterId = null,
        string? overrideCanaryClusterId = null,
        int? overrideCanaryWeight = null)
    {
        string? canaryId = !string.IsNullOrWhiteSpace(overrideCanaryClusterId) ? overrideCanaryClusterId : app.CanaryClusterId;
        int canaryWeight = overrideCanaryWeight ?? app.CanaryWeightPercent;
        if (!string.IsNullOrWhiteSpace(canaryId) &&
            canaryWeight > 0 &&
            _clusters.ContainsKey(canaryId))
        {
            int roll = Random.Shared.Next(100);
            if (roll < Math.Clamp(canaryWeight, 0, 100))
            {
                return canaryId;
            }
        }

        return !string.IsNullOrWhiteSpace(overrideClusterId) ? overrideClusterId : app.UpstreamClusterId;
    }

    public ValueTask<bool> ForwardToClusterAsync(
        HttpContext context,
        SiteApplicationConfig app,
        ReadOnlyMemory<char> subPath,
        CancellationToken ct) =>
        ForwardToClusterAsync(
            context,
            app,
            subPath,
            overrideClusterId: null,
            overrideCanaryClusterId: null,
            overrideCanaryWeight: null,
            ct);

    public async ValueTask<bool> ForwardToClusterAsync(
        HttpContext context,
        SiteApplicationConfig app,
        ReadOnlyMemory<char> subPath,
        string? overrideClusterId,
        string? overrideCanaryClusterId,
        int? overrideCanaryWeight,
        CancellationToken ct)
    {
        string? clusterId = ResolveTargetClusterId(app, overrideClusterId, overrideCanaryClusterId, overrideCanaryWeight);
        if (string.IsNullOrWhiteSpace(clusterId) || !_clusters.TryGetValue(clusterId, out ManagedCluster? cluster))
        {
            return false;
        }

        int maxRetries = IsIdempotentMethod(context.Request.Method) ? Math.Clamp(cluster.Config.RetryCount, 0, 3) : 0;
        int timeoutSec = Math.Clamp(cluster.Config.RequestTimeoutSeconds, 1, 300);
        HashSet<string>? excluded = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            ManagedDestination? dest = cluster.SelectDestination(context, excluded);
            if (dest is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return true;
            }

            Interlocked.Increment(ref dest.ActiveRequests);
            Interlocked.Increment(ref dest.TotalRequests);
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

                int status = await HttpProxyForwarder.ForwardAsync(
                    context,
                    _invoker,
                    dest.Config.Address,
                    subPath,
                    app.EnableStreamingPassThrough,
                    timeoutCts.Token).ConfigureAwait(false);

                if (status is 502 or 503 or 504)
                {
                    dest.RecordFailure(cluster.Config.PassiveCircuitBreaker, $"Upstream HTTP {status}", _timeProvider.GetUtcNow());
                    if (attempt < maxRetries && !context.Response.HasStarted)
                    {
                        excluded ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        excluded.Add(dest.Config.Id);
                        continue;
                    }
                }
                else
                {
                    dest.RecordSuccess();
                }

                return true;
            }
            catch (Exception ex) when (attempt < maxRetries && !context.Response.HasStarted && !ct.IsCancellationRequested)
            {
                dest.RecordFailure(cluster.Config.PassiveCircuitBreaker, ex.Message, _timeProvider.GetUtcNow());
                excluded ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                excluded.Add(dest.Config.Id);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                dest.RecordFailure(cluster.Config.PassiveCircuitBreaker, ex.Message, _timeProvider.GetUtcNow());
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                }
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref dest.ActiveRequests);
            }
        }

        return true;
    }

    /// <summary>
    /// Dispatches an asynchronous non-blocking shadow mirror copy of the request to <paramref name="shadowClusterId"/>
    /// without delaying or altering the primary client response.
    /// </summary>
    public async ValueTask ShadowRequestAsync(
        HttpContext context,
        string shadowClusterId,
        string transformedSubPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(shadowClusterId) ||
            !_clusters.TryGetValue(shadowClusterId, out ManagedCluster? cluster))
        {
            return;
        }

        ManagedDestination? dest = cluster.SelectDestination(context, excluded: null);
        if (dest is null)
        {
            return;
        }

        string method = context.Request.Method;
        string targetUri = $"{dest.Config.Address.TrimEnd('/')}{transformedSubPath}{context.Request.QueryString.Value}";
        var headers = new List<(string Key, string?[] Values)>();
        foreach (KeyValuePair<string, StringValues> h in context.Request.Headers)
        {
            if (string.Equals(h.Key, "Host", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(h.Key, "Connection", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(h.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            headers.Add((h.Key, h.Value.ToArray()));
        }

        byte[]? bodyBytes = null;
        if (HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method))
        {
            context.Request.EnableBuffering(256 * 1024);
            using var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
            if (context.Request.Body.CanSeek)
            {
                context.Request.Body.Position = 0;
            }
            bodyBytes = ms.ToArray();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var shadowCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var msg = new HttpRequestMessage(new HttpMethod(method), targetUri);
                msg.Headers.TryAddWithoutValidation("X-Kite-Shadow-Mirror", "true");
                if (bodyBytes is { Length: > 0 })
                {
                    msg.Content = new ByteArrayContent(bodyBytes);
                }

                foreach ((string k, string?[] vals) in headers)
                {
                    if (!msg.Headers.TryAddWithoutValidation(k, vals))
                    {
                        msg.Content?.Headers.TryAddWithoutValidation(k, vals);
                    }
                }

                using HttpResponseMessage _ = await _invoker.SendAsync(msg, shadowCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Shadow traffic never fails primary requests
            }
        });
    }

    public async ValueTask EvaluateActiveHealthChecksAsync(CancellationToken ct)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (ManagedCluster cluster in _clusters.Values)
        {
            ActiveHealthCheckConfig hc = cluster.Config.ActiveHealthCheck;
            if (!hc.Enabled)
            {
                continue;
            }

            if (now - cluster.LastHealthCheckUtc < TimeSpan.FromSeconds(Math.Max(1, hc.IntervalSeconds)))
            {
                continue;
            }

            cluster.LastHealthCheckUtc = now;
            int timeoutMs = Math.Clamp(hc.TimeoutSeconds, 1, 30) * 1000;

            foreach (ManagedDestination dest in cluster.Destinations)
            {
                string path = string.IsNullOrWhiteSpace(dest.Config.HealthCheckPath) ? hc.Path : dest.Config.HealthCheckPath;
                if (!path.StartsWith('/'))
                {
                    path = "/" + path;
                }

                string probeUri = $"{dest.Config.Address.TrimEnd('/')}{path}";
                try
                {
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probeCts.CancelAfter(timeoutMs);
                    using HttpResponseMessage resp = await _healthClient.GetAsync(probeUri, probeCts.Token).ConfigureAwait(false);
                    if ((int)resp.StatusCode == hc.ExpectedStatusCode)
                    {
                        dest.RecordSuccess();
                        dest.ActiveHealthy = true;
                    }
                    else
                    {
                        dest.ActiveHealthy = false;
                        dest.LastError = $"Health check returned {(int)resp.StatusCode}";
                    }
                }
                catch (Exception ex)
                {
                    dest.ActiveHealthy = false;
                    dest.LastError = ex.Message;
                }
            }
        }
    }

    public List<UpstreamClusterStatus> CaptureStatusSnapshot()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var list = new List<UpstreamClusterStatus>(_clusters.Count);
        foreach (ManagedCluster cluster in _clusters.Values)
        {
            var status = new UpstreamClusterStatus
            {
                ClusterId = cluster.Config.Id,
                LoadBalancingPolicy = cluster.Config.LoadBalancingPolicy
            };

            foreach (ManagedDestination dest in cluster.Destinations)
            {
                bool circuitOpen = dest.IsCircuitOpen(now);
                status.Destinations.Add(new UpstreamDestinationStatus
                {
                    Id = dest.Config.Id,
                    Address = dest.Config.Address,
                    IsHealthy = dest.ActiveHealthy && !circuitOpen,
                    CircuitOpen = circuitOpen,
                    ActiveRequests = Math.Max(0, Volatile.Read(ref dest.ActiveRequests)),
                    TotalRequests = Interlocked.Read(ref dest.TotalRequests),
                    ConsecutiveFailures = Volatile.Read(ref dest.ConsecutiveFailures),
                    LastError = dest.LastError
                });
            }

            list.Add(status);
        }

        return list;
    }

    private static bool IsIdempotentMethod(string method) =>
        HttpMethods.IsGet(method) ||
        HttpMethods.IsHead(method) ||
        HttpMethods.IsOptions(method) ||
        HttpMethods.IsPut(method) ||
        HttpMethods.IsDelete(method);

    public void Dispose()
    {
        _healthClient.Dispose();
        _invoker.Dispose();
        _socketsHandler.Dispose();
    }

    private sealed class ManagedCluster
    {
        private readonly TimeProvider _timeProvider;
        private int _rrIndex;
        public UpstreamClusterConfig Config { get; private set; }
        public ManagedDestination[] Destinations { get; private set; }
        public DateTimeOffset LastHealthCheckUtc { get; set; } = DateTimeOffset.MinValue;

        public ManagedCluster(UpstreamClusterConfig config, TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
            Config = config;
            Destinations = config.Destinations.Select(static d => new ManagedDestination(d)).ToArray();
        }

        public void Update(UpstreamClusterConfig next)
        {
            Dictionary<string, ManagedDestination> existingById = Destinations.ToDictionary(static d => d.Config.Id, StringComparer.OrdinalIgnoreCase);
            var updated = new ManagedDestination[next.Destinations.Count];
            for (int i = 0; i < next.Destinations.Count; i++)
            {
                UpstreamDestinationConfig dCfg = next.Destinations[i];
                if (existingById.TryGetValue(dCfg.Id, out ManagedDestination? old) &&
                    string.Equals(old.Config.Address, dCfg.Address, StringComparison.OrdinalIgnoreCase))
                {
                    old.Config = dCfg;
                    updated[i] = old;
                }
                else
                {
                    updated[i] = new ManagedDestination(dCfg);
                }
            }

            Config = next;
            Destinations = updated;
        }

        public ManagedDestination? SelectDestination(HttpContext context, HashSet<string>? excluded)
        {
            ManagedDestination[] all = Destinations;
            if (all.Length == 0)
            {
                return null;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            Span<int> healthyIndices = all.Length <= 64 ? stackalloc int[all.Length] : new int[all.Length];
            int healthyCount = 0;

            for (int i = 0; i < all.Length; i++)
            {
                ManagedDestination d = all[i];
                if (excluded is not null && excluded.Contains(d.Config.Id))
                {
                    continue;
                }

                if (d.ActiveHealthy && !d.IsCircuitOpen(now))
                {
                    healthyIndices[healthyCount++] = i;
                }
            }

            if (healthyCount == 0)
            {
                return null;
            }

            if (healthyCount == 1)
            {
                return all[healthyIndices[0]];
            }

            switch (Config.LoadBalancingPolicy)
            {
                case LoadBalancingPolicy.ConsistentHash:
                {
                    string key = ResolveStickyKey(context);
                    uint hash = ComputeFnv1a(key.AsSpan());
                    return all[healthyIndices[(int)(hash % (uint)healthyCount)]];
                }

                case LoadBalancingPolicy.LeastRequests:
                {
                    ManagedDestination best = all[healthyIndices[0]];
                    int bestActive = Volatile.Read(ref best.ActiveRequests);
                    for (int i = 1; i < healthyCount; i++)
                    {
                        ManagedDestination cand = all[healthyIndices[i]];
                        int act = Volatile.Read(ref cand.ActiveRequests);
                        if (act < bestActive)
                        {
                            best = cand;
                            bestActive = act;
                        }
                    }
                    return best;
                }

                case LoadBalancingPolicy.PowerOfTwoChoices:
                {
                    int i1 = Random.Shared.Next(healthyCount);
                    int i2 = Random.Shared.Next(healthyCount);
                    ManagedDestination d1 = all[healthyIndices[i1]];
                    ManagedDestination d2 = all[healthyIndices[i2]];
                    return Volatile.Read(ref d1.ActiveRequests) <= Volatile.Read(ref d2.ActiveRequests) ? d1 : d2;
                }

                case LoadBalancingPolicy.RoundRobin:
                default:
                {
                    uint next = (uint)Interlocked.Increment(ref _rrIndex);
                    return all[healthyIndices[(int)(next % (uint)healthyCount)]];
                }
            }
        }

        private string ResolveStickyKey(HttpContext context)
        {
            if (!string.IsNullOrWhiteSpace(Config.StickyHeaderOrCookie))
            {
                if (context.Request.Headers.TryGetValue(Config.StickyHeaderOrCookie, out StringValues hv) &&
                    !StringValues.IsNullOrEmpty(hv))
                {
                    return hv.ToString();
                }

                if (context.Request.Cookies.TryGetValue(Config.StickyHeaderOrCookie, out string? cookieVal) &&
                    !string.IsNullOrEmpty(cookieVal))
                {
                    return cookieVal;
                }
            }

            return context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
        }

        private static uint ComputeFnv1a(ReadOnlySpan<char> value)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= 16777619u;
            }
            return hash;
        }
    }

    private sealed class ManagedDestination(UpstreamDestinationConfig config)
    {
        public UpstreamDestinationConfig Config { get; set; } = config;
        public volatile bool ActiveHealthy = true;
        public int ActiveRequests;
        public long TotalRequests;
        public int ConsecutiveFailures;
        public string? LastError;
        private long _circuitOpenUntilTicks;

        public bool IsCircuitOpen(DateTimeOffset nowUtc)
        {
            long untilTicks = Volatile.Read(ref _circuitOpenUntilTicks);
            if (untilTicks == 0)
            {
                return false;
            }

            if (nowUtc.UtcTicks >= untilTicks)
            {
                Volatile.Write(ref _circuitOpenUntilTicks, 0);
                return false;
            }

            return true;
        }

        public void RecordSuccess()
        {
            Interlocked.Exchange(ref ConsecutiveFailures, 0);
            Volatile.Write(ref _circuitOpenUntilTicks, 0);
            LastError = null;
        }

        public void RecordFailure(PassiveCircuitBreakerConfig breaker, string error, DateTimeOffset nowUtc)
        {
            LastError = error;
            int failures = Interlocked.Increment(ref ConsecutiveFailures);
            if (breaker.Enabled && failures >= Math.Max(1, breaker.FailureThreshold))
            {
                long openUntil = nowUtc.AddSeconds(Math.Max(1, breaker.EjectionSeconds)).UtcTicks;
                Volatile.Write(ref _circuitOpenUntilTicks, openUntil);
            }
        }
    }
}
