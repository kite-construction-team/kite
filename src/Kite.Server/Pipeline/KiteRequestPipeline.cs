using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using Kite.Core.Memory;
using Kite.Core.Models;
using Kite.Core.Routing;
using Kite.Server.HttpModules;
using Kite.Server.Observability;
using Kite.Server.Plugins;
using Kite.Server.Pools;
using Kite.Server.State;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Primitives;

namespace Kite.Server.Pipeline;

public interface IKiteEarlyHintsFeature
{
    ValueTask SendEarlyHintsAsync(IReadOnlyList<string> linkHeaders, CancellationToken ct = default);
}

public sealed class LatencyRingTracker
{
    private const int Capacity = 256;
    private readonly double[] _samples = new double[Capacity];
    private readonly Lock _lock = new();
    private int _writeIndex;
    private int _count;

    public void Record(double durationMs)
    {
        lock (_lock)
        {
            _samples[_writeIndex] = durationMs;
            _writeIndex = (_writeIndex + 1) % Capacity;
            if (_count < Capacity)
            {
                _count++;
            }
        }
    }

    public (double AvgMs, double P99Ms) ComputeStats()
    {
        int n;
        Span<double> copy = stackalloc double[Capacity];
        lock (_lock)
        {
            n = _count;
            if (n == 0)
            {
                return (0.0, 0.0);
            }
            _samples.AsSpan(0, n).CopyTo(copy);
        }

        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            sum += copy[i];
        }

        copy[..n].Sort();
        int p99Idx = Math.Clamp((int)Math.Ceiling(n * 0.99) - 1, 0, n - 1);
        return (Math.Round(sum / n, 2), Math.Round(copy[p99Idx], 2));
    }
}

public sealed class SiteRuntimeTelemetry
{
    public long TotalRequests;
    public long Status2xxCount;
    public long Status3xxCount;
    public long Status4xxCount;
    public long Status5xxCount;
    public long TotalBytesOut;
    public long CacheHits;
    public long CacheLookups;
    public int ActiveWebSockets;
    public LatencyRingTracker Latency { get; } = new();

    private long _lastSnapshotTicks = Stopwatch.GetTimestamp();
    private long _lastTotalRequests;

    public (double Rps, double AvgLatencyMs, double P99LatencyMs, double CacheHitPct) SampleRateAndLatency()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedSec = Math.Max(0.1, Stopwatch.GetElapsedTime(_lastSnapshotTicks, now).TotalSeconds);
        _lastSnapshotTicks = now;

        long currentReqs = Interlocked.Read(ref TotalRequests);
        long prevReqs = Interlocked.Exchange(ref _lastTotalRequests, currentReqs);
        double rps = Math.Max(0.0, (currentReqs - prevReqs) / elapsedSec);

        (double avg, double p99) = Latency.ComputeStats();
        long lookups = Interlocked.Read(ref CacheLookups);
        long hits = Interlocked.Read(ref CacheHits);
        double hitPct = lookups == 0 ? 0.0 : Math.Round((hits * 100.0) / lookups, 1);

        return (Math.Round(rps, 1), avg, p99, hitPct);
    }
}

public sealed class KiteRequestPipeline : IDisposable
{
    private static readonly FileExtensionContentTypeProvider MimeProvider = CreateMimeProvider();
    private static readonly ObjectPool<FrebTraceScope> FrebScopePool =
        new DefaultObjectPoolProvider { MaximumRetained = Environment.ProcessorCount * 32 }
            .Create(new DefaultPooledObjectPolicy<FrebTraceScope>());

