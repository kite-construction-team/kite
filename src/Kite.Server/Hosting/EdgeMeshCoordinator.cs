using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;
using Kite.Core.Models;
using Kite.Core.Storage;

namespace Kite.Server.Hosting;

/// <summary>
/// Coordinates lock-backed leader election, peer discovery, ephemeral port allocation,
/// and live hostname-to-peer routing for Kite's self-forming local edge mesh.
/// </summary>
internal sealed class EdgeMeshCoordinator : IAsyncDisposable
{
    private readonly KiteHomeLayout _layout;
    private readonly bool _enableKestrelListeners;
    private readonly MeshMode? _modeOverride;
    private readonly string? _registryDirOverride;
    private readonly DateTimeOffset _startedAtUtc;

    private KiteInstanceRegistry _registry;
    private KiteInstanceRegistry.InstanceRegistrationHandle? _registration;
    private KiteInstanceRegistry.EdgeLeaderLockHandle? _leaderLock;
    private FileSystemWatcher? _registryWatcher;
    private volatile int _registryDirty;

    private MeshConfig _currentConfig = new();
    private InstanceRole _role = InstanceRole.Standalone;
    private int? _peerHttpPort;
    private string _autoDomainSlug;
    private FrozenDictionary<string, string> _peerRoutes = FrozenDictionary<string, string>.Empty;
    private List<string> _peerRoutesSummary = [];
    private List<string> _localHostedDomains = [];
    private IReadOnlyDictionary<int, int>? _activePortRemaps;
    private bool _disposed;

    public EdgeMeshCoordinator(
        KiteHomeLayout layout,
        DateTimeOffset startedAtUtc,
        bool enableKestrelListeners = true,
        MeshMode? modeOverride = null,
        string? registryDirOverride = null)
    {
        _layout = layout;
        _enableKestrelListeners = enableKestrelListeners;
        _startedAtUtc = startedAtUtc;
        _modeOverride = modeOverride;
        _registryDirOverride = registryDirOverride;
        _autoDomainSlug = KiteInstanceRegistry.ComputeAutoDomainSlug(layout.RootDirectory);
        InstanceId = KiteInstanceRegistry.ComputeInstanceId(layout.RootDirectory);
        _registry = new KiteInstanceRegistry(registryDirOverride);
    }

    public string InstanceId { get; }
    public InstanceRole Role => _role;
    public int? PeerHttpPort => _peerHttpPort;
    public IReadOnlyList<string> LocalHostedDomains => _localHostedDomains;
    public IReadOnlyList<string> PeerRoutesSummary => _peerRoutesSummary;
    public string? DirectHttpUrl { get; private set; }
    public string? DirectHttpsUrl { get; private set; }

    public MeshMode EffectiveMode => _modeOverride ?? _currentConfig.Mode;

    public string? ResolvePeerTarget(int localPort, string hostWithoutPort)
    {
        if (_role != InstanceRole.MeshLeader || string.IsNullOrWhiteSpace(hostWithoutPort))
        {
            return null;
        }

        return _peerRoutes.TryGetValue(hostWithoutPort, out string? target) ? target : null;
    }

    public bool HasPeerHost(string hostWithoutPort) =>
        _role == InstanceRole.MeshLeader &&
        !string.IsNullOrWhiteSpace(hostWithoutPort) &&
        _peerRoutes.ContainsKey(hostWithoutPort);

