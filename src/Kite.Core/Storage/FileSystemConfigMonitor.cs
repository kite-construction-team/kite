using System.Collections.Concurrent;
using Kite.Core.Models;
using Kite.Core.Serialization;

namespace Kite.Core.Storage;

public sealed record ConfigSnapshot(
    ServerConfig Server,
    IReadOnlyDictionary<string, ApplicationPoolConfig> Pools,
    IReadOnlyDictionary<string, SiteConfig> Sites);

/// <summary>
/// Resolves all directory and file paths inside a Kite data directory (<c>KITE_HOME</c>)
/// and watches <c>config/</c> for changes using <see cref="FileSystemWatcher"/> plus SHA-256 hash deduplication.
/// </summary>
public sealed class KiteHomeLayout
{
    public KiteHomeLayout(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        ConfigDirectory = Path.Combine(RootDirectory, "config");
        PoolsConfigDirectory = Path.Combine(ConfigDirectory, "pools");
        SitesConfigDirectory = Path.Combine(ConfigDirectory, "sites");
        CertsConfigDirectory = Path.Combine(ConfigDirectory, "certs");
        HistoryDirectory = Path.Combine(ConfigDirectory, ".history");
        PluginsDirectory = Path.Combine(RootDirectory, "plugins");

        StateDirectory = Path.Combine(RootDirectory, "state");
        PoolsStateDirectory = Path.Combine(StateDirectory, "pools");
        SitesStateDirectory = Path.Combine(StateDirectory, "sites");

        LogsDirectory = Path.Combine(RootDirectory, "logs");
        ServerLogsDirectory = Path.Combine(LogsDirectory, "server");
        SitesLogsDirectory = Path.Combine(LogsDirectory, "sites");
        FrebLogsDirectory = Path.Combine(LogsDirectory, "freb");
        DumpsLogsDirectory = Path.Combine(LogsDirectory, "dumps");
    }

    public string RootDirectory { get; }
    public string ConfigDirectory { get; }
    public string PoolsConfigDirectory { get; }
    public string SitesConfigDirectory { get; }
    public string CertsConfigDirectory { get; }
    public string HistoryDirectory { get; }
    public string PluginsDirectory { get; }
    public string StateDirectory { get; }
    public string PoolsStateDirectory { get; }
    public string SitesStateDirectory { get; }
    public string LogsDirectory { get; }
    public string ServerLogsDirectory { get; }
    public string SitesLogsDirectory { get; }
    public string FrebLogsDirectory { get; }
    public string DumpsLogsDirectory { get; }

    public string ServerConfigPath => Path.Combine(ConfigDirectory, "server.json");
    public string ServerStatusPath => Path.Combine(StateDirectory, "server.status.json");

    public string GetPoolConfigPath(string poolId) => Path.Combine(PoolsConfigDirectory, $"{SanitizeFileName(poolId)}.json");
    public string GetSiteConfigPath(string siteId) => Path.Combine(SitesConfigDirectory, $"{SanitizeFileName(siteId)}.json");
    public string GetPoolStatusPath(string poolId) => Path.Combine(PoolsStateDirectory, $"{SanitizeFileName(poolId)}.status.json");
    public string GetSiteStatusPath(string siteId) => Path.Combine(SitesStateDirectory, $"{SanitizeFileName(siteId)}.status.json");
    public string GetSiteLogDirectory(string siteId) => Path.Combine(SitesLogsDirectory, SanitizeFileName(siteId));
    public string GetSiteFrebDirectory(string siteId) => Path.Combine(FrebLogsDirectory, SanitizeFileName(siteId));
    public string GetDomainPfxPath(string domain) => Path.Combine(CertsConfigDirectory, $"{SanitizeFileName(domain)}.pfx");
    public string AcmeAccountPath => Path.Combine(CertsConfigDirectory, "acme-account.json");

