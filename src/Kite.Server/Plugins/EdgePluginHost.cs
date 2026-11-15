using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Kite.Core.Models;
using Kite.Core.Plugins;
using Microsoft.AspNetCore.Http;

namespace Kite.Server.Plugins;

/// <summary>
/// Hot-reloadable C# edge middleware plugin loader backed by collectible <see cref="AssemblyLoadContext"/>
/// instances and SHA-256 byte loading so on-disk DLLs never remain file-locked.
/// </summary>
public sealed class EdgePluginHost : IDisposable
{
    private readonly Lock _syncLock = new();
    private readonly Dictionary<string, LoadedPluginEntry> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private LoadedPluginEntry[] _orderedPipeline = [];

    public int LoadedCount => Volatile.Read(ref _orderedPipeline).Length;

    public void Reconcile(IReadOnlyList<EdgePluginConfig> configs, string pluginsDirectory)
    {
        lock (_syncLock)
        {
            var desiredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nextOrdered = new List<LoadedPluginEntry>();

            foreach (EdgePluginConfig cfg in configs)
            {
                if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.Id) || string.IsNullOrWhiteSpace(cfg.AssemblyPath))
                {
                    continue;
                }

                string fullPath = Path.IsPathRooted(cfg.AssemblyPath)
                    ? cfg.AssemblyPath
                    : Path.GetFullPath(Path.Combine(pluginsDirectory, cfg.AssemblyPath));

                if (!File.Exists(fullPath))
                {
                    continue;
                }

                desiredIds.Add(cfg.Id);
                byte[] dllBytes = File.ReadAllBytes(fullPath);
                string hash = Convert.ToHexString(SHA256.HashData(dllBytes));

                if (_loaded.TryGetValue(cfg.Id, out LoadedPluginEntry? existing) &&
                    string.Equals(existing.Sha256, hash, StringComparison.Ordinal) &&
                    string.Equals(existing.Config.TypeName, cfg.TypeName, StringComparison.Ordinal))
                {
                    existing.UpdateConfig(cfg);
                    nextOrdered.Add(existing);
                    continue;
                }

                if (existing is not null)
                {
                    existing.Unload();
                    _loaded.Remove(cfg.Id);
                }

                LoadedPluginEntry? created = TryLoadPlugin(cfg, fullPath, dllBytes, hash);
                if (created is not null)
                {
                    _loaded[cfg.Id] = created;
                    nextOrdered.Add(created);
                }
            }

            foreach (string id in _loaded.Keys.ToList())
            {
                if (!desiredIds.Contains(id))
                {
                    _loaded[id].Unload();
                    _loaded.Remove(id);
                }
            }

            nextOrdered.Sort(static (a, b) => a.Config.Order.CompareTo(b.Config.Order));
            _orderedPipeline = [.. nextOrdered];
        }
    }

    /// <summary>
    /// Executes all loaded plugins in ascending <c>Order</c>.
    /// Returns <see langword="false"/> if a plugin short-circuited the HTTP response.
    /// </summary>
    public ValueTask<bool> ExecuteAsync(HttpContext context, CancellationToken ct)
    {
        LoadedPluginEntry[] snapshot = Volatile.Read(ref _orderedPipeline);
        if (snapshot.Length == 0)
        {
            return ValueTask.FromResult(true);
        }

        if (snapshot.Length == 1)
        {
            LoadedPluginEntry single = snapshot[0];
            return single.Instance.InvokeAsync(context, single.Config.Settings, ct);
        }

        return ExecuteMultiAsync(snapshot, context, ct);
    }

    private static async ValueTask<bool> ExecuteMultiAsync(
        LoadedPluginEntry[] snapshot,
        HttpContext context,
        CancellationToken ct)
    {
        for (int i = 0; i < snapshot.Length; i++)
        {
            LoadedPluginEntry entry = snapshot[i];
            bool shouldContinue = await entry.Instance.InvokeAsync(context, entry.Config.Settings, ct).ConfigureAwait(false);
            if (!shouldContinue)
            {
                return false;
            }
        }

        return true;
    }

    private static LoadedPluginEntry? TryLoadPlugin(
        EdgePluginConfig cfg,
        string fullPath,
        byte[] dllBytes,
        string hash)
    {
        var alc = new CollectiblePluginLoadContext(fullPath);
        try
        {
            using var ms = new MemoryStream(dllBytes, writable: false);
            Assembly asm = alc.LoadFromStream(ms);

            Type? targetType = null;
            if (!string.IsNullOrWhiteSpace(cfg.TypeName))
            {
                targetType = asm.GetType(cfg.TypeName, throwOnError: false, ignoreCase: true);
            }

            targetType ??= asm.GetTypes().FirstOrDefault(t =>
                !t.IsAbstract && !t.IsInterface && typeof(IKiteEdgeMiddleware).IsAssignableFrom(t));

            if (targetType is null)
            {
                alc.Unload();
                return null;
            }

            if (Activator.CreateInstance(targetType) is not IKiteEdgeMiddleware instance)
            {
                alc.Unload();
                return null;
            }

            return new LoadedPluginEntry(cfg, hash, alc, instance);
        }
        catch
        {
            alc.Unload();
            return null;
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            foreach (LoadedPluginEntry entry in _loaded.Values)
            {
                entry.Unload();
            }
            _loaded.Clear();
            _orderedPipeline = [];
        }
    }

    private sealed class CollectiblePluginLoadContext(string pluginPath)
        : AssemblyLoadContext($"KitePlugin-{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Keep shared contract types resolved against the host ALC
            if (string.Equals(assemblyName.Name, "Kite.Core", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase) == true)
            {
                return null;
            }

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is not null ? LoadFromAssemblyPath(path) : null;
        }
    }

    private sealed class LoadedPluginEntry(
        EdgePluginConfig config,
        string sha256,
        AssemblyLoadContext loadContext,
        IKiteEdgeMiddleware instance)
    {
        public EdgePluginConfig Config { get; private set; } = config;
        public string Sha256 { get; } = sha256;
        public IKiteEdgeMiddleware Instance { get; } = instance;

        public void UpdateConfig(EdgePluginConfig cfg) => Config = cfg;
        public void Unload() => loadContext.Unload();
    }
}
