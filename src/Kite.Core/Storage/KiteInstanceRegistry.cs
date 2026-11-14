using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Kite.Core.Models;
using Kite.Core.Serialization;

namespace Kite.Core.Storage;

public sealed class KiteHomeLockedException(string message, int? ownerPid = null) : IOException(message)
{
    public int? OwnerPid { get; } = ownerPid;
}

/// <summary>
/// Cross-process and intra-process exclusive lock on <c>&lt;KITE_HOME&gt;/state/server.lock</c>
/// preventing two server engines from corrupting shared state, logs, and worker trees.
/// </summary>
public sealed class KiteHomeLock : IDisposable
{
    private static readonly ConcurrentDictionary<string, int> ActiveInProcessLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _lockPath;
    private FileStream? _stream;
    private bool _disposed;

    private KiteHomeLock(string lockPath, FileStream stream)
    {
        _lockPath = lockPath;
        _stream = stream;
    }

    public string LockPath => _lockPath;

    public static KiteHomeLock Acquire(KiteHomeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        layout.EnsureDirectoriesCreated();
        string lockPath = Path.GetFullPath(layout.ServerLockPath);

        if (!ActiveInProcessLocks.TryAdd(lockPath, Environment.ProcessId))
        {
            throw new KiteHomeLockedException(
                $"KITE_HOME '{layout.RootDirectory}' is already locked by running Kite instance in process PID {Environment.ProcessId}.",
                Environment.ProcessId);
        }

        FileStream? fs = null;
        try
        {
            fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            fs.SetLength(0);
            byte[] banner = Encoding.UTF8.GetBytes($"pid={Environment.ProcessId}\nstarted={DateTimeOffset.UtcNow:O}\n");
            fs.Write(banner);
            fs.Flush();
            return new KiteHomeLock(lockPath, fs);
        }
        catch (IOException)
        {
            fs?.Dispose();
            ActiveInProcessLocks.TryRemove(lockPath, out _);
            int? ownerPid = TryReadOwnerPid(lockPath);
            string ownerText = ownerPid.HasValue ? $" (PID {ownerPid.Value})" : string.Empty;
            throw new KiteHomeLockedException(
                $"KITE_HOME '{layout.RootDirectory}' is already in use by another running Kite server{ownerText}. Use a distinct --home / KITE_HOME directory or .kite/ workspace.",
                ownerPid);
        }
        catch
        {
            fs?.Dispose();
            ActiveInProcessLocks.TryRemove(lockPath, out _);
            throw;
        }
    }

    private static int? TryReadOwnerPid(string lockPath)
    {
        try
        {
            using var reader = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(reader, Encoding.UTF8);
            string? firstLine = sr.ReadLine();
            if (firstLine is not null &&
                firstLine.StartsWith("pid=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(firstLine.AsSpan(4), out int pid))
            {
                return pid;
            }
        }
        catch
        {
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream?.Dispose();
        _stream = null;
        ActiveInProcessLocks.TryRemove(_lockPath, out _);
        try
        {
            if (File.Exists(_lockPath))
            {
                File.Delete(_lockPath);
            }
        }
        catch
        {
        }
    }
}

/// <summary>
/// Machine-wide registry tracking live Kite server instances and coordinating
/// lock-backed leader election for the self-forming local edge mesh.
/// </summary>
public sealed class KiteInstanceRegistry
{
    private static readonly ConcurrentDictionary<string, string> ActiveLeaderLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, int> ActiveInstanceLocks = new(StringComparer.OrdinalIgnoreCase);

    public KiteInstanceRegistry(string? rootDirectory = null)
    {
        RootDirectory = ResolveRegistryDirectory(rootDirectory);
        MeshDirectory = Path.Combine(RootDirectory, "mesh");
        LeaderLockPath = Path.Combine(MeshDirectory, "edge-leader.lock");
    }

    public string RootDirectory { get; }
    public string MeshDirectory { get; }
    public string LeaderLockPath { get; }

    public static string ResolveRegistryDirectory(string? explicitOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitOverride))
        {
            return Path.GetFullPath(explicitOverride);
        }

        string? envDir = Environment.GetEnvironmentVariable("KITE_REGISTRY_DIR");
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            return Path.GetFullPath(envDir);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "Kite", "instances");
        }

