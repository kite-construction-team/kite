using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Kite.Core.Models;
using Kite.Core.Routing;
using Kite.Core.Serialization;
using Kite.Core.Storage;
using Kite.Server.HttpModules;
using Kite.Server.Observability;
using Kite.Server.Pipeline;
using Kite.Server.Plugins;
using Kite.Server.Pools;
using Kite.Server.State;
using Kite.Server.Tls;
using Kite.Server.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Kite.Server.Hosting;

/// <summary>
/// Orchestrates the entire Kite Server control plane and data plane:
/// - Filesystem configuration hot-reloading (<see cref="FileSystemConfigMonitor"/>)
/// - Application Pool Supervisors (<see cref="ApplicationPoolSupervisor"/>)
/// - Lock-free <see cref="HostRouteTable"/> and SNI Certificate Selection
/// - Dynamic Kestrel TCP/TLS/HTTP1.1/HTTP2 multi-port listeners
/// - Atomic <c>state/*.status.json</c> telemetry publishing and W3C file logging
/// </summary>
public sealed class KiteServerEngine : IAsyncDisposable
{
    private readonly KiteHomeLayout _layout;
    private readonly FileSystemConfigMonitor _configMonitor;
    private readonly IWorkerProcessLauncher _workerLauncher;
    private readonly TimeProvider _timeProvider;
    private readonly CertificateProvider _certificateProvider = new();
    private readonly AcmeCertificateManager _acmeManager;
    private readonly UpstreamClusterManager _upstreamManager;
    private readonly L4StreamListenerManager _l4StreamManager = new();
    private readonly OtlpHttpJsonExporter _otlpExporter = new();
    private readonly EdgePluginHost _pluginHost = new();
    private readonly EdgeMeshCoordinator _meshCoordinator;
    private RedisRespClient? _redisState;
    private readonly ConcurrentDictionary<string, ApplicationPoolSupervisor> _pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _pendingW3CLogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _pendingJsonLogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<FrebTraceRecord> _pendingFrebTraces = new();
    private readonly Queue<TelemetrySampleBucket> _recentSamples = new(64);
    private readonly Lock _sampleLock = new();
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private readonly DateTimeOffset _startedAtUtc;

    private KiteHomeLock? _homeLock;
    private HostRouteTable _routeTable = new([]);
    private KiteRequestPipeline _pipeline;
    private WebApplication? _kestrelApp;
    private HashSet<string> _boundEndpointsSignature = new(StringComparer.OrdinalIgnoreCase);
    private ITimer? _maintenanceAndStateTimer;
    private bool _enableKestrelListeners;
    private bool _disposed;

    public KiteServerEngine(
        KiteHomeLayout layout,
        IWorkerProcessLauncher? workerLauncher = null,
        TimeProvider? timeProvider = null,
        bool enableKestrelListeners = true,
        MeshMode? meshModeOverride = null,
        string? registryDirectoryOverride = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _workerLauncher = workerLauncher ?? new RealWorkerProcessLauncher();
        _enableKestrelListeners = enableKestrelListeners;
        _startedAtUtc = _timeProvider.GetUtcNow();
        _meshCoordinator = new EdgeMeshCoordinator(layout, _startedAtUtc, enableKestrelListeners, meshModeOverride, registryDirectoryOverride);

        _configMonitor = new FileSystemConfigMonitor(_layout, _timeProvider);
        _configMonitor.SnapshotChanged += OnConfigurationSnapshotChangedAsync;
        _acmeManager = new AcmeCertificateManager(_layout, _certificateProvider, timeProvider: _timeProvider);
        _upstreamManager = new UpstreamClusterManager(_timeProvider);

        _pipeline = new KiteRequestPipeline(
            () => Volatile.Read(ref _routeTable),
            GetPoolSupervisor,
            _timeProvider,
            EnqueueW3CLogLine,
            EnqueueFrebTrace,
            acmeToken => _acmeManager.TryResolveHttp01Challenge(acmeToken, out string? auth) ? auth : null,
            _upstreamManager,
            EnqueueJsonLogLine,
            () => _redisState,
            _pluginHost,
            _meshCoordinator.ResolvePeerTarget);
    }

    public AcmeCertificateManager AcmeManager => _acmeManager;