    public async ValueTask<bool> ReconcileAsync(ConfigSnapshot snapshot, CancellationToken ct)
    {
        MeshConfig nextMesh = snapshot.Server.Mesh ?? new MeshConfig();
        string? desiredRegDir = _registryDirOverride ?? nextMesh.RegistryDirectoryOverride;
        string resolvedRegDir = KiteInstanceRegistry.ResolveRegistryDirectory(desiredRegDir);
        if (!string.Equals(_registry.RootDirectory, resolvedRegDir, StringComparison.OrdinalIgnoreCase))
        {
            StopWatcher();
            if (_leaderLock is not null)
            {
                _leaderLock.Dispose();
                _leaderLock = null;
            }
            if (_registration is not null)
            {
                await _registration.DisposeAsync().ConfigureAwait(false);
                _registration = null;
            }
            _registry = new KiteInstanceRegistry(desiredRegDir);
        }

        _currentConfig = nextMesh;
        MeshMode mode = EffectiveMode;
        InstanceRole prevRole = _role;

        bool usesFrontDoorPort = snapshot.Sites.Values
            .Where(static s => s.DesiredState == DesiredResourceState.Started)
            .SelectMany(static s => s.Bindings)
            .Any(b => nextMesh.FrontDoorPorts.Contains(b.Port));
        bool hasExplicitMeshScope = _modeOverride is not null ||
                                    _registryDirOverride is not null ||
                                    !string.IsNullOrWhiteSpace(nextMesh.RegistryDirectoryOverride);

        if (!_enableKestrelListeners ||
            mode == MeshMode.Disabled ||
            (mode == MeshMode.Auto && !usesFrontDoorPort && !hasExplicitMeshScope))
        {
            ReleaseLeaderLock();
            _role = InstanceRole.Standalone;
            _peerHttpPort = null;
            _peerRoutes = FrozenDictionary<string, string>.Empty;
            _peerRoutesSummary = [];
        }
        else if (mode == MeshMode.PeerOnly)
        {
            ReleaseLeaderLock();
            _role = InstanceRole.MeshPeer;
            EnsurePeerPortAllocated();
            _peerRoutes = FrozenDictionary<string, string>.Empty;
            _peerRoutesSummary = [];
        }
        else if (mode == MeshMode.LeaderOnly)
        {
            _leaderLock ??= _registry.TryAcquireLeaderLock(InstanceId);
            if (_leaderLock is null)
            {
                throw new InvalidOperationException(
                    $"Edge mesh mode is 'LeaderOnly', but another Kite instance already owns the mesh leader lock '{_registry.LeaderLockPath}'.");
            }
            _role = InstanceRole.MeshLeader;
            _peerHttpPort = null;
        }
        else
        {
            // MeshMode.Auto
            _leaderLock ??= _registry.TryAcquireLeaderLock(InstanceId);
            if (_leaderLock is not null)
            {
                _role = InstanceRole.MeshLeader;
                _peerHttpPort = null;
            }
            else
            {
                _role = InstanceRole.MeshPeer;
                EnsurePeerPortAllocated();
                _peerRoutes = FrozenDictionary<string, string>.Empty;
                _peerRoutesSummary = [];
            }
        }

        ComputeHostedMetadata(snapshot, _activePortRemaps);
        await EnsureRegisteredAndWatchAsync(snapshot, ct).ConfigureAwait(false);

        if (_role == InstanceRole.MeshLeader)
        {
            await RefreshPeerRoutesAsync(ct).ConfigureAwait(false);
        }

        return _role != prevRole;
    }

    /// <summary>
    /// Called on maintenance ticks or registry file changes. Returns true if role changed (e.g., Peer promoted to Leader).
    /// </summary>
    public async ValueTask<bool> PollAndRefreshAsync(ConfigSnapshot snapshot, CancellationToken ct)
    {
        if (_disposed)
        {
            return false;
        }

        if (_role == InstanceRole.MeshPeer && EffectiveMode == MeshMode.Auto)
        {
            // Try acquiring leader lock in case the previous leader exited
            KiteInstanceRegistry.EdgeLeaderLockHandle? promoted = _registry.TryAcquireLeaderLock(InstanceId);
            if (promoted is not null)
            {
                _leaderLock = promoted;
                _role = InstanceRole.MeshLeader;
                _peerHttpPort = null;
                ComputeHostedMetadata(snapshot, _activePortRemaps);
                await RefreshPeerRoutesAsync(ct).ConfigureAwait(false);
                await UpdateRegistrationAsync(snapshot, ct).ConfigureAwait(false);
                return true;
            }
        }
        else if (_role == InstanceRole.MeshLeader && Interlocked.Exchange(ref _registryDirty, 0) != 0)
        {
            await RefreshPeerRoutesAsync(ct).ConfigureAwait(false);
            await UpdateRegistrationAsync(snapshot, ct).ConfigureAwait(false);
        }

        return false;
    }

