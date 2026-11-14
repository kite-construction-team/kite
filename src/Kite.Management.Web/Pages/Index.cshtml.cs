using Kite.Core.Models;
using Kite.Management.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kite.Management.Web.Pages;

[IgnoreAntiforgeryToken]
public sealed class IndexModel(KiteManagementService managementService) : PageModel
{
    public ManagementWorkspaceSnapshot Workspace { get; private set; } = default!;

    [BindProperty(SupportsGet = true)]
    public string Scope { get; set; } = "server";

    [BindProperty(SupportsGet = true)]
    public string? Entity { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Tab { get; set; } = "overview";

    public PoolCompositeView? SelectedPool { get; private set; }
    public SiteCompositeView? SelectedSite { get; private set; }
    public IReadOnlyList<string> SiteLogLines { get; private set; } = [];
    public string? FlashMessage { get; private set; }

    public async Task OnGetAsync(string? flash = null, CancellationToken ct = default)
    {
        FlashMessage = flash;
        await LoadWorkspaceContextAsync(ct).ConfigureAwait(false);
    }

    public async Task<IActionResult> OnPostSaveServerAsync(
        string serverName,
        int maxConcurrentConnections,
        long maxRequestBodySize,
        int keepAliveTimeoutSeconds,
        int headerTimeoutSeconds,
        bool defaultCompressionEnabled,
        bool defaultCachingEnabled,
        CancellationToken ct = default)
    {
        var snapshot = await managementService.GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
        ServerConfig cfg = snapshot.ServerConfig;
        cfg.ServerName = string.IsNullOrWhiteSpace(serverName) ? "Kite-Node" : serverName.Trim();
        cfg.MaxConcurrentConnections = Math.Max(10, maxConcurrentConnections);
        cfg.MaxRequestBodySize = Math.Max(1024, maxRequestBodySize);
        cfg.KeepAliveTimeoutSeconds = Math.Max(5, keepAliveTimeoutSeconds);
        cfg.HeaderTimeoutSeconds = Math.Max(5, headerTimeoutSeconds);
        cfg.DefaultCompressionEnabled = defaultCompressionEnabled;
        cfg.DefaultCachingEnabled = defaultCachingEnabled;

        await managementService.SaveServerConfigAsync(cfg, ct).ConfigureAwait(false);
        return RedirectToPage("/Index", new { scope = "server", tab = "settings", flash = "Server configuration saved atomically to config/server.json." });
    }

    public async Task<IActionResult> OnPostServerDaemonActionAsync(
        string actionType,
        CancellationToken ct = default)
    {
        if (string.Equals(actionType, "stop", StringComparison.OrdinalIgnoreCase))
        {
            await managementService.StopServerDaemonAsync(ct).ConfigureAwait(false);
            return RedirectToPage("/Index", new { scope = "server", flash = "Stopped Kite.Server background daemon process." });
        }

        if (string.Equals(actionType, "restart", StringComparison.OrdinalIgnoreCase))
        {
            await managementService.StopServerDaemonAsync(ct).ConfigureAwait(false);
        }

        bool started = await managementService.EnsureServerDaemonRunningAsync(ct).ConfigureAwait(false);
        string msg = started
            ? "Kite.Server background daemon is online and listening on configured bindings."
            : "Failed to start Kite.Server background daemon; check logs.";
        return RedirectToPage("/Index", new { scope = "server", flash = msg });
    }

    public async Task<IActionResult> OnPostPoolActionAsync(
        string poolId,
        string actionType,
        CancellationToken ct = default)
    {
        if (string.Equals(actionType, "recycle", StringComparison.OrdinalIgnoreCase))
        {
            long? nextVer = await managementService.RecyclePoolAsync(poolId, ct).ConfigureAwait(false);
            return RedirectToPage("/Index", new { scope = "pools", entity = poolId, flash = $"Triggered overlapped recycle (RecycleVersion={nextVer}) for pool '{poolId}'." });
        }

        DesiredResourceState desired = string.Equals(actionType, "stop", StringComparison.OrdinalIgnoreCase)
            ? DesiredResourceState.Stopped
            : DesiredResourceState.Started;

        await managementService.SetPoolDesiredStateAsync(poolId, desired, ct).ConfigureAwait(false);
        return RedirectToPage("/Index", new { scope = "pools", entity = poolId, flash = $"Application pool '{poolId}' desiredState set to {desired}." });
    }

    public async Task<IActionResult> OnPostSavePoolAsync(
        string poolId,
        string name,
        int workerCount,
        int idleTimeoutMinutes,
        PoolGcMode gcMode,
        long maxMemoryMegabytes,
        int periodicRestartMinutes,
        long maxRequests,
        bool disallowOverlappingRotation,
        bool rapidFailEnabled,
        int rapidFailMaxCrashes,
        int rapidFailIntervalMinutes,
        CancellationToken ct = default)
    {
        var snapshot = await managementService.GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
        ApplicationPoolConfig pool = snapshot.Pools
            .FirstOrDefault(p => string.Equals(p.Config.Id, poolId, StringComparison.OrdinalIgnoreCase))?.Config
            ?? new ApplicationPoolConfig { Id = poolId };

        pool.Name = string.IsNullOrWhiteSpace(name) ? poolId : name.Trim();
        pool.ProcessModel.WorkerCount = Math.Clamp(workerCount, 1, 64);
        pool.ProcessModel.IdleTimeoutMinutes = Math.Max(0, idleTimeoutMinutes);
        pool.ProcessModel.GcMode = gcMode;
        pool.Recycling.MaxMemoryMegabytes = Math.Max(0, maxMemoryMegabytes);
        pool.Recycling.PeriodicRestartMinutes = Math.Max(0, periodicRestartMinutes);
        pool.Recycling.MaxRequests = Math.Max(0, maxRequests);
        pool.Recycling.DisallowOverlappingRotation = disallowOverlappingRotation;
        pool.RapidFailProtection.Enabled = rapidFailEnabled;
        pool.RapidFailProtection.MaxCrashes = Math.Max(1, rapidFailMaxCrashes);
        pool.RapidFailProtection.IntervalMinutes = Math.Max(1, rapidFailIntervalMinutes);

        await managementService.SavePoolConfigAsync(pool, ct).ConfigureAwait(false);
        return RedirectToPage("/Index", new { scope = "pools", entity = pool.Id, tab = "process", flash = $"Saved pool '{pool.Id}' to config/pools/{pool.Id}.json." });
    }

    public async Task<IActionResult> OnPostSiteActionAsync(
        string siteId,
        string actionType,
        CancellationToken ct = default)
    {
        DesiredResourceState desired = string.Equals(actionType, "stop", StringComparison.OrdinalIgnoreCase)
            ? DesiredResourceState.Stopped
            : DesiredResourceState.Started;

        await managementService.SetSiteDesiredStateAsync(siteId, desired, ct).ConfigureAwait(false);
        return RedirectToPage("/Index", new { scope = "sites", entity = siteId, flash = $"Site '{siteId}' desiredState set to {desired}." });
    }

    public async Task<IActionResult> OnPostSaveSiteAsync(
        string siteId,
        string name,
        string poolId,
        string physicalPath,
        WorkloadHostingMode hostingMode,
        int httpPort,
        string httpHost,
        bool compressionEnabled,
        bool cachingEnabled,
        int cacheMaxAgeSeconds,
        bool hstsEnabled,
        bool directoryBrowsing,
        CancellationToken ct = default)
    {
        var snapshot = await managementService.GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
        SiteConfig site = snapshot.Sites
            .FirstOrDefault(s => string.Equals(s.Config.Id, siteId, StringComparison.OrdinalIgnoreCase))?.Config
            ?? new SiteConfig { Id = siteId };

        site.Name = string.IsNullOrWhiteSpace(name) ? siteId : name.Trim();
        site.RootApplication.PoolId = string.IsNullOrWhiteSpace(poolId) ? "default-pool" : poolId.Trim();
        site.RootApplication.PhysicalPath = physicalPath ?? "";
        site.RootApplication.HostingMode = hostingMode;

        if (httpPort > 0)
        {
            SiteBindingConfig? primaryHttp = site.Bindings.FirstOrDefault(b => string.Equals(b.Protocol, "http", StringComparison.OrdinalIgnoreCase));
            if (primaryHttp is null)
            {
                site.Bindings.Insert(0, new SiteBindingConfig
                {
                    Protocol = "http",
                    IpAddress = "*",
                    Port = httpPort,
                    Host = string.IsNullOrWhiteSpace(httpHost) ? "*" : httpHost.Trim()
                });
            }
            else
            {
                primaryHttp.Port = httpPort;
                primaryHttp.Host = string.IsNullOrWhiteSpace(httpHost) ? "*" : httpHost.Trim();
            }
        }

        site.Features.Compression.Enabled = compressionEnabled;
        site.Features.Caching.Enabled = cachingEnabled;
        site.Features.Caching.DefaultMaxAgeSeconds = Math.Max(0, cacheMaxAgeSeconds);
        site.Features.Hsts.Enabled = hstsEnabled;
        site.Features.DirectoryBrowsing = directoryBrowsing;

        await managementService.SaveSiteConfigAsync(site, ct).ConfigureAwait(false);
        return RedirectToPage("/Index", new { scope = "sites", entity = site.Id, flash = $"Saved site '{site.Id}' to config/sites/{site.Id}.json." });
    }

    private async Task LoadWorkspaceContextAsync(CancellationToken ct)
    {
        Workspace = await managementService.GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
        Scope = (Scope ?? "server").Trim().ToLowerInvariant();
        Tab = (Tab ?? "overview").Trim().ToLowerInvariant();

        if (Scope == "pools")
        {
            SelectedPool = Workspace.Pools.FirstOrDefault(p => string.Equals(p.Config.Id, Entity, StringComparison.OrdinalIgnoreCase))
                           ?? Workspace.Pools.FirstOrDefault();
            Entity = SelectedPool?.Config.Id;
        }
        else if (Scope == "sites")
        {
            SelectedSite = Workspace.Sites.FirstOrDefault(s => string.Equals(s.Config.Id, Entity, StringComparison.OrdinalIgnoreCase))
                           ?? Workspace.Sites.FirstOrDefault();
            Entity = SelectedSite?.Config.Id;

            if (SelectedSite is not null && Tab == "logs")
            {
                SiteLogLines = await managementService.ReadRecentSiteLogsAsync(SelectedSite.Config.Id, 100, ct).ConfigureAwait(false);
            }
        }
    }
}