    public void EnsureDirectoriesCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(PoolsConfigDirectory);
        Directory.CreateDirectory(SitesConfigDirectory);
        Directory.CreateDirectory(CertsConfigDirectory);
        Directory.CreateDirectory(HistoryDirectory);
        Directory.CreateDirectory(PluginsDirectory);
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(PoolsStateDirectory);
        Directory.CreateDirectory(SitesStateDirectory);
        Directory.CreateDirectory(ServerLogsDirectory);
        Directory.CreateDirectory(SitesLogsDirectory);
        Directory.CreateDirectory(FrebLogsDirectory);
        Directory.CreateDirectory(DumpsLogsDirectory);
    }

    public static string SanitizeFileName(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Create(id.Length, (id, invalid), static (span, state) =>
        {
            for (int i = 0; i < state.id.Length; i++)
            {
                char c = state.id[i];
                span[i] = Array.IndexOf(state.invalid, c) >= 0 ? '_' : c;
            }
        });
    }

    /// <summary>
    /// Resolves a shared <c>KITE_HOME</c> path across <c>Kite.Server</c> and <c>Kite.Desktop.WinForms</c>
    /// so both executables operate on the same configuration and state directory by default.
    /// </summary>
    public static KiteHomeLayout ResolveFromArgsAndEnv(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--home", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return new KiteHomeLayout(args[i + 1]);
            }
        }

        string? envHome = Environment.GetEnvironmentVariable("KITE_HOME");
        if (!string.IsNullOrWhiteSpace(envHome))
        {
            return new KiteHomeLayout(envHome);
        }

        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Kite.slnx")))
            {
                return new KiteHomeLayout(Path.Combine(current, "kite-data"));
            }

            current = Path.GetDirectoryName(current);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return new KiteHomeLayout(Path.Combine(localAppData, "Kite", "kite-data"));
        }

        return new KiteHomeLayout(Path.Combine(Environment.CurrentDirectory, "kite-data"));
    }
}

public sealed class FileSystemConfigMonitor : IAsyncDisposable
{
    private readonly KiteHomeLayout _layout;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, string> _fileHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private FileSystemWatcher? _watcher;
    private ITimer? _pollTimer;
    private ConfigSnapshot _currentSnapshot;
    private bool _disposed;

    public FileSystemConfigMonitor(KiteHomeLayout layout, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _layout.EnsureDirectoriesCreated();
        _currentSnapshot = new ConfigSnapshot(
            new ServerConfig(),
            new Dictionary<string, ApplicationPoolConfig>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SiteConfig>(StringComparer.OrdinalIgnoreCase));
    }

    public KiteHomeLayout Layout => _layout;
    public ConfigSnapshot CurrentSnapshot => Volatile.Read(ref _currentSnapshot);

    public event Func<ConfigSnapshot, ValueTask>? SnapshotChanged;

    public async ValueTask<ConfigSnapshot> InitializeAndStartAsync(
        bool seedDefaultsIfEmpty = true,
        CancellationToken cancellationToken = default)
    {
        _layout.EnsureDirectoriesCreated();

        if (seedDefaultsIfEmpty)
        {
            await EnsureSeedConfigAsync(cancellationToken).ConfigureAwait(false);
        }

        await ReloadIfChangedAsync(forceNotify: false, cancellationToken).ConfigureAwait(false);

        _watcher = new FileSystemWatcher(_layout.ConfigDirectory, "*.json")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnFileSystemEvent;
        _watcher.Changed += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Renamed += OnFileSystemEvent;

        _pollTimer = _timeProvider.CreateTimer(
            static state => _ = ((FileSystemConfigMonitor)state!).ReloadIfChangedAsync(forceNotify: false, CancellationToken.None),
            this,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));