    public IEnumerable<SiteConfig> BuildEffectiveSitesForRouting(
        ConfigSnapshot snapshot,
        IReadOnlyDictionary<int, int>? privilegedPortRemaps = null)
    {
        if (_role != InstanceRole.MeshPeer || !_peerHttpPort.HasValue)
        {
            if (privilegedPortRemaps is null || privilegedPortRemaps.Count == 0)
            {
                return snapshot.Sites.Values;
            }

            var fallbackRemapped = new List<SiteConfig>(snapshot.Sites.Count);
            foreach (SiteConfig site in snapshot.Sites.Values)
            {
                if (site.DesiredState != DesiredResourceState.Started)
                {
                    fallbackRemapped.Add(site);
                    continue;
                }

                var updatedBindings = new List<SiteBindingConfig>(site.Bindings.Count);
                foreach (SiteBindingConfig b in site.Bindings)
                {
                    int mappedPort = privilegedPortRemaps.TryGetValue(b.Port, out int alt) ? alt : b.Port;
                    updatedBindings.Add(new SiteBindingConfig
                    {
                        Protocol = b.Protocol,
                        IpAddress = b.IpAddress,
                        Port = mappedPort,
                        Host = b.Host,
                        Tls = b.Tls
                    });
                }

                fallbackRemapped.Add(new SiteConfig
                {
                    Schema = site.Schema,
                    ConfigVersion = site.ConfigVersion,
                    Id = site.Id,
                    Name = site.Name,
                    DesiredState = site.DesiredState,
                    Bindings = updatedBindings,
                    RootApplication = site.RootApplication,
                    NestedApplications = site.NestedApplications,
                    Features = site.Features
                });
            }

            return fallbackRemapped;
        }

        int peerPort = _peerHttpPort.Value;
        string suffix = string.IsNullOrWhiteSpace(_currentConfig.AutoDomainSuffix)
            ? ".localhost"
            : _currentConfig.AutoDomainSuffix;
        if (!suffix.StartsWith('.'))
        {
            suffix = "." + suffix;
        }

        var remapped = new List<SiteConfig>(snapshot.Sites.Count);
        bool hasAnyExplicitHost = snapshot.Sites.Values
            .Where(static s => s.DesiredState == DesiredResourceState.Started)
            .SelectMany(static s => s.Bindings)
            .Any(static b => !string.IsNullOrWhiteSpace(b.Host) && b.Host.Trim() != "*");

        bool addedAutoDomainWildcardSite = false;
        foreach (SiteConfig site in snapshot.Sites.Values)
        {
            if (site.DesiredState != DesiredResourceState.Started)
            {
                continue;
            }

            var peerBindings = new List<SiteBindingConfig>();
            var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (SiteBindingConfig b in site.Bindings)
            {
                string hostKey = string.IsNullOrWhiteSpace(b.Host) ? "*" : b.Host.Trim();
                if (seenHosts.Add(hostKey))
                {
                    peerBindings.Add(new SiteBindingConfig
                    {
                        Protocol = "http",
                        IpAddress = "127.0.0.1",
                        Port = peerPort,
                        Host = hostKey
                    });
                }
            }

            if (!hasAnyExplicitHost && !addedAutoDomainWildcardSite && peerBindings.Count > 0)
            {
                addedAutoDomainWildcardSite = true;
                string autoHost = $"{_autoDomainSlug}{suffix}";
                if (seenHosts.Add(autoHost))
                {
                    peerBindings.Add(new SiteBindingConfig
                    {
                        Protocol = "http",
                        IpAddress = "127.0.0.1",
                        Port = peerPort,
                        Host = autoHost
                    });
                }
            }

            remapped.Add(new SiteConfig
            {
                Schema = site.Schema,
                ConfigVersion = site.ConfigVersion,
                Id = site.Id,
                Name = site.Name,
                DesiredState = site.DesiredState,
                Bindings = peerBindings,
                RootApplication = site.RootApplication,
                NestedApplications = site.NestedApplications,
                Features = site.Features
            });
        }

        return remapped;
    }

