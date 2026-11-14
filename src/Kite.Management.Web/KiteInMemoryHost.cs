using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Kite.Core.Memory;
using Kite.Core.Models;
using Kite.Core.Routing;
using Kite.Core.Serialization;
using Kite.Core.Storage;
using Kite.Management.Core;

namespace Kite.Management.Web;

/// <summary>
/// Zero-port, zero-reflection in-memory HTML management host implementing the
/// "Obsidian Telemetry" Design System (<c>design/DesktopWinForms/obsidian_telemetry/DESIGN.md</c>)
/// across all four operational views:
/// 1. Dashboard &amp; Telemetry (<c>/?scope=server</c> / <c>/?scope=dashboard</c>)
/// 2. Websites &amp; Application Pools (<c>/?scope=sites</c> / <c>/?scope=pools</c>)
/// 3. Configuration &amp; Features Matrix (<c>/?scope=config</c> / <c>/?scope=server&amp;tab=settings</c>)
/// 4. Worker Processes &amp; Tracing (<c>/?scope=workers</c> / <c>/?scope=sites&amp;tab=logs</c>)
/// </summary>
public sealed partial class KiteInMemoryHost : IAsyncDisposable
{
    private readonly string? _registryDirectoryOverride;
    private KiteManagementService _managementService;
    private IReadOnlyList<InstanceDescriptor> _cachedFleetInstances = [];
    private long _cachedFleetAtTick;
    private bool _started;

    public KiteInMemoryHost(KiteHomeLayout layout, string? registryDirectoryOverride = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _registryDirectoryOverride = registryDirectoryOverride;
        _managementService = new KiteManagementService(layout);
    }

    public KiteManagementService ManagementService => _managementService;

    public void SwitchWorkspace(string kiteHomePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kiteHomePath);
        string full = Path.GetFullPath(kiteHomePath);
        if (!string.Equals(full, _managementService.Layout.RootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _managementService = new KiteManagementService(new KiteHomeLayout(full));
            Volatile.Write(ref _cachedFleetAtTick, 0);
        }
    }

    public async ValueTask<IReadOnlyList<InstanceDescriptor>> ListFleetInstancesAsync(
        CancellationToken ct = default,
        bool forceRefresh = false)
    {
        long nowTick = Environment.TickCount64;
        long prevTick = Volatile.Read(ref _cachedFleetAtTick);
        if (!forceRefresh && prevTick != 0 && (nowTick - prevTick) < 2000)
        {
            return _cachedFleetInstances;
        }

        ManagementWorkspaceSnapshot snap = await _managementService.GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
        var registry = new KiteInstanceRegistry(_registryDirectoryOverride ?? snap.ServerConfig.Mesh.RegistryDirectoryOverride);
        IReadOnlyList<InstanceDescriptor> fresh = await registry.ListLiveInstancesAsync(ct).ConfigureAwait(false);
        _cachedFleetInstances = fresh;
        Volatile.Write(ref _cachedFleetAtTick, nowTick);
        return fresh;
    }

    public async ValueTask StartAsync(bool seedDefaults = true, CancellationToken ct = default)
    {
        if (_started)
        {
            return;
        }

        if (seedDefaults)
        {
            await _managementService.EnsureDefaultWorkspaceSeededAsync(ct).ConfigureAwait(false);
        }

        _started = true;

        // Warm up view rendering JIT asynchronously so cold startup returns immediately while WebView2 initializes concurrently.
        _ = Task.Run(async () =>
        {
            try
            {
                ManagementWorkspaceSnapshot snap = await _managementService.GetWorkspaceSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                IReadOnlyList<InstanceDescriptor> bgFleet = await ListFleetInstancesAsync(CancellationToken.None).ConfigureAwait(false);
                StringBuilder bgSb = KiteObjectPools.StringBuilderPool.Get();
                try
                {
                    PoolCompositeView? p = snap.Pools.FirstOrDefault();
                    SiteCompositeView? s = snap.Sites.FirstOrDefault();
                    RenderPageHtml(bgSb, snap, "server", null, "overview", null, p, s, [], [], bgFleet);
                    bgSb.Clear();
                    RenderPageHtml(bgSb, snap, "pools", p?.Config.Id, null, null, p, s, [], [], bgFleet);
                    bgSb.Clear();
                    RenderPageHtml(bgSb, snap, "sites", s?.Config.Id, null, null, p, s, [], [], bgFleet);
                    bgSb.Clear();
                    RenderPageHtml(bgSb, snap, "server", null, "settings", null, p, s, [], [], bgFleet);
                    bgSb.Clear();
                    RenderPageHtml(bgSb, snap, "workers", null, null, null, p, s, [], [], bgFleet);
                }
                finally
                {
                    KiteObjectPools.StringBuilderPool.Return(bgSb);
                }
            }
            catch
            {
            }
        });
    }

    public async ValueTask<InMemoryUiResponse> SendAsync(
        string method,
        string pathAndQuery,
        Stream? requestBody = null,
        string? contentType = null,
        CancellationToken ct = default)
    {
        if (!_started)
        {
            await StartAsync(seedDefaults: true, ct).ConfigureAwait(false);
        }

        string path = pathAndQuery;
        string query = string.Empty;
        int qIdx = pathAndQuery.IndexOf('?');
        if (qIdx >= 0)
        {
            path = pathAndQuery[..qIdx];
            query = pathAndQuery[(qIdx + 1)..];
        }

        if (path.Equals("/css/kite-ui.css", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/css/kite-tailwind.css", StringComparison.OrdinalIgnoreCase))
        {
            var cssStream = new SegmentedBufferStream();
            byte[] cssBytes = Encoding.UTF8.GetBytes(KiteEmbeddedStyles.TailwindCss);
            cssStream.Write(cssBytes);
            cssStream.Position = 0;
            return new InMemoryUiResponse(200, "text/css; charset=utf-8", new Dictionary<string, string>(), cssStream);
        }

        if (path.Equals("/api/events/stream", StringComparison.OrdinalIgnoreCase))
        {
            ManagementWorkspaceSnapshot live = await _managementService.GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
            long reqs = live.IsServerOnline ? (live.ServerStatus?.TotalRequestsProcessed ?? 0) : 0;
            long conns = live.IsServerOnline ? (live.ServerStatus?.ActiveConnections ?? 0) : 0;
            double heapMb = live.IsServerOnline ? (live.ServerStatus?.ManagedHeapBytes ?? 0) / (1024.0 * 1024.0) : 0.0;
            double rps = live.IsServerOnline ? (live.ServerStatus?.RequestsPerSecond ?? 0.0) : 0.0;
            int upstreamsCount = live.ServerStatus?.Upstreams.Count ?? live.ServerConfig.Upstreams.Count;

            string ssePayload = $"retry: 2500\nevent: telemetry\ndata: {{\"isOnline\":{(live.IsServerOnline ? "true" : "false")},\"requests\":{reqs},\"activeConnections\":{conns},\"heapMb\":{heapMb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},\"rps\":{rps.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)},\"upstreams\":{upstreamsCount}}}\n\n";
            var sseStream = new SegmentedBufferStream();
            sseStream.Write(Encoding.UTF8.GetBytes(ssePayload));
            sseStream.Position = 0;
            return new InMemoryUiResponse(
                200,
                "text/event-stream; charset=utf-8",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Cache-Control"] = "no-cache"
                },
                sseStream);
        }

        if (path.Equals("/api/pipeline-simulate", StringComparison.OrdinalIgnoreCase))
        {
            var q = HttpUtility.ParseQueryString(query);
            string simMethod = q["method"] ?? "GET";
            string simScheme = q["scheme"] ?? "https";
            string simHost = q["host"] ?? "localhost";
            int simPort = int.TryParse(q["port"], out int sp) ? sp : (simScheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 8443 : 8080);
            string simPath = q["path"] ?? "/";
            PipelineTraceResult simResult = await _managementService
                .SimulatePipelineAsync(simMethod, simScheme, simHost, simPort, simPath, headers: null, ct)
                .ConfigureAwait(false);

            byte[] jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(simResult, KiteJsonContext.Default.PipelineTraceResult));
            var ms = new SegmentedBufferStream();
            ms.Write(jsonBytes);
            ms.Position = 0;
            return new InMemoryUiResponse(200, "application/json; charset=utf-8", new Dictionary<string, string>(), ms);
        }

        if (path.Equals("/api/history", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<ConfigSnapshotSummary> snaps = await _managementService.History.ListSnapshotsAsync(ct).ConfigureAwait(false);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snaps.ToList(), KiteJsonContext.Default.ListConfigSnapshotSummary));
            var ms = new SegmentedBufferStream();
            ms.Write(jsonBytes);
            ms.Position = 0;
            return new InMemoryUiResponse(200, "application/json; charset=utf-8", new Dictionary<string, string>(), ms);
        }