        return _currentSnapshot;
    }

    public async ValueTask<bool> ReloadIfChangedAsync(bool forceNotify = false, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return false;
        }

        await _reloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool anyChanged = forceNotify;
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Server config
            ServerConfig serverConfig = _currentSnapshot.Server;
            if (File.Exists(_layout.ServerConfigPath))
            {
                seenFiles.Add(_layout.ServerConfigPath);
                var (loadedServer, hash) = await AtomicJsonStore.ReadWithHashAsync(
                    _layout.ServerConfigPath,
                    KiteJsonContext.Default.ServerConfig,
                    cancellationToken).ConfigureAwait(false);

                if (loadedServer is not null && hash is not null &&
                    KiteConfigVersion.IsSupported(loadedServer.ConfigVersion))
                {
                    if (!_fileHashes.TryGetValue(_layout.ServerConfigPath, out string? oldHash) || oldHash != hash)
                    {
                        _fileHashes[_layout.ServerConfigPath] = hash;
                        anyChanged = true;
                    }
                    serverConfig = loadedServer;
                }
            }

            // 2. Pool configs
            var pools = new Dictionary<string, ApplicationPoolConfig>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(_layout.PoolsConfigDirectory))
            {
                foreach (string file in Directory.EnumerateFiles(_layout.PoolsConfigDirectory, "*.json"))
                {
                    if (file.Contains(".tmp.", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    seenFiles.Add(file);
                    var (pool, hash) = await AtomicJsonStore.ReadWithHashAsync(
                        file,
                        KiteJsonContext.Default.ApplicationPoolConfig,
                        cancellationToken).ConfigureAwait(false);

                    if (pool is not null && hash is not null &&
                        KiteConfigVersion.IsSupported(pool.ConfigVersion))
                    {
                        pools[pool.Id] = pool;
                        if (!_fileHashes.TryGetValue(file, out string? oldHash) || oldHash != hash)
                        {
                            _fileHashes[file] = hash;
                            anyChanged = true;
                        }
                    }
                }
            }

            // 3. Site configs
            var sites = new Dictionary<string, SiteConfig>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(_layout.SitesConfigDirectory))
            {
                foreach (string file in Directory.EnumerateFiles(_layout.SitesConfigDirectory, "*.json"))
                {
                    if (file.Contains(".tmp.", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    seenFiles.Add(file);
                    var (site, hash) = await AtomicJsonStore.ReadWithHashAsync(
                        file,
                        KiteJsonContext.Default.SiteConfig,
                        cancellationToken).ConfigureAwait(false);

                    if (site is not null && hash is not null &&
                        KiteConfigVersion.IsSupported(site.ConfigVersion))
                    {
                        sites[site.Id] = site;
                        if (!_fileHashes.TryGetValue(file, out string? oldHash) || oldHash != hash)
                        {
                            _fileHashes[file] = hash;
                            anyChanged = true;
                        }
                    }
                }
            }

            // Detect deleted files
            foreach (string existingKey in _fileHashes.Keys)
            {
                if (!seenFiles.Contains(existingKey))
                {
                    _fileHashes.TryRemove(existingKey, out _);
                    anyChanged = true;
                }
            }

            if (anyChanged)
            {
                var nextSnapshot = new ConfigSnapshot(serverConfig, pools, sites);
                Interlocked.Exchange(ref _currentSnapshot, nextSnapshot);

                Func<ConfigSnapshot, ValueTask>? handler = SnapshotChanged;
                if (handler is not null)
                {
                    foreach (Delegate d in handler.GetInvocationList())
                    {
                        await ((Func<ConfigSnapshot, ValueTask>)d)(nextSnapshot).ConfigureAwait(false);
                    }
                }
            }

            return anyChanged;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (e.Name is not null && e.Name.Contains(".tmp.", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = ReloadIfChangedAsync(forceNotify: false, CancellationToken.None);
    }

    private async ValueTask EnsureSeedConfigAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_layout.ServerConfigPath))
        {
            await AtomicJsonStore.WriteAtomicAsync(
                _layout.ServerConfigPath,
                new ServerConfig(),
                KiteJsonContext.Default.ServerConfig,
                cancellationToken).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);
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
                    """
                    <!DOCTYPE html>
                    <html lang="en">
                    <head>
                        <meta charset="utf-8" />
                        <title>Welcome to Kite Web Server (.NET 10 LTS)</title>
                        <style>
                            body { font-family: system-ui, -apple-system, sans-serif; background: #0f172a; color: #f8fafc; margin: 0; padding: 3rem; }
                            .card { max-width: 720px; margin: 0 auto; background: #1e293b; border: 1px solid #334155; border-radius: 12px; padding: 2rem; }
                            .badge { display: inline-block; background: #0284c7; color: #fff; font-weight: 700; padding: 0.25rem 0.75rem; border-radius: 6px; font-size: 0.85rem; }
                            h1 { margin-top: 1rem; color: #38bdf8; }
                            code { background: #090d16; padding: 0.2rem 0.4rem; border-radius: 4px; color: #34d399; }
                        </style>
                    </head>
                    <body>
                        <div class="card">
                            <span class="badge">KITE WEB SERVER &bull; .NET 10 LTS</span>
                            <h1>Welcome to Kite Web Server</h1>
                            <p>This default site is actively served by the <code>Kite.Server</code> Kestrel Multi-Binding Engine over HTTP/1.1 &amp; HTTP/2.</p>
                            <ul>
                                <li>HTTP Binding: <code>http://localhost:8080</code></li>
                                <li>HTTPS / TLS (SNI + ALPN) Binding: <code>https://localhost:8443</code></li>
                            </ul>
                        </div>
                    </body>
                    </html>
                    """,
                    cancellationToken).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        if (_pollTimer is not null)
        {
            await _pollTimer.DisposeAsync().ConfigureAwait(false);
        }
        _reloadLock.Dispose();
    }
}
