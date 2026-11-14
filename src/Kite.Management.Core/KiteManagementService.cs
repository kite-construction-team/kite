using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Kite.Core.Memory;
using Kite.Core.Models;
using Kite.Core.Routing;
using Kite.Core.Serialization;
using Kite.Core.Storage;
using Kite.Management.Core.Importers;
using Microsoft.AspNetCore.Http;

namespace Kite.Management.Core;

public sealed record PoolCompositeView(
    ApplicationPoolConfig Config,
    ApplicationPoolStatus? Status,
    bool IsServerOnline = true)
{
    public RuntimeResourceState EffectiveState =>
        IsServerOnline && Status is not null
            ? Status.ActualState
            : RuntimeResourceState.Stopped;
}

public sealed record SiteCompositeView(
    SiteConfig Config,
    SiteStatus? Status,
    bool IsServerOnline = true)
{
    public RuntimeResourceState EffectiveState =>
        IsServerOnline && Status is not null
            ? Status.ActualState
            : RuntimeResourceState.Stopped;
}

public sealed record ManagementWorkspaceSnapshot(
    string KiteHomePath,
    ServerConfig ServerConfig,
    ServerStatus? ServerStatus,
    IReadOnlyList<PoolCompositeView> Pools,
    IReadOnlyList<SiteCompositeView> Sites,
    bool IsServerOnline = false);

/// <summary>
/// Platform-agnostic management service for reading <c>state/*.status.json</c> and atomically mutating
/// <c>config/*.json</c> without opening any control socket to <c>Kite.Server</c>.
/// </summary>
public sealed class KiteManagementService
{
    private readonly KiteHomeLayout _layout;
    private ManagementWorkspaceSnapshot? _cachedSnapshot;
    private long _cachedAtTick;
    private long _cachedFingerprint;

    public KiteManagementService(KiteHomeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        _layout.EnsureDirectoriesCreated();
        History = new ConfigSnapshotHistoryService(layout);
    }

    public KiteHomeLayout Layout => _layout;
    public ConfigSnapshotHistoryService History { get; }

    public void InvalidateCache() => Interlocked.Exchange(ref _cachedAtTick, 0);