        if (path.Equals("/api/fleet/instances", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<InstanceDescriptor> fleet = await ListFleetInstancesAsync(ct, forceRefresh: true).ConfigureAwait(false);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fleet.ToList(), KiteJsonContext.Default.ListInstanceDescriptor));
            var ms = new SegmentedBufferStream();
            ms.Write(jsonBytes);
            ms.Position = 0;
            return new InMemoryUiResponse(200, "application/json; charset=utf-8", new Dictionary<string, string>(), ms);
        }

        if (path.Equals("/partials/config-modals", StringComparison.OrdinalIgnoreCase))
        {
            var modalQ = HttpUtility.ParseQueryString(query);
            string? modalSiteId = string.IsNullOrWhiteSpace(modalQ["siteId"]) ? null : modalQ["siteId"]!.Trim();
            ManagementWorkspaceSnapshot modalSnap = await _managementService.GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
            SiteCompositeView? modalSite = modalSiteId is not null
                ? modalSnap.Sites.FirstOrDefault(s => string.Equals(s.Config.Id, modalSiteId, StringComparison.OrdinalIgnoreCase))
                : null;
            SiteFeaturesConfig modalFeat = modalSite is not null
                ? CompiledSiteFeatures.ResolveEffectiveConfig(modalSite.Config.Features, modalSnap.ServerConfig.Features, out _)
                : modalSnap.ServerConfig.Features;
            IReadOnlyList<TlsCertificateChainStatus> modalCerts = modalSite is not null
                ? (modalSite.Status?.Certificates ?? [])
                : modalSnap.Sites.SelectMany(s => s.Status?.Certificates ?? []).ToList();
            string modalTargetHtml = modalSite is not null
                ? $"""<input type="hidden" name="targetSiteId" value="{Enc(modalSite.Config.Id)}" />"""
                : """<input type="hidden" name="targetSiteId" value="" />""";
            StringBuilder modalSb = KiteObjectPools.StringBuilderPool.Get();
            try
            {
                RenderFeatureConfigModals(modalSb, modalTargetHtml, modalFeat, modalCerts, modalSnap.ServerConfig);
                byte[] modalBytes = Encoding.UTF8.GetBytes(modalSb.ToString());
                var ms = new SegmentedBufferStream();
                ms.Write(modalBytes);
                ms.Position = 0;
                return new InMemoryUiResponse(200, "text/html; charset=utf-8", new Dictionary<string, string>(), ms);
            }
            finally
            {
                KiteObjectPools.StringBuilderPool.Return(modalSb);
            }
        }

        var queryParams = HttpUtility.ParseQueryString(query);
        string? requestedHome = queryParams["home"];
        if (!string.IsNullOrWhiteSpace(requestedHome) && Directory.Exists(requestedHome))
        {
            SwitchWorkspace(requestedHome);
        }

        string scope = (queryParams["scope"] ?? "server").Trim().ToLowerInvariant();
        string? entity = queryParams["entity"];
        string? tab = queryParams["tab"];
        string? flashMessage = null;

        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            var form = await ParseFormAsync(requestBody, ct).ConfigureAwait(false);
            string effectiveActionPath = path;
            string? handler = queryParams["handler"];
            if (!string.IsNullOrWhiteSpace(handler))
            {
                effectiveActionPath = handler.ToLowerInvariant() switch
                {
                    "poolaction" => "/actions/pool-command",
                    "savepool" => "/actions/pool-save",
                    "siteaction" => "/actions/site-command",
                    "savesite" => "/actions/site-save",
                    "saveserver" => "/actions/server-settings",
                    "daemonaction" => "/actions/server-daemon",
                    _ => path
                };
            }

            (scope, entity, tab, flashMessage) = await HandlePostActionAsync(effectiveActionPath, form, scope, entity, tab, ct).ConfigureAwait(false);
        }

        ManagementWorkspaceSnapshot workspace = await _managementService.GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
        IReadOnlyList<InstanceDescriptor> fleetInstances = await ListFleetInstancesAsync(ct).ConfigureAwait(false);

        PoolCompositeView? selectedPool = null;
        if (scope == "pools")
        {
            selectedPool = workspace.Pools.FirstOrDefault(p => string.Equals(p.Config.Id, entity, StringComparison.OrdinalIgnoreCase))
                           ?? workspace.Pools.FirstOrDefault();
            entity = selectedPool?.Config.Id;
        }

        SiteCompositeView? selectedSite = null;
        IReadOnlyList<string> recentSiteLogs = [];
        IReadOnlyList<FrebTraceRecord> recentFrebTraces = [];

        if (scope == "config")
        {
            // Only select a specific site in Config Matrix when entity is explicitly specified (otherwise Server Level)
            if (!string.IsNullOrWhiteSpace(entity))
            {
                selectedSite = workspace.Sites.FirstOrDefault(s => string.Equals(s.Config.Id, entity, StringComparison.OrdinalIgnoreCase))
                               ?? workspace.Sites.FirstOrDefault();
            }
        }
        else if (scope is "sites" or "workers")
        {
            selectedSite = workspace.Sites.FirstOrDefault(s => string.Equals(s.Config.Id, entity, StringComparison.OrdinalIgnoreCase))
                           ?? workspace.Sites.FirstOrDefault();
            if (scope == "sites")
            {
                entity = selectedSite?.Config.Id;
            }

            if (scope == "workers" || string.Equals(tab, "logs", StringComparison.OrdinalIgnoreCase))
            {
                string? filterSiteId = !string.IsNullOrWhiteSpace(entity) ? entity : selectedSite?.Config.Id;
                if (filterSiteId is not null)
                {
                    recentSiteLogs = await _managementService.ReadRecentSiteLogsAsync(filterSiteId, maxLines: 50, ct).ConfigureAwait(false);
                }

                // In Worker Processes & Tracing view, read all FREB traces unless narrowed to a site entity
                string? frebSiteFilter = string.IsNullOrWhiteSpace(entity) ? null : entity;
                recentFrebTraces = await _managementService.ReadRecentFrebTracesAsync(frebSiteFilter, maxCount: 40, ct).ConfigureAwait(false);
            }
        }
        else if (scope == "server" && !string.Equals(tab, "settings", StringComparison.OrdinalIgnoreCase) && workspace.Sites.Count > 0)
        {
            selectedSite = workspace.Sites[0];
        }

        if (selectedPool is null && workspace.Pools.Count > 0)
        {
            selectedPool = selectedSite is not null
                ? workspace.Pools.FirstOrDefault(p => string.Equals(p.Config.Id, selectedSite.Config.RootApplication.PoolId, StringComparison.OrdinalIgnoreCase)) ?? workspace.Pools[0]
                : workspace.Pools[0];
        }

        var bodyBuffer = new SegmentedBufferStream();
        StringBuilder sb = KiteObjectPools.StringBuilderPool.Get();
        try
        {
            RenderPageHtml(sb, workspace, scope, entity, tab, flashMessage, selectedPool, selectedSite, recentSiteLogs, recentFrebTraces, fleetInstances);
            byte[] utf8 = Encoding.UTF8.GetBytes(sb.ToString());
            bodyBuffer.Write(utf8);
            bodyBuffer.Position = 0;
        }
        finally
        {
            KiteObjectPools.StringBuilderPool.Return(sb);
        }

        return new InMemoryUiResponse(
            StatusCode: 200,
            ContentType: "text/html; charset=utf-8",
            Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cache-Control"] = "no-store"
            },
            BodyStream: bodyBuffer);
    }

    public Task<InMemoryUiResponse> SendAsync(string pathAndQuery, CancellationToken ct = default) =>
        SendAsync("GET", pathAndQuery, null, null, ct).AsTask();

    public async ValueTask<InMemoryHttpResponse> DispatchAsync(InMemoryHttpRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using Stream? bodyStream = request.Body is { Length: > 0 } ? new MemoryStream(request.Body, writable: false) : null;
        InMemoryUiResponse uiResp = await SendAsync(
            request.Method ?? "GET",
            request.PathAndQuery ?? "/",
            bodyStream,
            request.ContentType,
            ct).ConfigureAwait(false);

        return new InMemoryHttpResponse(
            uiResp.StatusCode,
            uiResp.StatusCode == 200 ? "OK" : "Error",
            uiResp.ContentType,
            uiResp.BodyStream);
    }

    private static async ValueTask<Dictionary<string, string>> ParseFormAsync(Stream? body, CancellationToken ct)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (body is null)
        {
            return dict;
        }

        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        string raw = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return dict;
        }

        string[] pairs = raw.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (string pair in pairs)
        {
            int eq = pair.IndexOf('=');
            if (eq > 0)
            {
                string k = Uri.UnescapeDataString(pair[..eq].Replace('+', ' '));
                string v = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                dict[k] = v;
            }
        }
        return dict;
    }

    private async ValueTask<(string Scope, string? Entity, string? Tab, string? Flash)> HandlePostActionAsync(
        string path,
        Dictionary<string, string> form,
        string defaultScope,
        string? defaultEntity,
        string? defaultTab,
        CancellationToken ct)
    {
        switch (path.ToLowerInvariant())
        {
            case "/actions/fleet-switch":
            {
                string targetHome = form.GetValueOrDefault("targetHome", string.Empty).Trim();
                string returnScope = form.GetValueOrDefault("returnScope", defaultScope);
                if (!string.IsNullOrWhiteSpace(targetHome) && Directory.Exists(targetHome))
                {
                    SwitchWorkspace(targetHome);
                    return (returnScope, null, null, $"Switched Studio target to instance workspace '{targetHome}'.");
                }
                return (returnScope, null, null, "Target instance workspace directory no longer exists.");
            }

            case "/actions/server-daemon":
            {
                string cmd = form.GetValueOrDefault("command", "start");
                string returnScope = form.GetValueOrDefault("returnScope", defaultScope);
                if (string.Equals(cmd, "stop", StringComparison.OrdinalIgnoreCase))
                {
                    await _managementService.StopServerDaemonAsync(ct).ConfigureAwait(false);
                    return (returnScope, null, null, "Kite.Server daemon stopped.");
                }
                bool started = await _managementService.EnsureServerDaemonRunningAsync(ct).ConfigureAwait(false);
                return (returnScope, null, null, started
                    ? "Kite.Server daemon is online and listening on configured bindings."
                    : "Unable to start Kite.Server daemon automatically.");
            }

            case "/actions/server-settings":
            {
                await _managementService.MutateServerConfigAsync(cfg =>
                {
                    string serverName = form.GetValueOrDefault("serverName", cfg.ServerName);
                    int maxConn = int.TryParse(form.GetValueOrDefault("maxConcurrentConnections"), out int mc) ? mc : cfg.MaxConcurrentConnections;
                    long maxBody = long.TryParse(form.GetValueOrDefault("maxRequestBodySize"), out long mb) ? mb : cfg.MaxRequestBodySize;
                    int keepAlive = int.TryParse(form.GetValueOrDefault("keepAliveTimeoutSeconds"), out int ka) ? ka : cfg.KeepAliveTimeoutSeconds;
                    int headerTimeout = int.TryParse(form.GetValueOrDefault("headerTimeoutSeconds"), out int ht) ? ht : cfg.HeaderTimeoutSeconds;
                    int statusInterval = int.TryParse(form.GetValueOrDefault("statusPublishIntervalMilliseconds"), out int si) ? si : cfg.StatusPublishIntervalMilliseconds;

                    cfg.ServerName = string.IsNullOrWhiteSpace(serverName) ? cfg.ServerName : serverName.Trim();
                    cfg.MaxConcurrentConnections = Math.Max(1, maxConn);
                    cfg.MaxRequestBodySize = Math.Max(1024, maxBody);
                    cfg.KeepAliveTimeoutSeconds = Math.Clamp(keepAlive, 5, 3600);
                    cfg.HeaderTimeoutSeconds = Math.Clamp(headerTimeout, 5, 300);
                    cfg.StatusPublishIntervalMilliseconds = Math.Clamp(statusInterval, 250, 10_000);

                    if (string.Equals(form.GetValueOrDefault("inlineTransportFieldsPresent"), "true", StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.Transport.EnableHttp3 = string.Equals(form.GetValueOrDefault("enableHttp3"), "true", StringComparison.OrdinalIgnoreCase);
                        cfg.Observability.EnablePrometheusMetrics = string.Equals(form.GetValueOrDefault("enablePrometheusMetrics"), "true", StringComparison.OrdinalIgnoreCase);
                        if (Enum.TryParse<AccessLogFormat>(form.GetValueOrDefault("accessLogFormat"), ignoreCase: true, out AccessLogFormat fmt))
                        {
                            cfg.Observability.AccessLogFormat = fmt;
                        }
                    }
                }, ct).ConfigureAwait(false);

                return ("server", null, "settings", "Server configuration saved atomically to config/server.json.");
            }

            case "/actions/pool-command":
            {
                string poolId = form.GetValueOrDefault("poolId", defaultEntity ?? "default-pool");
                string cmd = form.GetValueOrDefault("command", form.GetValueOrDefault("actionType", ""));
                if (string.Equals(cmd, "start", StringComparison.OrdinalIgnoreCase))
                {
                    await _managementService.SetPoolDesiredStateAsync(poolId, DesiredResourceState.Started, ct).ConfigureAwait(false);
                    return ("pools", poolId, null, $"Application Pool '{poolId}' desired state set to Started.");
                }
                if (string.Equals(cmd, "stop", StringComparison.OrdinalIgnoreCase))
                {
                    await _managementService.SetPoolDesiredStateAsync(poolId, DesiredResourceState.Stopped, ct).ConfigureAwait(false);
                    return ("pools", poolId, null, $"Application Pool '{poolId}' desired state set to Stopped.");
                }
                if (string.Equals(cmd, "recycle", StringComparison.OrdinalIgnoreCase))
                {
                    long? ver = await _managementService.RecyclePoolAsync(poolId, ct).ConfigureAwait(false);
                    return ("pools", poolId, null, $"Triggered Overlapped Recycle for '{poolId}' (RecycleVersion={ver}).");
                }
                return ("pools", poolId, null, null);
            }

            case "/actions/pool-recycle-all":
            {
                ManagementWorkspaceSnapshot snap = await _managementService.GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
                int count = 0;
                foreach (PoolCompositeView p in snap.Pools)
                {
                    await _managementService.RecyclePoolAsync(p.Config.Id, ct).ConfigureAwait(false);
                    count++;
                }
                string returnScope = form.GetValueOrDefault("returnScope", "server");
                return (returnScope, null, null, $"Triggered overlapped recycle across {count} Application Pool(s).");
            }

            case "/actions/pool-save":
            {
                string id = form.GetValueOrDefault("id", "default-pool");
                string name = form.GetValueOrDefault("name", id);
                int workerCount = int.TryParse(form.GetValueOrDefault("workerCount"), out int wc) ? wc : 1;
                string gcModeStr = form.GetValueOrDefault("gcMode", "DatasServer");
                string startModeStr = form.GetValueOrDefault("startMode", "AlwaysRunning");
                int idleMin = int.TryParse(form.GetValueOrDefault("idleTimeoutMinutes"), out int im) ? im : 20;
                long maxMem = long.TryParse(form.GetValueOrDefault("maxMemoryMegabytes"), out long mm) ? mm : 1024;
                int recycleMin = int.TryParse(form.GetValueOrDefault("periodicRestartMinutes"), out int rm) ? rm : 1740;
                long maxReqs = long.TryParse(form.GetValueOrDefault("maxRequests"), out long mr) ? mr : 0;
                string? scheduledTime = form.GetValueOrDefault("scheduledRestartUtcTime", "04:00");

                ManagementWorkspaceSnapshot snap = await _managementService.GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
                ApplicationPoolConfig existing = snap.Pools
                    .FirstOrDefault(p => string.Equals(p.Config.Id, id, StringComparison.OrdinalIgnoreCase))?.Config
                    ?? new ApplicationPoolConfig { Id = SanitizeIdentifier(id), Name = name };

                if (!Enum.TryParse<PoolGcMode>(gcModeStr, ignoreCase: true, out PoolGcMode parsedGc))
                {
                    parsedGc = PoolGcMode.DatasServer;
                }
                if (!Enum.TryParse<PoolStartMode>(startModeStr, ignoreCase: true, out PoolStartMode parsedStartMode))
                {
                    parsedStartMode = existing.ProcessModel.StartMode;
                }

                existing.Name = string.IsNullOrWhiteSpace(name) ? existing.Id : name.Trim();
                existing.ProcessModel.WorkerCount = Math.Clamp(workerCount, 0, 64);
                if (int.TryParse(form.GetValueOrDefault("minWorkers"), out int minW))
                {
                    existing.ProcessModel.MinWorkers = Math.Clamp(minW, 0, 64);
                }
                if (int.TryParse(form.GetValueOrDefault("maxWorkers"), out int maxW))
                {
                    existing.ProcessModel.MaxWorkers = Math.Clamp(maxW, Math.Max(1, existing.ProcessModel.MinWorkers), 128);
                }
                if (int.TryParse(form.GetValueOrDefault("scaleUpConcurrencyThreshold"), out int scaleThresh))
                {
                    existing.ProcessModel.ScaleUpConcurrencyThreshold = Math.Clamp(scaleThresh, 1, 1000);
                }
                if (int.TryParse(form.GetValueOrDefault("scaleDownIdleSeconds"), out int scaleDownSec))
                {
                    existing.ProcessModel.ScaleDownIdleSeconds = Math.Clamp(scaleDownSec, 5, 86400);
                }
                if (string.Equals(form.GetValueOrDefault("autoscaleFieldsPresent"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    existing.ProcessModel.UseUnixDomainSocket = string.Equals(form.GetValueOrDefault("useUnixDomainSocket"), "true", StringComparison.OrdinalIgnoreCase);
                }
                existing.ProcessModel.GcMode = parsedGc;
                existing.ProcessModel.StartMode = parsedStartMode;
                existing.ProcessModel.IdleTimeoutMinutes = Math.Max(0, idleMin);
                existing.Recycling.MaxMemoryMegabytes = Math.Max(0, maxMem);
                existing.Recycling.PeriodicRestartMinutes = Math.Max(0, recycleMin);
                existing.Recycling.MaxRequests = Math.Max(0, maxReqs);
                existing.Recycling.ScheduledRestartUtcTime = string.IsNullOrWhiteSpace(scheduledTime) ? null : scheduledTime.Trim();

                await _managementService.SavePoolConfigAsync(existing, ct).ConfigureAwait(false);
                return ("pools", existing.Id, null, $"Saved Application Pool '{existing.Id}' to config/pools/{existing.Id}.json.");
            }

            case "/actions/site-command":
            {
                string siteId = form.GetValueOrDefault("siteId", defaultEntity ?? "default-site");
                string cmd = form.GetValueOrDefault("command", "");
                if (string.Equals(cmd, "start", StringComparison.OrdinalIgnoreCase))
                {
                    await _managementService.SetSiteDesiredStateAsync(siteId, DesiredResourceState.Started, ct).ConfigureAwait(false);
                    return ("sites", siteId, null, $"Site '{siteId}' desired state set to Started.");
                }
                if (string.Equals(cmd, "stop", StringComparison.OrdinalIgnoreCase))
                {
                    await _managementService.SetSiteDesiredStateAsync(siteId, DesiredResourceState.Stopped, ct).ConfigureAwait(false);
                    return ("sites", siteId, null, $"Site '{siteId}' desired state set to Stopped.");
                }
                return ("sites", siteId, null, null);
            }

            case "/actions/site-save":
            {
                string id = form.GetValueOrDefault("id", "default-site");
                string name = form.GetValueOrDefault("name", id);
                string poolId = form.GetValueOrDefault("poolId", "default-pool");
                string physicalPath = form.GetValueOrDefault("physicalPath", "");
                string hostingModeStr = form.GetValueOrDefault("hostingMode", "StaticOnly");
                int httpPort = int.TryParse(form.GetValueOrDefault("httpPort"), out int hp) ? hp : 8080;
                string hostHeader = form.GetValueOrDefault("hostHeader", "*");
                int? httpsPort = int.TryParse(form.GetValueOrDefault("httpsPort"), out int hsp) && hsp > 0 ? hsp : null;
                bool hasRoutingInspector = string.Equals(form.GetValueOrDefault("routingFieldsPresent"), "true", StringComparison.OrdinalIgnoreCase);

                ManagementWorkspaceSnapshot snap = await _managementService.GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
                string cleanId = SanitizeIdentifier(id);
                SiteConfig existing = snap.Sites
                    .FirstOrDefault(s => string.Equals(s.Config.Id, cleanId, StringComparison.OrdinalIgnoreCase))?.Config
                    ?? new SiteConfig { Id = cleanId, Name = name };

                if (!Enum.TryParse<WorkloadHostingMode>(hostingModeStr, ignoreCase: true, out WorkloadHostingMode parsedMode))
                {
                    parsedMode = WorkloadHostingMode.StaticOnly;
                }

                existing.Name = string.IsNullOrWhiteSpace(name) ? existing.Id : name.Trim();
                existing.RootApplication.PoolId = string.IsNullOrWhiteSpace(poolId) ? "default-pool" : poolId.Trim();
                existing.RootApplication.PhysicalPath = string.IsNullOrWhiteSpace(physicalPath)
                    ? Path.Combine(_managementService.Layout.RootDirectory, "wwwroot", existing.Id)
                    : physicalPath.Trim();
                existing.RootApplication.HostingMode = parsedMode;

                if (form.TryGetValue("executable", out string? rawExec))
                {
                    existing.RootApplication.Executable = string.IsNullOrWhiteSpace(rawExec) ? null : rawExec.Trim();
                }

                if (form.TryGetValue("arguments", out string? rawArgs))
                {
                    existing.RootApplication.Arguments = string.IsNullOrWhiteSpace(rawArgs) ? null : rawArgs.Trim();
                }

                if (form.TryGetValue("upstreamUrl", out string? rawUpstreamUrl))
                {
                    existing.RootApplication.UpstreamUrl = string.IsNullOrWhiteSpace(rawUpstreamUrl) ? null : rawUpstreamUrl.Trim();
                }

                if (form.TryGetValue("upstreamClusterId", out string? rawClusterId))
                {
                    existing.RootApplication.UpstreamClusterId = string.IsNullOrWhiteSpace(rawClusterId) ? null : rawClusterId.Trim();
                }

                if (form.TryGetValue("canaryClusterId", out string? rawCanaryId))
                {
                    existing.RootApplication.CanaryClusterId = string.IsNullOrWhiteSpace(rawCanaryId) ? null : rawCanaryId.Trim();
                }

                if (int.TryParse(form.GetValueOrDefault("canaryWeightPercent"), out int cw))
                {
                    existing.RootApplication.CanaryWeightPercent = Math.Clamp(cw, 0, 100);
                }

                if (hasRoutingInspector)
                {
                    existing.RootApplication.EnableStreamingPassThrough = string.Equals(form.GetValueOrDefault("enableStreamingPassThrough"), "true", StringComparison.OrdinalIgnoreCase);
                    existing.RootApplication.EnableGrpcWebBridge = string.Equals(form.GetValueOrDefault("enableGrpcWebBridge"), "true", StringComparison.OrdinalIgnoreCase);
                    existing.RootApplication.ServePrecompressedSidecars = string.Equals(form.GetValueOrDefault("servePrecompressedSidecars"), "true", StringComparison.OrdinalIgnoreCase);
                }
                else if (form.TryGetValue("enableStreamingPassThrough", out string? streamVal))
                {
                    existing.RootApplication.EnableStreamingPassThrough = string.Equals(streamVal, "true", StringComparison.OrdinalIgnoreCase);
                }

                bool allowHttp3 = hasRoutingInspector
                    ? string.Equals(form.GetValueOrDefault("allowHttp3"), "true", StringComparison.OrdinalIgnoreCase)
                    : !form.TryGetValue("allowHttp3", out string? h3Val) || string.Equals(h3Val, "true", StringComparison.OrdinalIgnoreCase);

                var bindings = new List<SiteBindingConfig>
                {
                    new()
                    {
                        Protocol = "http",
                        IpAddress = "*",
                        Port = Math.Clamp(httpPort, 1, 65535),
                        Host = string.IsNullOrWhiteSpace(hostHeader) ? "*" : hostHeader.Trim()
                    }
                };

                if (httpsPort is > 0 and <= 65535)
                {
                    bindings.Add(new SiteBindingConfig
                    {
                        Protocol = "https",
                        IpAddress = "*",
                        Port = httpsPort.Value,
                        Host = string.IsNullOrWhiteSpace(hostHeader) || hostHeader == "*" ? "localhost" : hostHeader.Trim(),
                        Tls = new SiteTlsConfig
                        {
                            UseSelfSignedDevelopmentCert = true,
                            AllowHttp2 = true,
                            AllowHttp3 = allowHttp3
                        }
                    });
                }

                existing.Bindings = bindings;
                await _managementService.SaveSiteConfigAsync(existing, ct).ConfigureAwait(false);
                return ("sites", existing.Id, "bindings", $"Saved Site '{existing.Id}' ({existing.RootApplication.HostingMode}) to config/sites/{existing.Id}.json.");
            }

            case "/actions/site-add-vdir":
            {
                string siteId = form.GetValueOrDefault("siteId", "default-site");
                string vpath = form.GetValueOrDefault("virtualPath", "/assets");
                string ppath = form.GetValueOrDefault("physicalPath", "");
                ManagementWorkspaceSnapshot snap = await _managementService.GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
                SiteConfig? site = snap.Sites.FirstOrDefault(s => string.Equals(s.Config.Id, siteId, StringComparison.OrdinalIgnoreCase))?.Config;
                if (site is not null && !string.IsNullOrWhiteSpace(vpath) && !string.IsNullOrWhiteSpace(ppath))
                {
                    site.RootApplication.VirtualDirectories.RemoveAll(v => string.Equals(v.VirtualPath, vpath.Trim(), StringComparison.OrdinalIgnoreCase));
                    site.RootApplication.VirtualDirectories.Add(new VirtualDirectoryConfig
                    {
                        VirtualPath = vpath.StartsWith('/') ? vpath.Trim() : "/" + vpath.Trim(),
                        PhysicalPath = ppath.Trim()
                    });
                    await _managementService.SaveSiteConfigAsync(site, ct).ConfigureAwait(false);
                }
                return ("sites", siteId, "vdirs", $"Added virtual directory '{vpath}' to Site '{siteId}'.");
            }

            case "/actions/site-add-nested-app":
            {
                string siteId = form.GetValueOrDefault("siteId", "default-site");
                string appPath = form.GetValueOrDefault("appPath", "/api");
                string poolId = form.GetValueOrDefault("poolId", "default-pool");
                string ppath = form.GetValueOrDefault("physicalPath", "");
                string hostingMode = form.GetValueOrDefault("hostingMode", "KiteWorkerIpc");
                ManagementWorkspaceSnapshot snap = await _managementService.GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
                SiteConfig? site = snap.Sites.FirstOrDefault(s => string.Equals(s.Config.Id, siteId, StringComparison.OrdinalIgnoreCase))?.Config;
                if (site is not null && !string.IsNullOrWhiteSpace(appPath))
                {
                    if (!Enum.TryParse<WorkloadHostingMode>(hostingMode, ignoreCase: true, out WorkloadHostingMode parsedMode))
                    {
                        parsedMode = WorkloadHostingMode.KiteWorkerIpc;
                    }

                    site.NestedApplications.RemoveAll(a => string.Equals(a.Path, appPath.Trim(), StringComparison.OrdinalIgnoreCase));
                    site.NestedApplications.Add(new SiteApplicationConfig
                    {
                        Path = appPath.StartsWith('/') ? appPath.Trim() : "/" + appPath.Trim(),
                        PoolId = poolId.Trim(),
                        PhysicalPath = string.IsNullOrWhiteSpace(ppath) ? site.RootApplication.PhysicalPath : ppath.Trim(),
                        HostingMode = parsedMode
                    });
                    await _managementService.SaveSiteConfigAsync(site, ct).ConfigureAwait(false);
                }
                return ("sites", siteId, "vdirs", $"Added nested application '{appPath}' -> Pool '{poolId}' on Site '{siteId}'.");
            }

            case "/actions/site-route-save":
            {
                string siteId = form.GetValueOrDefault("siteId", "default-site");
                string routeId = form.GetValueOrDefault("routeId", "");
                string pathPrefix = form.GetValueOrDefault("pathPrefix", "/api");
                string methodsRaw = form.GetValueOrDefault("methods", "");
                string stripPrefix = form.GetValueOrDefault("stripPathPrefix", "");
                string rewritePrefix = form.GetValueOrDefault("rewritePathPrefix", "");
                string targetClusterId = form.GetValueOrDefault("targetClusterId", "");
                string targetPoolId = form.GetValueOrDefault("targetPoolId", "");
                string shadowClusterId = form.GetValueOrDefault("shadowClusterId", "");
                int shadowPct = int.TryParse(form.GetValueOrDefault("shadowPercent"), out int sp) ? Math.Clamp(sp, 0, 100) : 0;
                int abortPct = int.TryParse(form.GetValueOrDefault("abortPercent"), out int ap) ? Math.Clamp(ap, 0, 100) : 0;
                int abortStatus = int.TryParse(form.GetValueOrDefault("abortStatusCode"), out int ac) ? Math.Clamp(ac, 400, 599) : 503;
                bool removeRoute = string.Equals(form.GetValueOrDefault("removeRoute"), "true", StringComparison.OrdinalIgnoreCase);

                ManagementWorkspaceSnapshot snap = await _managementService.GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
                SiteConfig? site = snap.Sites.FirstOrDefault(s => string.Equals(s.Config.Id, siteId, StringComparison.OrdinalIgnoreCase))?.Config;
                if (site is not null)
                {
                    if (removeRoute && !string.IsNullOrWhiteSpace(routeId))
                    {
                        site.Features.Routes.RemoveAll(r => string.Equals(r.Id, routeId, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        string id = string.IsNullOrWhiteSpace(routeId) ? $"route-{site.Features.Routes.Count + 1}" : SanitizeIdentifier(routeId);
                        site.Features.Routes.RemoveAll(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                        site.Features.Routes.Add(new SiteRouteRuleConfig
                        {
                            Id = id,
                            Enabled = true,
                            PathPrefix = string.IsNullOrWhiteSpace(pathPrefix) ? "/" : (pathPrefix.StartsWith('/') ? pathPrefix.Trim() : "/" + pathPrefix.Trim()),
                            Methods = SplitFormList(methodsRaw),
                            TargetClusterId = string.IsNullOrWhiteSpace(targetClusterId) ? null : targetClusterId.Trim(),
                            TargetPoolId = string.IsNullOrWhiteSpace(targetPoolId) ? null : targetPoolId.Trim(),
                            ShadowClusterId = string.IsNullOrWhiteSpace(shadowClusterId) ? null : shadowClusterId.Trim(),
                            ShadowPercent = shadowPct,
                            Transform = new RouteTransformConfig
                            {
                                StripPathPrefix = string.IsNullOrWhiteSpace(stripPrefix) ? null : stripPrefix.Trim(),
                                RewritePathPrefix = string.IsNullOrWhiteSpace(rewritePrefix) ? null : rewritePrefix.Trim()
                            },
                            FaultInjection = new FaultInjectionConfig
                            {
                                Enabled = abortPct > 0,
                                AbortPercent = abortPct,
                                AbortStatusCode = abortStatus
                            }
                        });
                    }
                    site.Features.InheritServerDefaults = false;
                    await _managementService.SaveSiteConfigAsync(site, ct).ConfigureAwait(false);
                }
                return ("sites", siteId, "bindings", removeRoute ? $"Removed route rule '{routeId}' from '{siteId}'." : $"Saved gateway route rule on '{siteId}'.");
            }

            case "/actions/site-slot-deploy":
            {
                string siteId = form.GetValueOrDefault("siteId", "default-site");
                string slotAction = form.GetValueOrDefault("slotAction", "stage");
                string stagingPath = form.GetValueOrDefault("stagingPhysicalPath", "");
                if (string.Equals(slotAction, "swap", StringComparison.OrdinalIgnoreCase))
                {
                    SiteConfig? swapped = await _managementService.SwapSiteSlotAsync(siteId, ct).ConfigureAwait(false);
                    return ("sites", siteId, null, swapped is not null
                        ? $"Zero-downtime slot swapped! Active slot is now '{swapped.RootApplication.ActiveSlot.ToUpperInvariant()}' ({swapped.RootApplication.PhysicalPath})."
                        : "Cannot swap slot: stage a target folder first.");
                }
                SiteConfig? staged = await _managementService.StageSiteSlotAsync(siteId, stagingPath.Trim(), ct).ConfigureAwait(false);
                return ("sites", siteId, null, staged is not null
                    ? $"Staged inactive slot folder '{stagingPath}' on Site '{siteId}'."
                    : $"Site '{siteId}' not found.");
            }

            default:
                return await HandleFeatureAndWorkerPostAsync(path, form, defaultScope, defaultEntity, defaultTab, ct).ConfigureAwait(false);
        }
    }

    private static void RenderPageHtml(
        StringBuilder sb,
        ManagementWorkspaceSnapshot workspace,
        string scope,
        string? entity,
        string? tab,
        string? flashMessage,
        PoolCompositeView? selectedPool,
        SiteCompositeView? selectedSite,
        IReadOnlyList<string> recentSiteLogs,
        IReadOnlyList<FrebTraceRecord> recentFrebTraces,
        IReadOnlyList<InstanceDescriptor>? fleetInstances = null)
    {
        ServerConfig srvCfg = workspace.ServerConfig;
        ServerStatus? srvStatus = workspace.ServerStatus;
        bool isOnline = workspace.IsServerOnline;

        bool isConfigView = scope == "config" || (scope == "server" && string.Equals(tab, "settings", StringComparison.OrdinalIgnoreCase));
        bool isWorkersView = scope == "workers" || (scope == "sites" && string.Equals(tab, "logs", StringComparison.OrdinalIgnoreCase));
        bool isSitesOrPoolsView = !isWorkersView && (scope is "sites" or "pools");
        bool isDashboardView = !isConfigView && !isWorkersView && !isSitesOrPoolsView;

        int onlineSitesCount = workspace.Sites.Count(s => s.EffectiveState == RuntimeResourceState.Started);
        int runningPoolsCount = workspace.Pools.Count(p => p.EffectiveState == RuntimeResourceState.Started);
        int totalWorkersCount = workspace.Pools.Sum(p => p.Status?.ActiveWorkers.Count ?? (isOnline && p.Config.DesiredState == DesiredResourceState.Started ? p.Config.ProcessModel.WorkerCount : 0));
        int httpsBindingsCount = workspace.Sites.Sum(s => s.Config.Bindings.Count(b => string.Equals(b.Protocol, "https", StringComparison.OrdinalIgnoreCase)));
        string listeningPorts = string.Join(", ", workspace.Sites
            .Where(s => s.Config.DesiredState == DesiredResourceState.Started)
            .SelectMany(s => s.Config.Bindings.Select(b => b.Port))
            .Distinct()
            .OrderBy(p => p));
        if (string.IsNullOrEmpty(listeningPorts))
        {
            listeningPorts = "8080, 8443";
        }

        double serverHeapMb = isOnline ? (srvStatus?.ManagedHeapBytes ?? 0) / (1024.0 * 1024.0) : 0.0;
        double uiHeapMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        string uptimeStr = isOnline && srvStatus is not null
            ? FormatUptime(DateTimeOffset.UtcNow - srvStatus.StartedAtUtc)
            : "0d 0h 0m";

        sb.Append("""
            <!DOCTYPE html>
            <html class="dark" lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>Kite Manager Studio v10.0 — Obsidian Telemetry</title>
                <link rel="stylesheet" href="/css/kite-tailwind.css" />
            </head>
            <body class="bg-surface-container-lowest text-on-surface select-none overflow-x-hidden min-h-screen">
            """);

        // ============================================================================================
        // 1. MASTER SHELL HEADER (App Toolbar, Global Search & Scope Switcher; native Win32 frame owns caption controls)
        // ============================================================================================
        sb.Append("""
            <header class="fixed top-0 left-0 right-0 h-14 bg-surface-container-lowest/95 border-b border-surface-variant z-50 flex items-center justify-between px-space-md shadow-lg" onmousedown="if(event.target === this) sendHostMsg('window:drag')">
                <div class="flex items-center gap-space-md pointer-events-auto" onmousedown="sendHostMsg('window:drag')">
                    <div class="w-8 h-8 rounded-lg bg-surface-container-high border border-surface-variant flex items-center justify-center shadow-md" style="box-shadow: 0 0 10px rgba(0,242,254,0.2);">
                        <svg class="svg-icon-lg text-primary-container" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polygon points="12 2 2 7 12 12 22 7 12 2"/><polyline points="2 17 12 22 22 17"/><polyline points="2 12 12 17 22 12"/></svg>
                    </div>
                    <div class="flex flex-col">
                        <span class="font-headline-sm text-primary tracking-tight font-semibold">Kite Manager Studio v10.0</span>
                        <span class="font-label-code-sm text-on-surface-variant">WebView2 Zero-Port • .NET 10 x64 Native</span>
                    </div>
            """);

        if (isOnline)
        {
            string meshSuffix = srvStatus?.MeshRole switch
            {
                InstanceRole.MeshLeader => " • Edge Leader",
                InstanceRole.MeshPeer when !string.IsNullOrWhiteSpace(srvStatus.MeshDirectUrl) => $" • Mesh Peer ({srvStatus.MeshDirectUrl})",
                InstanceRole.MeshPeer => " • Mesh Peer",
                _ => string.Empty
            };
            sb.Append($"""
                    <div class="flex items-center gap-space-xs px-space-sm py-0.5 rounded-full bg-surface-container border border-surface-variant text-tertiary">
                        <span class="dot-emerald animate-pulse"></span>
                        <span class="font-label-code-sm font-medium">{Enc(srvCfg.ServerName)} [Localhost] • Kite.Server Online (PID {srvStatus?.ServerPid}{Enc(meshSuffix)})</span>
                    </div>
                """);
        }
        else
        {
            sb.Append($"""
                    <div class="flex items-center gap-space-xs px-space-sm py-0.5 rounded-full bg-error-container/20 border border-surface-variant text-error">
                        <span class="dot-crimson"></span>
                        <span class="font-label-code-sm font-medium">{Enc(srvCfg.ServerName)} [Localhost] • Kite.Server Offline (Stopped)</span>
                    </div>
                """);
        }

        if (fleetInstances is { Count: > 0 })
        {
            sb.Append($"""
                    <select id="fleetInstanceSwitcher" title="Switch inspected Kite instance" class="obsidian-input py-0.5 px-2 font-label-code-sm text-on-surface bg-surface-container border border-surface-variant rounded" style="max-width: 240px;" onchange="if(this.value) window.location.href='/?scope={Enc(scope)}&home=' + encodeURIComponent(this.value);">
                """);
            bool currentInFleet = fleetInstances.Any(i => string.Equals(
                Path.GetFullPath(i.KiteHomePath),
                Path.GetFullPath(workspace.KiteHomePath),
                StringComparison.OrdinalIgnoreCase));
            if (!currentInFleet)
            {
                sb.Append($"""<option value="{Enc(workspace.KiteHomePath)}" selected="selected">Current: {Enc(srvCfg.ServerName)} (Offline)</option>""");
            }
            foreach (InstanceDescriptor inst in fleetInstances)
            {
                bool isSelected = string.Equals(
                    Path.GetFullPath(inst.KiteHomePath),
                    Path.GetFullPath(workspace.KiteHomePath),
                    StringComparison.OrdinalIgnoreCase);
                string selAttr = isSelected ? " selected=\"selected\"" : string.Empty;
                string roleTag = inst.Role switch
                {
                    InstanceRole.MeshLeader => "Leader",
                    InstanceRole.MeshPeer => "Peer",
                    _ => "Solo"
                };
                string hostsTag = inst.HostedDomains.Count > 0 ? $" [{string.Join(", ", inst.HostedDomains.Take(2))}]" : string.Empty;
                sb.Append($"""<option value="{Enc(inst.KiteHomePath)}"{selAttr}>[{roleTag}] {Enc(inst.ServerName)} (PID {inst.Pid}){Enc(hostsTag)}</option>""");
            }
            sb.Append("</select>");
        }

        sb.Append("""
                </div>
                <div class="flex items-center gap-space-md flex-1 max-w-md mx-space-xl pointer-events-auto">
                    <div class="relative w-full">
                        <svg class="svg-icon-sm absolute text-on-surface-variant" style="left: 10px; top: 50%; transform: translateY(-50%);" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>
                        <input id="globalSearchInput" type="text" class="obsidian-input pl-8 pr-16 py-1" placeholder="Filter sites, pools, workers, or config directives..." oninput="filterGlobalElements(this.value)" />
                        <span class="absolute font-badge-label bg-surface-container-high px-1.5 py-0.5 rounded text-on-surface-variant" style="right: 8px; top: 50%; transform: translateY(-50%);">CTRL+K</span>
                    </div>
                </div>
                <div class="flex items-center gap-space-sm pointer-events-auto">
                    <a href="/?scope=server&tab=overview" class="px-2 py-1 rounded bg-surface-container hover:bg-surface-container-high text-on-surface-variant hover:text-primary font-label-code-sm">Server</a>
                    <a href="/?scope=pools" class="px-2 py-1 rounded bg-surface-container hover:bg-surface-container-high text-on-surface-variant hover:text-secondary font-label-code-sm">Application Pools</a>
                    <a href="/?scope=sites" class="px-2 py-1 rounded bg-surface-container hover:bg-surface-container-high text-on-surface-variant hover:text-tertiary-container font-label-code-sm">Sites</a>
                </div>
            </header>
            """);

        // ============================================================================================
        // 2. LEFT NAVIGATION & INVENTORY SIDEBAR (4 Screens + Live Sites & App Pools Tree + Host Stats)
        // ============================================================================================
        string navDashboardCls = isDashboardView
            ? "flex items-center gap-space-sm px-space-sm py-1.5 rounded-lg bg-primary-container text-on-primary-container font-semibold font-body-md"
            : "flex items-center gap-space-sm px-space-sm py-1.5 rounded-lg text-on-surface-variant hover:bg-surface-container-high hover:text-on-surface font-body-md";
        string navSitesPoolsCls = isSitesOrPoolsView
            ? "flex items-center gap-space-sm px-space-sm py-1.5 rounded-lg bg-primary-container text-on-primary-container font-semibold font-body-md"
            : "flex items-center gap-space-sm px-space-sm py-1.5 rounded-lg text-on-surface-variant hover:bg-surface-container-high hover:text-on-surface font-body-md";
        string navConfigCls = isConfigView
            ? "flex items-center gap-space-sm px-space-sm py-1.5 rounded-lg bg-primary-container text-on-primary-container font-semibold font-body-md"
            : "flex items-center gap-space-sm px-space-sm py-1.5 rounded-lg text-on-surface-variant hover:bg-surface-container-high hover:text-on-surface font-body-md";
        string navWorkersCls = isWorkersView
            ? "flex items-center gap-space-sm px-space-sm py-1.5 rounded-lg bg-primary-container text-on-primary-container font-semibold font-body-md"
            : "flex items-center gap-space-sm px-space-sm py-1.5 rounded-lg text-on-surface-variant hover:bg-surface-container-high hover:text-on-surface font-body-md";

        sb.Append($"""
            <aside class="fixed left-0 top-14 bottom-8 w-72 bg-surface-container-low/95 border-b border-surface-variant z-40 flex flex-col justify-between overflow-y-auto" style="border-right: 1px solid rgba(255,255,255,0.07);">
                <div class="p-space-sm">
                    <div class="flex items-center gap-space-sm px-space-sm py-1.5 rounded bg-surface-container border border-surface-variant text-on-surface mb-space-sm">
                        <svg class="svg-icon text-primary-container" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="2" y="2" width="20" height="8" rx="2"/><rect x="2" y="14" width="20" height="8" rx="2"/><line x1="6" y1="6" x2="6.01" y2="6"/><line x1="6" y1="18" x2="6.01" y2="18"/></svg>
                        <div class="flex flex-col truncate">
                            <span class="font-label-code-md text-on-surface font-semibold truncate">{Enc(srvCfg.ServerName)}</span>
                            <span class="font-label-code-sm text-on-surface-variant truncate">Kite Kestrel 10.0 • Zero-Alloc</span>
                        </div>
                    </div>
                    <nav class="flex flex-col gap-0.5">
                        <a href="/?scope=server" class="{navDashboardCls}" data-path="dashboard-and-telemetry">
                            <svg class="svg-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 20v-6M6 20V10M18 20V4"/></svg>
                            <span>Dashboard &amp; Telemetry</span>
                        </a>
                        <a href="/?scope=sites" class="{navSitesPoolsCls}" data-path="sites-and-app-pools">
                            <svg class="svg-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="3" width="7" height="7"/><rect x="14" y="3" width="7" height="7"/><rect x="14" y="14" width="7" height="7"/><rect x="3" y="14" width="7" height="7"/></svg>
                            <span>Sites &amp; App Pools</span>
                        </a>
                        <a href="/?scope=server&tab=settings" class="{navConfigCls}" data-path="configuration-and-features">
                            <svg class="svg-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><line x1="4" y1="21" x2="4" y2="14"/><line x1="4" y1="10" x2="4" y2="3"/><line x1="12" y1="21" x2="12" y2="12"/><line x1="12" y1="8" x2="12" y2="3"/><line x1="20" y1="21" x2="20" y2="16"/><line x1="20" y1="12" x2="20" y2="3"/></svg>
                            <span>Configuration &amp; Features</span>
                        </a>
                        <a href="/?scope=workers" class="{navWorkersCls}" data-path="worker-processes-and-tracing">
                            <svg class="svg-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="22 12 18 12 15 21 9 3 6 12 2 12"/></svg>
                            <span>Worker Processes &amp; Tracing</span>
                        </a>
                    </nav>

                    <div class="mt-space-md pt-space-xs" style="border-top: 1px solid rgba(255,255,255,0.05);">
                        <div class="flex items-center justify-between px-space-sm py-0.5 text-on-surface-variant font-badge-label uppercase tracking-wider">
                            <span>Sites Inventory</span>
                            <span class="font-label-code-sm text-primary">{onlineSitesCount} Online</span>
                        </div>
                        <div class="flex flex-col gap-0.5 mt-1">
            """);

        foreach (SiteCompositeView s in workspace.Sites)
        {
            bool sOnline = s.EffectiveState == RuntimeResourceState.Started;
            string dotCls = sOnline ? "dot-emerald" : "dot-crimson";
            int firstPort = s.Config.Bindings.FirstOrDefault()?.Port ?? 8080;
            sb.Append($"""
                            <a href="/?scope=sites&entity={Uri.EscapeDataString(s.Config.Id)}" class="flex items-center justify-between px-space-sm py-1 rounded hover:bg-surface-container-high text-on-surface font-label-code-sm">
                                <span class="flex items-center gap-1.5 truncate"><span class="{dotCls}"></span><span class="truncate">{Enc(s.Config.Name)}</span></span>
                                <span class="text-on-surface-variant font-label-code-sm">:{firstPort}</span>
                            </a>
                """);
        }

        sb.Append($"""
                        </div>
                    </div>

                    <div class="mt-space-md pt-space-xs" style="border-top: 1px solid rgba(255,255,255,0.05);">
                        <div class="flex items-center justify-between px-space-sm py-0.5 text-on-surface-variant font-badge-label uppercase tracking-wider">
                            <span>Application Pools</span>
                            <span class="font-label-code-sm text-secondary">{runningPoolsCount} Running</span>
                        </div>
                        <div class="flex flex-col gap-0.5 mt-1">
            """);

        foreach (PoolCompositeView p in workspace.Pools)
        {
            bool pOnline = p.EffectiveState == RuntimeResourceState.Started;
            string dotCls = pOnline ? "dot-cyan" : "dot-crimson";
            sb.Append($"""
                            <a href="/?scope=pools&entity={Uri.EscapeDataString(p.Config.Id)}" class="flex items-center justify-between px-space-sm py-1 rounded hover:bg-surface-container-high text-on-surface font-label-code-sm">
                                <span class="flex items-center gap-1.5 truncate"><span class="{dotCls}"></span><span class="truncate">{Enc(p.Config.Name)}</span></span>
                                <span class="text-on-surface-variant font-label-code-sm">v{p.Config.RecycleVersion}</span>
                            </a>
                """);
        }

        sb.Append($"""
                        </div>
                    </div>
                </div>

                <div class="p-space-sm m-space-xs rounded bg-surface-container-lowest border border-surface-variant text-on-surface">
                    <div class="flex items-center justify-between mb-space-xs">
                        <span class="font-badge-label uppercase text-on-surface-variant">Host Resources</span>
                        <span class="font-label-code-sm text-tertiary-container">{(isOnline ? "Nominal" : "Standby")}</span>
                    </div>
                    <div class="grid grid-cols-2 gap-space-xs font-label-code-sm text-on-surface-variant">
                        <div class="flex flex-col">
                            <span class="text-on-surface-variant/70">Logical CPUs</span>
                            <span class="text-on-surface font-semibold">{Environment.ProcessorCount} vCPUs (x64)</span>
                        </div>
                        <div class="flex flex-col">
                            <span class="text-on-surface-variant/70">Server Heap</span>
                            <span class="text-on-surface font-semibold">{serverHeapMb:F2} MB</span>
                        </div>
                        <div class="flex flex-col">
                            <span class="text-on-surface-variant/70">Kite Uptime</span>
                            <span class="text-on-surface font-semibold">{uptimeStr}</span>
                        </div>
                        <div class="flex flex-col">
                            <span class="text-on-surface-variant/70">WebView2 Bridge</span>
                            <span class="text-primary-container font-semibold">{uiHeapMb:F1} MB RSS</span>
                        </div>
                    </div>
                </div>
            </aside>
            """);

        // ============================================================================================
        // 3. MAIN CENTRAL TELEMETRY & CONTROL CANVAS
        // ============================================================================================
        sb.Append("""
            <div class="pl-72">
                <main class="pt-14 pb-10 min-h-screen w-full px-space-lg bg-surface-container-lowest">
                    <div class="flex flex-col w-full gap-space-md pt-space-md">
            """);

        if (!string.IsNullOrWhiteSpace(flashMessage))
        {
            sb.Append($"""
                        <div class="p-space-sm px-space-md rounded-lg bg-surface-container border border-surface-variant laser-edge-cyan flex items-center justify-between font-label-code-md text-primary">
                            <span class="flex items-center gap-2"><span class="dot-cyan"></span>{Enc(flashMessage)}</span>
                            <span class="font-badge-label text-tertiary-container">ATOMIC SYNC OK</span>
                        </div>
                """);
        }

        if (isDashboardView)
        {
            RenderDashboardAndTelemetryView(sb, workspace, recentSiteLogs);
        }
        else if (isSitesOrPoolsView)
        {
            RenderSitesAndAppPoolsView(sb, workspace, scope, tab, selectedSite, selectedPool);
        }
        else if (isConfigView)
        {
            RenderConfigurationMatrixView(sb, workspace, selectedSite);
        }
        else
        {
            RenderWorkerProcessesAndTracingView(sb, workspace, selectedSite, recentSiteLogs, recentFrebTraces);
        }

        sb.Append("""
                    </div>
                </main>
            </div>
            """);

        // ============================================================================================
        // 4. MASTER SHELL FOOTER (Global Socket, Certs, Protocol & Quick Daemon Control)
        // ============================================================================================
        sb.Append($"""
            <footer class="fixed bottom-0 left-0 right-0 h-8 bg-surface-container-lowest/95 border-t border-surface-variant z-50 flex items-center justify-between px-space-md text-on-surface-variant font-label-code-sm">
                <div class="flex items-center gap-space-md">
                    <span class="flex items-center gap-1.5">
                        <span class="{(isOnline ? "dot-emerald" : "dot-crimson")}"></span>
                        Socket: {(isOnline ? $"Kite.Server Connected (PID {srvStatus?.ServerPid})" : "Kite.Server Offline")}
                    </span>
                    <span class="flex items-center gap-1.5">
                        <svg class="svg-icon-sm text-primary" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>
                        SSL Certs: {httpsBindingsCount} Valid (TLS 1.3)
                    </span>
                    <span class="flex items-center gap-1.5">
                        <svg class="svg-icon-sm text-secondary" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2"/></svg>
                        {(srvCfg.Transport.EnableHttp3 ? "HTTP/3 (QUIC) + HTTP/2 &amp; WebSockets: Enabled" : "HTTP/2 &amp; WebSockets: Enabled (HTTP/3 Off)")}
                    </span>
                    <span class="text-on-surface-variant/70">KITE_HOME: {Enc(workspace.KiteHomePath)}</span>
                </div>
                <div class="flex items-center gap-space-md">
                    <span class="text-on-surface-variant">Ports: <strong class="text-primary">{listeningPorts}</strong> [{(isOnline ? "LISTENING" : "STOPPED")}]</span>
                    <form method="post" action="/actions/server-daemon" style="display:inline;">
                        <input type="hidden" name="returnScope" value="{Enc(scope)}" />
                        <button type="submit" name="command" value="start" class="flex items-center gap-1 px-2 py-0.5 rounded bg-surface-container hover:bg-surface-container-high text-primary hover:text-on-surface">
                            ↻ {(isOnline ? "Sync Engine" : "Start Kite.Server")}
                        </button>
                    </form>
                </div>
            </footer>
            """);

        sb.Append("""
            <script>
                function sendHostMsg(msg) {
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage(msg);
                    }
                }
                function filterGlobalElements(query) {
                    const q = (query || '').toLowerCase();
                    document.querySelectorAll('[data-filterable]').forEach(el => {
                        const text = el.textContent.toLowerCase();
                        el.style.display = text.includes(q) ? '' : 'none';
                    });
                }
                function copyToClipboard(text) {
                    if (navigator.clipboard) {
                        navigator.clipboard.writeText(text);
                    }
                }
                function toggleModal(id, show) {
                    const el = document.getElementById(id);
                    if (!el) return;
                    if (show) {
                        if (el.childElementCount === 0 && id.startsWith('modal-')) {
                            const ctx = document.getElementById('kite-modals-ctx');
                            const site = ctx ? (ctx.getAttribute('data-site') || '') : '';
                            const xhr = new XMLHttpRequest();
                            xhr.open('GET', '/partials/config-modals?siteId=' + encodeURIComponent(site), false);
                            xhr.send(null);
                            if (xhr.status === 200) {
                                const tmp = document.createElement('div');
                                tmp.innerHTML = xhr.responseText;
                                tmp.querySelectorAll('[id^="modal-"]').forEach(m => {
                                    const target = document.getElementById(m.id);
                                    if (target) {
                                        target.innerHTML = '';
                                        while (m.firstChild) target.appendChild(m.firstChild);
                                    }
                                });
                            }
                        }
                        el.classList.remove('hidden'); el.classList.add('flex');
                    }
                    else { el.classList.add('hidden'); el.classList.remove('flex'); }
                }
                window.addEventListener('keydown', function(e) {
                    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
                        e.preventDefault();
                        const input = document.getElementById('siteSearchInput') || document.getElementById('globalSearchInput');
                        if (input) { input.focus(); input.select(); }
                    }
                });
            </script>
            </body>
            </html>
            """);
    }

    // ================================================================================================
    // SCREEN 1: DASHBOARD & TELEMETRY (design/DesktopWinForms/server_dashboard_telemetry)
    // ================================================================================================
    private static void RenderDashboardAndTelemetryView(
        StringBuilder sb,
        ManagementWorkspaceSnapshot workspace,
        IReadOnlyList<string> recentLogs)
    {
        ServerConfig srvCfg = workspace.ServerConfig;
        ServerStatus? srvStatus = workspace.ServerStatus;
        bool isOnline = workspace.IsServerOnline;

        long totalReqs = isOnline ? (srvStatus?.TotalRequestsProcessed ?? 0) : 0;
        long activeConns = isOnline ? (srvStatus?.ActiveConnections ?? 0) : 0;
        int activeBindings = isOnline ? (srvStatus?.ActiveBindingsCount ?? 0) : 0;
        long totalBytesSent = isOnline ? (srvStatus?.TotalBytesSent ?? 0) : 0;
        double heapMb = isOnline ? (srvStatus?.ManagedHeapBytes ?? 0) / (1024.0 * 1024.0) : 0.0;
        int gen0 = isOnline ? (srvStatus?.Gen0Collections ?? 0) : 0;
        int gen1 = isOnline ? (srvStatus?.Gen1Collections ?? 0) : 0;
        int gen2 = isOnline ? (srvStatus?.Gen2Collections ?? 0) : 0;
        long totalCacheHits = workspace.Sites.Sum(s => s.Status?.CacheHits ?? 0);

        sb.Append($"""
            <section class="flex flex-col md:flex-row md:items-center justify-between gap-space-md p-space-md rounded-xl obsidian-card shadow-lg relative overflow-hidden">
                <div class="flex items-center gap-space-md min-w-0 relative z-10">
                    <div class="w-10 h-10 rounded-lg bg-surface-container-highest flex items-center justify-center shrink-0">
                        <svg class="svg-icon-lg text-primary-container" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="22 12 18 12 15 21 9 3 6 12 2 12"/></svg>
                    </div>
                    <div class="flex flex-col min-w-0">
                        <div class="flex items-center gap-space-sm">
                            <span class="font-headline-lg text-on-surface tracking-tight truncate">Server Node: {Enc(srvCfg.ServerName)} — Global Telemetry</span>
                            <span class="inline-flex items-center gap-1.5 px-space-sm py-0.5 rounded-full bg-surface-container-highest font-label-code-sm {(isOnline ? "text-tertiary" : "text-error")}">
                                <span class="{(isOnline ? "dot-emerald animate-pulse" : "dot-crimson")}"></span>
                                {(isOnline ? "LIVE FS TELEMETRY" : "DAEMON STOPPED")}
                            </span>
                        </div>
                        <span class="font-label-code-sm text-on-surface-variant truncate">Kestrel Multi-Binding Engine • Server GC + DATAS • Polling @ {srvCfg.StatusPublishIntervalMilliseconds}ms</span>
                    </div>
                </div>
                <div class="flex flex-wrap items-center gap-space-sm relative z-10">
                    <div class="flex items-center bg-surface-container-lowest p-0.5 rounded-lg border border-surface-variant">
                        <a href="/?scope=server&tab=overview" class="px-space-sm py-1 rounded bg-primary-container text-on-primary-container font-label-code-sm font-semibold">Live</a>
                        <a href="/?scope=server&tab=settings" class="px-space-sm py-1 rounded text-on-surface-variant hover:text-on-surface font-label-code-sm">Global Server Settings</a>
                    </div>
                    <div class="flex items-center gap-1.5">
                        <form method="post" action="/actions/pool-recycle-all" style="display:inline;">
                            <input type="hidden" name="returnScope" value="server" />
                            <button type="submit" class="cyber-btn-secondary flex items-center gap-1.5 px-space-sm py-1 rounded font-body-sm">
                                <span class="text-secondary">↻</span>
                                <span>Recycle All Pools</span>
                            </button>
                        </form>
                        <form method="post" action="/actions/server-daemon" style="display:inline;">
                            <input type="hidden" name="returnScope" value="server" />
            """);

        if (isOnline)
        {
            sb.Append("""
                            <button type="submit" name="command" value="stop" class="cyber-btn-danger flex items-center gap-1.5 px-space-sm py-1 rounded font-body-sm">
                                <span>⏻ Stop Kite.Server</span>
                            </button>
                """);
        }
        else
        {
            sb.Append("""
                            <button type="submit" name="command" value="start" class="cyber-btn-primary flex items-center gap-1.5 px-space-sm py-1 rounded font-body-sm">
                                <span>▶ Start Kite.Server</span>
                            </button>
                """);
        }

        sb.Append($"""
                        </form>
                    </div>
                </div>
            </section>

            <!-- Top 4 KPI Stat Cards -->
            <section class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-space-md">
                <div class="p-space-md rounded-xl obsidian-card shadow-md flex flex-col justify-between">
                    <div class="flex items-center justify-between">
                        <span class="font-badge-label uppercase text-on-surface-variant">Total Requests Processed</span>
                        <span class="font-label-code-sm text-tertiary-container bg-surface-container-highest px-1.5 py-0.5 rounded">0.0 B/op Route</span>
                    </div>
                    <div class="my-space-sm flex items-baseline justify-between">
                        <span id="kpi-total-reqs" class="font-headline-xl text-primary font-bold font-mono">{totalReqs:N0}</span>
                        <span class="font-label-code-md text-on-surface-variant">Active: <span id="kpi-active-conns">{activeConns}</span></span>
                    </div>
                    <div class="h-8 w-full mt-1">
                        <svg class="w-full h-full text-primary-container" fill="none" preserveAspectRatio="none" viewBox="0 0 160 36">
                            <path d="M0,26 Q20,28 40,18 T80,20 T120,10 T160,8" stroke="currentColor" stroke-linecap="round" stroke-width="2"></path>
                            <path d="M0,26 Q20,28 40,18 T80,20 T120,10 T160,8 L160,36 L0,36 Z" fill="currentColor" fill-opacity="0.10"></path>
                        </svg>
                    </div>
                    <div class="flex items-center justify-between mt-2 pt-1 font-label-code-sm text-on-surface-variant">
                        <span>Worker Queues: <strong class="text-on-surface">0 blocked</strong></span>
                        <span>Cache Hits: <strong class="text-tertiary-container">{totalCacheHits}</strong></span>
                    </div>
                </div>

                <div class="p-space-md rounded-xl obsidian-card shadow-md flex flex-col justify-between">
                    <div class="flex items-center justify-between">
                        <span class="font-badge-label uppercase text-on-surface-variant">Active Bindings</span>
                        <span class="font-label-code-sm text-on-surface-variant">{Environment.ProcessorCount} vCPUs Logical</span>
                    </div>
                    <div class="my-space-sm flex items-baseline justify-between">
                        <span class="font-headline-xl text-on-surface font-bold font-mono">{activeBindings} <span class="text-primary-container text-headline-sm">Listeners</span></span>
                        <span class="font-label-code-sm text-tertiary-container bg-surface-container-high px-1.5 py-0.5 rounded">Dual-Stack IPv4/v6</span>
                    </div>
                    <div class="grid grid-cols-8 gap-1 h-3 my-1">
                        <div class="bg-primary-container/20 rounded-sm"></div>
                        <div class="bg-primary-container rounded-sm"></div>
                        <div class="bg-primary-container/20 rounded-sm"></div>
                        <div class="bg-primary-container/15 rounded-sm"></div>
                        <div class="bg-primary-container rounded-sm"></div>
                        <div class="bg-primary-container/20 rounded-sm"></div>
                        <div class="bg-primary-container/15 rounded-sm"></div>
                        <div class="bg-primary-container rounded-sm"></div>
                    </div>
                    <div class="flex items-center justify-between mt-2 pt-1 font-label-code-sm text-on-surface-variant">
                        <span>Hosted Sites: <strong class="text-primary">{workspace.Sites.Count}</strong></span>
                        <span>Max Conn: <strong class="text-on-surface">{srvCfg.MaxConcurrentConnections}</strong></span>
                    </div>
                </div>

                <div class="p-space-md rounded-xl obsidian-card shadow-md flex flex-col justify-between">
                    <div class="flex items-center justify-between">
                        <span class="font-badge-label uppercase text-on-surface-variant">.NET 10 Managed Heap</span>
                        <span class="font-label-code-sm text-secondary bg-surface-container-highest px-1.5 py-0.5 rounded">GC: {gen0}/{gen1}/{gen2}</span>
                    </div>
                    <div class="my-space-sm flex items-baseline justify-between">
                        <span class="font-headline-xl text-on-surface font-bold font-mono"><span id="kpi-heap-mb">{heapMb:F2}</span> <span class="text-secondary text-headline-sm">MB</span></span>
                        <span class="font-label-code-sm text-on-surface-variant">DATAS Server GC</span>
                    </div>
                    <div class="w-full bg-surface-container-highest h-2 rounded-full overflow-hidden my-1">
                        <div class="h-full bg-secondary rounded-full" style="width: {Math.Clamp(heapMb * 2.5, 8, 95):F0}%"></div>
                    </div>
                    <div class="flex items-center justify-between mt-2 pt-1 font-label-code-sm text-on-surface-variant">
                        <span>LOH Allocations: <strong class="text-secondary">0 B</strong></span>
                        <span>ArrayPool: <strong class="text-on-surface">8 KB Slabs</strong></span>
                    </div>
                </div>

                <div class="p-space-md rounded-xl obsidian-card shadow-md flex flex-col justify-between">
                    <div class="flex items-center justify-between">
                        <span class="font-badge-label uppercase text-on-surface-variant">Network Egress &amp; Latency</span>
                        <span id="kpi-rps" class="font-label-code-sm text-on-surface-variant">{(srvStatus?.RequestsPerSecond ?? 0.0):F1} rps</span>
                    </div>
                    <div class="my-space-sm flex flex-col gap-0.5">
                        <div class="flex items-center justify-between font-label-code-lg text-on-surface">
                            <span class="text-primary-container">↑ {(srvStatus?.MbpsOut ?? 0.0):F2} Mbps</span>
                            <span class="font-label-code-sm text-on-surface-variant">Out ({FormatBytes(totalBytesSent)})</span>
                        </div>
                        <div class="flex items-center justify-between font-label-code-lg text-on-surface">
                            <span class="text-secondary">↓ {(srvStatus?.MbpsIn ?? 0.0):F2} Mbps</span>
                            <span class="font-label-code-sm text-on-surface-variant">Avg {(srvStatus?.AvgLatencyMs ?? 0.0):F1}ms / p99 {(srvStatus?.P99LatencyMs ?? 0.0):F1}ms</span>
                        </div>
                    </div>
                    <div class="h-3 w-full flex items-center gap-0.5 mt-1">
                        <div class="h-1.5 flex-1 bg-primary-container rounded-sm"></div>
                        <div class="h-1.5 w-16 bg-secondary rounded-sm"></div>
                        <div class="h-1.5 w-8 bg-surface-container-highest rounded-sm"></div>
                    </div>
                    <div class="flex items-center justify-between mt-2 pt-1 font-label-code-sm text-on-surface-variant">
                        <span>Pools: <strong class="text-on-surface">{workspace.Pools.Count}</strong></span>
                        <span>Cache Hit: <strong class="text-tertiary-container">{(srvStatus?.CacheHitRatioPercent ?? 0.0):F1}%</strong></span>
                    </div>
                </div>
            </section>

            <!-- Middle Telemetry Visualizations Grid (Dynamic 60s SVG Area Chart + Core Services) -->
            <section class="grid grid-cols-1 lg:grid-cols-12 gap-space-md">
                <div class="lg:col-span-8 p-space-md rounded-xl obsidian-card shadow-lg flex flex-col justify-between">
                    <div class="flex flex-wrap items-center justify-between gap-space-sm pb-space-sm">
                        <div class="flex flex-col">
                            <span class="font-headline-sm text-on-surface font-semibold">HTTP Response Codes &amp; Traffic Distribution</span>
                            <span class="font-label-code-sm text-on-surface-variant">60s rolling telemetry across {activeBindings} Kestrel host binding(s)</span>
                        </div>
                        <div class="flex items-center gap-space-sm font-label-code-sm">
                            <span class="flex items-center gap-1.5 text-tertiary"><span class="dot-emerald"></span>2xx OK</span>
                            <span class="flex items-center gap-1.5 text-primary-container"><span class="dot-cyan"></span>3xx Cache/Redir</span>
                            <span class="flex items-center gap-1.5 text-secondary"><span class="dot-violet"></span>4xx Warn</span>
                            <span class="flex items-center gap-1.5 text-error"><span class="dot-crimson"></span>5xx Err</span>
                        </div>
                    </div>
                    <div class="relative w-full h-44 my-space-xs bg-surface-container-lowest/80 rounded-lg p-space-sm overflow-hidden flex flex-col justify-between border border-surface-variant">
                        {BuildTelemetrySvgChart(srvStatus)}
                        <div class="flex justify-between items-center font-label-code-sm text-on-surface-variant/50">
                            <span>-60s</span><span>-45s</span><span>-30s</span><span>-15s</span><span class="text-primary-container font-semibold">0s ({(srvStatus?.RequestsPerSecond ?? 0.0):F1} rps Live)</span>
                        </div>
                    </div>
                    <div class="grid grid-cols-3 gap-space-sm pt-space-xs font-label-code-sm text-on-surface-variant">
                        <div class="flex flex-col"><span>Route Lookup Complexity</span><span class="text-on-surface font-semibold">O(1) FrozenDictionary (0 B/op)</span></div>
                        <div class="flex flex-col"><span>99th Pct Latency</span><span class="text-on-surface font-semibold">{(srvStatus?.P99LatencyMs ?? 0.0):F2} ms (Avg {(srvStatus?.AvgLatencyMs ?? 0.0):F2} ms)</span></div>
                        <div class="flex flex-col"><span>In-Memory Static Cache</span><span class="text-primary-container font-semibold">{totalCacheHits} Hits ({(srvStatus?.CacheHitRatioPercent ?? 0.0):F1}% ratio)</span></div>
                    </div>
                </div>

                <div class="lg:col-span-4 p-space-md rounded-xl obsidian-card shadow-lg flex flex-col justify-between">
                    <div class="flex items-center justify-between pb-space-xs">
                        <div class="flex flex-col">
                            <span class="font-headline-sm text-on-surface font-semibold">Core Kite Subsystems</span>
                            <span class="font-label-code-sm text-on-surface-variant">Kestrel &amp; Filesystem Control Plane</span>
                        </div>
                        <span class="font-badge-label px-1.5 py-0.5 rounded bg-surface-container-highest text-primary-container">v10.0</span>
                    </div>
                    <div class="flex flex-col gap-2 my-space-xs">
                        <div class="p-space-sm rounded-lg bg-surface-container-low border border-surface-variant flex items-center justify-between">
                            <div class="flex flex-col min-w-0">
                                <div class="flex items-center gap-1.5">
                                    <span class="{(isOnline ? "dot-emerald" : "dot-crimson")}"></span>
                                    <span class="font-label-code-md text-on-surface font-semibold">Kite.Server Engine</span>
                                </div>
                                <span class="font-label-code-sm text-on-surface-variant">PID {(srvStatus?.ServerPid ?? 0)} • Kestrel Multi-Binding</span>
                            </div>
                            <span class="px-1.5 py-0.5 rounded {(isOnline ? "bg-tertiary/10 text-tertiary-container" : "bg-error-container/20 text-error")} font-badge-label">{(isOnline ? "RUNNING" : "STOPPED")}</span>
                        </div>
                        <div class="p-space-sm rounded-lg bg-surface-container-low border border-surface-variant flex items-center justify-between">
                            <div class="flex flex-col min-w-0">
                                <div class="flex items-center gap-1.5">
                                    <span class="dot-cyan"></span>
                                    <span class="font-label-code-md text-on-surface font-semibold">ApplicationPoolSupervisor</span>
                                </div>
                                <span class="font-label-code-sm text-on-surface-variant">{workspace.Pools.Count} Pools • Overlapped Recycle</span>
                            </div>
                            <span class="px-1.5 py-0.5 rounded bg-primary-container/10 text-primary-container font-badge-label">ACTIVE</span>
                        </div>
                        <div class="p-space-sm rounded-lg bg-surface-container-low border border-surface-variant flex items-center justify-between">
                            <div class="flex flex-col min-w-0">
                                <div class="flex items-center gap-1.5">
                                    <span class="dot-emerald"></span>
                                    <span class="font-label-code-md text-on-surface font-semibold">FileSystemConfigMonitor</span>
                                </div>
                                <span class="font-label-code-sm text-on-surface-variant">SHA-256 Debounced • Atomic JSON</span>
                            </div>
                            <span class="px-1.5 py-0.5 rounded bg-tertiary/10 text-tertiary-container font-badge-label">SYNCED</span>
                        </div>
                    </div>
                    <div class="pt-space-xs flex items-center justify-between font-label-code-sm text-on-surface-variant">
                        <span class="text-tertiary-container">✓ All Listeners Synchronized</span>
                        <a href="/?scope=server&tab=settings" class="text-primary hover:underline">Configure →</a>
                    </div>
                </div>
            </section>

            <!-- Bottom Split Section: Application Pools & Hosted Sites Summary + Recent W3C Trace Terminal -->
            <section class="grid grid-cols-1 lg:grid-cols-12 gap-space-md">
                <div class="lg:col-span-6 p-space-md rounded-xl obsidian-card shadow-lg flex flex-col justify-between">
                    <div class="flex items-center justify-between pb-space-sm">
                        <div class="flex flex-col">
                            <span class="font-headline-sm text-on-surface font-semibold">Application Pools &amp; Hosted Sites</span>
                            <span class="font-label-code-sm text-on-surface-variant">Live state reconciliation across pools &amp; virtual hosts</span>
                        </div>
                        <a href="/?scope=pools" class="text-primary hover:underline font-label-code-sm">Manage Pools →</a>
                    </div>
                    <div class="w-full overflow-x-auto rounded-lg bg-surface-container-lowest border border-surface-variant mb-space-sm">
                        <table class="w-full text-left font-label-code-sm">
                            <thead class="bg-surface-container-high text-on-surface-variant font-badge-label uppercase">
                                <tr>
                                    <th class="py-1.5 px-space-sm">Pool</th>
                                    <th class="py-1.5 px-space-sm">State</th>
                                    <th class="py-1.5 px-space-sm">GC Mode</th>
                                    <th class="py-1.5 px-space-sm">Workers</th>
                                    <th class="py-1.5 px-space-sm text-right">Recycle Ver</th>
                                </tr>
                            </thead>
                            <tbody>
            """);

        foreach (PoolCompositeView p in workspace.Pools)
        {
            bool started = p.EffectiveState == RuntimeResourceState.Started;
            sb.Append($"""
                                <tr class="hover:bg-surface-container-high/40" data-filterable>
                                    <td class="py-1.5 px-space-sm font-semibold text-primary"><a href="/?scope=pools&entity={Uri.EscapeDataString(p.Config.Id)}">{Enc(p.Config.Name)}</a></td>
                                    <td class="py-1.5 px-space-sm {(started ? "text-tertiary-container" : "text-error")} font-semibold">{p.EffectiveState}</td>
                                    <td class="py-1.5 px-space-sm text-on-surface-variant">{p.Config.ProcessModel.GcMode}</td>
                                    <td class="py-1.5 px-space-sm text-on-surface">{p.Config.ProcessModel.WorkerCount}</td>
                                    <td class="py-1.5 px-space-sm text-right text-secondary font-semibold">v{p.Config.RecycleVersion}</td>
                                </tr>
                """);
        }

        sb.Append("""
                            </tbody>
                        </table>
                    </div>
                    <div class="w-full overflow-x-auto rounded-lg bg-surface-container-lowest border border-surface-variant">
                        <table class="w-full text-left font-label-code-sm">
                            <thead class="bg-surface-container-high text-on-surface-variant font-badge-label uppercase">
                                <tr>
                                    <th class="py-1.5 px-space-sm">Site</th>
                                    <th class="py-1.5 px-space-sm">State</th>
                                    <th class="py-1.5 px-space-sm">Bindings</th>
                                    <th class="py-1.5 px-space-sm text-right">Requests</th>
                                </tr>
                            </thead>
                            <tbody>
            """);

        foreach (SiteCompositeView s in workspace.Sites)
        {
            bool started = s.EffectiveState == RuntimeResourceState.Started;
            string bStr = string.Join(", ", s.Config.Bindings.Select(b => $"{b.Protocol}:{b.Port}"));
            sb.Append($"""
                                <tr class="hover:bg-surface-container-high/40" data-filterable>
                                    <td class="py-1.5 px-space-sm font-semibold text-primary"><a href="/?scope=sites&entity={Uri.EscapeDataString(s.Config.Id)}">{Enc(s.Config.Name)}</a></td>
                                    <td class="py-1.5 px-space-sm {(started ? "text-tertiary-container" : "text-error")} font-semibold">{s.EffectiveState}</td>
                                    <td class="py-1.5 px-space-sm text-primary-container">{Enc(bStr)} • {(s.Status?.RequestsPerSecond ?? 0):F1} rps ({(s.Status?.AvgLatencyMs ?? 0):F1}ms)</td>
                                    <td class="py-1.5 px-space-sm text-right text-on-surface font-semibold">{(s.Status?.TotalRequests ?? 0)}</td>
                                </tr>
                """);
        }

        sb.Append("""
                            </tbody>
                        </table>
                    </div>
                </div>

                <!-- Recent Health Alerts & W3C Access Trace Terminal (6 Cols) -->
                <div class="lg:col-span-6 p-space-md rounded-xl obsidian-card shadow-lg flex flex-col justify-between">
                    <div class="flex items-center justify-between pb-space-sm">
                        <div class="flex items-center gap-space-sm">
                            <span class="text-secondary font-mono font-bold">&gt;_</span>
                            <span class="font-headline-sm text-on-surface font-semibold">Recent Health Alerts &amp; W3C Traces</span>
                        </div>
                        <div class="flex items-center gap-1.5">
                            <span class="dot-emerald animate-pulse"></span>
                            <span class="font-label-code-sm text-tertiary">Tail Active</span>
                        </div>
                    </div>
                    <div class="w-full bg-surface-container-lowest p-space-sm rounded-lg border border-surface-variant font-label-code-sm flex flex-col gap-1.5 h-44 overflow-y-auto">
                        <div class="flex items-start gap-2 p-0.5 rounded">
                            <span class="px-1 rounded bg-primary-container/10 text-primary-container font-badge-label shrink-0">SYNC</span>
                            <span class="text-on-surface truncate">WebView2 In-Memory Bridge active on <strong class="text-primary-container">http://kite.localhost</strong> (0 TCP ports)</span>
                        </div>
                        <div class="flex items-start gap-2 p-0.5 rounded">
                            <span class="px-1 rounded bg-tertiary/10 text-tertiary-container font-badge-label shrink-0">INFO</span>
                            <span class="text-on-surface truncate">HostRouteTable compiled for <strong class="text-tertiary">{workspace.Sites.Count} site(s)</strong> with zero-allocation ReadOnlySpan lookup</span>
                        </div>
            """);

        foreach (string logLine in recentLogs.TakeLast(6))
        {
            sb.Append($"""
                        <div class="flex items-start gap-2 p-0.5 rounded">
                            <span class="px-1 rounded bg-secondary/10 text-secondary font-badge-label shrink-0">W3C</span>
                            <span class="text-on-surface truncate font-mono">{Enc(logLine)}</span>
                        </div>
                """);
        }

        sb.Append("""
                    </div>
                    <div class="pt-space-xs mt-2 flex items-center justify-between font-label-code-sm text-on-surface-variant">
                        <span>Log Format: <strong>W3C Extended / Structured NDJSON ({srvCfg.Observability.AccessLogFormat})</strong></span>
                        <a href="/?scope=workers" class="text-primary hover:underline">Full Trace Console →</a>
                    </div>
                </div>
            </section>

            <!-- Modern Edge Telemetry: Upstream Reverse Proxy Clusters & Cloud Observability -->
            <section class="grid grid-cols-1 lg:grid-cols-12 gap-space-md">
                <div class="lg:col-span-8 p-space-md rounded-xl obsidian-card shadow-lg flex flex-col gap-space-sm">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-1.5">
                        <div class="flex items-center gap-space-sm">
                            <span class="font-headline-sm text-on-surface font-semibold">Upstream Reverse Proxy Clusters &amp; Health</span>
                            <span class="font-badge-label px-2 py-0.5 rounded bg-surface-container-high text-primary-container">{srvCfg.Upstreams.Count} Configured</span>
                        </div>
                        <a href="/?scope=server&tab=settings" class="text-primary hover:underline font-label-code-sm">Manage Clusters →</a>
                    </div>
            """);

        if (srvCfg.Upstreams.Count == 0 && (srvStatus?.Upstreams.Count ?? 0) == 0)
        {
            sb.Append("""
                    <div class="p-space-sm rounded-lg bg-surface-container-lowest border border-surface-variant font-label-code-sm text-on-surface-variant flex items-center justify-between">
                        <span>No L7 reverse proxy clusters configured yet. Open Configuration &amp; Features → Upstream Proxy Clusters to add active health-checked backends or canary pools.</span>
                        <a href="/?scope=sites" class="text-primary hover:underline shrink-0 ml-2">Configure Site Routing →</a>
                    </div>
                """);
        }
        else
        {
            sb.Append("""
                    <div class="grid grid-cols-1 md:grid-cols-2 gap-space-sm font-label-code-sm">
                """);

            IReadOnlyList<UpstreamClusterStatus> liveClusters = srvStatus?.Upstreams ?? [];
            foreach (UpstreamClusterConfig uc in srvCfg.Upstreams)
            {
                UpstreamClusterStatus? live = liveClusters.FirstOrDefault(c => string.Equals(c.ClusterId, uc.Id, StringComparison.OrdinalIgnoreCase));
                int healthyCount = live is not null
                    ? live.Destinations.Count(d => d.IsHealthy && !d.CircuitOpen)
                    : uc.Destinations.Count;
                int totalDest = live?.Destinations.Count ?? uc.Destinations.Count;
                long totalClusterReqs = live?.Destinations.Sum(d => d.TotalRequests) ?? 0;
                bool allHealthy = totalDest > 0 && healthyCount == totalDest;

                sb.Append($"""
                        <div class="p-space-sm rounded-lg bg-surface-container-lowest border border-surface-variant flex flex-col gap-1">
                            <div class="flex items-center justify-between">
                                <span class="font-semibold text-primary">{Enc(uc.Id)}</span>
                                <span class="font-badge-label px-1.5 py-0.5 rounded {(allHealthy ? "bg-tertiary/10 text-tertiary-container" : "bg-error-container/20 text-error")}">{healthyCount}/{totalDest} HEALTHY</span>
                            </div>
                            <div class="text-on-surface-variant">Policy: <strong class="text-on-surface">{uc.LoadBalancingPolicy}</strong> • Retries: {uc.RetryCount} • Reqs: {totalClusterReqs:N0}</div>
                            <div class="text-on-surface-variant truncate font-mono">{Enc(string.Join(", ", uc.Destinations.Select(d => d.Address)))}</div>
                        </div>
                    """);
            }

            sb.Append("""
                    </div>
                """);
        }

        sb.Append($"""
                </div>

                <div class="lg:col-span-4 p-space-md rounded-xl obsidian-card shadow-lg flex flex-col justify-between gap-space-sm font-label-code-sm">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-1.5">
                        <span class="font-headline-sm text-on-surface font-semibold">Modern Edge Telemetry</span>
                        <span class="font-badge-label px-1.5 py-0.5 rounded {(srvCfg.Transport.EnableHttp3 ? "bg-primary-container/15 text-primary-container" : "bg-surface-container-high text-on-surface-variant")}">{(srvCfg.Transport.EnableHttp3 ? "HTTP/3 QUIC" : "HTTP/2 + 1.1")}</span>
                    </div>
                    <div class="flex flex-col gap-1.5">
                        <div class="flex items-center justify-between">
                            <span class="text-on-surface-variant">Prometheus Scrape</span>
                            <span class="font-mono text-primary">{(srvCfg.Observability.EnablePrometheusMetrics ? Enc(srvCfg.Observability.MetricsPath) : "Disabled")}</span>
                        </div>
                        <div class="flex items-center justify-between">
                            <span class="text-on-surface-variant">Health &amp; Readiness</span>
                            <span class="font-mono text-tertiary-container">{(srvCfg.Observability.EnableHealthEndpoints ? $"{Enc(srvCfg.Observability.HealthEndpointPath)} • {Enc(srvCfg.Observability.ReadyEndpointPath)}" : "Disabled")}</span>
                        </div>
                        <div class="flex items-center justify-between">
                            <span class="text-on-surface-variant">OpenTelemetry Tracing</span>
                            <span class="{(srvCfg.Observability.EnableOpenTelemetryTracing ? "text-tertiary-container" : "text-on-surface-variant")}">{(srvCfg.Observability.EnableOpenTelemetryTracing ? "W3C ActivitySource ON" : "OFF")}</span>
                        </div>
                        <div class="flex items-center justify-between">
                            <span class="text-on-surface-variant">Control Plane Stream</span>
                            <span class="text-primary-container font-mono">/api/events/stream (SSE)</span>
                        </div>
                    </div>
                    <div class="pt-1 flex justify-between items-center border-t border-surface-variant">
                        <span class="text-on-surface-variant">Graceful Drain: {srvCfg.Transport.GracefulDrainTimeoutSeconds}s</span>
                        <a href="/?scope=sites" class="text-primary hover:underline">Canary &amp; Proxy Routes →</a>
                    </div>
                </div>
            </section>
            """);

        sb.Append("""
            <script>
                (function () {
                    if (!window.EventSource) return;
                    let es = null;
                    const timer = setTimeout(function () {
                        es = new EventSource('/api/events/stream');
                        es.addEventListener('telemetry', function (evt) {
                            try {
                                const d = JSON.parse(evt.data);
                                const r = document.getElementById('kpi-total-reqs');
                                const c = document.getElementById('kpi-active-conns');
                                const h = document.getElementById('kpi-heap-mb');
                                const p = document.getElementById('kpi-rps');
                                if (r) r.textContent = Number(d.requests || 0).toLocaleString();
                                if (c) c.textContent = String(d.activeConnections || 0);
                                if (h) h.textContent = Number(d.heapMb || 0).toFixed(2);
                                if (p) p.textContent = Number(d.rps || 0).toFixed(1) + ' rps';
                            } catch (_) {}
                        });
                    }, 1200);
                    window.addEventListener('beforeunload', () => { clearTimeout(timer); if (es) es.close(); });
                })();
            </script>
            """);
    }

    private static void AppendClusterOptions(
        StringBuilder sb,
        IReadOnlyList<UpstreamClusterConfig> clusters,
        string activeId,
        bool includePolicy)
    {
        bool matched = false;
        foreach (UpstreamClusterConfig uc in clusters)
        {
            bool isSel = string.Equals(uc.Id, activeId, StringComparison.OrdinalIgnoreCase);
            matched |= isSel;
            string label = includePolicy ? $"{uc.Id} ({uc.LoadBalancingPolicy})" : uc.Id;
            sb.Append($"<option value=\"{Enc(uc.Id)}\"{(isSel ? " selected" : "")}>{Enc(label)}</option>");
        }

        if (!string.IsNullOrEmpty(activeId) && !matched)
        {
            sb.Append($"<option value=\"{Enc(activeId)}\" selected>{Enc(activeId)} (Custom ID)</option>");
        }
    }

    // ================================================================================================
    // SCREEN 2: WEBSITES & APPLICATION POOLS (design/DesktopWinForms/websites_application_pools)
    // ================================================================================================
    private static void RenderSitesAndAppPoolsView(
        StringBuilder sb,
        ManagementWorkspaceSnapshot workspace,
        string scope,
        string? tab,
        SiteCompositeView? selectedSite,
        PoolCompositeView? selectedPool)
    {
        bool isPoolsTab = scope == "pools";
        long totalReqs = workspace.Sites.Sum(s => s.Status?.TotalRequests ?? 0);
        long totalCacheHits = workspace.Sites.Sum(s => s.Status?.CacheHits ?? 0);
        int totalBindings = workspace.Sites.Sum(s => s.Config.Bindings.Count);
        int totalWorkers = workspace.Pools.Sum(p => p.Config.ProcessModel.WorkerCount);

        string sitesTabBtnCls = !isPoolsTab
            ? "flex items-center gap-space-xs px-3 py-1.5 rounded-lg bg-surface-container-high text-primary font-headline-sm shadow-sm"
            : "flex items-center gap-space-xs px-3 py-1.5 rounded-lg text-on-surface-variant hover:text-on-surface font-headline-sm";
        string poolsTabBtnCls = isPoolsTab
            ? "flex items-center gap-space-xs px-3 py-1.5 rounded-lg bg-surface-container-high text-secondary font-headline-sm shadow-sm"
            : "flex items-center gap-space-xs px-3 py-1.5 rounded-lg text-on-surface-variant hover:text-on-surface font-headline-sm";

        sb.Append($"""
            <div class="flex items-center justify-between px-space-xs text-on-surface-variant font-label-code-sm">
                <div class="flex items-center gap-space-xs">
                    <span class="text-primary-fixed-dim">KITE://LOCALHOST/W3SVC</span>
                    <span>/</span>
                    <span class="text-on-surface">SITES &amp; APP POOLS CLUSTER</span>
                    <span class="px-1.5 py-0.5 rounded bg-surface-container font-badge-label text-tertiary">SCHEMA v10.0</span>
                </div>
                <div class="flex items-center gap-space-md">
                    <span>Sync: <strong class="text-on-surface">Atomic Filesystem</strong></span>
                    <span>Configured Workers: <strong class="text-secondary">{totalWorkers} active</strong></span>
                </div>
            </div>

            <div class="p-space-md rounded-lg obsidian-card shadow-md flex flex-wrap items-center justify-between gap-space-md">
                <div class="flex items-center gap-space-xs bg-surface-container-lowest p-1 rounded-lg border border-surface-variant">
                    <a href="/?scope=sites" class="{sitesTabBtnCls}">
                        <span>Websites</span>
                        <span class="px-1.5 py-0.5 rounded bg-surface-container font-badge-label text-tertiary-container">{workspace.Sites.Count} Active</span>
                    </a>
                    <a href="/?scope=pools" class="{poolsTabBtnCls}">
                        <span>Application Pools</span>
                        <span class="px-1.5 py-0.5 rounded bg-surface-container font-badge-label text-secondary">{workspace.Pools.Count} Active</span>
                    </a>
                </div>
                <div class="relative flex-1 max-w-md">
                    <input id="siteSearchInput" type="text" class="obsidian-input pl-3 pr-12 py-1.5" placeholder="Filter by binding, pool, path or domain..." oninput="filterGlobalElements(this.value)" />
                    <span class="absolute font-badge-label bg-surface-container-high px-1.5 py-0.5 rounded text-on-surface-variant" style="right: 8px; top: 50%; transform: translateY(-50%);">Ctrl+K</span>
                </div>
                <div class="flex items-center gap-space-xs">
                    <button type="button" onclick="toggleModal('modal-new-pool', true)" class="cyber-btn-secondary flex items-center gap-1.5 px-3 py-1.5 rounded font-body-md">
                        <span class="text-secondary">⊕</span>
                        <span>Add Pool</span>
                    </button>
                    <button type="button" onclick="toggleModal('modal-new-site', true)" class="cyber-btn-solid flex items-center gap-1.5 px-3.5 py-1.5 rounded font-headline-sm">
                        <span>+ Add Website</span>
                    </button>
                </div>
            </div>

            <div class="grid grid-cols-1 md:grid-cols-4 gap-space-sm">
                <div class="p-space-sm rounded-lg obsidian-card flex items-center justify-between">
                    <div class="flex flex-col">
                        <span class="font-badge-label uppercase text-on-surface-variant">Combined Ingress</span>
                        <span class="font-label-code-lg text-primary-fixed mt-0.5">{totalReqs:N0} reqs</span>
                        <span class="font-label-code-sm text-tertiary-container">Lock-Free Counter</span>
                    </div>
                    <span class="text-primary-container font-mono text-headline-lg">⚡</span>
                </div>
                <div class="p-space-sm rounded-lg obsidian-card flex items-center justify-between">
                    <div class="flex flex-col">
                        <span class="font-badge-label uppercase text-on-surface-variant">Application Pools</span>
                        <span class="font-label-code-lg text-secondary mt-0.5">{workspace.Pools.Count} Pools ({totalWorkers} Workers)</span>
                        <span class="font-label-code-sm text-on-surface-variant">Overlapped Recycle Ready</span>
                    </div>
                    <span class="text-secondary font-mono text-headline-lg">⚙</span>
                </div>
                <div class="p-space-sm rounded-lg obsidian-card flex items-center justify-between">
                    <div class="flex flex-col">
                        <span class="font-badge-label uppercase text-on-surface-variant">TLS / SNI Health</span>
                        <span class="font-label-code-lg text-tertiary mt-0.5">{totalBindings} Binding Chains</span>
                        <span class="font-label-code-sm text-tertiary-container">SNI Wildcard + Exact</span>
                    </div>
                    <span class="text-tertiary-container font-mono text-headline-lg">🔒</span>
                </div>
                <div class="p-space-sm rounded-lg obsidian-card flex items-center justify-between">
                    <div class="flex flex-col">
                        <span class="font-badge-label uppercase text-on-surface-variant">Static Cache Hits</span>
                        <span class="font-label-code-lg text-primary mt-0.5">{totalCacheHits:N0} Hits</span>
                        <span class="font-label-code-sm text-tertiary-container">ETag / 304 Fast-Path</span>
                    </div>
                    <span class="text-primary-container font-mono text-headline-lg">🚀</span>
                </div>
            </div>
            """);

        if (!isPoolsTab)
        {
            sb.Append($"""
                <div class="rounded-lg obsidian-card shadow-lg overflow-hidden flex flex-col">
                    <div class="px-space-md py-2 bg-surface-container flex items-center justify-between border-b border-surface-variant">
                        <div class="flex items-center gap-space-sm">
                            <span class="font-headline-sm text-on-surface">Configured Sites Roster</span>
                            <span class="font-badge-label px-2 py-0.5 rounded bg-surface-container-high text-primary-container">{workspace.Sites.Count} Configured</span>
                        </div>
                        <span class="font-label-code-sm text-on-surface-variant">Zero-Downtime Hot Reload via config/sites/*.json</span>
                    </div>
                    <div class="overflow-x-auto w-full">
                        <table class="w-full text-left select-text">
                            <thead>
                                <tr class="bg-surface-container-high text-on-surface-variant font-badge-label uppercase h-8">
                                    <th class="py-1 px-3 w-28">Status</th>
                                    <th class="py-1 px-3">Site Identifier</th>
                                    <th class="py-1 px-3">Bindings &amp; Protocols</th>
                                    <th class="py-1 px-3">Physical Storage Path</th>
                                    <th class="py-1 px-3">App Pool Runtime</th>
                                    <th class="py-1 px-3 text-right">Requests</th>
                                    <th class="py-1 px-3 text-center w-36">Actions</th>
                                </tr>
                            </thead>
                            <tbody class="font-label-code-md">
                """);

            foreach (SiteCompositeView s in workspace.Sites)
            {
                bool isSelected = selectedSite is not null && string.Equals(s.Config.Id, selectedSite.Config.Id, StringComparison.OrdinalIgnoreCase);
                bool running = s.EffectiveState == RuntimeResourceState.Started;
                string rowCls = isSelected ? "row-selected hover:bg-surface-container-highest" : "hover:bg-surface-container-high/60";
                SiteBindingConfig? firstBinding = s.Config.Bindings.FirstOrDefault();
                string browseUrl = firstBinding is not null
                    ? $"{firstBinding.Protocol}://localhost:{firstBinding.Port}"
                    : "http://localhost:8080";

                sb.Append($"""
                                <tr class="{rowCls}" data-filterable>
                                    <td class="py-2 px-3 whitespace-nowrap">
                                        <div class="flex items-center gap-1.5">
                                            <span class="{(running ? "dot-emerald" : "dot-crimson")}"></span>
                                            <span class="font-label-code-sm {(running ? "text-tertiary" : "text-error")} font-semibold">{(running ? "RUNNING" : "STOPPED")}</span>
                                        </div>
                                    </td>
                                    <td class="py-2 px-3 whitespace-nowrap">
                                        <a href="/?scope=sites&entity={Uri.EscapeDataString(s.Config.Id)}" class="flex flex-col">
                                            <span class="font-semibold text-primary">{Enc(s.Config.Name)}</span>
                                            <span class="font-label-code-sm text-on-surface-variant">ID: {Enc(s.Config.Id)} • {s.Config.RootApplication.HostingMode}</span>
                                        </a>
                                    </td>
                                    <td class="py-2 px-3 whitespace-nowrap">
                                        <div class="flex items-center gap-1">
                    """);

                foreach (SiteBindingConfig b in s.Config.Bindings)
                {
                    string badgeColor = string.Equals(b.Protocol, "https", StringComparison.OrdinalIgnoreCase) ? "text-primary-container" : "text-on-surface";
                    sb.Append($"""<span class="px-1.5 py-0.5 rounded bg-surface-container font-label-code-sm {badgeColor}">{Enc(b.Protocol)}:{Enc(b.IpAddress)}:{b.Port}</span>""");
                }

                sb.Append($"""
                                        </div>
                                    </td>
                                    <td class="py-2 px-3 whitespace-nowrap">
                                        <div class="flex items-center gap-1 text-on-surface-variant">
                                            <span class="font-label-code-sm truncate max-w-xs">{Enc(s.Config.RootApplication.PhysicalPath)}</span>
                                            <button type="button" onclick="copyToClipboard('{EncJs(s.Config.RootApplication.PhysicalPath)}')" class="px-1 rounded hover:text-primary" title="Copy Path">📋</button>
                                        </div>
                                    </td>
                                    <td class="py-2 px-3 whitespace-nowrap">
                                        <a href="/?scope=pools&entity={Uri.EscapeDataString(s.Config.RootApplication.PoolId)}" class="px-2 py-0.5 rounded bg-primary/10 text-primary-container font-badge-label">
                                            {Enc(s.Config.RootApplication.PoolId)}
                                        </a>
                                    </td>
                                    <td class="py-2 px-3 text-right whitespace-nowrap font-semibold text-tertiary">
                                        {(s.Status?.RequestsPerSecond ?? 0.0):F1}/s <span class="text-on-surface-variant font-normal">({(s.Status?.TotalRequests ?? 0):N0})</span>
                                    </td>
                                    <td class="py-2 px-3 whitespace-nowrap">
                                        <div class="flex items-center justify-center gap-1">
                                            <button type="button" onclick="sendHostMsg('browser:open:{EncJs(browseUrl)}')" class="px-1.5 py-1 rounded bg-surface-container hover:bg-surface-container-highest text-primary" title="Browse {Enc(browseUrl)}">🌐</button>
                                            <button type="button" onclick="sendHostMsg('explorer:open:{EncJs(s.Config.RootApplication.PhysicalPath)}')" class="px-1.5 py-1 rounded bg-surface-container hover:bg-surface-container-highest text-on-surface-variant" title="Open Folder">📁</button>
                                            <form method="post" action="/actions/site-command" style="display:inline;">
                                                <input type="hidden" name="siteId" value="{Enc(s.Config.Id)}" />
                    """);

                if (s.Config.DesiredState == DesiredResourceState.Started)
                {
                    sb.Append("""<button type="submit" name="command" value="stop" class="px-1.5 py-1 rounded bg-error-container/20 hover:bg-error-container text-error" title="Stop Site">■</button>""");
                }
                else
                {
                    sb.Append("""<button type="submit" name="command" value="start" class="px-1.5 py-1 rounded bg-tertiary/10 text-tertiary-container" title="Start Site">▶</button>""");
                }

                sb.Append("""
                                            </form>
                                        </div>
                                    </td>
                                </tr>
                    """);
            }

            sb.Append("""
                            </tbody>
                        </table>
                    </div>
                </div>
                """);

            if (selectedSite is not null)
            {
                SiteConfig sc = selectedSite.Config;
                SiteBindingConfig? httpBinding = sc.Bindings.FirstOrDefault(b => string.Equals(b.Protocol, "http", StringComparison.OrdinalIgnoreCase));
                SiteBindingConfig? httpsBinding = sc.Bindings.FirstOrDefault(b => string.Equals(b.Protocol, "https", StringComparison.OrdinalIgnoreCase));
                bool allowH3 = httpsBinding?.Tls?.AllowHttp3 ?? true;
                int httpPortVal = httpBinding?.Port ?? 8080;
                int? httpsPortVal = httpsBinding?.Port;
                string hostHeaderVal = httpBinding?.Host ?? httpsBinding?.Host ?? "*";
                string activeCluster = sc.RootApplication.UpstreamClusterId ?? "";
                string activeCanary = sc.RootApplication.CanaryClusterId ?? "";

                sb.Append($"""
                    <!-- Unified Selected Site Inspector: Bindings, Workload Runtime & Reverse Proxy Routing -->
                    <section class="rounded-lg obsidian-card shadow-lg overflow-hidden">
                        <div class="px-space-md py-2 bg-surface-container flex flex-wrap items-center justify-between gap-2 border-b border-surface-variant">
                            <div class="flex flex-wrap items-center gap-space-sm">
                                <span class="font-headline-sm text-primary">Site: {Enc(sc.Name)}</span>
                                <span class="font-badge-label px-2 py-0.5 rounded bg-surface-container-high text-on-surface">config/sites/{Enc(sc.Id)}.json</span>
                                <span class="font-badge-label px-2 py-0.5 rounded bg-primary-container/15 text-primary-container">{sc.RootApplication.HostingMode}</span>
                                <a href="/?scope=config&entity={Uri.EscapeDataString(sc.Id)}" class="px-2 py-0.5 rounded bg-surface-container-high text-secondary font-label-code-sm hover:underline">Site Feature Matrix →</a>
                                <a href="/?scope=sites&entity={Uri.EscapeDataString(sc.Id)}&tab=logs" class="px-2 py-0.5 rounded bg-surface-container-high text-primary-container font-label-code-sm hover:underline">View W3C Access Logs →</a>
                            </div>
                            <button type="submit" form="form-save-site" class="cyber-btn-solid px-4 py-1 rounded font-label-code-sm">Commit Site Bindings &amp; Routing</button>
                        </div>

                        <form id="form-save-site" method="post" action="/actions/site-save" class="p-space-md grid grid-cols-1 md:grid-cols-3 gap-space-md font-label-code-sm">
                            <input type="hidden" name="id" value="{Enc(sc.Id)}" />
                            <input type="hidden" name="routingFieldsPresent" value="true" />
                            <div>
                                <label class="text-on-surface-variant">Site Name</label>
                                <input type="text" name="name" value="{Enc(sc.Name)}" class="obsidian-input mt-1" />
                            </div>
                            <div>
                                <label class="text-on-surface-variant">Workload Hosting Mode</label>
                                <select name="hostingMode" class="obsidian-input mt-1">
                                    <option value="StaticOnly" {(sc.RootApplication.HostingMode == WorkloadHostingMode.StaticOnly ? "selected" : "")}>StaticOnly (Zero-Alloc File Cache)</option>
                                    <option value="ExternalReverseProxy" {(sc.RootApplication.HostingMode == WorkloadHostingMode.ExternalReverseProxy ? "selected" : "")}>ExternalReverseProxy (L7 Cluster / URL)</option>
                                    <option value="KiteWorkerIpc" {(sc.RootApplication.HostingMode == WorkloadHostingMode.KiteWorkerIpc ? "selected" : "")}>KiteWorkerIpc (Managed In-Process Pipe)</option>
                                    <option value="OutOfProcessProxy" {(sc.RootApplication.HostingMode == WorkloadHostingMode.OutOfProcessProxy ? "selected" : "")}>OutOfProcessProxy (Process Supervisor)</option>
                                </select>
                            </div>
                            <div>
                                <label class="text-on-surface-variant">Application Pool Runtime</label>
                                <select name="poolId" class="obsidian-input mt-1">
                    """);

                foreach (PoolCompositeView p in workspace.Pools)
                {
                    string selPool = string.Equals(p.Config.Id, sc.RootApplication.PoolId, StringComparison.OrdinalIgnoreCase) ? " selected" : "";
                    sb.Append($"<option value=\"{Enc(p.Config.Id)}\"{selPool}>{Enc(p.Config.Name)} ({Enc(p.Config.Id)})</option>");
                }

                sb.Append($"""
                                </select>
                            </div>
                            <div class="md:col-span-2">
                                <label class="text-on-surface-variant">Physical Storage Root Path</label>
                                <input type="text" name="physicalPath" value="{Enc(sc.RootApplication.PhysicalPath)}" class="obsidian-input mt-1 font-mono" />
                            </div>
                            <div>
                                <label class="text-on-surface-variant">Primary Host Header / SNI</label>
                                <input type="text" name="hostHeader" value="{Enc(hostHeaderVal)}" placeholder="* or api.example.com" class="obsidian-input mt-1 font-mono" />
                            </div>
                            <div>
                                <label class="text-on-surface-variant">HTTP Listen Port</label>
                                <input type="number" name="httpPort" value="{httpPortVal}" min="1" max="65535" class="obsidian-input mt-1" />
                            </div>
                            <div>
                                <label class="text-on-surface-variant">HTTPS Listen Port (blank = HTTP only)</label>
                                <input type="number" name="httpsPort" value="{(httpsPortVal?.ToString() ?? "")}" min="1" max="65535" placeholder="8443" class="obsidian-input mt-1" />
                            </div>
                            <div class="flex items-end pb-1.5">
                                <label class="flex items-center gap-2 text-on-surface">
                                    <input type="checkbox" name="allowHttp3" value="true" {(allowH3 ? "checked" : "")} />
                                    Allow HTTP/3 (QUIC) on HTTPS Binding
                                </label>
                            </div>

                            <div>
                                <label class="text-on-surface-variant">Process Executable (OutOfProcessProxy)</label>
                                <input type="text" name="executable" value="{Enc(sc.RootApplication.Executable ?? "")}" placeholder="node, python, or dotnet" class="obsidian-input mt-1 font-mono" />
                            </div>
                            <div class="md:col-span-2">
                                <label class="text-on-surface-variant">Process Arguments (supports {"{port}"} / {"{url}"})</label>
                                <input type="text" name="arguments" value="{Enc(sc.RootApplication.Arguments ?? "")}" placeholder="server.js or app.py" class="obsidian-input mt-1 font-mono" />
                            </div>

                            <div>
                                <label class="text-on-surface-variant">Primary Upstream Cluster (L7 Proxy)</label>
                                <select name="upstreamClusterId" class="obsidian-input mt-1">
                                    <option value="">-- None (Direct Upstream URL or Local) --</option>
                    """);

                AppendClusterOptions(sb, workspace.ServerConfig.Upstreams, activeCluster, includePolicy: true);

                sb.Append($"""
                                </select>
                            </div>
                            <div>
                                <label class="text-on-surface-variant">Direct Upstream URL (Single Target Fallback)</label>
                                <input type="text" name="upstreamUrl" value="{Enc(sc.RootApplication.UpstreamUrl ?? "")}" placeholder="http://127.0.0.1:5001" class="obsidian-input mt-1 font-mono" />
                            </div>
                            <div class="flex items-end pb-1.5">
                                <label class="flex items-center gap-2 text-on-surface">
                                    <input type="checkbox" name="enableStreamingPassThrough" value="true" {(sc.RootApplication.EnableStreamingPassThrough ? "checked" : "")} />
                                    Unbuffered SSE / gRPC / Streaming Pass-Through
                                </label>
                            </div>

                            <div>
                                <label class="text-on-surface-variant">Canary Deployment Cluster</label>
                                <select name="canaryClusterId" class="obsidian-input mt-1">
                                    <option value="">-- Disabled --</option>
                    """);

                AppendClusterOptions(sb, workspace.ServerConfig.Upstreams, activeCanary, includePolicy: false);

                sb.Append($"""
                                </select>
                            </div>
                            <div>
                                <label class="text-on-surface-variant">Canary Traffic Split Weight (%)</label>
                                <input type="number" name="canaryWeightPercent" value="{sc.RootApplication.CanaryWeightPercent}" min="0" max="100" class="obsidian-input mt-1" />
                            </div>
                            <div class="flex flex-col justify-end gap-1 pb-1">
                                <label class="flex items-center gap-2 text-on-surface">
                                    <input type="checkbox" name="enableGrpcWebBridge" value="true" {(sc.RootApplication.EnableGrpcWebBridge ? "checked" : "")} />
                                    Native gRPC-Web Bridge (HTTP/1.1 ↔ gRPC H2)
                                </label>
                                <label class="flex items-center gap-2 text-on-surface">
                                    <input type="checkbox" name="servePrecompressedSidecars" value="true" {(sc.RootApplication.ServePrecompressedSidecars ? "checked" : "")} />
                                    Serve Pre-compressed .zst/.br/.gz Sidecars (Zero CPU)
                                </label>
                            </div>
                        </form>
                        <div class="grid grid-cols-1 md:grid-cols-2 gap-space-sm p-space-md pt-space-sm border-t border-surface-variant font-label-code-sm">
                            <!-- API Gateway Route Rules Panel -->
                            <div class="p-space-sm rounded bg-surface-container-lowest border border-surface-variant flex flex-col gap-2">
                                <div class="flex items-center justify-between">
                                    <span class="font-badge-label uppercase text-on-surface-variant">API Gateway Route Rules</span>
                                    <span class="text-primary font-semibold">{sc.Features.Routes.Count} Rule(s)</span>
                                </div>
                                {(sc.Features.Routes.Count == 0 ? "<span class=\"text-on-surface-variant\">No path/method/shadow/chaos route rules defined yet.</span>" : string.Join("", sc.Features.Routes.Select(r => $"<div class=\"flex items-center justify-between p-1 rounded bg-surface-container text-xs\"><span><strong class=\"text-primary\">{Enc(r.Id)}</strong> {Enc(r.PathPrefix)} → {Enc(r.TargetClusterId ?? r.TargetPoolId ?? "root")}{(r.ShadowPercent > 0 ? $" (Shadow {r.ShadowPercent}%)" : "")}{(r.FaultInjection.Enabled ? $" (Chaos {r.FaultInjection.AbortPercent}%)" : "")}</span><form method=\"post\" action=\"/actions/site-route-save\" style=\"display:inline\"><input type=\"hidden\" name=\"siteId\" value=\"{Enc(sc.Id)}\" /><input type=\"hidden\" name=\"routeId\" value=\"{Enc(r.Id)}\" /><input type=\"hidden\" name=\"removeRoute\" value=\"true\" /><button type=\"submit\" class=\"text-error hover:underline\">✕</button></form></div>")))}
                                <form id="form-site-route-add" method="post" action="/actions/site-route-save" class="grid grid-cols-3 gap-1.5 pt-1 border-t border-surface-variant">
                                    <input type="hidden" name="siteId" value="{Enc(sc.Id)}" />
                                    <input type="text" name="routeId" placeholder="route-api" class="obsidian-input" />
                                    <input type="text" name="pathPrefix" placeholder="/api/v1" class="obsidian-input font-mono" />
                                    <input type="text" name="targetClusterId" placeholder="Cluster ID" class="obsidian-input" />
                                    <input type="text" name="stripPathPrefix" placeholder="Strip /api/v1" class="obsidian-input font-mono" />
                                    <input type="text" name="shadowClusterId" placeholder="Shadow Cluster" class="obsidian-input" />
                                    <button type="submit" class="cyber-btn-secondary py-1 rounded font-semibold">+ Add Route Rule</button>
                                </form>
                            </div>
                            <!-- Blue/Green Zero-Downtime Slot Staging & Swap -->
                            <div class="p-space-sm rounded bg-surface-container-lowest border border-surface-variant flex flex-col justify-between gap-2">
                                <div class="flex items-center justify-between">
                                    <span class="font-badge-label uppercase text-on-surface-variant">Blue / Green Slot Deployment</span>
                                    <span class="px-2 py-0.5 rounded bg-primary-container/15 text-primary-container font-bold uppercase">Active: {Enc(sc.RootApplication.ActiveSlot)}</span>
                                </div>
                                <form id="form-site-slot-deploy" method="post" action="/actions/site-slot-deploy" class="flex flex-col gap-1.5">
                                    <input type="hidden" name="siteId" value="{Enc(sc.Id)}" />
                                    <input type="text" name="stagingPhysicalPath" value="{Enc(sc.RootApplication.StagingPhysicalPath ?? "")}" placeholder="C:\releases\green-slot" class="obsidian-input font-mono" />
                                    <div class="flex gap-2">
                                        <button type="submit" name="slotAction" value="stage" class="cyber-btn-secondary flex-1 py-1 rounded">Stage Inactive Slot</button>
                                        <button type="submit" name="slotAction" value="swap" class="cyber-btn-solid flex-1 py-1 rounded">⇄ Atomic Slot Swap</button>
                                    </div>
                                </form>
                            </div>
                        </div>
                        <div class="grid grid-cols-1 md:grid-cols-2 gap-space-sm p-space-md pt-space-sm border-t border-surface-variant font-label-code-sm">
                            <div class="p-space-sm rounded bg-surface-container-lowest border border-surface-variant">
                                <div class="flex items-center justify-between mb-1">
                                    <span class="font-badge-label uppercase text-on-surface-variant">Nested Applications</span>
                                    <span class="text-secondary">{sc.NestedApplications.Count} Mounted</span>
                                </div>
                    """);

                if (sc.NestedApplications.Count == 0)
                {
                    sb.Append("""<span class="text-on-surface-variant">No nested applications mounted on this site.</span>""");
                }
                else
                {
                    foreach (SiteApplicationConfig app in sc.NestedApplications)
                    {
                        sb.Append($"""
                                <div class="flex items-center justify-between py-0.5 px-1.5 rounded bg-surface-container mt-1">
                                    <span class="text-primary font-semibold">{Enc(app.Path)}</span>
                                    <span class="text-on-surface-variant">Pool: <strong class="text-secondary">{Enc(app.PoolId)}</strong> ({app.HostingMode})</span>
                                </div>
                            """);
                    }
                }

                sb.Append($"""
                            </div>
                            <div class="p-space-sm rounded bg-surface-container-lowest border border-surface-variant">
                                <div class="flex items-center justify-between mb-1">
                                    <span class="font-badge-label uppercase text-on-surface-variant">Virtual Directories</span>
                                    <span class="text-tertiary-container">{sc.RootApplication.VirtualDirectories.Count} Mapped</span>
                                </div>
                    """);

                if (sc.RootApplication.VirtualDirectories.Count == 0)
                {
                    sb.Append("""<span class="text-on-surface-variant">Root (/) mapped directly to site physical path.</span>""");
                }
                else
                {
                    foreach (VirtualDirectoryConfig vd in sc.RootApplication.VirtualDirectories)
                    {
                        sb.Append($"""
                                <div class="flex items-center justify-between py-0.5 px-1.5 rounded bg-surface-container mt-1">
                                    <span class="text-tertiary-container font-semibold">{Enc(vd.VirtualPath)}</span>
                                    <span class="text-on-surface-variant truncate">{Enc(vd.PhysicalPath)}</span>
                                </div>
                            """);
                    }
                }

                sb.Append("""
                            </div>
                        </div>
                    </section>
                    """);
            }
        }
        else
        {
            sb.Append($"""
                <div class="rounded-lg obsidian-card shadow-lg overflow-hidden flex flex-col">
                    <div class="px-space-md py-2 bg-surface-container flex items-center justify-between border-b border-surface-variant">
                        <div class="flex items-center gap-space-sm">
                            <span class="font-headline-sm text-on-surface">Configured Application Pools Roster</span>
                            <span class="font-badge-label px-2 py-0.5 rounded bg-surface-container-high text-secondary">{workspace.Pools.Count} Configured</span>
                        </div>
                        <span class="font-label-code-sm text-on-surface-variant">Overlapped Recycling &amp; Web Garden Process Supervision</span>
                    </div>
                    <div class="overflow-x-auto w-full">
                        <table class="w-full text-left select-text">
                            <thead>
                                <tr class="bg-surface-container-high text-on-surface-variant font-badge-label uppercase h-8">
                                    <th class="py-1 px-3 w-28">Status</th>
                                    <th class="py-1 px-3">Pool Identifier</th>
                                    <th class="py-1 px-3">GC &amp; CLR Mode</th>
                                    <th class="py-1 px-3">Web Garden Workers</th>
                                    <th class="py-1 px-3">Recycle Version</th>
                                    <th class="py-1 px-3 text-center w-48">Actions</th>
                                </tr>
                            </thead>
                            <tbody class="font-label-code-md">
                """);

            foreach (PoolCompositeView p in workspace.Pools)
            {
                bool isSelected = selectedPool is not null && string.Equals(p.Config.Id, selectedPool.Config.Id, StringComparison.OrdinalIgnoreCase);
                bool running = p.EffectiveState == RuntimeResourceState.Started;
                string rowCls = isSelected ? "row-selected hover:bg-surface-container-highest" : "hover:bg-surface-container-high/60";

                sb.Append($"""
                                <tr class="{rowCls}" data-filterable>
                                    <td class="py-2 px-3 whitespace-nowrap">
                                        <div class="flex items-center gap-1.5">
                                            <span class="{(running ? "dot-emerald" : "dot-crimson")}"></span>
                                            <span class="font-label-code-sm {(running ? "text-tertiary" : "text-error")} font-semibold">{(running ? "RUNNING" : "STOPPED")}</span>
                                        </div>
                                    </td>
                                    <td class="py-2 px-3 whitespace-nowrap">
                                        <a href="/?scope=pools&entity={Uri.EscapeDataString(p.Config.Id)}" class="flex flex-col">
                                            <span class="font-semibold text-primary">{Enc(p.Config.Name)}</span>
                                            <span class="font-label-code-sm text-on-surface-variant">ID: {Enc(p.Config.Id)}</span>
                                        </a>
                                    </td>
                                    <td class="py-2 px-3 whitespace-nowrap">
                                        <span class="px-2 py-0.5 rounded bg-secondary/10 text-secondary font-badge-label">{p.Config.ProcessModel.GcMode} (.NET 10)</span>
                                    </td>
                                    <td class="py-2 px-3 whitespace-nowrap font-semibold text-on-surface">
                                        {p.Config.ProcessModel.WorkerCount} Worker(s)
                                    </td>
                                    <td class="py-2 px-3 whitespace-nowrap font-mono text-primary-container">
                                        v{p.Config.RecycleVersion} (RecycleVersion={p.Config.RecycleVersion})
                                    </td>
                                    <td class="py-2 px-3 whitespace-nowrap text-center">
                                        <form method="post" action="/actions/pool-command" class="inline-flex items-center gap-1">
                                            <input type="hidden" name="poolId" value="{Enc(p.Config.Id)}" />
                                            <button type="submit" name="command" value="recycle" class="cyber-btn-primary px-2 py-1 rounded font-label-code-sm">↻ Overlapped Recycle</button>
                    """);

                if (p.Config.DesiredState == DesiredResourceState.Started)
                {
                    sb.Append("""<button type="submit" name="command" value="stop" class="cyber-btn-danger px-2 py-1 rounded font-label-code-sm">Stop</button>""");
                }
                else
                {
                    sb.Append("""<button type="submit" name="command" value="start" class="cyber-btn-secondary px-2 py-1 rounded font-label-code-sm">Start</button>""");
                }

                sb.Append("""
                                        </form>
                                    </td>
                                </tr>
                    """);
            }

            sb.Append("""
                            </tbody>
                        </table>
                    </div>
                </div>
                """);
        }

        // BOTTOM APPLICATION POOL INSPECTOR PANEL
        if (selectedPool is not null)
        {
            ApplicationPoolConfig pCfg = selectedPool.Config;
            IReadOnlyList<WorkerProcessStatus> workers = selectedPool.Status?.ActiveWorkers ?? [];
            bool pOnline = selectedPool.EffectiveState == RuntimeResourceState.Started;

            sb.Append($"""
                <div class="rounded-lg obsidian-card shadow-xl p-space-md flex flex-col gap-space-md relative overflow-hidden">
                    <div class="absolute top-0 left-0 right-0" style="height: 2px; background: linear-gradient(90deg, transparent, #00f2fe, transparent);"></div>
                    <div class="flex flex-wrap items-center justify-between gap-space-sm pb-space-xs border-b border-surface-variant">
                        <div class="flex items-center gap-space-sm">
                            <div class="w-8 h-8 rounded bg-surface-container-highest flex items-center justify-center text-primary-container font-bold">⚙</div>
                            <div class="flex flex-col">
                                <div class="flex items-center gap-2">
                                    <span class="font-headline-sm text-on-surface">Application Pool: {Enc(pCfg.Name)}</span>
                                    <span class="px-1.5 py-0.5 rounded {(pOnline ? "bg-tertiary/10 text-tertiary" : "bg-error-container/20 text-error")} font-badge-label font-bold">{(pOnline ? "ATTACHED & HEALTHY" : "STOPPED")}</span>
                                </div>
                                <span class="font-label-code-sm text-on-surface-variant">Pool ID: {Enc(pCfg.Id)} • RecycleVersion={pCfg.RecycleVersion} (v{pCfg.RecycleVersion})</span>
                            </div>
                        </div>
                        <form method="post" action="/actions/pool-command" class="flex items-center gap-space-xs">
                            <input type="hidden" name="poolId" value="{Enc(pCfg.Id)}" />
                            <button type="submit" name="command" value="recycle" class="cyber-btn-primary px-3 py-1.5 rounded font-headline-sm">↻ Recycle Now</button>
                """);

            if (pCfg.DesiredState == DesiredResourceState.Started)
            {
                sb.Append("""<button type="submit" name="command" value="stop" class="cyber-btn-danger px-3 py-1.5 rounded font-body-sm">Stop Pool</button>""");
            }
            else
            {
                sb.Append("""<button type="submit" name="command" value="start" class="cyber-btn-secondary px-3 py-1.5 rounded font-body-sm">Start Pool</button>""");
            }

            sb.Append($"""
                        </form>
                    </div>

                    <div class="grid grid-cols-1 md:grid-cols-3 gap-space-md">
                        <div class="p-space-sm rounded bg-surface-container-lowest border border-surface-variant flex flex-col justify-between">
                            <div class="flex items-center justify-between pb-1 border-b border-surface-variant">
                                <span class="font-badge-label uppercase text-on-surface-variant">CLR Execution Engine</span>
                                <span class="font-label-code-sm text-primary">Kestrel / Worker</span>
                            </div>
                            <div class="flex flex-col gap-1.5 font-label-code-md pt-2">
                                <div class="flex items-center justify-between"><span class="text-on-surface-variant">.NET CLR Runtime:</span><span class="text-on-surface font-semibold">.NET 10.0 LTS (x64)</span></div>
                                <div class="flex items-center justify-between"><span class="text-on-surface-variant">GC Strategy:</span><span class="text-tertiary font-semibold">{pCfg.ProcessModel.GcMode}</span></div>
                                <div class="flex items-center justify-between"><span class="text-on-surface-variant">Start Mode:</span><span class="text-primary-container font-semibold">{pCfg.ProcessModel.StartMode}</span></div>
                                <div class="flex items-center justify-between"><span class="text-on-surface-variant">Schedule / MaxReq:</span><span class="text-secondary font-semibold">{Enc(pCfg.Recycling.ScheduledRestartUtcTime ?? "Manual")} UTC / {(pCfg.Recycling.MaxRequests == 0 ? "Unlimited" : pCfg.Recycling.MaxRequests.ToString())}</span></div>
                                <div class="flex items-center justify-between"><span class="text-on-surface-variant">Identity Principal:</span><span class="text-on-surface font-semibold">{Enc(pCfg.ProcessModel.Identity)}</span></div>
                            </div>
                        </div>

                        <div class="p-space-sm rounded bg-surface-container-lowest border border-surface-variant flex flex-col justify-between">
                            <div class="flex items-center justify-between pb-1 border-b border-surface-variant">
                                <span class="font-badge-label uppercase text-on-surface-variant">Attached Worker Processes</span>
                                <span class="font-label-code-sm text-tertiary">{workers.Count} Active</span>
                            </div>
                            <div class="flex flex-col gap-1 font-label-code-sm pt-2 max-h-36 overflow-y-auto">
                """);

            if (workers.Count == 0)
            {
                sb.Append("""<span class="text-on-surface-variant">No worker processes currently active.</span>""");
            }
            else
            {
                foreach (WorkerProcessStatus w in workers)
                {
                    sb.Append($"""
                                <div class="flex items-center justify-between py-0.5 px-1.5 rounded bg-surface-container">
                                    <span class="text-primary font-semibold">{Enc(w.WorkerId)} (PID {w.ProcessId})</span>
                                    <span class="text-tertiary-container">{w.HostingMode} • {Enc(w.TransportAddress)}</span>
                                </div>
                        """);
                }
            }

            sb.Append($"""
                            </div>
                        </div>

                        <form id="form-save-pool" method="post" action="/actions/pool-save" class="p-space-sm rounded bg-surface-container-lowest border border-surface-variant flex flex-col justify-between gap-2">
                            <input type="hidden" name="id" value="{Enc(pCfg.Id)}" />
                            <input type="hidden" name="name" value="{Enc(pCfg.Name)}" />
                            <input type="hidden" name="autoscaleFieldsPresent" value="true" />
                            <div class="flex items-center justify-between pb-1 border-b border-surface-variant">
                                <span class="font-badge-label uppercase text-on-surface-variant">Pool Directives &amp; Autoscaling</span>
                                <span class="font-label-code-sm text-secondary">config/pools/{Enc(pCfg.Id)}.json</span>
                            </div>
                            <div class="grid grid-cols-2 gap-2 font-label-code-sm">
                                <div>
                                    <label class="text-on-surface-variant">Web Garden Workers</label>
                                    <input type="number" name="workerCount" value="{pCfg.ProcessModel.WorkerCount}" min="0" max="64" class="obsidian-input mt-0.5" />
                                </div>
                                <div>
                                    <label class="text-on-surface-variant">GC Mode</label>
                                    <select name="gcMode" class="obsidian-input mt-0.5">
                """);

            foreach (PoolGcMode mode in Enum.GetValues<PoolGcMode>())
            {
                string sel = mode == pCfg.ProcessModel.GcMode ? " selected" : "";
                sb.Append($"<option value=\"{mode}\"{sel}>{mode}</option>");
            }

            sb.Append($"""
                                    </select>
                                </div>
                                <div>
                                    <label class="text-on-surface-variant">Min Workers (0=Scale-0)</label>
                                    <input type="number" name="minWorkers" value="{pCfg.ProcessModel.MinWorkers}" min="0" max="64" class="obsidian-input mt-0.5" />
                                </div>
                                <div>
                                    <label class="text-on-surface-variant">Max Workers (Autoscale)</label>
                                    <input type="number" name="maxWorkers" value="{pCfg.ProcessModel.MaxWorkers}" min="1" max="128" class="obsidian-input mt-0.5" />
                                </div>
                                <div>
                                    <label class="text-on-surface-variant">Scale-Up Concurrency</label>
                                    <input type="number" name="scaleUpConcurrencyThreshold" value="{pCfg.ProcessModel.ScaleUpConcurrencyThreshold}" min="1" max="1000" class="obsidian-input mt-0.5" />
                                </div>
                                <div>
                                    <label class="text-on-surface-variant">Scale-Down Idle (s)</label>
                                    <input type="number" name="scaleDownIdleSeconds" value="{pCfg.ProcessModel.ScaleDownIdleSeconds}" min="5" max="86400" class="obsidian-input mt-0.5" />
                                </div>
                                <div>
                                    <label class="text-on-surface-variant">Idle Timeout (min)</label>
                                    <input type="number" name="idleTimeoutMinutes" value="{pCfg.ProcessModel.IdleTimeoutMinutes}" class="obsidian-input mt-0.5" />
                                </div>
                                <div>
                                    <label class="text-on-surface-variant">Periodic Restart (min)</label>
                                    <input type="number" name="periodicRestartMinutes" value="{pCfg.Recycling.PeriodicRestartMinutes}" class="obsidian-input mt-0.5" />
                                </div>
                                <div>
                                    <label class="text-on-surface-variant">Start Mode</label>
                                    <select name="startMode" class="obsidian-input mt-0.5">
                                        <option value="AlwaysRunning"{(pCfg.ProcessModel.StartMode == PoolStartMode.AlwaysRunning ? " selected" : "")}>AlwaysRunning</option>
                                        <option value="OnDemand"{(pCfg.ProcessModel.StartMode == PoolStartMode.OnDemand ? " selected" : "")}>OnDemand</option>
                                    </select>
                                </div>
                                <div>
                                    <label class="text-on-surface-variant">Request Limit (0=∞)</label>
                                    <input type="number" name="maxRequests" value="{pCfg.Recycling.MaxRequests}" min="0" class="obsidian-input mt-0.5" />
                                </div>
                                <div class="flex items-end pb-1">
                                    <label class="flex items-center gap-1.5 text-on-surface">
                                        <input type="checkbox" name="useUnixDomainSocket" value="true" {(pCfg.ProcessModel.UseUnixDomainSocket ? "checked" : "")} />
                                        AF_UNIX Socket ({pCfg.ProcessModel.Sidecars.Count} sidecar)
                                    </label>
                                </div>
                                <div>
                                    <label class="text-on-surface-variant">Scheduled UTC (HH:mm)</label>
                                    <input type="text" name="scheduledRestartUtcTime" value="{Enc(pCfg.Recycling.ScheduledRestartUtcTime ?? "04:00")}" placeholder="04:00" class="obsidian-input mt-0.5" />
                                </div>
                            </div>
                            <input type="hidden" name="maxMemoryMegabytes" value="{pCfg.Recycling.MaxMemoryMegabytes}" />
                            <button type="submit" class="cyber-btn-solid w-full py-1.5 rounded font-label-code-md">Save Application Pool Config</button>
                        </form>
                    </div>
                </div>
                """);
        }

        sb.Append("""
            <div id="modal-new-pool" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-md w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <span class="font-headline-lg text-secondary">Create New Application Pool</span>
                        <button type="button" onclick="toggleModal('modal-new-pool', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form method="post" action="/actions/pool-save" class="flex flex-col gap-space-sm font-label-code-sm">
                        <div><label>Pool Identifier</label><input type="text" name="id" value="api-pool" class="obsidian-input mt-1" /></div>
                        <div><label>Display Name</label><input type="text" name="name" value="High-Throughput API Pool" class="obsidian-input mt-1" /></div>
                        <div><label>Web Garden Worker Count</label><input type="number" name="workerCount" value="2" class="obsidian-input mt-1" /></div>
                        <input type="hidden" name="gcMode" value="DatasServer" />
                        <div class="flex justify-end gap-2 pt-2">
                            <button type="button" onclick="toggleModal('modal-new-pool', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                            <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Create Pool</button>
                        </div>
                    </form>
                </div>
            </div>
            <div id="modal-new-site" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-md w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <span class="font-headline-lg text-primary">Create New Hosted Website</span>
                        <button type="button" onclick="toggleModal('modal-new-site', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form method="post" action="/actions/site-save" class="flex flex-col gap-space-sm font-label-code-sm">
                        <div><label>Site Identifier</label><input type="text" name="id" value="edge-api" class="obsidian-input mt-1" /></div>
                        <div><label>Site Name</label><input type="text" name="name" value="edge-api.internal" class="obsidian-input mt-1" /></div>
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>HTTP Port</label><input type="number" name="httpPort" value="8085" class="obsidian-input mt-1" /></div>
                            <div><label>Application Pool</label><input type="text" name="poolId" value="default-pool" class="obsidian-input mt-1" /></div>
                        </div>
                        <div class="grid grid-cols-2 gap-2">
                            <div>
                                <label>Hosting Mode</label>
                                <select name="hostingMode" class="obsidian-input mt-1">
                                    <option value="StaticOnly">StaticOnly</option>
                                    <option value="ExternalReverseProxy">ExternalReverseProxy</option>
                                    <option value="KiteWorkerIpc">KiteWorkerIpc</option>
                                    <option value="OutOfProcessProxy">OutOfProcessProxy</option>
                                </select>
                            </div>
                            <div>
                                <label>Upstream Cluster (Optional)</label>
                                <input type="text" name="upstreamClusterId" placeholder="api-cluster" class="obsidian-input mt-1" />
                            </div>
                        </div>
                        <div class="flex justify-end gap-2 pt-2">
                            <button type="button" onclick="toggleModal('modal-new-site', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                            <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Create Website</button>
                        </div>
                    </form>
                </div>
            </div>
            """);
    }

    private static string BuildTelemetrySvgChart(ServerStatus? srvStatus)
    {
        IReadOnlyList<TelemetrySampleBucket> samples = srvStatus?.RecentSamples ?? [];
        if (samples.Count < 2)
        {
            return """
                <svg class="w-full h-full overflow-visible" preserveAspectRatio="none" viewBox="0 0 500 140">
                    <line stroke="rgba(255,255,255,0.06)" stroke-dasharray="4 4" x1="0" x2="500" y1="35" y2="35"></line>
                    <line stroke="rgba(255,255,255,0.06)" stroke-dasharray="4 4" x1="0" x2="500" y1="70" y2="70"></line>
                    <line stroke="rgba(255,255,255,0.06)" stroke-dasharray="4 4" x1="0" x2="500" y1="105" y2="105"></line>
                    <line stroke="#67f4b7" stroke-width="1.5" x1="0" x2="500" y1="132" y2="132"></line>
                </svg>
                """;
        }

        int maxVal = Math.Max(1, samples.Max(b => b.Status2xxCount + b.Status3xxCount + b.Status4xxCount + b.Status5xxCount));
        var pts2xx = new StringBuilder("0,136 ");
        var pts3xx = new StringBuilder("0,136 ");
        var pts5xx = new StringBuilder();
        double step = 500.0 / Math.Max(1, samples.Count - 1);

        for (int i = 0; i < samples.Count; i++)
        {
            double x = i * step;
            TelemetrySampleBucket b = samples[i];
            double y2 = 136 - Math.Clamp((b.Status2xxCount / (double)maxVal) * 115.0, 0, 120);
            double y3 = 136 - Math.Clamp((b.Status3xxCount / (double)maxVal) * 115.0, 0, 120);
            double y5 = 136 - Math.Clamp((b.Status5xxCount / (double)maxVal) * 115.0, 0, 120);
            pts2xx.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{x:F1},{y2:F1} "));
            pts3xx.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{x:F1},{y3:F1} "));
            pts5xx.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{x:F1},{y5:F1} "));
        }

        string line2xx = pts2xx.ToString().Trim();
        string area2xx = line2xx + " 500,136 0,136";
        string line3xx = pts3xx.ToString().Trim();
        string line5xx = pts5xx.ToString().Trim();

        return $"""
            <svg class="w-full h-full overflow-visible" preserveAspectRatio="none" viewBox="0 0 500 140">
                <line stroke="rgba(255,255,255,0.06)" stroke-dasharray="4 4" x1="0" x2="500" y1="35" y2="35"></line>
                <line stroke="rgba(255,255,255,0.06)" stroke-dasharray="4 4" x1="0" x2="500" y1="70" y2="70"></line>
                <line stroke="rgba(255,255,255,0.06)" stroke-dasharray="4 4" x1="0" x2="500" y1="105" y2="105"></line>
                <polygon points="{area2xx}" fill="rgba(103, 244, 183, 0.16)"></polygon>
                <polyline points="{line2xx}" fill="none" stroke="#67f4b7" stroke-width="2"></polyline>
                <polyline points="{line3xx}" fill="none" stroke="#00f2fe" stroke-width="1.5"></polyline>
                <polyline points="{line5xx}" fill="none" stroke="#ffb4ab" stroke-dasharray="3 2" stroke-width="1.5"></polyline>
            </svg>
            """;
    }

    private static string FormatUptime(TimeSpan ts)
    {
        if (ts.TotalSeconds < 0)
        {
            ts = TimeSpan.Zero;
        }
        return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string EncJs(string? value) =>
        (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'");

    private static string SanitizeIdentifier(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "resource-" + Guid.NewGuid().ToString("N")[..6];
        }

        Span<char> buf = stackalloc char[Math.Min(raw.Length, 64)];
        int idx = 0;
        foreach (char c in raw.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
            {
                buf[idx++] = char.ToLowerInvariant(c);
                if (idx == buf.Length)
                {
                    break;
                }
            }
            else if (c == ' ' && idx < buf.Length)
            {
                buf[idx++] = '-';
            }
        }

        return idx == 0 ? "resource" : new string(buf[..idx]);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed record InMemoryUiResponse(
    int StatusCode,
    string ContentType,
    IReadOnlyDictionary<string, string> Headers,
    SegmentedBufferStream BodyStream) : IDisposable
{
    public async Task<string> ReadBodyAsStringAsync()
    {
        BodyStream.Position = 0;
        using var reader = new StreamReader(BodyStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    public void Dispose() => BodyStream.Dispose();
}

public sealed class InMemoryHttpRequest
{
    public string Method { get; init; } = "GET";
    public string PathAndQuery { get; init; } = "/";
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public byte[]? Body { get; init; }
    public string? ContentType { get; init; }
}

public sealed record InMemoryHttpResponse(
    int StatusCode,
    string ReasonPhrase,
    string ContentType,
    SegmentedBufferStream BodyPayload) : IDisposable
{
    public async Task<string> ReadBodyAsStringAsync()
    {
        BodyPayload.Position = 0;
        using var reader = new StreamReader(BodyPayload, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    public void Dispose() => BodyPayload.Dispose();
}