    public KiteHomeLayout Layout => _layout;
    public FileSystemConfigMonitor ConfigMonitor => _configMonitor;
    public KiteRequestPipeline Pipeline => _pipeline;
    public HostRouteTable CurrentRouteTable => Volatile.Read(ref _routeTable);
    public IReadOnlyDictionary<string, ApplicationPoolSupervisor> Pools => _pools;
    public string InstanceId => _meshCoordinator.InstanceId;
    public InstanceRole MeshRole => _meshCoordinator.Role;
    public int? MeshPeerHttpPort => _meshCoordinator.PeerHttpPort;
    public string? MeshDirectHttpUrl => _meshCoordinator.DirectHttpUrl;
    public IReadOnlyList<string> MeshHostedDomains => _meshCoordinator.LocalHostedDomains;
    public IReadOnlyList<string> MeshPeerRoutes => _meshCoordinator.PeerRoutesSummary;

    public ApplicationPoolSupervisor? GetPoolSupervisor(string poolId) =>
        _pools.TryGetValue(poolId, out ApplicationPoolSupervisor? pool) ? pool : null;

    public async ValueTask StartAsync(bool seedDefaultsIfEmpty = true, CancellationToken ct = default)
    {
        _homeLock ??= KiteHomeLock.Acquire(_layout);

        try
        {
            ConfigSnapshot initial = await _configMonitor.InitializeAndStartAsync(seedDefaultsIfEmpty, ct).ConfigureAwait(false);
            await ApplySnapshotAsync(initial, ct).ConfigureAwait(false);
            await PublishStateSnapshotsAsync(ct).ConfigureAwait(false);

            int intervalMs = Math.Clamp(initial.Server.StatusPublishIntervalMilliseconds, 250, 10_000);
            _maintenanceAndStateTimer = _timeProvider.CreateTimer(
                static state => _ = ((KiteServerEngine)state!).RunMaintenanceAndPublishTickAsync(),
                this,
                TimeSpan.FromMilliseconds(intervalMs),
                TimeSpan.FromMilliseconds(intervalMs));
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask ApplySnapshotAsync(ConfigSnapshot snapshot, CancellationToken ct = default)
    {
        await _reconcileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Map primary SiteApplicationConfig per pool so pools know what executable/DLL to run.
            // Prefer dynamic workloads (KiteWorkerIpc / OutOfProcessProxy) over StaticOnly when a pool serves both.
            var primaryAppByPool = new Dictionary<string, SiteApplicationConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (SiteConfig site in snapshot.Sites.Values)
            {
                if (!string.IsNullOrWhiteSpace(site.RootApplication.PoolId))
                {
                    if (!primaryAppByPool.TryGetValue(site.RootApplication.PoolId, out SiteApplicationConfig? existing) ||
                        existing.HostingMode == WorkloadHostingMode.StaticOnly)
                    {
                        primaryAppByPool[site.RootApplication.PoolId] = site.RootApplication;
                    }
                }
                foreach (SiteApplicationConfig nested in site.NestedApplications)
                {
                    if (!string.IsNullOrWhiteSpace(nested.PoolId))
                    {
                        if (!primaryAppByPool.TryGetValue(nested.PoolId, out SiteApplicationConfig? existing) ||
                            existing.HostingMode == WorkloadHostingMode.StaticOnly)
                        {
                            primaryAppByPool[nested.PoolId] = nested;
                        }
                    }
                }
            }

            // 1. Reconcile Application Pools & Upstream Reverse Proxy Clusters
            _upstreamManager.ReconcileClusters(snapshot.Server.Upstreams);
            foreach (var (poolId, poolCfg) in snapshot.Pools)
            {
                primaryAppByPool.TryGetValue(poolId, out SiteApplicationConfig? primaryApp);
                if (!_pools.TryGetValue(poolId, out ApplicationPoolSupervisor? supervisor))
                {
                    supervisor = new ApplicationPoolSupervisor(poolCfg, _workerLauncher, _timeProvider);
                    _pools[poolId] = supervisor;
                }

                await supervisor.StartOrReconcileAsync(poolCfg, primaryApp, ct).ConfigureAwait(false);
            }

            // Remove deleted pools
            foreach (string existingPoolId in _pools.Keys.ToList())
            {
                if (!snapshot.Pools.ContainsKey(existingPoolId) &&
                    _pools.TryRemove(existingPoolId, out ApplicationPoolSupervisor? removedPool))
                {
                    await removedPool.DisposeAsync().ConfigureAwait(false);
                    string oldStatusPath = _layout.GetPoolStatusPath(existingPoolId);
                    if (File.Exists(oldStatusPath))
                    {
                        try { File.Delete(oldStatusPath); } catch { }
                    }
                }
            }

            // 2. Reconcile Edge Mesh leadership/peer role and atomically swap the lock-free HostRouteTable
            await _meshCoordinator.ReconcileAsync(snapshot, ct).ConfigureAwait(false);
            IEnumerable<SiteConfig> effectiveSites = _meshCoordinator.BuildEffectiveSitesForRouting(snapshot);
            var nextRouteTable = new HostRouteTable(effectiveSites, snapshot.Server);
            Interlocked.Exchange(ref _routeTable, nextRouteTable);

            // 2b. Reconcile Distributed Redis State, OTLP Trace Exporter, Edge C# Plugins & L4 TCP/SNI Streams
            if (snapshot.Server.DistributedState.Enabled)
            {
                if (_redisState is null)
                {
                    _redisState = new RedisRespClient(snapshot.Server.DistributedState);
                }
                else
                {
                    _redisState.UpdateConfig(snapshot.Server.DistributedState);
                }
            }
            else if (_redisState is not null)
            {
                _redisState.Dispose();
                _redisState = null;
            }

            _otlpExporter.Reconcile(snapshot.Server.Observability);
            _pluginHost.Reconcile(snapshot.Server.EdgePlugins, _layout.PluginsDirectory);
            if (_enableKestrelListeners)
            {
                List<L4StreamProxyConfig> activeStreams = _meshCoordinator.Role == InstanceRole.MeshPeer
                    ? []
                    : snapshot.Server.TcpStreams;
                await _l4StreamManager.ReconcileAsync(activeStreams, ct).ConfigureAwait(false);
            }

            // 3. Rebind Kestrel TCP/TLS listeners if ports/protocols changed
            if (_enableKestrelListeners)
            {
                await ReconcileKestrelListenersAsync(snapshot, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    public async ValueTask PublishStateSnapshotsAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return;
        }

        ConfigSnapshot snapshot = _configMonitor.CurrentSnapshot;
        HostRouteTable routeTable = CurrentRouteTable;
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // 1. Publish Pool Status files
        foreach (ApplicationPoolSupervisor pool in _pools.Values)
        {
            ApplicationPoolStatus poolStatus = pool.GetStatusSnapshot();
            await AtomicJsonStore.WriteAtomicAsync(
                _layout.GetPoolStatusPath(pool.PoolId),
                poolStatus,
                KiteJsonContext.Default.ApplicationPoolStatus,
                ct).ConfigureAwait(false);
        }

        // 2. Publish Site Status files
        var activeBindingStrings = new List<string>();
        foreach (SiteConfig site in snapshot.Sites.Values)
        {
            SiteRuntimeTelemetry telemetry = _pipeline.GetSiteTelemetry(site.Id);
            (double siteRps, double siteAvgLat, double siteP99Lat, double siteCacheHitPct) = telemetry.SampleRateAndLatency();
            var bindingStatuses = new List<BindingRuntimeStatus>(site.Bindings.Count);
            var certificates = new List<TlsCertificateChainStatus>();
            bool siteStarted = site.DesiredState == DesiredResourceState.Started;

            foreach (SiteBindingConfig b in site.Bindings)
            {
                string display = b.ToDisplayEndpoint();
                if (siteStarted)
                {
                    activeBindingStrings.Add(display);
                }

                bool isHttps = string.Equals(b.Protocol, "https", StringComparison.OrdinalIgnoreCase);
                bindingStatuses.Add(new BindingRuntimeStatus
                {
                    Endpoint = $"{b.IpAddress}:{b.Port}",
                    Protocol = b.Protocol,
                    Host = b.Host,
                    IsListening = siteStarted,
                    TlsEnabled = isHttps
                });

                if (isHttps)
                {
                    try
                    {
                        bool mtlsOn = site.Features.TlsPolicy.Mtls.ClientCertificateMode != MtlsClientCertificateMode.None ||
                                      snapshot.Server.Features.TlsPolicy.Mtls.ClientCertificateMode != MtlsClientCertificateMode.None;
                        TlsCertificateChainStatus? certStatus = _certificateProvider.TryInspectCertificateStatus(
                            b,
                            allowGenerateSelfSigned: _enableKestrelListeners,
                            _layout.ConfigDirectory,
                            mtlsEnabled: mtlsOn);
                        if (certStatus is not null)
                        {
                            certificates.Add(certStatus);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            var siteStatus = new SiteStatus
            {
                Id = site.Id,
                Name = site.Name,
                DesiredState = site.DesiredState,
                ActualState = siteStarted ? RuntimeResourceState.Started : RuntimeResourceState.Stopped,
                BindingStatuses = bindingStatuses,
                Certificates = certificates,
                TotalRequests = Interlocked.Read(ref telemetry.TotalRequests),
                Status2xxCount = Interlocked.Read(ref telemetry.Status2xxCount),
                Status3xxCount = Interlocked.Read(ref telemetry.Status3xxCount),
                Status4xxCount = Interlocked.Read(ref telemetry.Status4xxCount),
                Status5xxCount = Interlocked.Read(ref telemetry.Status5xxCount),
                RequestsPerSecond = siteRps,
                AvgLatencyMs = siteAvgLat,
                P99LatencyMs = siteP99Lat,
                CacheHitRatioPercent = siteCacheHitPct,
                TotalBytesOut = Interlocked.Read(ref telemetry.TotalBytesOut),
                CacheHits = Interlocked.Read(ref telemetry.CacheHits),
                ActiveWebSockets = Volatile.Read(ref telemetry.ActiveWebSockets),
                ValidationErrors = routeTable.BindingConflicts
                    .Where(c => c.Contains($"'{site.Id}'", StringComparison.OrdinalIgnoreCase))
                    .ToList(),
                UpdatedAtUtc = now
            };

            await AtomicJsonStore.WriteAtomicAsync(
                _layout.GetSiteStatusPath(site.Id),
                siteStatus,
                KiteJsonContext.Default.SiteStatus,
                ct).ConfigureAwait(false);
        }

        // 3. Publish Server Status file with 60-second telemetry ring buffer
        TelemetrySampleBucket latestBucket = _pipeline.DrainIntervalTelemetry(now, out double globalAvgLatency);
        List<TelemetrySampleBucket> recentHistory;
        lock (_sampleLock)
        {
            _recentSamples.Enqueue(latestBucket);
            while (_recentSamples.Count > 60)
            {
                _recentSamples.Dequeue();
            }
            recentHistory = new List<TelemetrySampleBucket>(_recentSamples);
        }

        ThreadPool.GetMaxThreads(out int maxWorkerThreads, out int maxIoThreads);
        ThreadPool.GetAvailableThreads(out int availWorkerThreads, out int availIoThreads);
        long workingSet = 0;
        try { workingSet = Environment.WorkingSet; } catch { }

        var serverStatus = new ServerStatus
        {
            ServerPid = Environment.ProcessId,
            ServerName = snapshot.Server.ServerName,
            InstanceId = _meshCoordinator.InstanceId,
            MeshRole = _meshCoordinator.Role,
            MeshDirectUrl = _meshCoordinator.DirectHttpUrl,
            HostedDomains = _meshCoordinator.LocalHostedDomains.ToList(),
            StartedAtUtc = _startedAtUtc,
            UpdatedAtUtc = now,
            ActiveBindingsCount = activeBindingStrings.Count,
            TotalRequestsProcessed = _pipeline.TotalRequestsProcessed,
            ActiveConnections = _pipeline.ActiveConnections,
            TotalBytesSent = _pipeline.TotalBytesSent,
            TotalBytesReceived = _pipeline.TotalBytesReceived,
            RequestsPerSecond = latestBucket.RequestsPerSecond,
            MbpsOut = Math.Round((latestBucket.BytesSentPerSecond * 8.0) / 1_000_000.0, 3),
            MbpsIn = Math.Round((latestBucket.BytesReceivedPerSecond * 8.0) / 1_000_000.0, 3),
            AvgLatencyMs = globalAvgLatency,
            P99LatencyMs = latestBucket.P99LatencyMs,
            ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            WorkingSetBytes = workingSet,
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            ActiveWorkerThreads = Math.Max(0, maxWorkerThreads - availWorkerThreads),
            ActiveIoThreads = Math.Max(0, maxIoThreads - availIoThreads),
            LockContentionCount = Monitor.LockContentionCount,
            CacheHitRatioPercent = _pipeline.OutputCache.HitRatioPercent,
            ActiveBindings = activeBindingStrings,
            ConfigurationErrors = routeTable.BindingConflicts.ToList(),
            RecentSamples = recentHistory,
            Upstreams = _upstreamManager.CaptureStatusSnapshot()
        };

        await AtomicJsonStore.WriteAtomicAsync(
            _layout.ServerStatusPath,
            serverStatus,
            KiteJsonContext.Default.ServerStatus,
            ct).ConfigureAwait(false);
        await _meshCoordinator.UpdateRegistrationAsync(snapshot, ct).ConfigureAwait(false);

        // 4. Flush pending W3C access logs and FREB traces
        await FlushW3CLogsAsync(now, ct).ConfigureAwait(false);
        await FlushFrebTracesAsync(snapshot, ct).ConfigureAwait(false);
    }

    private async ValueTask OnConfigurationSnapshotChangedAsync(ConfigSnapshot nextSnapshot)
    {
        await ApplySnapshotAsync(nextSnapshot, CancellationToken.None).ConfigureAwait(false);
        await PublishStateSnapshotsAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RunMaintenanceAndPublishTickAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            bool roleChanged = await _meshCoordinator.PollAndRefreshAsync(_configMonitor.CurrentSnapshot, CancellationToken.None).ConfigureAwait(false);
            if (roleChanged)
            {
                await ApplySnapshotAsync(_configMonitor.CurrentSnapshot, CancellationToken.None).ConfigureAwait(false);
            }

            await _acmeManager.EnsureAllDomainsProvisionedAsync(_configMonitor.CurrentSnapshot, CancellationToken.None).ConfigureAwait(false);
            await _upstreamManager.EvaluateActiveHealthChecksAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (ApplicationPoolSupervisor pool in _pools.Values)
            {
                await pool.EvaluateMaintenanceTickAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await PublishStateSnapshotsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Prevent background timer exceptions from crashing the host
        }
    }

    private async ValueTask ReconcileKestrelListenersAsync(ConfigSnapshot snapshot, CancellationToken ct)
    {
        var desiredSignature = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listenerMap = new Dictionary<(string IpKey, int Port), (bool IsHttps, bool AllowHttp3)>();

        if (_meshCoordinator.Role == InstanceRole.MeshPeer && _meshCoordinator.PeerHttpPort.HasValue)
        {
            int peerPort = _meshCoordinator.PeerHttpPort.Value;
            desiredSignature.Add($"mesh-peer:127.0.0.1:{peerPort}");
            listenerMap[("127.0.0.1", peerPort)] = (false, false);
        }
        else
        {
            foreach (SiteConfig site in snapshot.Sites.Values)
            {
                if (site.DesiredState != DesiredResourceState.Started)
                {
                    continue;
                }

                foreach (SiteBindingConfig b in site.Bindings)
                {
                    bool isHttps = string.Equals(b.Protocol, "https", StringComparison.OrdinalIgnoreCase);
                    bool bindingHttp3 = isHttps && snapshot.Server.Transport.EnableHttp3 && (b.Tls?.AllowHttp3 ?? true);
                    string ipKey = NormalizeBindIpKey(b.IpAddress);
                    desiredSignature.Add($"{b.Protocol}:{ipKey}:{b.Port}:h3={bindingHttp3}");

                    var key = (ipKey, b.Port);
                    if (!listenerMap.TryGetValue(key, out var existing))
                    {
                        listenerMap[key] = (isHttps, bindingHttp3);
                    }
                    else
                    {
                        listenerMap[key] = (existing.IsHttps || isHttps, existing.AllowHttp3 || bindingHttp3);
                    }
                }
            }

            // If wildcard '*' exists on a port, coalesce any specific-IP listeners on that same port into AnyIP.
            foreach ((string _, int port) in listenerMap.Keys.Where(static k => k.IpKey == "*").ToList())
            {
                var specifics = listenerMap.Where(kvp => kvp.Key.Port == port && kvp.Key.IpKey != "*").ToList();
                if (specifics.Count > 0)
                {
                    var wild = listenerMap[("*", port)];
                    foreach (var item in specifics)
                    {
                        wild = (wild.IsHttps || item.Value.IsHttps, wild.AllowHttp3 || item.Value.AllowHttp3);
                        listenerMap.Remove(item.Key);
                    }

                    listenerMap[("*", port)] = wild;
                }
            }
        }

        if (desiredSignature.SetEquals(_boundEndpointsSignature))
        {
            return;
        }

        if (_kestrelApp is not null)
        {
            try
            {
                await _kestrelApp.StopAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                await _kestrelApp.DisposeAsync().ConfigureAwait(false);
                _kestrelApp = null;
            }
        }

        _boundEndpointsSignature = desiredSignature;
        if (listenerMap.Count == 0)
        {
            return;
        }

        bool quicSupported = snapshot.Server.Transport.EnableHttp3 && QuicListener.IsSupported;
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxConcurrentConnections = snapshot.Server.MaxConcurrentConnections;
            kestrel.Limits.MaxRequestBodySize = snapshot.Server.MaxRequestBodySize;
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(Math.Max(5, snapshot.Server.KeepAliveTimeoutSeconds));
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(Math.Max(5, snapshot.Server.HeaderTimeoutSeconds));

            TimeSpan grace = TimeSpan.FromSeconds(Math.Max(1, snapshot.Server.Transport.SlowlorisGracePeriodSeconds));
            kestrel.Limits.MinRequestBodyDataRate = snapshot.Server.Transport.MinRequestBodyDataRateBytesPerSecond > 0
                ? new MinDataRate(snapshot.Server.Transport.MinRequestBodyDataRateBytesPerSecond, grace)
                : null;
            kestrel.Limits.MinResponseDataRate = snapshot.Server.Transport.MinResponseDataRateBytesPerSecond > 0
                ? new MinDataRate(snapshot.Server.Transport.MinResponseDataRateBytesPerSecond, grace)
                : null;

            foreach (var ((ipKey, port), (isHttps, bindingHttp3)) in listenerMap)
            {
                bool enablePortHttp3 = isHttps && bindingHttp3 && quicSupported;
                Action<ListenOptions> configureListen = listenOptions =>
                {
                    listenOptions.Protocols = enablePortHttp3
                        ? HttpProtocols.Http1AndHttp2AndHttp3
                        : HttpProtocols.Http1AndHttp2;
                    if (isHttps)
                    {
                        listenOptions.UseHttps(new TlsHandshakeCallbackOptions
                        {
                            OnConnection = context =>
                            {
                                string sniHost = context.ClientHelloInfo.ServerName;

                                // RFC 8737: Check for TLS-ALPN-01 ACME verification handshake
                                if (!string.IsNullOrEmpty(sniHost) &&
                                    _acmeManager.TryGetTlsAlpnChallengeCertificate(sniHost, out X509Certificate2? alpnCert) &&
                                    alpnCert is not null)
                                {
                                    return ValueTask.FromResult(new SslServerAuthenticationOptions
                                    {
                                        ServerCertificate = alpnCert,
                                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                                        ApplicationProtocols = [new SslApplicationProtocol("acme-tls/1")]
                                    });
                                }

                                HostRouteTable table = CurrentRouteTable;
                                X509Certificate2 cert;
                                bool enforceTls13 = snapshot.Server.Features.TlsPolicy.EnforceTls13;
                                MtlsPolicyConfig mtls = snapshot.Server.Features.TlsPolicy.Mtls;

                                if (table.TryMatchNamedSni(sniHost.AsSpan(), out CompiledSiteRoute? matchedRoute) &&
                                    matchedRoute is not null)
                                {
                                    cert = _certificateProvider.GetOrCreateCertificate(
                                        matchedRoute.Binding,
                                        _layout.ConfigDirectory,
                                        _layout.CertsConfigDirectory);
                                    enforceTls13 = matchedRoute.Features.EffectiveConfig.TlsPolicy.EnforceTls13;
                                    mtls = matchedRoute.Features.EffectiveConfig.TlsPolicy.Mtls;
                                }
                                else if (!string.IsNullOrEmpty(sniHost) && _meshCoordinator.HasPeerHost(sniHost))
                                {
                                    cert = _certificateProvider.GetOrCreateCertificate(
                                        new SiteBindingConfig { Host = sniHost },
                                        _layout.ConfigDirectory,
                                        _layout.CertsConfigDirectory);
                                }
                                else if (table.TryMatchSni(sniHost.AsSpan(), out matchedRoute) &&
                                         matchedRoute is not null)
                                {
                                    cert = _certificateProvider.GetOrCreateCertificate(
                                        matchedRoute.Binding,
                                        _layout.ConfigDirectory,
                                        _layout.CertsConfigDirectory);
                                    enforceTls13 = matchedRoute.Features.EffectiveConfig.TlsPolicy.EnforceTls13;
                                    mtls = matchedRoute.Features.EffectiveConfig.TlsPolicy.Mtls;
                                }
                                else
                                {
                                    // On-Demand TLS check for unknown SNI hostnames before issuing certificate
                                    OnDemandTlsConfig onDemand = snapshot.Server.Features.TlsPolicy.OnDemandTls;
                                    if (!string.IsNullOrEmpty(sniHost) &&
                                        onDemand.Enabled &&
                                        !_acmeManager.IsOnDemandSniAllowedAsync(sniHost, onDemand).AsTask().GetAwaiter().GetResult())
                                    {
                                        throw new InvalidOperationException($"SNI host '{sniHost}' denied by On-Demand TLS policy.");
                                    }

                                    cert = _certificateProvider.GetOrCreateCertificate(
                                        new SiteBindingConfig { Host = string.IsNullOrEmpty(sniHost) ? "localhost" : sniHost },
                                        _layout.ConfigDirectory,
                                        _layout.CertsConfigDirectory);
                                }

                                bool requireClientCert = mtls.ClientCertificateMode == MtlsClientCertificateMode.RequireCertificate;
                                bool requestClientCert = mtls.ClientCertificateMode != MtlsClientCertificateMode.None;
                                var sslOptions = new SslServerAuthenticationOptions
                                {
                                    ServerCertificate = cert,
                                    ClientCertificateRequired = requireClientCert,
                                    EnabledSslProtocols = enforceTls13
                                        ? SslProtocols.Tls13
                                        : (SslProtocols.Tls12 | SslProtocols.Tls13),
                                    ApplicationProtocols =
                                    [
                                        SslApplicationProtocol.Http2,
                                        SslApplicationProtocol.Http11
                                    ]
                                };

                                if (requestClientCert)
                                {
                                    HashSet<string> allowed = mtls.TrustedThumbprints
                                        .Where(static t => !string.IsNullOrWhiteSpace(t))
                                        .Select(static t => t.Replace(":", "", StringComparison.Ordinal).Trim())
                                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                                    sslOptions.RemoteCertificateValidationCallback = (_, clientCert, _, sslErrors) =>
                                    {
                                        if (clientCert is null)
                                        {
                                            return !requireClientCert;
                                        }

                                        if (allowed.Count > 0)
                                        {
                                            return allowed.Contains(clientCert.GetCertHashString());
                                        }

                                        return sslErrors == SslPolicyErrors.None;
                                    };
                                }
                                return ValueTask.FromResult(sslOptions);
                            }
                        });
                    }
                };

                if (ipKey == "*" || !IPAddress.TryParse(ipKey, out IPAddress? parsedIp))
                {
                    kestrel.ListenAnyIP(port, configureListen);
                }
                else
                {
                    kestrel.Listen(parsedIp, port, configureListen);
                }
            }
        });

        WebApplication app = builder.Build();
        app.UseWebSockets();
        app.Run(ctx => _pipeline.ExecuteAsync(ctx));

        await app.StartAsync(ct).ConfigureAwait(false);
        _kestrelApp = app;
    }

    private static string NormalizeBindIpKey(string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return "*";
        }

        string trimmed = ipAddress.Trim();
        if (trimmed == "*" || trimmed == "0.0.0.0" || trimmed == "::" || trimmed == "[::]")
        {
            return "*";
        }

        return IPAddress.TryParse(trimmed, out IPAddress? parsed) ? parsed.ToString() : "*";
    }

    private void EnqueueW3CLogLine(string siteId, string line)
    {
        ConcurrentQueue<string> queue = _pendingW3CLogs.GetOrAdd(siteId, static _ => new ConcurrentQueue<string>());
        queue.Enqueue(line);
    }

    private void EnqueueJsonLogLine(string siteId, string line)
    {
        ConcurrentQueue<string> queue = _pendingJsonLogs.GetOrAdd(siteId, static _ => new ConcurrentQueue<string>());
        queue.Enqueue(line);
    }

    private void EnqueueFrebTrace(FrebTraceRecord trace)
    {
        _pendingFrebTraces.Enqueue(trace);
    }

    private async ValueTask FlushFrebTracesAsync(ConfigSnapshot snapshot, CancellationToken ct)
    {
        var touchedSites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (_pendingFrebTraces.TryDequeue(out FrebTraceRecord? trace))
        {
            string frebDir = _layout.GetSiteFrebDirectory(trace.SiteId);
            Directory.CreateDirectory(frebDir);
            string filePath = Path.Combine(frebDir, $"{trace.TimestampUtc.UtcTicks:D19}-{trace.CorrelationId}.json");
            await AtomicJsonStore.WriteAtomicAsync(
                filePath,
                trace,
                KiteJsonContext.Default.FrebTraceRecord,
                ct).ConfigureAwait(false);
            touchedSites.Add(trace.SiteId);
        }

        foreach (string siteId in touchedSites)
        {
            int maxKeep = snapshot.Sites.TryGetValue(siteId, out SiteConfig? siteCfg)
                ? Math.Clamp(siteCfg.Features.FailedRequestTracing.MaxRetainedTraces, 5, 500)
                : 50;

            string frebDir = _layout.GetSiteFrebDirectory(siteId);
            if (!Directory.Exists(frebDir))
            {
                continue;
            }

            List<string> files = Directory.EnumerateFiles(frebDir, "*.json")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = maxKeep; i < files.Count; i++)
            {
                try { File.Delete(files[i]); } catch { }
            }
        }
    }

    private async ValueTask FlushW3CLogsAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (siteId, queue) in _pendingW3CLogs)
        {
            if (queue.IsEmpty) continue;
            string logDir = _layout.GetSiteLogDirectory(siteId);
            string logFile = Path.Combine(logDir, $"u_ex{now:yyMMdd}.log");
            await AppendLinesToFileAsync(logFile, queue, W3CAccessLogFormatter.W3CHeaderBlock, ct).ConfigureAwait(false);
        }

        foreach (var (siteId, queue) in _pendingJsonLogs)
        {
            if (queue.IsEmpty) continue;
            string logDir = _layout.GetSiteLogDirectory(siteId);
            string ndjsonFile = Path.Combine(logDir, $"access_{now:yyMMdd}.ndjson");
            await AppendLinesToFileAsync(ndjsonFile, queue, header: null, ct).ConfigureAwait(false);
        }
    }

    private static async ValueTask AppendLinesToFileAsync(
        string filePath,
        ConcurrentQueue<string> queue,
        string? header,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        bool writeHeader = header is not null && !File.Exists(filePath);

        await using var fs = new FileStream(
            filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        await using var writer = new StreamWriter(fs);

        if (writeHeader)
        {
            await writer.WriteAsync(header.AsMemory(), ct).ConfigureAwait(false);
        }

        while (queue.TryDequeue(out string? line))
        {
            await writer.WriteAsync(line.AsMemory(), ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_maintenanceAndStateTimer is not null)
        {
            await _maintenanceAndStateTimer.DisposeAsync().ConfigureAwait(false);
        }

        await _configMonitor.DisposeAsync().ConfigureAwait(false);

        if (_kestrelApp is not null)
        {
            int drainSeconds = Math.Clamp(_configMonitor.CurrentSnapshot.Server.Transport.GracefulDrainTimeoutSeconds, 1, 120);
            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(drainSeconds));
            try { await _kestrelApp.StopAsync(drainCts.Token).ConfigureAwait(false); } catch { }
            await _kestrelApp.DisposeAsync().ConfigureAwait(false);
        }

        foreach (ApplicationPoolSupervisor pool in _pools.Values)
        {
            await pool.DisposeAsync().ConfigureAwait(false);
        }
        _pools.Clear();

        _pipeline.Dispose();
        await _l4StreamManager.DisposeAsync().ConfigureAwait(false);
        await _otlpExporter.DisposeAsync().ConfigureAwait(false);
        _pluginHost.Dispose();
        _redisState?.Dispose();
        _upstreamManager.Dispose();
        _acmeManager.Dispose();
        _certificateProvider.Dispose();
        await _meshCoordinator.DisposeAsync().ConfigureAwait(false);
        _homeLock?.Dispose();
        _homeLock = null;
        _reconcileLock.Dispose();
    }
}