    public async ValueTask EnsureDefaultWorkspaceSeededAsync(CancellationToken ct = default)
    {
        _layout.EnsureDirectoriesCreated();

        if (!File.Exists(_layout.ServerConfigPath))
        {
            await AtomicJsonStore.WriteAtomicAsync(
                _layout.ServerConfigPath,
                new ServerConfig(),
                KiteJsonContext.Default.ServerConfig,
                ct).ConfigureAwait(false);
        }

        if (!Directory.EnumerateFiles(_layout.PoolsConfigDirectory, "*.json").Any())
        {
            var defaultPool = new ApplicationPoolConfig
            {
                Id = "default-pool",
                Name = "Default Application Pool",
                DesiredState = DesiredResourceState.Started,
                RecycleVersion = 1,
                ProcessModel = new PoolProcessModelConfig
                {
                    WorkerCount = 2,
                    IdleTimeoutMinutes = 20,
                    GcMode = PoolGcMode.DatasServer
                }
            };

            await AtomicJsonStore.WriteAtomicAsync(
                _layout.GetPoolConfigPath(defaultPool.Id),
                defaultPool,
                KiteJsonContext.Default.ApplicationPoolConfig,
                ct).ConfigureAwait(false);
        }

        if (!Directory.EnumerateFiles(_layout.SitesConfigDirectory, "*.json").Any())
        {
            string defaultStaticRoot = Path.Combine(_layout.RootDirectory, "wwwroot", "default-site");
            Directory.CreateDirectory(defaultStaticRoot);
            string indexPath = Path.Combine(defaultStaticRoot, "index.html");
            if (!File.Exists(indexPath))
            {
                await File.WriteAllTextAsync(
                    indexPath,
                    "<!DOCTYPE html><html><head><title>Kite Default Site</title></head><body><h1>Welcome to Kite Web Server</h1></body></html>",
                    ct).ConfigureAwait(false);
            }

            var defaultSite = new SiteConfig
            {
                Id = "default-site",
                Name = "Default Web Site",
                DesiredState = DesiredResourceState.Started,
                Bindings =
                [
                    new SiteBindingConfig { Protocol = "http", IpAddress = "*", Port = 8080, Host = "*" },
                    new SiteBindingConfig
                    {
                        Protocol = "https",
                        IpAddress = "*",
                        Port = 8443,
                        Host = "localhost",
                        Tls = new SiteTlsConfig { UseSelfSignedDevelopmentCert = true, AllowHttp2 = true }
                    }
                ],
                RootApplication = new SiteApplicationConfig
                {
                    Path = "/",
                    PoolId = "default-pool",
                    PhysicalPath = defaultStaticRoot,
                    HostingMode = WorkloadHostingMode.StaticOnly
                }
            };

            await AtomicJsonStore.WriteAtomicAsync(
                _layout.GetSiteConfigPath(defaultSite.Id),
                defaultSite,
                KiteJsonContext.Default.SiteConfig,
                ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<ManagementWorkspaceSnapshot> GetWorkspaceSnapshotAsync(CancellationToken ct = default)
    {
        long nowTick = Environment.TickCount64;
        long currentFingerprint = ComputeFilesystemTimestampFingerprint();
        if (_cachedSnapshot is { } cached &&
            (cached.IsServerOnline || !File.Exists(_layout.ServerStatusPath)) &&
            nowTick - Volatile.Read(ref _cachedAtTick) < 1000 &&
            currentFingerprint == Volatile.Read(ref _cachedFingerprint))
        {
            return cached;
        }

        var (serverCfg, _) = await AtomicJsonStore.ReadWithHashAsync(
            _layout.ServerConfigPath,
            KiteJsonContext.Default.ServerConfig,
            ct).ConfigureAwait(false);
        serverCfg ??= new ServerConfig();

        var (serverStatus, _) = await AtomicJsonStore.ReadWithHashAsync(
            _layout.ServerStatusPath,
            KiteJsonContext.Default.ServerStatus,
            ct).ConfigureAwait(false);

        bool isServerOnline = IsServerProcessRunning(serverStatus);

        var pools = new List<PoolCompositeView>();
        if (Directory.Exists(_layout.PoolsConfigDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(_layout.PoolsConfigDirectory, "*.json").OrderBy(f => f))
            {
                if (file.Contains(".tmp.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var (poolCfg, _) = await AtomicJsonStore.ReadWithHashAsync(
                    file,
                    KiteJsonContext.Default.ApplicationPoolConfig,
                    ct).ConfigureAwait(false);

                if (poolCfg is not null)
                {
                    var (poolStatus, _) = await AtomicJsonStore.ReadWithHashAsync(
                        _layout.GetPoolStatusPath(poolCfg.Id),
                        KiteJsonContext.Default.ApplicationPoolStatus,
                        ct).ConfigureAwait(false);
                    pools.Add(new PoolCompositeView(poolCfg, poolStatus, isServerOnline));
                }
            }
        }

        var sites = new List<SiteCompositeView>();
        if (Directory.Exists(_layout.SitesConfigDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(_layout.SitesConfigDirectory, "*.json").OrderBy(f => f))
            {
                if (file.Contains(".tmp.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var (siteCfg, _) = await AtomicJsonStore.ReadWithHashAsync(
                    file,
                    KiteJsonContext.Default.SiteConfig,
                    ct).ConfigureAwait(false);

                if (siteCfg is not null)
                {
                    var (siteStatus, _) = await AtomicJsonStore.ReadWithHashAsync(
                        _layout.GetSiteStatusPath(siteCfg.Id),
                        KiteJsonContext.Default.SiteStatus,
                        ct).ConfigureAwait(false);
                    sites.Add(new SiteCompositeView(siteCfg, siteStatus, isServerOnline));
                }
            }
        }

        var snapshot = new ManagementWorkspaceSnapshot(_layout.RootDirectory, serverCfg, serverStatus, pools, sites, isServerOnline);
        _cachedSnapshot = snapshot;
        Volatile.Write(ref _cachedFingerprint, currentFingerprint);
        Volatile.Write(ref _cachedAtTick, Environment.TickCount64);
        return snapshot;
    }

    private long ComputeFilesystemTimestampFingerprint()
    {
        long hash = 17;
        hash = (hash * 31) + File.GetLastWriteTimeUtc(_layout.ServerConfigPath).Ticks;
        hash = (hash * 31) + Directory.GetLastWriteTimeUtc(_layout.PoolsConfigDirectory).Ticks;
        hash = (hash * 31) + Directory.GetLastWriteTimeUtc(_layout.SitesConfigDirectory).Ticks;
        return hash;
    }

    public static bool IsServerProcessRunning(ServerStatus? status)
    {
        if (status is null || status.ServerPid <= 0)
        {
            return false;
        }

        if (status.ServerPid == Environment.ProcessId)
        {
            return true;
        }

        // Fast-path: if status heartbeat was updated within the last 3 seconds, avoid opening an OS process handle on every UI render
        if ((DateTimeOffset.UtcNow - status.UpdatedAtUtc).TotalSeconds < 3.0)
        {
            return true;
        }

        try
        {
            using Process proc = Process.GetProcessById(status.ServerPid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ensures the <c>Kite.Server</c> background daemon process is running for this <c>KITE_HOME</c>.
    /// If not already running, locates <c>Kite.Server.dll</c> and spawns it in the background.
    /// </summary>
    public async ValueTask<bool> EnsureServerDaemonRunningAsync(CancellationToken ct = default)
    {
        var (existingStatus, _) = await AtomicJsonStore.ReadWithHashAsync(
            _layout.ServerStatusPath,
            KiteJsonContext.Default.ServerStatus,
            ct).ConfigureAwait(false);

        if (IsServerProcessRunning(existingStatus))
        {
            return true;
        }

        string? serverDllPath = LocateKiteServerDll();
        if (string.IsNullOrEmpty(serverDllPath) || !File.Exists(serverDllPath))
        {
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{serverDllPath}\" --home \"{_layout.RootDirectory}\"",
            WorkingDirectory = Path.GetDirectoryName(serverDllPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["KITE_HOME"] = _layout.RootDirectory;

        Process? spawnedProcess;
        try
        {
            spawnedProcess = Process.Start(psi);
            if (spawnedProcess is null)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        for (int i = 0; i < 60; i++)
        {
            if (spawnedProcess.HasExited)
            {
                return false;
            }

            await Task.Delay(25, ct).ConfigureAwait(false);
            var (status, _) = await AtomicJsonStore.ReadWithHashAsync(
                _layout.ServerStatusPath,
                KiteJsonContext.Default.ServerStatus,
                ct).ConfigureAwait(false);

            if (IsServerProcessRunning(status))
            {
                InvalidateCache();
                return true;
            }
        }

        return false;
    }

    public async ValueTask<bool> StopServerDaemonAsync(CancellationToken ct = default)
    {
        var (status, _) = await AtomicJsonStore.ReadWithHashAsync(
            _layout.ServerStatusPath,
            KiteJsonContext.Default.ServerStatus,
            ct).ConfigureAwait(false);

        if (status is not null && status.ServerPid > 0 && status.ServerPid != Environment.ProcessId)
        {
            try
            {
                using Process proc = Process.GetProcessById(status.ServerPid);
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Process already exited
            }
        }

        if (File.Exists(_layout.ServerStatusPath))
        {
            try { File.Delete(_layout.ServerStatusPath); } catch { }
        }

        InvalidateCache();
        return true;
    }

    private static string? LocateKiteServerDll()
    {
        string localCandidate = Path.Combine(AppContext.BaseDirectory, "Kite.Server.dll");
        if (IsRunnableDotNetApp(localCandidate))
        {
            return localCandidate;
        }

        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Kite.slnx")))
            {
                string debugDll = Path.Combine(current, "src", "Kite.Server", "bin", "Debug", "net10.0", "Kite.Server.dll");
                if (IsRunnableDotNetApp(debugDll))
                {
                    return debugDll;
                }

                string releaseDll = Path.Combine(current, "src", "Kite.Server", "bin", "Release", "net10.0", "Kite.Server.dll");
                if (IsRunnableDotNetApp(releaseDll))
                {
                    return releaseDll;
                }

                break;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private static bool IsRunnableDotNetApp(string dllPath) =>
        File.Exists(dllPath) && File.Exists(Path.ChangeExtension(dllPath, ".runtimeconfig.json"));

    public async ValueTask SaveServerConfigAsync(ServerConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        KiteConfigVersion.EnsureSupported(config.ConfigVersion, "config/server.json");
        await CapturePreSaveSnapshotIfExistingAsync(_layout.ServerConfigPath, "Update server.json", "server", "server", ct).ConfigureAwait(false);
        await AtomicJsonStore.WriteAtomicAsync(
            _layout.ServerConfigPath,
            config,
            KiteJsonContext.Default.ServerConfig,
            ct).ConfigureAwait(false);
        InvalidateCache();
    }

    public async ValueTask SavePoolConfigAsync(ApplicationPoolConfig pool, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pool);
        KiteConfigVersion.EnsureSupported(pool.ConfigVersion, $"config/pools/{pool.Id}.json");
        string poolPath = _layout.GetPoolConfigPath(pool.Id);
        await CapturePreSaveSnapshotIfExistingAsync(poolPath, $"Update pool '{pool.Id}'", "pool", pool.Id, ct).ConfigureAwait(false);
        await AtomicJsonStore.WriteAtomicAsync(
            poolPath,
            pool,
            KiteJsonContext.Default.ApplicationPoolConfig,
            ct).ConfigureAwait(false);
        InvalidateCache();
    }

    public async ValueTask<bool> SetPoolDesiredStateAsync(string poolId, DesiredResourceState state, CancellationToken ct = default)
    {
        string path = _layout.GetPoolConfigPath(poolId);
        var (pool, _) = await AtomicJsonStore.ReadWithHashAsync(path, KiteJsonContext.Default.ApplicationPoolConfig, ct).ConfigureAwait(false);
        if (pool is null)
        {
            return false;
        }

        pool.DesiredState = state;
        await SavePoolConfigAsync(pool, ct).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<long?> RecyclePoolAsync(string poolId, CancellationToken ct = default)
    {
        string path = _layout.GetPoolConfigPath(poolId);
        var (pool, _) = await AtomicJsonStore.ReadWithHashAsync(path, KiteJsonContext.Default.ApplicationPoolConfig, ct).ConfigureAwait(false);
        if (pool is null)
        {
            return null;
        }

        pool.RecycleVersion++;
        await SavePoolConfigAsync(pool, ct).ConfigureAwait(false);
        return pool.RecycleVersion;
    }

    public async ValueTask SaveSiteConfigAsync(SiteConfig site, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        KiteConfigVersion.EnsureSupported(site.ConfigVersion, $"config/sites/{site.Id}.json");
        string sitePath = _layout.GetSiteConfigPath(site.Id);
        await CapturePreSaveSnapshotIfExistingAsync(sitePath, $"Update site '{site.Id}'", "site", site.Id, ct).ConfigureAwait(false);
        await AtomicJsonStore.WriteAtomicAsync(
            sitePath,
            site,
            KiteJsonContext.Default.SiteConfig,
            ct).ConfigureAwait(false);
        InvalidateCache();
    }

    private async ValueTask CapturePreSaveSnapshotIfExistingAsync(
        string targetFile,
        string reason,
        string kind,
        string id,
        CancellationToken ct)
    {
        if (!File.Exists(targetFile))
        {
            return;
        }

        try
        {
            await History.CaptureSnapshotAsync(reason, kind, id, ct).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public async ValueTask<bool> RollbackSnapshotAsync(string snapshotId, CancellationToken ct = default)
    {
        bool ok = await History.RollbackToSnapshotAsync(snapshotId, ct).ConfigureAwait(false);
        if (ok)
        {
            InvalidateCache();
        }
        return ok;
    }

    /// <summary>
    /// Stages an alternate folder path onto the inactive blue/green deployment slot of a site.
    /// </summary>
    public async ValueTask<SiteConfig?> StageSiteSlotAsync(
        string siteId,
        string stagingPhysicalPath,
        CancellationToken ct = default)
    {
        string path = _layout.GetSiteConfigPath(siteId);
        var (site, _) = await AtomicJsonStore.ReadWithHashAsync(path, KiteJsonContext.Default.SiteConfig, ct).ConfigureAwait(false);
        if (site is null)
        {
            return null;
        }

        site.RootApplication.StagingPhysicalPath = stagingPhysicalPath;
        await SaveSiteConfigAsync(site, ct).ConfigureAwait(false);
        return site;
    }

    /// <summary>
    /// Atomically swaps <c>PhysicalPath</c> and <c>StagingPhysicalPath</c> and toggles <c>ActiveSlot</c> (<c>blue</c> &lt;-&gt; <c>green</c>).
    /// </summary>
    public async ValueTask<SiteConfig?> SwapSiteSlotAsync(string siteId, CancellationToken ct = default)
    {
        string path = _layout.GetSiteConfigPath(siteId);
        var (site, _) = await AtomicJsonStore.ReadWithHashAsync(path, KiteJsonContext.Default.SiteConfig, ct).ConfigureAwait(false);
        if (site is null || string.IsNullOrWhiteSpace(site.RootApplication.StagingPhysicalPath))
        {
            return null;
        }

        string oldActive = site.RootApplication.PhysicalPath;
        site.RootApplication.PhysicalPath = site.RootApplication.StagingPhysicalPath;
        site.RootApplication.StagingPhysicalPath = oldActive;
        site.RootApplication.ActiveSlot = string.Equals(site.RootApplication.ActiveSlot, "blue", StringComparison.OrdinalIgnoreCase)
            ? "green"
            : "blue";

        await SaveSiteConfigAsync(site, ct).ConfigureAwait(false);
        return site;
    }

    /// <summary>
    /// Parses a third-party configuration (<c>nginx</c>, <c>caddy</c>, or <c>iis</c>) and optionally applies it to disk.
    /// </summary>
    public async ValueTask<LegacyImportResult> ImportLegacyConfigAsync(
        string format,
        string content,
        bool apply,
        CancellationToken ct = default)
    {
        LegacyImportResult result = LegacyConfigImporter.Import(format, content);
        if (!apply)
        {
            return result;
        }

        await History.CaptureSnapshotAsync($"Pre-import ({format})", "import", format, ct).ConfigureAwait(false);

        foreach (ApplicationPoolConfig pool in result.Pools)
        {
            await AtomicJsonStore.WriteAtomicAsync(_layout.GetPoolConfigPath(pool.Id), pool, KiteJsonContext.Default.ApplicationPoolConfig, ct)
                .ConfigureAwait(false);
        }

        if (result.Upstreams.Count > 0)
        {
            var (srv, _) = await AtomicJsonStore.ReadWithHashAsync(_layout.ServerConfigPath, KiteJsonContext.Default.ServerConfig, ct)
                .ConfigureAwait(false);
            srv ??= new ServerConfig();
            foreach (UpstreamClusterConfig uc in result.Upstreams)
            {
                srv.Upstreams.RemoveAll(u => string.Equals(u.Id, uc.Id, StringComparison.OrdinalIgnoreCase));
                srv.Upstreams.Add(uc);
            }
            await AtomicJsonStore.WriteAtomicAsync(_layout.ServerConfigPath, srv, KiteJsonContext.Default.ServerConfig, ct)
                .ConfigureAwait(false);
        }

        foreach (SiteConfig site in result.Sites)
        {
            await AtomicJsonStore.WriteAtomicAsync(_layout.GetSiteConfigPath(site.Id), site, KiteJsonContext.Default.SiteConfig, ct)
                .ConfigureAwait(false);
        }

        InvalidateCache();
        return result;
    }

    /// <summary>
    /// Simulates a request through all active routing, security, WAF, rewrite, and gateway stages without network I/O.
    /// </summary>
    public async ValueTask<PipelineTraceResult> SimulatePipelineAsync(
        string method,
        string scheme,
        string host,
        int port,
        string path,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        ManagementWorkspaceSnapshot snap = await GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
        var routeTable = new HostRouteTable(snap.Sites.Select(s => s.Config), snap.ServerConfig);
        string cleanPath = string.IsNullOrWhiteSpace(path) ? "/" : (path.StartsWith('/') ? path : "/" + path);
        string cleanMethod = string.IsNullOrWhiteSpace(method) ? "GET" : method.ToUpperInvariant();
        string cleanHost = string.IsNullOrWhiteSpace(host) ? "localhost" : host;
        int cleanPort = port > 0 ? port : (string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);

        var trace = new PipelineTraceResult
        {
            Method = cleanMethod,
            Host = cleanHost,
            Path = cleanPath,
            RewrittenPath = cleanPath
        };

        if (!routeTable.TryMatchSite(cleanPort, cleanHost.AsSpan(), out CompiledSiteRoute? siteRoute) || siteRoute is null)
        {
            trace.SimulatedStatusCode = 404;
            trace.FinalOutcome = $"404 Not Found: No active site matched {scheme}://{cleanHost}:{cleanPort}";
            trace.Stages.Add(new FrebStageTiming { StageName = "HostBindingMatch", Outcome = "No match (404)" });
            return trace;
        }

        trace.MatchedSiteId = siteRoute.Site.Id;
        trace.Stages.Add(new FrebStageTiming { StageName = "HostBindingMatch", Outcome = $"Site '{siteRoute.Site.Id}'" });

        SiteFeaturesConfig eff = siteRoute.Features.EffectiveConfig;
        if (!string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) && eff.TlsPolicy.RedirectHttpToHttps)
        {
            trace.SimulatedStatusCode = eff.TlsPolicy.RedirectHttpToHttpsStatusCode;
            trace.FinalOutcome = $"HTTPS Redirect ({trace.SimulatedStatusCode})";
            trace.Stages.Add(new FrebStageTiming { StageName = "HttpsRedirect", Outcome = trace.FinalOutcome });
            return trace;
        }

        if (eff.EdgeWaf.Enabled && siteRoute.Features.WafTargetPatterns is { } patterns && cleanPath.AsSpan().ContainsAny(patterns))
        {
            trace.SimulatedStatusCode = 403;
            trace.FinalOutcome = "Blocked by Edge WAF (403.501)";
            trace.Stages.Add(new FrebStageTiming { StageName = "EdgeWaf", Outcome = trace.FinalOutcome });
            return trace;
        }
        trace.Stages.Add(new FrebStageTiming { StageName = "EdgeWaf", Outcome = eff.EdgeWaf.Enabled ? "Passed" : "Disabled" });

        trace.Stages.Add(new FrebStageTiming
        {
            StageName = "RateLimiting",
            Outcome = eff.RateLimiting.Enabled
                ? $"{eff.RateLimiting.PermitLimit} req/{eff.RateLimiting.WindowSeconds}s ({eff.RateLimiting.Algorithm})"
                : "Disabled"
        });

        // Simulate sub-app & declarative route rule matching
        var routeCtx = new KiteRouteMatchContext();
        siteRoute.PathTree.MatchInto(cleanPath.AsSpan(), routeCtx);
        SiteApplicationConfig app = routeCtx.Application ?? siteRoute.Site.RootApplication;
        trace.MatchedApplicationPath = app.Path;

        var fakeContext = new DefaultHttpContext();
        fakeContext.Request.Method = cleanMethod;
        fakeContext.Request.Path = cleanPath;
        if (headers is not null)
        {
            foreach (KeyValuePair<string, string> kv in headers)
            {
                fakeContext.Request.Headers[kv.Key] = kv.Value;
            }
        }

        RouteMatchResult ruleMatch = RouteRuleMatcher.Match(eff.Routes, fakeContext.Request);
        if (ruleMatch.Rule is { } rule)
        {
            trace.MatchedRouteRuleId = rule.Id;
            trace.RewrittenPath = ruleMatch.TransformedPath;
            foreach (KeyValuePair<string, string> h in rule.Transform.SetRequestHeaders)
            {
                trace.AppliedHeaders[h.Key] = h.Value;
            }

            if (rule.FaultInjection.Enabled && rule.FaultInjection.AbortPercent >= 100)
            {
                trace.SimulatedStatusCode = rule.FaultInjection.AbortStatusCode;
                trace.FinalOutcome = $"Fault Injected Abort ({trace.SimulatedStatusCode})";
                trace.Stages.Add(new FrebStageTiming { StageName = "RouteRuleMatch", Outcome = $"Rule '{rule.Id}' -> Abort {trace.SimulatedStatusCode}" });
                return trace;
            }

            string targetDesc = !string.IsNullOrWhiteSpace(rule.TargetClusterId)
                ? $"cluster://{rule.TargetClusterId}"
                : (!string.IsNullOrWhiteSpace(rule.TargetPoolId) ? $"pool://{rule.TargetPoolId}" : app.HostingMode.ToString());
            trace.ResolvedTarget = targetDesc;
            trace.Stages.Add(new FrebStageTiming { StageName = "RouteRuleMatch", Outcome = $"Matched '{rule.Id}' -> {targetDesc}" });
        }
        else
        {
            trace.RewrittenPath = routeCtx.GetRemainingSubPath(cleanPath.AsSpan()).ToString();
            trace.ResolvedTarget = !string.IsNullOrWhiteSpace(app.UpstreamClusterId)
                ? $"cluster://{app.UpstreamClusterId}"
                : (app.HostingMode == WorkloadHostingMode.StaticOnly ? $"static://{app.PhysicalPath}" : $"pool://{app.PoolId}");
            trace.Stages.Add(new FrebStageTiming { StageName = "ApplicationDispatch", Outcome = trace.ResolvedTarget });
        }

        if (eff.ForwardAuth.Enabled)
        {
            trace.Stages.Add(new FrebStageTiming { StageName = "ForwardAuth", Outcome = $"Check -> {eff.ForwardAuth.EndpointUrl}" });
        }

        trace.SimulatedStatusCode = 200;
        trace.FinalOutcome = $"Allowed -> {trace.ResolvedTarget} (path='{trace.RewrittenPath}')";
        return trace;
    }

    public async ValueTask<bool> SetSiteDesiredStateAsync(string siteId, DesiredResourceState state, CancellationToken ct = default)
    {
        string path = _layout.GetSiteConfigPath(siteId);
        var (site, _) = await AtomicJsonStore.ReadWithHashAsync(path, KiteJsonContext.Default.SiteConfig, ct).ConfigureAwait(false);
        if (site is null)
        {
            return false;
        }

        site.DesiredState = state;
        await SaveSiteConfigAsync(site, ct).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<IReadOnlyList<string>> ReadRecentSiteLogsAsync(string siteId, int maxLines = 100, CancellationToken ct = default)
    {
        string logDir = _layout.GetSiteLogDirectory(siteId);
        if (!Directory.Exists(logDir))
        {
            return [];
        }

        string? latestLog = Directory.EnumerateFiles(logDir, "*.log")
            .Concat(Directory.EnumerateFiles(logDir, "*.ndjson"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(f => f)
            .FirstOrDefault();

        if (latestLog is null || !File.Exists(latestLog))
        {
            return [];
        }

        var lines = new List<string>();
        await using var fs = new FileStream(latestLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is string line)
        {
            if (!line.StartsWith('#'))
            {
                lines.Add(line);
            }
        }

        return lines.Count <= maxLines ? lines : lines.Skip(lines.Count - maxLines).ToList();
    }

    public async ValueTask<bool> MutateFeaturesAsync(
        string? siteId,
        Action<SiteFeaturesConfig> mutate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        if (string.IsNullOrWhiteSpace(siteId))
        {
            var (srv, _) = await AtomicJsonStore.ReadWithHashAsync(
                _layout.ServerConfigPath,
                KiteJsonContext.Default.ServerConfig,
                ct).ConfigureAwait(false);
            srv ??= new ServerConfig();
            srv.Features.InheritServerDefaults = false;
            mutate(srv.Features);
            await SaveServerConfigAsync(srv, ct).ConfigureAwait(false);
            return true;
        }

        string sitePath = _layout.GetSiteConfigPath(siteId);
        var (site, _) = await AtomicJsonStore.ReadWithHashAsync(
            sitePath,
            KiteJsonContext.Default.SiteConfig,
            ct).ConfigureAwait(false);
        if (site is null)
        {
            return false;
        }

        if (site.Features.InheritServerDefaults)
        {
            var (srv, _) = await AtomicJsonStore.ReadWithHashAsync(
                _layout.ServerConfigPath,
                KiteJsonContext.Default.ServerConfig,
                ct).ConfigureAwait(false);
            if (srv is not null)
            {
                site.Features = CompiledSiteFeatures.ResolveEffectiveConfig(site.Features, srv.Features, out _);
            }
            site.Features.InheritServerDefaults = false;
        }

        mutate(site.Features);
        await SaveSiteConfigAsync(site, ct).ConfigureAwait(false);
        return true;
    }

    public async ValueTask MutateServerConfigAsync(Action<ServerConfig> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var (srv, _) = await AtomicJsonStore.ReadWithHashAsync(
            _layout.ServerConfigPath,
            KiteJsonContext.Default.ServerConfig,
            ct).ConfigureAwait(false);
        srv ??= new ServerConfig();
        mutate(srv);
        await SaveServerConfigAsync(srv, ct).ConfigureAwait(false);
    }

    public async ValueTask<bool> SetSiteInheritanceAsync(string siteId, bool inheritFromServer, CancellationToken ct = default)
    {
        string sitePath = _layout.GetSiteConfigPath(siteId);
        var (site, _) = await AtomicJsonStore.ReadWithHashAsync(sitePath, KiteJsonContext.Default.SiteConfig, ct).ConfigureAwait(false);
        if (site is null)
        {
            return false;
        }

        if (!inheritFromServer && site.Features.InheritServerDefaults)
        {
            var (srv, _) = await AtomicJsonStore.ReadWithHashAsync(_layout.ServerConfigPath, KiteJsonContext.Default.ServerConfig, ct).ConfigureAwait(false);
            if (srv is not null)
            {
                site.Features = CompiledSiteFeatures.ResolveEffectiveConfig(site.Features, srv.Features, out _);
            }
        }

        site.Features.InheritServerDefaults = inheritFromServer;
        await SaveSiteConfigAsync(site, ct).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<string> SaveRawConfigJsonAsync(string? siteId, string rawJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteId))
        {
            ServerConfig? parsed = JsonSerializer.Deserialize(rawJson, KiteJsonContext.Default.ServerConfig);
            if (parsed is null)
            {
                throw new InvalidOperationException("Deserialized ServerConfig was null.");
            }
            await SaveServerConfigAsync(parsed, ct).ConfigureAwait(false);
            return "Validated and saved raw JSON to config/server.json.";
        }

        SiteConfig? parsedSite = JsonSerializer.Deserialize(rawJson, KiteJsonContext.Default.SiteConfig);
        if (parsedSite is null)
        {
            throw new InvalidOperationException("Deserialized SiteConfig was null.");
        }
        if (string.IsNullOrWhiteSpace(parsedSite.Id))
        {
            parsedSite.Id = siteId;
        }
        await SaveSiteConfigAsync(parsedSite, ct).ConfigureAwait(false);
        return $"Validated and saved raw JSON to config/sites/{parsedSite.Id}.json.";
    }

    public async ValueTask<long> FlushOutputCacheAsync(string? siteId, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(siteId))
        {
            string sitePath = _layout.GetSiteConfigPath(siteId);
            var (site, _) = await AtomicJsonStore.ReadWithHashAsync(sitePath, KiteJsonContext.Default.SiteConfig, ct).ConfigureAwait(false);
            if (site is not null)
            {
                site.Features.Caching.FlushGeneration++;
                await SaveSiteConfigAsync(site, ct).ConfigureAwait(false);
                return site.Features.Caching.FlushGeneration;
            }
        }

        var (srv, _) = await AtomicJsonStore.ReadWithHashAsync(_layout.ServerConfigPath, KiteJsonContext.Default.ServerConfig, ct).ConfigureAwait(false);
        srv ??= new ServerConfig();
        srv.Features.Caching.FlushGeneration++;
        await SaveServerConfigAsync(srv, ct).ConfigureAwait(false);

        if (Directory.Exists(_layout.SitesConfigDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(_layout.SitesConfigDirectory, "*.json"))
            {
                if (file.Contains(".tmp.", StringComparison.OrdinalIgnoreCase)) continue;
                var (s, _) = await AtomicJsonStore.ReadWithHashAsync(file, KiteJsonContext.Default.SiteConfig, ct).ConfigureAwait(false);
                if (s is not null && s.Features.Caching.FlushGeneration < srv.Features.Caching.FlushGeneration)
                {
                    s.Features.Caching.FlushGeneration = srv.Features.Caching.FlushGeneration;
                    await SaveSiteConfigAsync(s, ct).ConfigureAwait(false);
                }
            }
        }

        return srv.Features.Caching.FlushGeneration;
    }

    public async ValueTask<IReadOnlyList<FrebTraceRecord>> ReadRecentFrebTracesAsync(
        string? siteId = null,
        int maxCount = 50,
        CancellationToken ct = default)
    {
        var files = new List<string>();
        if (!string.IsNullOrWhiteSpace(siteId))
        {
            string dir = _layout.GetSiteFrebDirectory(siteId);
            if (Directory.Exists(dir))
            {
                files.AddRange(Directory.EnumerateFiles(dir, "*.json"));
            }
        }
        else if (Directory.Exists(_layout.FrebLogsDirectory))
        {
            files.AddRange(Directory.EnumerateFiles(_layout.FrebLogsDirectory, "*.json", SearchOption.AllDirectories));
        }

        var results = new List<FrebTraceRecord>();
        foreach (string file in files.OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).Take(maxCount))
        {
            var (rec, _) = await AtomicJsonStore.ReadWithHashAsync(file, KiteJsonContext.Default.FrebTraceRecord, ct).ConfigureAwait(false);
            if (rec is not null)
            {
                results.Add(rec);
            }
        }

        return results;
    }

    public async ValueTask<string> CreateWorkerDumpAsync(int processId, string poolId, CancellationToken ct = default)
    {
        _layout.EnsureDirectoriesCreated();
        string safePool = KiteHomeLayout.SanitizeFileName(string.IsNullOrWhiteSpace(poolId) ? "pool" : poolId);
        string dumpPath = Path.Combine(_layout.DumpsLogsDirectory, $"kite_{safePool}_pid{processId}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.dmp");

        if (OperatingSystem.IsWindows() && processId > 0 && processId != Environment.ProcessId)
        {
            try
            {
                using Process proc = Process.GetProcessById(processId);
                await using var fs = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                bool ok = MiniDumpWriteDump(
                    proc.Handle,
                    (uint)processId,
                    fs.SafeFileHandle,
                    2 /* MiniDumpWithFullMemory */,
                    nint.Zero,
                    nint.Zero,
                    nint.Zero);
                if (ok)
                {
                    return dumpPath;
                }
            }
            catch
            {
            }
        }

        // In-process worker or cross-platform diagnostic dump snapshot
        var sb = new StringBuilder();
        sb.AppendLine($"# KITE WORKER DIAGNOSTIC DUMP SNAPSHOT ({DateTimeOffset.UtcNow:O})");
        sb.AppendLine($"Pool: {poolId}");
        sb.AppendLine($"PID: {processId} (Host PID: {Environment.ProcessId})");
        sb.AppendLine($"Managed Heap: {GC.GetTotalMemory(false):N0} bytes");
        try
        {
            using Process proc = processId > 0 ? Process.GetProcessById(processId) : Process.GetCurrentProcess();
            sb.AppendLine($"Working Set: {proc.WorkingSet64:N0} bytes");
            sb.AppendLine($"Virtual Memory: {proc.VirtualMemorySize64:N0} bytes");
            sb.AppendLine($"Threads: {proc.Threads.Count}");
        }
        catch
        {
        }

        await File.WriteAllTextAsync(dumpPath, sb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
        return dumpPath;
    }

    public async ValueTask<string> KillWorkerProcessAsync(int processId, string poolId, CancellationToken ct = default)
    {
        if (processId > 0 && processId != Environment.ProcessId)
        {
            try
            {
                using Process proc = Process.GetProcessById(processId);
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                    InvalidateCache();
                    return $"Terminated external worker PID {processId} in pool '{poolId}'. Rapid-Fail monitor will auto-heal.";
                }
            }
            catch (Exception ex)
            {
                return $"Unable to kill PID {processId}: {ex.Message}";
            }
        }

        long? ver = await RecyclePoolAsync(poolId, ct).ConfigureAwait(false);
        return $"Recycled in-process worker pool '{poolId}' cleanly without terminating host process (RecycleVersion={ver}).";
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        nint hProcess,
        uint processId,
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
        uint dumpType,
        nint exceptionParam,
        nint userStreamParam,
        nint callbackParam);
}