    public async ValueTask RefreshPeerRoutesAsync(CancellationToken ct)
    {
        if (_role != InstanceRole.MeshLeader)
        {
            return;
        }

        IReadOnlyList<InstanceDescriptor> instances = await _registry.ListLiveInstancesAsync(ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var summary = new List<string>();

        foreach (InstanceDescriptor inst in instances)
        {
            if (string.Equals(inst.InstanceId, InstanceId, StringComparison.OrdinalIgnoreCase) ||
                inst.Role != InstanceRole.MeshPeer ||
                string.IsNullOrWhiteSpace(inst.DirectHttpUrl))
            {
                continue;
            }

            foreach (string domain in inst.HostedDomains)
            {
                if (string.IsNullOrWhiteSpace(domain) || domain == "*")
                {
                    continue;
                }

                if (map.TryAdd(domain.Trim(), inst.DirectHttpUrl))
                {
                    summary.Add($"{domain.Trim()} -> {inst.DirectHttpUrl} ({inst.ServerName})");
                }
            }
        }

        _peerRoutes = map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _peerRoutesSummary = summary;
    }

    public InstanceDescriptor BuildDescriptor(
        ConfigSnapshot snapshot,
        IReadOnlyDictionary<int, int>? portRemaps = null)
    {
        IReadOnlyDictionary<int, int>? remaps = portRemaps ?? _activePortRemaps;
        var frontDoor = snapshot.Sites.Values
            .Where(static s => s.DesiredState == DesiredResourceState.Started)
            .SelectMany(static s => s.Bindings)
            .Select(b => remaps is not null && remaps.TryGetValue(b.Port, out int alt) ? alt : b.Port)
            .Distinct()
            .OrderBy(static p => p)
            .ToList();

        return new InstanceDescriptor
        {
            InstanceId = InstanceId,
            KiteHomePath = _layout.RootDirectory,
            ServerName = snapshot.Server.ServerName,
            Pid = Environment.ProcessId,
            StartedAtUtc = _startedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Role = _role,
            FrontDoorPorts = frontDoor,
            DirectHttpUrl = DirectHttpUrl,
            DirectHttpsUrl = DirectHttpsUrl,
            MeshHttpIngressPort = _peerHttpPort,
            HostedDomains = _localHostedDomains.ToList(),
            PeerRoutesSummary = _peerRoutesSummary.ToList()
        };
    }

    public ValueTask UpdateRegistrationAsync(
        ConfigSnapshot snapshot,
        CancellationToken ct,
        IReadOnlyDictionary<int, int>? portRemaps = null)
    {
        if (portRemaps is not null)
        {
            _activePortRemaps = portRemaps.Count > 0 ? portRemaps : null;
            ComputeHostedMetadata(snapshot, _activePortRemaps);
        }

        return _registry.UpdateAsync(BuildDescriptor(snapshot, _activePortRemaps), ct);
    }

    private async ValueTask EnsureRegisteredAndWatchAsync(ConfigSnapshot snapshot, CancellationToken ct)
    {
        InstanceDescriptor desc = BuildDescriptor(snapshot);
        if (_registration is null)
        {
            _registration = await _registry.RegisterAsync(desc, ct).ConfigureAwait(false);
        }
        else
        {
            await _registry.UpdateAsync(desc, ct).ConfigureAwait(false);
        }

        if (_registryWatcher is null && Directory.Exists(_registry.RootDirectory))
        {
            var fsw = new FileSystemWatcher(_registry.RootDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            fsw.Created += (_, _) => Interlocked.Exchange(ref _registryDirty, 1);
            fsw.Changed += (_, _) => Interlocked.Exchange(ref _registryDirty, 1);
            fsw.Deleted += (_, _) => Interlocked.Exchange(ref _registryDirty, 1);
            _registryWatcher = fsw;
        }
    }

    private void ComputeHostedMetadata(
        ConfigSnapshot snapshot,
        IReadOnlyDictionary<int, int>? portRemaps = null)
    {
        var hosts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? firstHttpPort = null;
        int? firstHttpsPort = null;
        string? firstHost = null;

        foreach (SiteConfig site in snapshot.Sites.Values)
        {
            if (site.DesiredState != DesiredResourceState.Started)
            {
                continue;
            }

            foreach (SiteBindingConfig b in site.Bindings)
            {
                int effectivePort = portRemaps is not null && portRemaps.TryGetValue(b.Port, out int alt)
                    ? alt
                    : b.Port;
                bool isHttps = string.Equals(b.Protocol, "https", StringComparison.OrdinalIgnoreCase);
                if (isHttps)
                {
                    firstHttpsPort ??= effectivePort;
                }
                else
                {
                    firstHttpPort ??= effectivePort;
                }

                if (!string.IsNullOrWhiteSpace(b.Host) && b.Host.Trim() != "*")
                {
                    string h = b.Host.Trim();
                    firstHost ??= h;
                    if (seen.Add(h))
                    {
                        hosts.Add(h);
                    }
                }
            }
        }

        if (_role == InstanceRole.MeshPeer)
        {
            if (hosts.Count == 0 && snapshot.Sites.Values.Any(static s => s.DesiredState == DesiredResourceState.Started))
            {
                string suffix = string.IsNullOrWhiteSpace(_currentConfig.AutoDomainSuffix)
                    ? ".localhost"
                    : _currentConfig.AutoDomainSuffix;
                if (!suffix.StartsWith('.'))
                {
                    suffix = "." + suffix;
                }

                string autoHost = $"{_autoDomainSlug}{suffix}";
                hosts.Add(autoHost);
            }

            DirectHttpUrl = _peerHttpPort.HasValue ? $"http://127.0.0.1:{_peerHttpPort.Value}" : null;
            DirectHttpsUrl = null;
        }
        else
        {
            string hostLabel = firstHost ?? "127.0.0.1";
            DirectHttpUrl = firstHttpPort.HasValue ? $"http://{hostLabel}:{firstHttpPort.Value}" : null;
            DirectHttpsUrl = firstHttpsPort.HasValue ? $"https://{hostLabel}:{firstHttpsPort.Value}" : null;
        }

        _localHostedDomains = hosts;
    }

    private void EnsurePeerPortAllocated()
    {
        if (_peerHttpPort.HasValue)
        {
            return;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _peerHttpPort = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private void ReleaseLeaderLock()
    {
        if (_leaderLock is not null)
        {
            _leaderLock.Dispose();
            _leaderLock = null;
        }
    }

    private void StopWatcher()
    {
        if (_registryWatcher is not null)
        {
            _registryWatcher.EnableRaisingEvents = false;
            _registryWatcher.Dispose();
            _registryWatcher = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopWatcher();
        if (_registration is not null)
        {
            await _registration.DisposeAsync().ConfigureAwait(false);
            _registration = null;
        }

        ReleaseLeaderLock();
    }
}