    private readonly Func<HostRouteTable> _routeTableAccessor;
    private readonly Func<string, ApplicationPoolSupervisor?> _poolAccessor;
    private readonly KiteOutputCacheModule _outputCache;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SiteRuntimeTelemetry> _siteTelemetry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string, string>? _w3cLogSink;
    private readonly Action<string, string>? _jsonLogSink;
    private readonly Action<FrebTraceRecord>? _frebSink;
    private readonly Func<string, string?>? _acmeChallengeResolver;
    private readonly UpstreamClusterManager? _upstreamManager;
    private readonly Func<IDistributedEdgeState?>? _distributedStateAccessor;
    private readonly EdgePluginHost? _pluginHost;
    private readonly Func<int, string, string?>? _meshPeerResolver;
    private readonly HttpMessageInvoker _fallbackMeshInvoker = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5)
    });
    private readonly LatencyRingTracker _globalLatency = new();

    private long _totalRequestsProcessed;
    private long _totalBytesSent;
    private long _totalBytesReceived;
    private long _activeConnections;
    private long _interval2xx;
    private long _interval3xx;
    private long _interval4xx;
    private long _interval5xx;
    private long _lastIntervalRequests;
    private long _lastIntervalBytesOut;
    private long _lastIntervalBytesIn;
    private long _lastIntervalTimestamp = Stopwatch.GetTimestamp();

    public KiteRequestPipeline(
        Func<HostRouteTable> routeTableAccessor,
        Func<string, ApplicationPoolSupervisor?> poolAccessor,
        TimeProvider? timeProvider = null,
        Action<string, string>? w3cLogSink = null,
        Action<FrebTraceRecord>? frebSink = null,
        Func<string, string?>? acmeChallengeResolver = null,
        UpstreamClusterManager? upstreamManager = null,
        Action<string, string>? jsonLogSink = null,
        Func<IDistributedEdgeState?>? distributedStateAccessor = null,
        EdgePluginHost? pluginHost = null,
        Func<int, string, string?>? meshPeerResolver = null)
    {
        _routeTableAccessor = routeTableAccessor;
        _poolAccessor = poolAccessor;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _outputCache = new KiteOutputCacheModule(_timeProvider);
        _w3cLogSink = w3cLogSink;
        _jsonLogSink = jsonLogSink;
        _frebSink = frebSink;
        _acmeChallengeResolver = acmeChallengeResolver;
        _upstreamManager = upstreamManager;
        _distributedStateAccessor = distributedStateAccessor;
        _pluginHost = pluginHost;
        _meshPeerResolver = meshPeerResolver;
    }

    public IReadOnlyCollection<string> GetTrackedSiteIds() => _siteTelemetry.Keys.ToList();

    public long TotalRequestsProcessed => Interlocked.Read(ref _totalRequestsProcessed);
    public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);
    public long ActiveConnections => Interlocked.Read(ref _activeConnections);
    public KiteOutputCacheModule OutputCache => _outputCache;

    public TelemetrySampleBucket DrainIntervalTelemetry(DateTimeOffset nowUtc, out double avgLatencyMs)
    {
        long nowTs = Stopwatch.GetTimestamp();
        long prevTs = Interlocked.Exchange(ref _lastIntervalTimestamp, nowTs);
        double sec = Math.Max(0.25, Stopwatch.GetElapsedTime(prevTs, nowTs).TotalSeconds);

        long totalReqs = TotalRequestsProcessed;
        long deltaReqs = Math.Max(0, totalReqs - Interlocked.Exchange(ref _lastIntervalRequests, totalReqs));

        long totalOut = TotalBytesSent;
        long deltaOut = Math.Max(0, totalOut - Interlocked.Exchange(ref _lastIntervalBytesOut, totalOut));

        long totalIn = TotalBytesReceived;
        long deltaIn = Math.Max(0, totalIn - Interlocked.Exchange(ref _lastIntervalBytesIn, totalIn));

        int c2 = (int)Interlocked.Exchange(ref _interval2xx, 0);
        int c3 = (int)Interlocked.Exchange(ref _interval3xx, 0);
        int c4 = (int)Interlocked.Exchange(ref _interval4xx, 0);
        int c5 = (int)Interlocked.Exchange(ref _interval5xx, 0);

        (double avg, double p99) = _globalLatency.ComputeStats();
        avgLatencyMs = avg;

        return new TelemetrySampleBucket
        {
            TimestampEpochSeconds = nowUtc.ToUnixTimeSeconds(),
            RequestsPerSecond = Math.Round(deltaReqs / sec, 2),
            BytesSentPerSecond = (long)(deltaOut / sec),
            BytesReceivedPerSecond = (long)(deltaIn / sec),
            Status2xxCount = c2,
            Status3xxCount = c3,
            Status4xxCount = c4,
            Status5xxCount = c5,
            P99LatencyMs = p99
        };
    }

    public SiteRuntimeTelemetry GetSiteTelemetry(string siteId) =>
        _siteTelemetry.GetOrAdd(siteId, static _ => new SiteRuntimeTelemetry());

    public async Task ExecuteAsync(HttpContext context)
    {
        Interlocked.Increment(ref _activeConnections);
        Interlocked.Increment(ref _totalRequestsProcessed);
        long startTimestamp = Stopwatch.GetTimestamp();

        if (context.Request.ContentLength is long reqLen && reqLen > 0)
        {
            Interlocked.Add(ref _totalBytesReceived, reqLen);
        }

        KiteRouteMatchContext routeCtx = KiteObjectPools.RouteContextPool.Get();
        routeCtx.RequestStartTimestamp = startTimestamp;
        FrebTraceScope frebScope = FrebScopePool.Get();
        frebScope.Begin(startTimestamp);

        CompiledSiteRoute? compiledSite = null;
        SiteRuntimeTelemetry? telemetry = null;
        string rawHost = string.Empty;
        int localPort = context.Connection.LocalPort;
        string requestPath = context.Request.Path.Value ?? "/";
        HostRouteTable routeTable = _routeTableAccessor();
        using Activity? activity = KiteMetricsAndTracing.StartRequestActivity(context, routeTable.ServerConfig.Observability);

        try
        {
            if (await KiteMetricsAndTracing.TryHandleObservabilityEndpointAsync(
                    context,
                    requestPath,
                    routeTable.ServerConfig,
                    this,
                    routeTable,
                    _upstreamManager).ConfigureAwait(false))
            {
                return;
            }

            // 1. RFC 9112 Section 5.4: Validate Host header presence & uniqueness
            if (!ValidateHostHeader(context.Request, out rawHost))
            {
                await WriteProblemDetailsAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Bad Request",
                    "RFC 9112 Section 5.4: A valid single Host header field is required.").ConfigureAwait(false);
                return;
            }

            // 2. Resolve Site via lock-free HostRouteTable
            if (localPort <= 0 && HostString.FromUriComponent(rawHost).Port is int parsedPort)
            {
                localPort = parsedPort;
            }
            if (localPort <= 0)
            {
                localPort = string.Equals(context.Request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
            }

            // Named local site wins; otherwise check active Edge Mesh peer hostnames before wildcard '*' fallback.
            if (!routeTable.TryMatchNamedSite(localPort, rawHost.AsSpan(), out compiledSite) &&
                _meshPeerResolver is not null)
            {
                ReadOnlySpan<char> meshHost = HostRouteTable.ExtractHostWithoutPort(rawHost.AsSpan());
                if (!meshHost.IsEmpty)
                {
                    string? peerTargetBase = _meshPeerResolver(localPort, new string(meshHost));
                    if (!string.IsNullOrWhiteSpace(peerTargetBase))
                    {
                        int hop = int.TryParse(context.Request.Headers["X-Kite-Mesh-Hop"], out int parsedHop) ? parsedHop : 0;
                        if (hop >= 2)
                        {
                            await WriteProblemDetailsAsync(
                                context,
                                StatusCodes.Status508LoopDetected,
                                "Mesh Routing Loop Detected",
                                $"Local edge mesh hop limit exceeded for host '{rawHost}'.").ConfigureAwait(false);
                            return;
                        }

                        HttpMessageInvoker invoker = _upstreamManager?.ProxyInvoker ?? _fallbackMeshInvoker;
                        await HttpProxyForwarder.ForwardAsync(
                            context,
                            invoker,
                            peerTargetBase,
                            requestPath.AsMemory(),
                            enableStreamingPassThrough: true,
                            context.RequestAborted,
                            preserveHostHeader: true).ConfigureAwait(false);
                        return;
                    }
                }
            }

            if (compiledSite is null &&
                (!routeTable.TryMatchSite(localPort, rawHost.AsSpan(), out compiledSite) || compiledSite is null))
            {
                // Allow RFC 8555 HTTP-01 challenge responses even before host bindings match
                const string unmatchedAcmePrefix = "/.well-known/acme-challenge/";
                if (_acmeChallengeResolver is not null &&
                    requestPath.StartsWith(unmatchedAcmePrefix, StringComparison.Ordinal))
                {
                    string token = requestPath[unmatchedAcmePrefix.Length..];
                    string? keyAuth = _acmeChallengeResolver(token);
                    if (!string.IsNullOrEmpty(keyAuth))
                    {
                        context.Response.StatusCode = StatusCodes.Status200OK;
                        context.Response.ContentType = "text/plain; charset=utf-8";
                        await context.Response.WriteAsync(keyAuth, context.RequestAborted).ConfigureAwait(false);
                        return;
                    }
                }

                await WriteProblemDetailsAsync(
                    context,
                    StatusCodes.Status404NotFound,
                    "Site Not Found",
                    $"No active site binding matched port {localPort} and host '{rawHost}'.").ConfigureAwait(false);
                return;
            }

            SiteConfig site = compiledSite.Site;
            CompiledSiteFeatures compiledFeatures = compiledSite.Features;
            SiteFeaturesConfig effective = compiledFeatures.EffectiveConfig;

            telemetry = GetSiteTelemetry(site.Id);
            Interlocked.Increment(ref telemetry.TotalRequests);
            frebScope.RecordStage("BeginRequest");

            // RFC 7838 / RFC 9114: Advertise HTTP/3 QUIC endpoint when enabled
            if (routeTable.ServerConfig.EnableAltSvcHeader &&
                routeTable.ServerConfig.Transport.EnableHttp3 &&
                context.Request.IsHttps &&
                !string.Equals(context.Request.Protocol, "HTTP/3", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers["Alt-Svc"] = $"h3=\":{localPort}\"; ma={Math.Max(60, routeTable.ServerConfig.Transport.AltSvcMaxAgeSeconds)}";
            }

            // RFC 8555: Built-in ACME HTTP-01 challenge responder (runs ahead of HTTP->HTTPS & IP/Auth gates)
            const string acmePrefix = "/.well-known/acme-challenge/";
            if (_acmeChallengeResolver is not null &&
                effective.TlsPolicy.EnableAcmeHttp01PassThrough &&
                requestPath.StartsWith(acmePrefix, StringComparison.Ordinal))
            {
                string token = requestPath[acmePrefix.Length..];
                string? keyAuth = _acmeChallengeResolver(token);
                if (!string.IsNullOrEmpty(keyAuth))
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "text/plain; charset=utf-8";
                    await context.Response.WriteAsync(keyAuth, context.RequestAborted).ConfigureAwait(false);
                    frebScope.RecordStage("AcmeHttp01Challenge", "Responded");
                    return;
                }
            }

            // Automatic HTTP -> HTTPS canonical redirect (301/308) when configured on site TLS policy
            if (!context.Request.IsHttps && effective.TlsPolicy.RedirectHttpToHttps)
            {
                int httpsPort = 443;
                foreach (SiteBindingConfig b in site.Bindings)
                {
                    if (string.Equals(b.Protocol, "https", StringComparison.OrdinalIgnoreCase))
                    {
                        httpsPort = b.Port;
                        break;
                    }
                }

                string hostOnly = HostString.FromUriComponent(rawHost).Host;
                if (string.IsNullOrEmpty(hostOnly))
                {
                    hostOnly = "localhost";
                }
                string portSuffix = httpsPort == 443 ? "" : $":{httpsPort}";
                string location = $"https://{hostOnly}{portSuffix}{requestPath}{context.Request.QueryString}";
                context.Response.StatusCode = effective.TlsPolicy.RedirectHttpToHttpsStatusCode is StatusCodes.Status301MovedPermanently or StatusCodes.Status308PermanentRedirect
                    ? effective.TlsPolicy.RedirectHttpToHttpsStatusCode
                    : StatusCodes.Status308PermanentRedirect;
                context.Response.Headers["Location"] = location;
                frebScope.RecordStage("HttpsRedirect", $"{context.Response.StatusCode} -> {location}");
                return;
            }

            // Mutual TLS (mTLS) client certificate verification & trusted identity header injection
            if (effective.TlsPolicy.Mtls.ClientCertificateMode != MtlsClientCertificateMode.None)
            {
                context.Request.Headers.Remove("X-Kite-Client-Cert-Thumbprint");
                context.Request.Headers.Remove("X-Kite-Client-Cert-Subject");
                var clientCert = context.Connection.ClientCertificate;
                if (clientCert is null)
                {
                    if (effective.TlsPolicy.Mtls.ClientCertificateMode == MtlsClientCertificateMode.RequireCertificate)
                    {
                        frebScope.RecordStage("MtlsVerification", "403 Client Cert Required");
                        await WriteErrorOrCustomPageAsync(
                            context,
                            site,
                            compiledFeatures,
                            StatusCodes.Status403Forbidden,
                            "Client Certificate Required",
                            "A valid mutual-TLS client certificate is required.").ConfigureAwait(false);
                        return;
                    }
                }
                else
                {
                    if (compiledFeatures.TrustedMtlsThumbprints.Count > 0 &&
                        !compiledFeatures.TrustedMtlsThumbprints.Contains(clientCert.Thumbprint))
                    {
                        frebScope.RecordStage("MtlsVerification", "403 Untrusted Client Cert");
                        await WriteErrorOrCustomPageAsync(
                            context,
                            site,
                            compiledFeatures,
                            StatusCodes.Status403Forbidden,
                            "Untrusted Client Certificate",
                            "Client certificate thumbprint is not authorized.").ConfigureAwait(false);
                        return;
                    }

                    context.Request.Headers["X-Kite-Client-Cert-Thumbprint"] = clientCert.Thumbprint;
                    context.Request.Headers["X-Kite-Client-Cert-Subject"] = clientCert.Subject;
                }
            }

            // 3. Apply Site Security, CORS, Custom Headers & Removal List
            CustomHeadersAndCorsModule.ApplyResponseHeaders(context, compiledFeatures);
            if (CustomHeadersAndCorsModule.TryApplyCorsAndHandlePreflight(context, compiledFeatures))
            {
                frebScope.RecordStage("CorsPreflight", "Completed");
                return;
            }

            // 4. IP Restrictions & IIS-style Request Filtering Gate
            if (!RequestFilteringAndIpGateModule.Evaluate(
                    context,
                    compiledFeatures,
                    out int gateStatus,
                    out string subStatus,
                    out string gateTitle,
                    out string gateDetail))
            {
                frebScope.SubStatus = subStatus;
                if (!string.IsNullOrEmpty(subStatus))
                {
                    context.Response.Headers["X-Kite-SubStatus"] = subStatus;
                }
                frebScope.RecordStage("RequestFilteringAndIpGate", $"Rejected ({subStatus})");
                await WriteErrorOrCustomPageAsync(context, site, compiledFeatures, gateStatus, gateTitle, gateDetail).ConfigureAwait(false);
                return;
            }

            (bool bodyAllowed, int bodyStatus, string bodySub, string bodyTitle, string bodyDetail) =
                await RequestFilteringAndIpGateModule.EvaluateBodyAsync(context, compiledFeatures, context.RequestAborted).ConfigureAwait(false);
            if (!bodyAllowed)
            {
                frebScope.SubStatus = bodySub;
                if (!string.IsNullOrEmpty(bodySub))
                {
                    context.Response.Headers["X-Kite-SubStatus"] = bodySub;
                }
                frebScope.RecordStage("RequestFilteringAndIpGate", $"Body Rejected ({bodySub})");
                await WriteErrorOrCustomPageAsync(context, site, compiledFeatures, bodyStatus, bodyTitle, bodyDetail).ConfigureAwait(false);
                return;
            }
            frebScope.RecordStage("RequestFilteringAndIpGate");

            // 4b. Edge Rate Limiting (429 Too Many Requests + Retry-After, local or distributed Redis)
            (bool rateAllowed, int retryAfterSeconds) = await EdgeRateLimiterModule
                .TryAcquireAsync(
                    context,
                    compiledFeatures,
                    site.Id,
                    _distributedStateAccessor?.Invoke(),
                    context.RequestAborted)
                .ConfigureAwait(false);
            if (!rateAllowed)
            {
                frebScope.SubStatus = "429.1";
                frebScope.RecordStage("EdgeRateLimiter", $"Throttled (Retry-After: {retryAfterSeconds}s)");
                await WriteErrorOrCustomPageAsync(
                    context,
                    site,
                    compiledFeatures,
                    StatusCodes.Status429TooManyRequests,
                    "Too Many Requests",
                    $"Rate limit exceeded. Retry after {retryAfterSeconds} second(s).").ConfigureAwait(false);
                return;
            }
            frebScope.RecordStage("EdgeRateLimiter");

            // 4c. Hot-reloadable C# Edge Middleware Plugins
            if (_pluginHost is not null && _pluginHost.LoadedCount > 0)
            {
                bool pluginContinued = await _pluginHost.ExecuteAsync(context, context.RequestAborted).ConfigureAwait(false);
                if (!pluginContinued)
                {
                    frebScope.RecordStage("EdgePlugins", "Short-circuited");
                    return;
                }
                frebScope.RecordStage("EdgePlugins");
            }

            // 5. URL Rewrite & Canonical Redirects
            UrlRewriteOutcome rewriteOutcome = await UrlRewriteModule.ExecuteAsync(
                context,
                compiledFeatures,
                rawHost,
                context.RequestAborted).ConfigureAwait(false);

            if (rewriteOutcome == UrlRewriteOutcome.Responded)
            {
                frebScope.RecordStage("UrlRewriteAndRouting", "Redirected");
                return;
            }

            if (rewriteOutcome == UrlRewriteOutcome.RewrittenInPlace)
            {
                requestPath = context.Request.Path.Value ?? "/";
            }

            // 6. Resolve Nested Application, Virtual Directory & Declarative Route Rules
            compiledSite.PathTree.MatchInto(requestPath.AsSpan(), routeCtx);
            SiteApplicationConfig app = routeCtx.Application!;
            ReadOnlySpan<char> subPathSpan = routeCtx.GetRemainingSubPath(requestPath.AsSpan());
            string subPathString = subPathSpan.ToString();

            RouteMatchResult routeMatch = RouteRuleMatcher.Match(effective.Routes, context.Request);
            SiteRouteRuleConfig? matchedRoute = routeMatch.Rule;
            if (matchedRoute is not null)
            {
                subPathString = routeMatch.TransformedPath;
                RouteTransformConfig transform = matchedRoute.Transform;
                foreach (string removeReq in transform.RemoveRequestHeaders)
                {
                    context.Request.Headers.Remove(removeReq);
                }
                foreach (KeyValuePair<string, string> setReq in transform.SetRequestHeaders)
                {
                    context.Request.Headers[setReq.Key] = setReq.Value;
                }
                if (transform.SetResponseHeaders.Count > 0 || transform.RemoveResponseHeaders.Count > 0)
                {
                    context.Response.OnStarting(() =>
                    {
                        foreach (string remResp in transform.RemoveResponseHeaders)
                        {
                            context.Response.Headers.Remove(remResp);
                        }
                        foreach (KeyValuePair<string, string> setResp in transform.SetResponseHeaders)
                        {
                            context.Response.Headers[setResp.Key] = setResp.Value;
                        }
                        return Task.CompletedTask;
                    });
                }
            }
            frebScope.RecordStage("UrlRewriteAndRouting");

            // 7. Edge Authentication (Anonymous / JWT / API Key / Basic) + ForwardAuth (ext_authz)
            if (!EdgeAuthenticationModule.Authenticate(
                    context,
                    compiledFeatures,
                    _timeProvider,
                    out _,
                    out string? wwwAuth,
                    out string? authFailure))
            {
                if (!string.IsNullOrEmpty(wwwAuth))
                {
                    context.Response.Headers["WWW-Authenticate"] = wwwAuth;
                }
                frebScope.RecordStage("AuthenticateRequest", "401 Unauthorized");
                await WriteErrorOrCustomPageAsync(
                    context,
                    site,
                    compiledFeatures,
                    StatusCodes.Status401Unauthorized,
                    "Unauthorized",
                    authFailure ?? "Authentication required.").ConfigureAwait(false);
                return;
            }

            if (effective.ForwardAuth.Enabled)
            {
                (bool fwdAllowed, int fwdStatus, string? fwdWwwAuth, string? fwdErr) =
                    await EdgeAuthenticationModule.EvaluateForwardAuthAsync(
                        context,
                        effective.ForwardAuth,
                        httpClientOverride: null,
                        context.RequestAborted).ConfigureAwait(false);

                if (!fwdAllowed)
                {
                    if (!string.IsNullOrEmpty(fwdWwwAuth))
                    {
                        context.Response.Headers["WWW-Authenticate"] = fwdWwwAuth;
                    }
                    frebScope.RecordStage("ForwardAuth", $"{fwdStatus} Rejected");
                    await WriteErrorOrCustomPageAsync(
                        context,
                        site,
                        compiledFeatures,
                        fwdStatus,
                        fwdStatus == StatusCodes.Status403Forbidden ? "Forbidden" : "Unauthorized",
                        fwdErr ?? "ForwardAuth rejected request.").ConfigureAwait(false);
                    return;
                }
                frebScope.RecordStage("ForwardAuth");
            }
            frebScope.RecordStage("AuthenticateRequest");

            // 7b. Fault Injection & Chaos Testing (after auth gates, before upstream dispatch)
            if (matchedRoute?.FaultInjection is { Enabled: true } fi)
            {
                if (fi.DelayPercent > 0 && fi.DelayMilliseconds > 0 && Random.Shared.Next(100) < Math.Clamp(fi.DelayPercent, 0, 100))
                {
                    await Task.Delay(fi.DelayMilliseconds, context.RequestAborted).ConfigureAwait(false);
                }

                if (fi.AbortPercent > 0 && Random.Shared.Next(100) < Math.Clamp(fi.AbortPercent, 0, 100))
                {
                    int abortStatus = Math.Clamp(fi.AbortStatusCode, 400, 599);
                    foreach (string remResp in matchedRoute.Transform.RemoveResponseHeaders)
                    {
                        context.Response.Headers.Remove(remResp);
                    }
                    foreach (KeyValuePair<string, string> setResp in matchedRoute.Transform.SetResponseHeaders)
                    {
                        context.Response.Headers[setResp.Key] = setResp.Value;
                    }
                    frebScope.RecordStage("FaultInjection", $"Aborted ({abortStatus})");
                    await WriteErrorOrCustomPageAsync(
                        context,
                        site,
                        compiledFeatures,
                        abortStatus,
                        "Fault Injected",
                        fi.AbortMessage ?? $"Injected chaos abort HTTP {abortStatus}.").ConfigureAwait(false);
                    return;
                }
            }

            // 7c. Non-blocking Shadow / Dark-Launch Traffic Mirroring
            if (matchedRoute is not null &&
                !string.IsNullOrWhiteSpace(matchedRoute.ShadowClusterId) &&
                matchedRoute.ShadowPercent > 0 &&
                _upstreamManager is not null &&
                Random.Shared.Next(100) < Math.Clamp(matchedRoute.ShadowPercent, 0, 100))
            {
                await _upstreamManager
                    .ShadowRequestAsync(context, matchedRoute.ShadowClusterId, subPathString, context.RequestAborted)
                    .ConfigureAwait(false);
            }

            // 8. Handle OPTIONS * or non-CORS OPTIONS (RFC 9110 Section 9.3.7)
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.Headers["Allow"] = "GET, HEAD, POST, PUT, DELETE, OPTIONS, PATCH";
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            // 9. Handle WebSocket Requests (RFC 6455 / RFC 8441)
            if (context.WebSockets.IsWebSocketRequest)
            {
                await HandleWebSocketRequestAsync(context, telemetry).ConfigureAwait(false);
                frebScope.RecordStage("ExecuteRequestHandler", "WebSocket");
                return;
            }

            // 10. Output Cache generation & unsafe method invalidation (RFC 9111 Section 4.4)
            _outputCache.EnsureGeneration(site.Id, effective.Caching.FlushGeneration);

            if (IsUnsafeMethod(context.Request.Method) && effective.Caching.Enabled)
            {
                _outputCache.InvalidateByUri(site.Id, requestPath, context.Request.QueryString.Value ?? "");
            }

            // 11. Negotiate Content-Encoding (RFC 9110 Section 12.5.3: zstd, br, gzip, deflate)
            string negotiatedEncoding = KiteCompressionModule.NegotiateEncoding(
                context.Request.Headers,
                effective.Compression);

            if (effective.Compression.Enabled)
            {
                AppendVaryAcceptEncoding(context.Response.Headers);
            }

            // 12. Check RFC 9111 Output Cache
            if (effective.Caching.Enabled &&
                KiteOutputCacheModule.RequestAllowsCacheLookup(context.Request))
            {
                Interlocked.Increment(ref telemetry.CacheLookups);
                if (_outputCache.TryGetFreshEntry(site.Id, context.Request, negotiatedEncoding, out CachedResponseEntry? cached, out int ageSeconds) &&
                    cached is not null)
                {
                    Interlocked.Increment(ref telemetry.CacheHits);
                    frebScope.RecordStage("ResolveRequestCache", "Cache HIT");
                    await ServeCachedEntryAsync(context, cached, ageSeconds, telemetry).ConfigureAwait(false);
                    return;
                }
            }
            frebScope.RecordStage("ResolveRequestCache");

            // 13. Dispatch either Static / Virtual Directory or Application Pool / Route Cluster Workload
            bool routeOverridesDynamic = matchedRoute is not null &&
                                         (!string.IsNullOrWhiteSpace(matchedRoute.TargetClusterId) ||
                                          !string.IsNullOrWhiteSpace(matchedRoute.TargetPoolId));
            bool shouldServeStatic = !routeOverridesDynamic &&
                                     (routeCtx.VirtualDirectory is not null ||
                                      app.HostingMode == WorkloadHostingMode.StaticOnly);

            if (shouldServeStatic)
            {
                string physicalRoot = routeCtx.VirtualDirectory?.PhysicalPath ?? app.PhysicalPath;
                await ServeStaticOrDirectoryAsync(
                    context,
                    site,
                    compiledFeatures,
                    physicalRoot,
                    subPathString,
                    negotiatedEncoding,
                    telemetry,
                    app.ServePrecompressedSidecars).ConfigureAwait(false);
            }
            else
            {
                await DispatchDynamicWorkloadWithCacheAndCompressionAsync(
                    context,
                    site,
                    compiledFeatures,
                    app,
                    subPathString,
                    negotiatedEncoding,
                    telemetry,
                    matchedRoute).ConfigureAwait(false);
            }

            frebScope.RecordStage("ExecuteRequestHandler");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            frebScope.CaptureException(ex);
            frebScope.RecordStage("ExecuteRequestHandler", "CRASH / Unhandled Exception");
            if (!context.Response.HasStarted)
            {
                try
                {
                    await WriteProblemDetailsAsync(
                        context,
                        StatusCodes.Status500InternalServerError,
                        "Internal Server Error",
                        ex.Message).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
        finally
        {
            frebScope.RecordStage("SendResponse");
            double elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            _globalLatency.Record(elapsedMs);

            int finalStatus = context.Response.StatusCode;
            RecordStatusClass(finalStatus, telemetry);
            telemetry?.Latency.Record(elapsedMs);

            if (compiledSite is not null)
            {
                AccessLogFormat logFormat = routeTable.ServerConfig.Observability.AccessLogFormat;
                bool emitW3C = compiledSite.Features.EffectiveConfig.W3CLoggingEnabled &&
                               (logFormat == AccessLogFormat.W3C || logFormat == AccessLogFormat.Both);
                bool emitJson = compiledSite.Features.EffectiveConfig.W3CLoggingEnabled &&
                                (logFormat == AccessLogFormat.Json || logFormat == AccessLogFormat.Both);

                DateTimeOffset logNow = _timeProvider.GetUtcNow();
                ReadOnlySpan<char> clientIpSpan = (context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1").AsSpan();
                ReadOnlySpan<char> querySpan = (context.Request.QueryString.Value ?? "").AsSpan();
                long bytesSent = context.Response.ContentLength ?? 0;

                if (emitW3C && _w3cLogSink is not null)
                {
                    string line = W3CAccessLogFormatter.FormatLinePooled(
                        logNow,
                        clientIpSpan,
                        context.Request.Method.AsSpan(),
                        requestPath.AsSpan(),
                        querySpan,
                        localPort,
                        rawHost.AsSpan(),
                        finalStatus,
                        bytesSent,
                        (long)elapsedMs);
                    _w3cLogSink(compiledSite.Site.Id, line);
                }

                if (emitJson && _jsonLogSink is not null)
                {
                    string traceId = activity?.TraceId.ToHexString() ?? string.Empty;
                    string jsonLine = StructuredJsonLogFormatter.FormatLinePooled(
                        logNow,
                        compiledSite.Site.Id,
                        clientIpSpan,
                        context.Request.Method.AsSpan(),
                        requestPath.AsSpan(),
                        querySpan,
                        localPort,
                        rawHost.AsSpan(),
                        context.Request.Protocol.AsSpan(),
                        finalStatus,
                        bytesSent,
                        (long)elapsedMs,
                        traceId.AsSpan());
                    _jsonLogSink(compiledSite.Site.Id, jsonLine);
                }

                if (activity is not null)
                {
                    activity.SetTag("http.response.status_code", finalStatus);
                    if (finalStatus >= 500)
                    {
                        activity.SetStatus(ActivityStatusCode.Error);
                    }
                }

                // FREB Diagnostic Capture
                if (_frebSink is not null && frebScope.ShouldEmitTrace(compiledSite.Features, finalStatus, elapsedMs))
                {
                    FrebTraceRecord trace = frebScope.BuildRecord(
                        context,
                        compiledSite.Site.Id,
                        rawHost,
                        _timeProvider.GetUtcNow(),
                        elapsedMs,
                        compiledSite.Features);
                    _frebSink(trace);
                }
            }

            FrebScopePool.Return(frebScope);
            KiteObjectPools.RouteContextPool.Return(routeCtx);
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    private void RecordStatusClass(int statusCode, SiteRuntimeTelemetry? telemetry)
    {
        if (statusCode is >= 200 and < 300)
        {
            Interlocked.Increment(ref _interval2xx);
            if (telemetry is not null) Interlocked.Increment(ref telemetry.Status2xxCount);
        }
        else if (statusCode is >= 300 and < 400)
        {
            Interlocked.Increment(ref _interval3xx);
            if (telemetry is not null) Interlocked.Increment(ref telemetry.Status3xxCount);
        }
        else if (statusCode is >= 400 and < 500)
        {
            Interlocked.Increment(ref _interval4xx);
            if (telemetry is not null) Interlocked.Increment(ref telemetry.Status4xxCount);
        }
        else if (statusCode >= 500)
        {
            Interlocked.Increment(ref _interval5xx);
            if (telemetry is not null) Interlocked.Increment(ref telemetry.Status5xxCount);
        }
    }

    private static bool ValidateHostHeader(HttpRequest request, out string rawHost)
    {
        rawHost = string.Empty;
        if (!request.Headers.TryGetValue("Host", out StringValues hostValues))
        {
            // HTTP/2 may populate request.Host via :authority pseudo-header
            if (request.Host.HasValue)
            {
                rawHost = request.Host.Value!;
                return true;
            }
            return false;
        }

        // RFC 9112 Section 5.4: MUST respond 400 to any request with more than one Host header field
        if (hostValues.Count != 1)
        {
            return false;
        }

        string single = hostValues[0] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(single) || single.Contains(' ') || single.Contains('\t') || single.Contains('/'))
        {
            return false;
        }

        rawHost = single;
        return true;
    }

    private static async ValueTask WriteErrorOrCustomPageAsync(
        HttpContext context,
        SiteConfig site,
        CompiledSiteFeatures compiled,
        int statusCode,
        string title,
        string detail)
    {
        RedirectAndErrorsConfig re = compiled.EffectiveConfig.RedirectsAndErrors;
        if (re.ErrorMode == CustomErrorMode.CustomPages &&
            re.CustomErrorPages.TryGetValue(statusCode, out string? errorPageRel) &&
            !string.IsNullOrWhiteSpace(errorPageRel) &&
            !string.IsNullOrWhiteSpace(site.RootApplication.PhysicalPath))
        {
            string fullRoot = Path.GetFullPath(site.RootApplication.PhysicalPath);
            string rel = errorPageRel.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(fullRoot, rel));
            if (candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
            {
                byte[] htmlBytes = await File.ReadAllBytesAsync(candidate, context.RequestAborted).ConfigureAwait(false);
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength = htmlBytes.Length;
                if (!HttpMethods.IsHead(context.Request.Method))
                {
                    await context.Response.Body.WriteAsync(htmlBytes, context.RequestAborted).ConfigureAwait(false);
                }
                return;
            }
        }

        await WriteProblemDetailsAsync(context, statusCode, title, detail).ConfigureAwait(false);
    }

    private static async Task HandleWebSocketRequestAsync(HttpContext context, SiteRuntimeTelemetry telemetry)
    {
        Interlocked.Increment(ref telemetry.ActiveWebSockets);
        try
        {
            using WebSocket ws = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                while (ws.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
                {
                    ValueWebSocketReceiveResult result = await ws.ReceiveAsync(
                        buffer.AsMemory(0, 4096),
                        context.RequestAborted).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(
                            ws.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            ws.CloseStatusDescription ?? "Closed by client",
                            context.RequestAborted).ConfigureAwait(false);
                        break;
                    }

                    await ws.SendAsync(
                        buffer.AsMemory(0, result.Count),
                        result.MessageType,
                        result.EndOfMessage,
                        context.RequestAborted).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            Interlocked.Decrement(ref telemetry.ActiveWebSockets);
        }
    }

    private static bool IsStreamingRequest(HttpContext context, SiteApplicationConfig app)
    {
        if (app.EnableStreamingPassThrough)
        {
            return true;
        }

        if (context.Request.ContentType is { } reqCt &&
            reqCt.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (context.Request.Headers.TryGetValue("Accept", out StringValues accept) &&
            accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private async Task ServeCachedEntryAsync(
        HttpContext context,
        CachedResponseEntry cached,
        int ageSeconds,
        SiteRuntimeTelemetry telemetry)
    {
        PreconditionOutcome precond = ConditionalAndRangeEvaluator.EvaluatePreconditions(
            context.Request.Headers,
            context.Request.Method,
            cached.ETag,
            cached.StoredAtUtc);

        for (int i = 0; i < cached.Headers.Count; i++)
        {
            KeyValuePair<string, string> kvp = cached.Headers[i];
            context.Response.Headers[kvp.Key] = kvp.Value;
        }

        context.Response.Headers["ETag"] = cached.ETag;
        context.Response.Headers["Age"] = ageSeconds.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-Kite-Cache"] = "HIT";

        if (precond == PreconditionOutcome.NotModified304)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            context.Response.ContentLength = null;
            return;
        }

        if (precond == PreconditionOutcome.PreconditionFailed412)
        {
            context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
            return;
        }

        context.Response.StatusCode = cached.StatusCode;
        context.Response.ContentLength = cached.Payload.Length;

        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await cached.Payload.WriteToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            AddBytesSent(telemetry, cached.Payload.Length);
        }
    }

    private async Task ServeStaticOrDirectoryAsync(
        HttpContext context,
        SiteConfig site,
        CompiledSiteFeatures compiledFeatures,
        string physicalRoot,
        string subPath,
        string negotiatedEncoding,
        SiteRuntimeTelemetry telemetry,
        bool servePrecompressedSidecars = true)
    {
        SiteFeaturesConfig effective = compiledFeatures.EffectiveConfig;

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.Headers["Allow"] = "GET, HEAD, OPTIONS";
            await WriteErrorOrCustomPageAsync(
                context,
                site,
                compiledFeatures,
                StatusCodes.Status405MethodNotAllowed,
                "Method Not Allowed",
                $"Method '{context.Request.Method}' is not allowed for static resources.").ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(physicalRoot) || !Directory.Exists(physicalRoot))
        {
            await WriteErrorOrCustomPageAsync(
                context,
                site,
                compiledFeatures,
                StatusCodes.Status404NotFound,
                "Not Found",
                "The configured physical root directory does not exist.").ConfigureAwait(false);
            return;
        }

        string relative = subPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string fullRoot = Path.GetFullPath(physicalRoot);
        string candidatePath = Path.GetFullPath(Path.Combine(fullRoot, relative));

        // Prevent directory traversal outside physicalRoot
        if (!candidatePath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorOrCustomPageAsync(
                context,
                site,
                compiledFeatures,
                StatusCodes.Status403Forbidden,
                "Forbidden",
                "Path traversal outside site physical root is forbidden.").ConfigureAwait(false);
            return;
        }

        // Check Default Documents or Directory Browsing when path is a directory
        if (Directory.Exists(candidatePath))
        {
            string? resolvedDefaultDoc = null;
            foreach (string defaultDoc in effective.DefaultDocuments)
            {
                string docCandidate = Path.Combine(candidatePath, defaultDoc);
                if (File.Exists(docCandidate))
                {
                    resolvedDefaultDoc = docCandidate;
                    break;
                }
            }

            if (resolvedDefaultDoc is not null)
            {
                candidatePath = resolvedDefaultDoc;
            }
            else if (effective.DirectoryBrowsing)
            {
                await ServeDirectoryListingHtmlAsync(context, candidatePath, subPath, telemetry).ConfigureAwait(false);
                return;
            }
            else
            {
                await WriteErrorOrCustomPageAsync(
                    context,
                    site,
                    compiledFeatures,
                    StatusCodes.Status403Forbidden,
                    "Directory Listing Denied",
                    "Directory browsing is disabled for this site.").ConfigureAwait(false);
                return;
            }
        }

        if (!File.Exists(candidatePath))
        {
            if (TryResolveSpaFallback(effective.SpaFallback, fullRoot, subPath, out string? spaPath))
            {
                candidatePath = spaPath!;
            }
            else
            {
                await WriteErrorOrCustomPageAsync(
                    context,
                    site,
                    compiledFeatures,
                    StatusCodes.Status404NotFound,
                    "File Not Found",
                    $"The requested resource '{subPath}' was not found.").ConfigureAwait(false);
                return;
            }
        }

        var fileInfo = new FileInfo(candidatePath);
        DateTimeOffset lastModifiedUtc = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);
        long fileLength = fileInfo.Length;
        string etag = ConditionalAndRangeEvaluator.GenerateStrongETag(lastModifiedUtc, fileLength);

        ReadOnlySpan<char> extSpan = Path.GetExtension(candidatePath.AsSpan());
        string? contentType;
        if (!extSpan.IsEmpty && compiledFeatures.MimeSpanLookup.TryGetValue(extSpan, out string? customMime))
        {
            contentType = customMime;
        }
        else if (!MimeProvider.TryGetContentType(candidatePath, out contentType))
        {
            contentType = effective.MimeMappings.FallbackContentType;
        }

        context.Response.Headers["Accept-Ranges"] = "bytes";
        context.Response.Headers["ETag"] = etag;
        context.Response.Headers["Last-Modified"] = lastModifiedUtc.ToString("R", CultureInfo.InvariantCulture);

        if (effective.Caching.Enabled && effective.Caching.DefaultMaxAgeSeconds > 0)
        {
            context.Response.Headers["Cache-Control"] = $"public, max-age={effective.Caching.DefaultMaxAgeSeconds}";
        }

        // Evaluate Conditional Preconditions (RFC 9110 Section 13)
        PreconditionOutcome precond = ConditionalAndRangeEvaluator.EvaluatePreconditions(
            context.Request.Headers,
            context.Request.Method,
            etag,
            lastModifiedUtc);

        if (precond == PreconditionOutcome.NotModified304)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        if (precond == PreconditionOutcome.PreconditionFailed412)
        {
            await WriteProblemDetailsAsync(
                context,
                StatusCodes.Status412PreconditionFailed,
                "Precondition Failed",
                "One or more precondition headers evaluated to false (RFC 9110 Section 13).").ConfigureAwait(false);
            return;
        }

        // Evaluate Byte-Range Requests (RFC 9110 Section 14)
        RangeParseOutcome rangeOutcome = ConditionalAndRangeEvaluator.EvaluateRange(
            context.Request.Headers,
            context.Request.Method,
            fileLength,
            etag,
            lastModifiedUtc,
            out ByteRangeSpec rangeSpec);

        if (rangeOutcome == RangeParseOutcome.NotSatisfiable416)
        {
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            context.Response.Headers["Content-Range"] = $"bytes */{fileLength}";
            return;
        }

        if (rangeOutcome == RangeParseOutcome.Satisfiable)
        {
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.ContentType = contentType;
            context.Response.ContentLength = rangeSpec.Length;
            context.Response.Headers["Content-Range"] = $"bytes {rangeSpec.Start}-{rangeSpec.End}/{fileLength}";

            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await StreamFileSliceAsync(
                    candidatePath,
                    rangeSpec.Start,
                    rangeSpec.Length,
                    context.Response.Body,
                    context.RequestAborted).ConfigureAwait(false);
                AddBytesSent(telemetry, rangeSpec.Length);
            }
            return;
        }

        // Check static pre-compressed companion file (.zst, .br, .gz) if compression is negotiated
        if (servePrecompressedSidecars && negotiatedEncoding != "identity")
        {
            string? ext = KiteCompressionModule.GetPrecompressedExtension(negotiatedEncoding);
            if (ext is not null)
            {
                string precompressedPath = candidatePath + ext;
                if (File.Exists(precompressedPath))
                {
                    var preInfo = new FileInfo(precompressedPath);
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = contentType;
                    context.Response.Headers["Content-Encoding"] = negotiatedEncoding;
                    AppendVaryAcceptEncoding(context.Response.Headers);
                    context.Response.ContentLength = preInfo.Length;

                    if (!HttpMethods.IsHead(context.Request.Method))
                    {
                        await StreamFileSliceAsync(
                            precompressedPath,
                            0,
                            preInfo.Length,
                            context.Response.Body,
                            context.RequestAborted).ConfigureAwait(false);
                        AddBytesSent(telemetry, preInfo.Length);
                    }
                    return;
                }
            }
        }

        // Dynamic compression or direct streaming
        bool shouldCompressDynamically =
            negotiatedEncoding != "identity" &&
            effective.Compression.Enabled &&
            fileLength >= effective.Compression.MinSizeBytes &&
            KiteCompressionModule.IsCompressibleMimeType(contentType.AsSpan());

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentType;

        await using var bufferStream = new SegmentedBufferStream();
        if (shouldCompressDynamically)
        {
            context.Response.Headers["Content-Encoding"] = negotiatedEncoding;
            await using (Stream compressor = KiteCompressionModule.CreateCompressionStream(bufferStream, negotiatedEncoding, effective.Compression))
            {
                await StreamFileSliceAsync(candidatePath, 0, fileLength, compressor, context.RequestAborted).ConfigureAwait(false);
            }
        }
        else
        {
            await StreamFileSliceAsync(candidatePath, 0, fileLength, bufferStream, context.RequestAborted).ConfigureAwait(false);
        }

        context.Response.ContentLength = bufferStream.Length;

        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await bufferStream.CopySegmentsToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            AddBytesSent(telemetry, bufferStream.Length);
        }

        // Store in RFC 9111 Output Cache if eligible
        if (effective.Caching.Enabled &&
            bufferStream.Length <= effective.Caching.MaxCacheBodyBytes &&
            KiteOutputCacheModule.RequestAllowsCacheLookup(context.Request))
        {
            PooledBytePayload detached = bufferStream.DetachPayload();
            _outputCache.StoreResponse(
                site.Id,
                context.Request,
                negotiatedEncoding,
                StatusCodes.Status200OK,
                context.Response.Headers,
                detached,
                effective.Caching.DefaultMaxAgeSeconds,
                effective.Caching.MaxCacheSizeMegabytes);
        }
    }

    private async Task DispatchDynamicWorkloadWithCacheAndCompressionAsync(
        HttpContext context,
        SiteConfig site,
        CompiledSiteFeatures compiledFeatures,
        SiteApplicationConfig app,
        string subPath,
        string negotiatedEncoding,
        SiteRuntimeTelemetry telemetry,
        SiteRouteRuleConfig? matchedRoute = null)
    {
        SiteFeaturesConfig effective = compiledFeatures.EffectiveConfig;

        string? effectiveClusterId = !string.IsNullOrWhiteSpace(matchedRoute?.TargetClusterId)
            ? matchedRoute.TargetClusterId
            : app.UpstreamClusterId;
        string effectivePoolId = !string.IsNullOrWhiteSpace(matchedRoute?.TargetPoolId)
            ? matchedRoute.TargetPoolId
            : app.PoolId;
        bool useUpstreamCluster = string.IsNullOrWhiteSpace(matchedRoute?.TargetPoolId) &&
                                  !string.IsNullOrWhiteSpace(effectiveClusterId);
        ApplicationPoolSupervisor? pool = null;

        if (useUpstreamCluster)
        {
            if (_upstreamManager is null || !_upstreamManager.HasCluster(effectiveClusterId))
            {
                await WriteErrorOrCustomPageAsync(
                    context,
                    site,
                    compiledFeatures,
                    StatusCodes.Status503ServiceUnavailable,
                    "Upstream Cluster Not Found",
                    $"Upstream cluster '{effectiveClusterId}' is not registered.").ConfigureAwait(false);
                return;
            }
        }
        else
        {
            pool = _poolAccessor(effectivePoolId);
            if (pool is null)
            {
                await WriteErrorOrCustomPageAsync(
                    context,
                    site,
                    compiledFeatures,
                    StatusCodes.Status503ServiceUnavailable,
                    "Application Pool Not Found",
                    $"Application pool '{effectivePoolId}' is not registered.").ConfigureAwait(false);
                return;
            }
        }

        // Native gRPC-Web bridge (application/grpc-web -> HTTP/2 application/grpc)
        if (app.EnableGrpcWebBridge &&
            GrpcWebBridgeMiddleware.IsGrpcWebRequest(context.Request))
        {
            string? grpcTargetBase = useUpstreamCluster
                ? _upstreamManager!.ResolveAddressForContext(effectiveClusterId, context)
                : app.UpstreamUrl;
            if (!string.IsNullOrWhiteSpace(grpcTargetBase) && _upstreamManager is not null)
            {
                await GrpcWebBridgeMiddleware.ForwardGrpcWebAsync(
                    context,
                    _upstreamManager.ProxyInvoker,
                    grpcTargetBase,
                    subPath.AsMemory(),
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }
        }

        // RFC 8297: Emit 103 Early Hints (and fallback Link preload headers) prior to awaiting dynamic workload
        if (effective.EarlyHintsLinks.Count > 0)
        {
            IKiteEarlyHintsFeature? earlyHintsFeature = context.Features.Get<IKiteEarlyHintsFeature>();
            if (earlyHintsFeature is not null)
            {
                await earlyHintsFeature.SendEarlyHintsAsync(effective.EarlyHintsLinks, context.RequestAborted).ConfigureAwait(false);
            }

            for (int i = 0; i < effective.EarlyHintsLinks.Count; i++)
            {
                context.Response.Headers.Append("Link", effective.EarlyHintsLinks[i]);
            }
        }

        // Streaming pass-through for SSE, gRPC, or explicitly streamed endpoints (avoids full-body buffering)
        if (IsStreamingRequest(context, app))
        {
            if (useUpstreamCluster)
            {
                await _upstreamManager!.ForwardToClusterAsync(
                    context,
                    app,
                    subPath.AsMemory(),
                    matchedRoute?.TargetClusterId,
                    matchedRoute?.CanaryClusterId,
                    matchedRoute?.CanaryWeightPercent,
                    context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                await pool!.ForwardHttpAsync(context, app, subPath.AsMemory(), context.RequestAborted).ConfigureAwait(false);
            }
            return;
        }

        Stream originalBody = context.Response.Body;
        await using var captureStream = new SegmentedBufferStream();
        context.Response.Body = captureStream;

        try
        {
            if (useUpstreamCluster)
            {
                await _upstreamManager!.ForwardToClusterAsync(
                    context,
                    app,
                    subPath.AsMemory(),
                    matchedRoute?.TargetClusterId,
                    matchedRoute?.CanaryClusterId,
                    matchedRoute?.CanaryWeightPercent,
                    context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                await pool!.ForwardHttpAsync(context, app, subPath.AsMemory(), context.RequestAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        bool canCompress =
            negotiatedEncoding != "identity" &&
            effective.Compression.Enabled &&
            !context.Response.Headers.ContainsKey("Content-Encoding") &&
            captureStream.Length >= effective.Compression.MinSizeBytes &&
            KiteCompressionModule.IsCompressibleMimeType((context.Response.ContentType ?? "").AsSpan());

        SegmentedBufferStream finalStream = captureStream;
        SegmentedBufferStream? compressedStream = null;

        try
        {
            if (canCompress)
            {
                compressedStream = new SegmentedBufferStream();
                context.Response.Headers["Content-Encoding"] = negotiatedEncoding;
                await using (Stream compressor = KiteCompressionModule.CreateCompressionStream(compressedStream, negotiatedEncoding, effective.Compression))
                {
                    await captureStream.CopySegmentsToAsync(compressor, context.RequestAborted).ConfigureAwait(false);
                }
                finalStream = compressedStream;
            }

            context.Response.ContentLength = finalStream.Length;

            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await finalStream.CopySegmentsToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
                AddBytesSent(telemetry, finalStream.Length);
            }

            if (effective.Caching.Enabled &&
                context.Response.StatusCode == StatusCodes.Status200OK &&
                finalStream.Length <= effective.Caching.MaxCacheBodyBytes &&
                KiteOutputCacheModule.RequestAllowsCacheLookup(context.Request))
            {
                PooledBytePayload payload = finalStream.DetachPayload();
                _outputCache.StoreResponse(
                    site.Id,
                    context.Request,
                    negotiatedEncoding,
                    context.Response.StatusCode,
                    context.Response.Headers,
                    payload,
                    effective.Caching.DefaultMaxAgeSeconds,
                    effective.Caching.MaxCacheSizeMegabytes);
            }
        }
        finally
        {
            if (compressedStream is not null)
            {
                await compressedStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ServeDirectoryListingHtmlAsync(
        HttpContext context,
        string directoryPath,
        string requestSubPath,
        SiteRuntimeTelemetry telemetry)
    {
        StringBuilder sb = KiteObjectPools.StringBuilderPool.Get();
        try
        {
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/><title>Index of ")
              .Append(System.Net.WebUtility.HtmlEncode(requestSubPath))
              .Append("</title></head><body><h1>Index of ")
              .Append(System.Net.WebUtility.HtmlEncode(requestSubPath))
              .Append("</h1><ul>");

            foreach (string entry in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                string name = Path.GetFileName(entry);
                sb.Append("<li><a href=\"")
                  .Append(Uri.EscapeDataString(name))
                  .Append("\">")
                  .Append(System.Net.WebUtility.HtmlEncode(name))
                  .Append("</a></li>");
            }

            sb.Append("</ul></body></html>");

            byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength = bytes.Length;

            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
                AddBytesSent(telemetry, bytes.Length);
            }
        }
        finally
        {
            KiteObjectPools.StringBuilderPool.Return(sb);
        }
    }

    private static async ValueTask StreamFileSliceAsync(
        string filePath,
        long startOffset,
        long byteCount,
        Stream destination,
        CancellationToken ct)
    {
        if (byteCount <= 0)
        {
            return;
        }

        await using var fs = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (startOffset > 0)
        {
            fs.Seek(startOffset, SeekOrigin.Begin);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            long remaining = byteCount;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int read = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Writes a standardized RFC 9457 Problem Details JSON response.
    /// </summary>
    public static async ValueTask WriteProblemDetailsAsync(
        HttpContext context,
        int statusCode,
        string title,
        string detail)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json; charset=utf-8";

        string instance = context.Request.Path.Value ?? "/";
        string payload = $$"""{"type":"https://rfc-editor.org/rfc/rfc9457#status-{{statusCode}}","title":"{{title}}","status":{{statusCode}},"detail":"{{detail}}","instance":"{{instance}}"}""";
        byte[] utf8 = Encoding.UTF8.GetBytes(payload);
        context.Response.ContentLength = utf8.Length;

        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(utf8, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static bool TryResolveSpaFallback(
        SpaFallbackConfig spa,
        string fullRoot,
        string subPath,
        out string? fallbackPath)
    {
        fallbackPath = null;
        if (!spa.Enabled || string.IsNullOrWhiteSpace(spa.FallbackDocument))
        {
            return false;
        }

        if (!Path.GetExtension(subPath.AsSpan()).IsEmpty)
        {
            return false;
        }

        for (int i = 0; i < spa.ExcludePrefixes.Count; i++)
        {
            string prefix = spa.ExcludePrefixes[i];
            if (!string.IsNullOrWhiteSpace(prefix) &&
                subPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        string rel = spa.FallbackDocument.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, rel));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
        {
            return false;
        }

        fallbackPath = candidate;
        return true;
    }

    private void AddBytesSent(SiteRuntimeTelemetry siteTelemetry, long bytes)
    {
        Interlocked.Add(ref _totalBytesSent, bytes);
        Interlocked.Add(ref siteTelemetry.TotalBytesOut, bytes);
    }

    private static void AppendVaryAcceptEncoding(IHeaderDictionary headers)
    {
        if (!headers.TryGetValue("Vary", out StringValues existing) || StringValues.IsNullOrEmpty(existing))
        {
            headers["Vary"] = "Accept-Encoding";
        }
        else if (!existing.ToString().Contains("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
        {
            headers["Vary"] = $"{existing}, Accept-Encoding";
        }
    }

    private static bool IsUnsafeMethod(string method) =>
        HttpMethods.IsPost(method) ||
        HttpMethods.IsPut(method) ||
        HttpMethods.IsDelete(method) ||
        HttpMethods.IsPatch(method);

    private static FileExtensionContentTypeProvider CreateMimeProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".wasm"] = "application/wasm";
        provider.Mappings[".json"] = "application/json; charset=utf-8";
        provider.Mappings[".html"] = "text/html; charset=utf-8";
        provider.Mappings[".css"] = "text/css; charset=utf-8";
        provider.Mappings[".js"] = "application/javascript; charset=utf-8";
        return provider;
    }

    public void Dispose()
    {
        _outputCache.Dispose();
        _fallbackMeshInvoker.Dispose();
    }
}