        return Path.Combine(Path.GetTempPath(), "kite-instances");
    }

    public void EnsureDirectoriesCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(MeshDirectory);
    }

    public static string ComputeInstanceId(string kiteHomePath)
    {
        string full = Path.GetFullPath(kiteHomePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string folderName = Path.GetFileName(full);
        if (string.Equals(folderName, ".kite", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(folderName, "kite-data", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(full);
            string? parentName = parent is not null ? Path.GetFileName(parent) : null;
            if (!string.IsNullOrWhiteSpace(parentName))
            {
                folderName = parentName;
            }
        }

        string slug = SanitizeSlug(folderName);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant()));
        string suffix = Convert.ToHexString(hash[..4]).ToLowerInvariant();
        return $"{slug}-{suffix}";
    }

    public static string ComputeAutoDomainSlug(string kiteHomePath)
    {
        string full = Path.GetFullPath(kiteHomePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string folderName = Path.GetFileName(full);
        if (string.Equals(folderName, ".kite", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(folderName, "kite-data", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(full);
            string? parentName = parent is not null ? Path.GetFileName(parent) : null;
            if (!string.IsNullOrWhiteSpace(parentName))
            {
                folderName = parentName;
            }
        }

        return SanitizeSlug(folderName);
    }

    public async ValueTask<InstanceRegistrationHandle> RegisterAsync(
        InstanceDescriptor descriptor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        EnsureDirectoriesCreated();

        string id = string.IsNullOrWhiteSpace(descriptor.InstanceId)
            ? ComputeInstanceId(descriptor.KiteHomePath)
            : descriptor.InstanceId;
        descriptor.InstanceId = id;

        string jsonPath = Path.Combine(RootDirectory, $"{id}.json");
        string lockPath = Path.GetFullPath(Path.Combine(RootDirectory, $"{id}.lock"));

        ActiveInstanceLocks[lockPath] = Environment.ProcessId;
        FileStream? lockStream = null;
        try
        {
            lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            lockStream.SetLength(0);
            lockStream.Write(Encoding.UTF8.GetBytes($"pid={Environment.ProcessId}\n"));
            lockStream.Flush();
        }
        catch
        {
            lockStream?.Dispose();
            ActiveInstanceLocks.TryRemove(lockPath, out _);
            throw;
        }

        await AtomicJsonStore.WriteAtomicAsync(jsonPath, descriptor, KiteJsonContext.Default.InstanceDescriptor, ct).ConfigureAwait(false);
        return new InstanceRegistrationHandle(id, jsonPath, lockPath, lockStream);
    }

    public async ValueTask UpdateAsync(InstanceDescriptor descriptor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        EnsureDirectoriesCreated();
        string jsonPath = Path.Combine(RootDirectory, $"{descriptor.InstanceId}.json");
        await AtomicJsonStore.WriteAtomicAsync(jsonPath, descriptor, KiteJsonContext.Default.InstanceDescriptor, ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<InstanceDescriptor>> ListLiveInstancesAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(RootDirectory))
        {
            return [];
        }

        var results = new List<InstanceDescriptor>();
        foreach (string jsonPath in Directory.EnumerateFiles(RootDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            string id = Path.GetFileNameWithoutExtension(jsonPath);
            string lockPath = Path.GetFullPath(Path.Combine(RootDirectory, $"{id}.lock"));

            if (!IsLockActive(lockPath))
            {
                TryDeleteQuietly(jsonPath);
                TryDeleteQuietly(lockPath);
                continue;
            }

            try
            {
                (InstanceDescriptor? desc, _) = await AtomicJsonStore
                    .ReadWithHashAsync(jsonPath, KiteJsonContext.Default.InstanceDescriptor, ct)
                    .ConfigureAwait(false);
                if (desc is not null)
                {
                    results.Add(desc);
                }
            }
            catch
            {
            }
        }

        return results
            .OrderByDescending(d => d.Role == InstanceRole.MeshLeader)
            .ThenBy(d => d.StartedAtUtc)
            .ToList();
    }

    public EdgeLeaderLockHandle? TryAcquireLeaderLock(string ownerInstanceId)
    {
        EnsureDirectoriesCreated();
        string fullLockPath = Path.GetFullPath(LeaderLockPath);

        if (!ActiveLeaderLocks.TryAdd(fullLockPath, ownerInstanceId))
        {
            return null;
        }

        FileStream? fs = null;
        try
        {
            fs = new FileStream(fullLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            fs.SetLength(0);
            fs.Write(Encoding.UTF8.GetBytes($"owner={ownerInstanceId}\npid={Environment.ProcessId}\n"));
            fs.Flush();
            return new EdgeLeaderLockHandle(fullLockPath, ownerInstanceId, fs);
        }
        catch (IOException)
        {
            fs?.Dispose();
            ActiveLeaderLocks.TryRemove(fullLockPath, out _);
            return null;
        }
        catch
        {
            fs?.Dispose();
            ActiveLeaderLocks.TryRemove(fullLockPath, out _);
            throw;
        }
    }

    public bool IsLeaderLockHeld() => IsLockActive(Path.GetFullPath(LeaderLockPath), checkLeaderTable: true);

    private static bool IsLockActive(string fullLockPath, bool checkLeaderTable = false)
    {
        if (checkLeaderTable && ActiveLeaderLocks.ContainsKey(fullLockPath))
        {
            return true;
        }

        if (!checkLeaderTable && ActiveInstanceLocks.ContainsKey(fullLockPath))
        {
            return true;
        }

        if (!File.Exists(fullLockPath))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(fullLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeSlug(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "kite-node";
        }

        Span<char> buf = stackalloc char[Math.Min(32, raw.Length)];
        int written = 0;
        bool lastDash = false;
        foreach (char ch in raw)
        {
            if (written >= buf.Length)
            {
                break;
            }

            char lower = char.ToLowerInvariant(ch);
            if ((lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9'))
            {
                buf[written++] = lower;
                lastDash = false;
            }
            else if (!lastDash && written > 0)
            {
                buf[written++] = '-';
                lastDash = true;
            }
        }

        while (written > 0 && buf[written - 1] == '-')
        {
            written--;
        }

        return written > 0 ? new string(buf[..written]) : "kite-node";
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    public sealed class InstanceRegistrationHandle(
        string instanceId,
        string jsonPath,
        string lockPath,
        FileStream lockStream) : IAsyncDisposable
    {
        private bool _disposed;

        public string InstanceId => instanceId;

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            lockStream.Dispose();
            TryDeleteQuietly(jsonPath);
            TryDeleteQuietly(lockPath);
            ActiveInstanceLocks.TryRemove(lockPath, out _);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class EdgeLeaderLockHandle(
        string lockPath,
        string ownerInstanceId,
        FileStream lockStream) : IDisposable
    {
        private bool _disposed;

        public string OwnerInstanceId => ownerInstanceId;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lockStream.Dispose();
            TryDeleteQuietly(lockPath);
            ActiveLeaderLocks.TryRemove(lockPath, out _);
        }
    }
}
