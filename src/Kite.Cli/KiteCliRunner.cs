using System.Security.Cryptography.X509Certificates;
using Kite.Core.Models;
using Kite.Core.Routing;
using Kite.Core.Serialization;
using Kite.Core.Storage;
using Kite.Management.Core;
using Kite.Management.Web;
using Kite.Server.Hosting;

namespace Kite.Cli;

/// <summary>
/// Entry logic for the <c>kite</c> CLI tool (<c>run</c>, <c>validate</c>, <c>reload</c>,
/// <c>route-test</c>, <c>certs status</c>, <c>logs</c>, and <c>schema</c>).
/// </summary>
public static class KiteCliRunner
{
    public static async Task<int> ExecuteAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken ct = default)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(output);
            return 0;
        }

        string command = args[0].Trim().ToLowerInvariant();
        string? homeArg = ReadOption(args, "--home");
        KiteHomeLayout layout = homeArg is not null
            ? new KiteHomeLayout(Path.GetFullPath(homeArg))
            : KiteHomeLayout.ResolveFromArgsAndEnv(args);

        switch (command)
        {
            case "run":
                return await RunServerAsync(args, layout, output, ct).ConfigureAwait(false);

            case "validate":
                return await ValidateConfigAsync(layout, output, error, ct).ConfigureAwait(false);

            case "reload":
                return await ReloadConfigAsync(layout, output, ct).ConfigureAwait(false);

            case "route-test":
                return await RouteTestAsync(args, layout, output, error, ct).ConfigureAwait(false);

            case "certs":
                return await CertsStatusAsync(args, layout, output, error, ct).ConfigureAwait(false);

            case "logs":
                return await TailLogsAsync(args, layout, output, ct).ConfigureAwait(false);

            case "schema":
                return await ExportSchemaAsync(args, output, error, ct).ConfigureAwait(false);

            case "history":
                return await HistoryListAsync(layout, output, ct).ConfigureAwait(false);

            case "rollback":
                return await HistoryRollbackAsync(args, layout, output, error, ct).ConfigureAwait(false);

            case "deploy":
                return await DeployBlueGreenAsync(args, layout, output, error, ct).ConfigureAwait(false);

            case "import":
                return await ImportLegacyCliAsync(args, layout, output, error, ct).ConfigureAwait(false);

            case "init":
                return await InitWorkspaceAsync(args, output, ct).ConfigureAwait(false);

            case "ps":
            case "instances":
                return await ListInstancesAsync(output, ct).ConfigureAwait(false);

            default:
                await error.WriteLineAsync($"Unknown command '{args[0]}'.").ConfigureAwait(false);
                PrintUsage(output);
                return 1;
        }
    }

    private static async Task<int> RunServerAsync(string[] args, KiteHomeLayout layout, TextWriter output, CancellationToken ct)
    {
        MeshMode? meshOverride = ReadOption(args, "--mesh")?.Trim().ToLowerInvariant() switch
        {
            "auto" => MeshMode.Auto,
            "leader" or "leaderonly" => MeshMode.LeaderOnly,
            "peer" or "peeronly" => MeshMode.PeerOnly,
            "off" or "disabled" => MeshMode.Disabled,
            _ => null
        };

        await using var engine = new KiteServerEngine(layout, meshModeOverride: meshOverride);
        HeadlessAdminHost? adminHost = null;

        int adminPort = int.TryParse(ReadOption(args, "--admin-port"), out int ap) ? ap : 0;
        string? adminUnix = ReadOption(args, "--admin-unix");
        string? adminToken = ReadOption(args, "--admin-token");
        if (adminPort > 0 || !string.IsNullOrWhiteSpace(adminUnix))
        {
            adminHost = new HeadlessAdminHost(layout, new HeadlessAdminOptions(adminPort, adminUnix, adminToken));
            await adminHost.StartAsync(ct).ConfigureAwait(false);
            await output.WriteLineAsync($"Kite Headless Admin listening on loopback:{adminHost.ListeningPort}").ConfigureAwait(false);
        }

        await engine.StartAsync(seedDefaultsIfEmpty: true, ct).ConfigureAwait(false);
        string domainsLabel = engine.MeshHostedDomains.Count > 0 ? string.Join(", ", engine.MeshHostedDomains) : "*";
        await output.WriteLineAsync($"Kite.Server running (id={engine.InstanceId}, role={engine.MeshRole}, home={layout.RootDirectory}, hosts=[{domainsLabel}]).").ConfigureAwait(false);
        if (engine.ActivePortRemaps.Count > 0)
        {
            string remapSummary = string.Join(", ", engine.ActivePortRemaps.OrderBy(static kv => kv.Key).Select(static kv => $"{kv.Key}->{kv.Value}"));
            await output.WriteLineAsync($"  Port fallback active ({remapSummary}) because privileged port(s) were in use or blocked by OS permissions.").ConfigureAwait(false);
        }
        if (engine.MeshRole == InstanceRole.MeshPeer && !string.IsNullOrWhiteSpace(engine.MeshDirectHttpUrl))
        {
            await output.WriteLineAsync($"  Joined local edge mesh as peer on {engine.MeshDirectHttpUrl} (routed by leader on front-door ports).").ConfigureAwait(false);
        }
        await output.WriteLineAsync("Press Ctrl+C or SIGTERM to drain and exit.").ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (adminHost is not null)
        {
            await adminHost.DisposeAsync().ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<ConfigSnapshot> LoadSnapshotOnceAsync(KiteHomeLayout layout, CancellationToken ct)
    {
        layout.EnsureDirectoriesCreated();
        await using var monitor = new FileSystemConfigMonitor(layout);
        await monitor.ReloadIfChangedAsync(cancellationToken: ct).ConfigureAwait(false);
        return monitor.CurrentSnapshot;
    }

    private static async Task<int> ValidateConfigAsync(KiteHomeLayout layout, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var versionErrors = new List<string>();
        await ValidateJsonVersionOnDiskAsync(layout.ServerConfigPath, "config/server.json", versionErrors, ct).ConfigureAwait(false);
        if (Directory.Exists(layout.PoolsConfigDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(layout.PoolsConfigDirectory, "*.json"))
            {
                await ValidateJsonVersionOnDiskAsync(file, $"config/pools/{Path.GetFileName(file)}", versionErrors, ct).ConfigureAwait(false);
            }
        }
        if (Directory.Exists(layout.SitesConfigDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(layout.SitesConfigDirectory, "*.json"))
            {
                await ValidateJsonVersionOnDiskAsync(file, $"config/sites/{Path.GetFileName(file)}", versionErrors, ct).ConfigureAwait(false);
            }
        }

        ConfigSnapshot snapshot = await LoadSnapshotOnceAsync(layout, ct).ConfigureAwait(false);
        var table = new HostRouteTable(snapshot.Sites.Values, snapshot.Server);

        await output.WriteLineAsync($"Validated server='{snapshot.Server.ServerName}', configVersion={KiteConfigVersion.Current}, pools={snapshot.Pools.Count}, sites={snapshot.Sites.Count}, upstreams={snapshot.Server.Upstreams.Count}.").ConfigureAwait(false);

        if (versionErrors.Count > 0 || table.BindingConflicts.Count > 0)
        {
            foreach (string err in versionErrors)
            {
                await error.WriteLineAsync($"Version Error: {err}").ConfigureAwait(false);
            }
            foreach (string conflict in table.BindingConflicts)
            {
                await error.WriteLineAsync($"Conflict: {conflict}").ConfigureAwait(false);
            }
            return 2;
        }

        await output.WriteLineAsync($"Configuration is VALID (0 conflicts) [configVersion={KiteConfigVersion.Current}].").ConfigureAwait(false);
        return 0;
    }

    private static async Task ValidateJsonVersionOnDiskAsync(string path, string label, List<string> errors, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using System.Text.Json.JsonDocument doc = await System.Text.Json.JsonDocument.ParseAsync(fs, new System.Text.Json.JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip
            }, ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("configVersion", out System.Text.Json.JsonElement verEl) ||
                verEl.ValueKind != System.Text.Json.JsonValueKind.String ||
                !KiteConfigVersion.IsSupported(verEl.GetString()))
            {
                string actual = verEl.ValueKind == System.Text.Json.JsonValueKind.Undefined
                    ? "<missing>"
                    : verEl.ToString();
                errors.Add($"{label}: expected 'configVersion': \"{KiteConfigVersion.Current}\", found '{actual}'.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{label}: invalid JSON ({ex.Message}).");
        }
    }

    private static async Task<int> ReloadConfigAsync(KiteHomeLayout layout, TextWriter output, CancellationToken ct)
    {
        layout.EnsureDirectoriesCreated();
        if (!File.Exists(layout.ServerConfigPath))
        {
            await AtomicJsonStore.WriteAtomicAsync(layout.ServerConfigPath, new ServerConfig(), KiteJsonContext.Default.ServerConfig, ct).ConfigureAwait(false);
        }
        else
        {
            var (srv, _) = await AtomicJsonStore.ReadWithHashAsync(layout.ServerConfigPath, KiteJsonContext.Default.ServerConfig, ct).ConfigureAwait(false);
            await AtomicJsonStore.WriteAtomicAsync(layout.ServerConfigPath, srv ?? new ServerConfig(), KiteJsonContext.Default.ServerConfig, ct).ConfigureAwait(false);
        }

        await output.WriteLineAsync($"Triggered atomic reload stamp on {layout.ServerConfigPath}.").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RouteTestAsync(string[] args, KiteHomeLayout layout, TextWriter output, TextWriter error, CancellationToken ct)
    {
        string? rawUrl = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (string.IsNullOrWhiteSpace(rawUrl) || !Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri))
        {
            await error.WriteLineAsync("Usage: kite route-test <absolute-url> [--home <path>]").ConfigureAwait(false);
            return 1;
        }

        ConfigSnapshot snapshot = await LoadSnapshotOnceAsync(layout, ct).ConfigureAwait(false);
        var table = new HostRouteTable(snapshot.Sites.Values, snapshot.Server);

        int port = uri.Port;
        if (!table.TryMatchSite(port, uri.Host.AsSpan(), out CompiledSiteRoute? siteRoute) || siteRoute is null)
        {
            await error.WriteLineAsync($"NO MATCH for {uri.Scheme}://{uri.Host}:{port}{uri.AbsolutePath}").ConfigureAwait(false);
            return 1;
        }

        VirtualRouteMatch routeMatch = siteRoute.PathTree.Match(uri.AbsolutePath.AsSpan());
        SiteApplicationConfig matchedApp = routeMatch.Application;
        string subPath = routeMatch.GetSubPath(uri.AbsolutePath.AsSpan()).ToString();
        SiteFeaturesConfig eff = siteRoute.Features.EffectiveConfig;

        await output.WriteLineAsync($"Matched Site      : {siteRoute.Site.Id} ({siteRoute.Site.Name})").ConfigureAwait(false);
        await output.WriteLineAsync($"Hosting Mode      : {matchedApp.HostingMode}").ConfigureAwait(false);
        await output.WriteLineAsync($"SubPath           : {subPath}").ConfigureAwait(false);
        await output.WriteLineAsync($"Upstream/Pool     : {(string.IsNullOrWhiteSpace(matchedApp.UpstreamClusterId) ? matchedApp.PoolId : $"cluster:{matchedApp.UpstreamClusterId}")}").ConfigureAwait(false);
        await output.WriteLineAsync($"Rate Limiting     : {(eff.RateLimiting.Enabled ? $"{eff.RateLimiting.Algorithm} ({eff.RateLimiting.PermitLimit}/{eff.RateLimiting.WindowSeconds}s)" : "Off")}").ConfigureAwait(false);
        await output.WriteLineAsync($"Edge WAF / SPA    : WAF={(eff.EdgeWaf.Enabled ? "On" : "Off")}, SPA={(eff.SpaFallback.Enabled ? eff.SpaFallback.FallbackDocument : "Off")}").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> CertsStatusAsync(string[] args, KiteHomeLayout layout, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (args.Length > 1 && !args[1].Equals("status", StringComparison.OrdinalIgnoreCase) && !args[1].StartsWith('-'))
        {
            await error.WriteLineAsync("Usage: kite certs status [--home <path>]").ConfigureAwait(false);
            return 1;
        }

        var svc = new KiteManagementService(layout);
        ManagementWorkspaceSnapshot snap = await svc.GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
        await output.WriteLineAsync($"Certificate Store: {layout.CertsConfigDirectory}").ConfigureAwait(false);

        if (Directory.Exists(layout.CertsConfigDirectory))
        {
            foreach (string pfx in Directory.EnumerateFiles(layout.CertsConfigDirectory, "*.pfx"))
            {
                try
                {
                    using X509Certificate2 cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, "");
                    await output.WriteLineAsync($"- {Path.GetFileName(pfx)} | Subject={cert.Subject} | Expires={cert.NotAfter.ToUniversalTime():yyyy-MM-dd} | Thumbprint={cert.Thumbprint}").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await output.WriteLineAsync($"- {Path.GetFileName(pfx)} | Error reading cert: {ex.Message}").ConfigureAwait(false);
                }
            }
        }

        foreach (SiteCompositeView site in snap.Sites)
        {
            var acme = site.Config.Features.TlsPolicy.Acme;
            var mtls = site.Config.Features.TlsPolicy.Mtls;
            await output.WriteLineAsync($"Site '{site.Config.Id}': ACME={(acme.Enabled ? $"On ({acme.ChallengeType}, domains={string.Join(',', acme.Domains)})" : "Off")}, mTLS={mtls.ClientCertificateMode}").ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> TailLogsAsync(string[] args, KiteHomeLayout layout, TextWriter output, CancellationToken ct)
    {
        string? siteId = ReadOption(args, "--site");
        int maxLines = int.TryParse(ReadOption(args, "--lines"), out int n) ? Math.Clamp(n, 1, 1000) : 25;
        bool follow = args.Any(a => a.Equals("--tail", StringComparison.OrdinalIgnoreCase));

        var svc = new KiteManagementService(layout);
        ManagementWorkspaceSnapshot snap = await svc.GetWorkspaceSnapshotAsync(ct).ConfigureAwait(false);
        string targetSite = siteId ?? snap.Sites.FirstOrDefault()?.Config.Id ?? "default-site";

        IReadOnlyList<string> lines = await svc.ReadRecentSiteLogsAsync(targetSite, maxLines, ct).ConfigureAwait(false);
        foreach (string line in lines)
        {
            await output.WriteLineAsync(line).ConfigureAwait(false);
        }

        if (follow && !ct.IsCancellationRequested)
        {
            int seen = lines.Count;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                IReadOnlyList<string> updated = await svc.ReadRecentSiteLogsAsync(targetSite, maxLines, ct).ConfigureAwait(false);
                for (int i = seen; i < updated.Count; i++)
                {
                    await output.WriteLineAsync(updated[i]).ConfigureAwait(false);
                }
                seen = updated.Count;
            }
        }

        return 0;
    }

    private static async Task<int> ExportSchemaAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        string target = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-')) ?? "all";
        string? outDir = ReadOption(args, "--out");

        string[] kinds = target.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? ["server", "pool", "site"]
            : [target];

        foreach (string kind in kinds)
        {
            string schemaJson;
            try
            {
                schemaJson = KiteSchemaGenerator.GenerateSchema(kind);
            }
            catch (ArgumentException ex)
            {
                await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 1;
            }

            if (!string.IsNullOrWhiteSpace(outDir))
            {
                Directory.CreateDirectory(outDir);
                string filePath = Path.Combine(outDir, $"{kind.ToLowerInvariant()}.v1.schema.json");
                await File.WriteAllTextAsync(filePath, schemaJson, ct).ConfigureAwait(false);
                await output.WriteLineAsync($"Wrote {filePath}").ConfigureAwait(false);
            }
            else
            {
                await output.WriteLineAsync(schemaJson).ConfigureAwait(false);
            }
        }

        return 0;
    }

    private static string? ReadOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("Kite Edge Server CLI (.NET 10)");
        output.WriteLine("Usage: kite <command> [options]");
        output.WriteLine("Commands:");
        output.WriteLine("  init [--home <path>] [--host <name.localhost>]");
        output.WriteLine("  ps   | instances");
        output.WriteLine("  run [--home <path>] [--mesh <auto|leader|peer|off>] [--admin-port <port>] [--admin-token <token>]");
        output.WriteLine("  validate [--home <path>]");
        output.WriteLine("  reload [--home <path>]");
        output.WriteLine("  route-test <url> [--home <path>]");
        output.WriteLine("  certs status [--home <path>]");
        output.WriteLine("  logs [--site <id>] [--lines <n>] [--tail] [--home <path>]");
        output.WriteLine("  schema <server|pool|site|all> [--out <dir>]");
        output.WriteLine("  history [--home <path>]");
        output.WriteLine("  rollback <snapshotId> [--home <path>]");
        output.WriteLine("  deploy --site <id> [--path <dir>] [--swap] [--home <path>]");
        output.WriteLine("  import --format <nginx|caddy|iis> --file <path> [--apply] [--home <path>]");
    }

    private static async Task<int> InitWorkspaceAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        string? homeOpt = ReadOption(args, "--home");
        string targetHome = !string.IsNullOrWhiteSpace(homeOpt)
            ? Path.GetFullPath(homeOpt)
            : Path.Combine(Environment.CurrentDirectory, ".kite");

        var layout = new KiteHomeLayout(targetHome);
        layout.EnsureDirectoriesCreated();

        string slug = KiteInstanceRegistry.ComputeAutoDomainSlug(targetHome);
        string? explicitHost = ReadOption(args, "--host");
        string siteHost = !string.IsNullOrWhiteSpace(explicitHost) ? explicitHost.Trim() : $"{slug}.localhost";

        var mgmt = new KiteManagementService(layout);
        await mgmt.EnsureDefaultWorkspaceSeededAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(explicitHost))
        {
            await mgmt.MutateSiteConfigAsync("default-site", site =>
            {
                site.Name = $"{slug} Local Site";
                foreach (SiteBindingConfig binding in site.Bindings)
                {
                    binding.Host = siteHost;
                }
            }, ct).ConfigureAwait(false);
        }

        await output.WriteLineAsync($"Initialized Kite workspace at: {layout.RootDirectory}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Suggested mesh host: {siteHost} (configVersion={KiteConfigVersion.Current})").ConfigureAwait(false);
        await output.WriteLineAsync("  Start it with: kite run").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ListInstancesAsync(TextWriter output, CancellationToken ct)
    {
        var registry = new KiteInstanceRegistry();
        IReadOnlyList<InstanceDescriptor> instances = await registry.ListLiveInstancesAsync(ct).ConfigureAwait(false);
        if (instances.Count == 0)
        {
            await output.WriteLineAsync($"No live Kite instances registered in '{registry.RootDirectory}'.").ConfigureAwait(false);
            return 0;
        }

        await output.WriteLineAsync($"Live Kite Instances ({instances.Count}) [registry={registry.RootDirectory}]:").ConfigureAwait(false);
        foreach (InstanceDescriptor inst in instances)
        {
            string hosts = inst.HostedDomains.Count > 0 ? string.Join(", ", inst.HostedDomains) : "*";
            string ports = inst.FrontDoorPorts.Count > 0 ? string.Join(",", inst.FrontDoorPorts) : "-";
            await output.WriteLineAsync(
                $"  {inst.InstanceId,-22} | {inst.Role,-10} | PID {inst.Pid,-7} | direct={inst.DirectHttpUrl ?? "-",-24} | hosts=[{hosts}] | ports=[{ports}] | home={inst.KiteHomePath}")
                .ConfigureAwait(false);
            foreach (string route in inst.PeerRoutesSummary)
            {
                await output.WriteLineAsync($"    -> mesh route: {route}").ConfigureAwait(false);
            }
        }
        return 0;
    }

    private static async Task<int> HistoryListAsync(KiteHomeLayout layout, TextWriter output, CancellationToken ct)
    {
        var svc = new KiteManagementService(layout);
        IReadOnlyList<ConfigSnapshotSummary> snaps = await svc.History.ListSnapshotsAsync(ct).ConfigureAwait(false);
        await output.WriteLineAsync($"Found {snaps.Count} configuration snapshot(s):").ConfigureAwait(false);
        foreach (ConfigSnapshotSummary s in snaps)
        {
            await output.WriteLineAsync($"  {s.SnapshotId} | {s.CreatedAtUtc:O} | {s.ResourceKind}:{s.ResourceId} | {s.Reason} | sha={s.Sha256}").ConfigureAwait(false);
        }
        return 0;
    }

    private static async Task<int> HistoryRollbackAsync(string[] args, KiteHomeLayout layout, TextWriter output, TextWriter error, CancellationToken ct)
    {
        string? snapshotId = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            await error.WriteLineAsync("Usage: kite rollback <snapshotId> [--home <path>]").ConfigureAwait(false);
            return 1;
        }

        var svc = new KiteManagementService(layout);
        bool ok = await svc.RollbackSnapshotAsync(snapshotId, ct).ConfigureAwait(false);
        if (!ok)
        {
            await error.WriteLineAsync($"Snapshot '{snapshotId}' not found.").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync($"Rolled back configuration to snapshot '{snapshotId}'.").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> DeployBlueGreenAsync(string[] args, KiteHomeLayout layout, TextWriter output, TextWriter error, CancellationToken ct)
    {
        string? siteId = ReadOption(args, "--site");
        if (string.IsNullOrWhiteSpace(siteId))
        {
            await error.WriteLineAsync("Usage: kite deploy --site <id> [--path <dir>] [--swap]").ConfigureAwait(false);
            return 1;
        }

        string? stagePath = ReadOption(args, "--path");
        bool doSwap = args.Any(a => a.Equals("--swap", StringComparison.OrdinalIgnoreCase));
        var svc = new KiteManagementService(layout);

        if (!string.IsNullOrWhiteSpace(stagePath))
        {
            SiteConfig? staged = await svc.StageSiteSlotAsync(siteId, Path.GetFullPath(stagePath), ct).ConfigureAwait(false);
            if (staged is null)
            {
                await error.WriteLineAsync($"Site '{siteId}' not found.").ConfigureAwait(false);
                return 1;
            }
            await output.WriteLineAsync($"Staged path '{staged.RootApplication.StagingPhysicalPath}' onto standby slot for site '{siteId}' (active={staged.RootApplication.ActiveSlot}).").ConfigureAwait(false);
        }

        if (doSwap)
        {
            SiteConfig? swapped = await svc.SwapSiteSlotAsync(siteId, ct).ConfigureAwait(false);
            if (swapped is null)
            {
                await error.WriteLineAsync($"Cannot swap slot for '{siteId}': no staging path configured.").ConfigureAwait(false);
                return 1;
            }
            await output.WriteLineAsync($"Swapped active slot for '{siteId}' -> {swapped.RootApplication.ActiveSlot} ({swapped.RootApplication.PhysicalPath}).").ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> ImportLegacyCliAsync(string[] args, KiteHomeLayout layout, TextWriter output, TextWriter error, CancellationToken ct)
    {
        string? format = ReadOption(args, "--format");
        string? file = ReadOption(args, "--file");
        bool apply = args.Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(format) || string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            await error.WriteLineAsync("Usage: kite import --format <nginx|caddy|iis> --file <path> [--apply]").ConfigureAwait(false);
            return 1;
        }

        string content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        var svc = new KiteManagementService(layout);
        var result = await svc.ImportLegacyConfigAsync(format, content, apply, ct).ConfigureAwait(false);
        foreach (string note in result.Notes)
        {
            await output.WriteLineAsync(note).ConfigureAwait(false);
        }
        await output.WriteLineAsync(apply ? "Applied imported configuration to KITE_HOME." : "Dry-run complete. Re-run with --apply to write config files.").ConfigureAwait(false);
        return 0;
    }
}
