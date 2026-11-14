using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kite.Core.Models;
using Kite.Core.Serialization;
using Kite.Core.Storage;

namespace Kite.Management.Core;

/// <summary>
/// Manages atomic immutable configuration snapshots inside <c>config/.history/</c>
/// supporting unified line diffs and one-click rollback.
/// </summary>
public sealed class ConfigSnapshotHistoryService
{
    private const int MaxRetainedSnapshots = 30;
    private readonly KiteHomeLayout _layout;

    public ConfigSnapshotHistoryService(KiteHomeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
    }

    public async ValueTask<ConfigSnapshotSummary> CaptureSnapshotAsync(
        string reason,
        string resourceKind = "all",
        string resourceId = "*",
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_layout.HistoryDirectory);

        string snapshotId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}";
        string snapDir = Path.Combine(_layout.HistoryDirectory, snapshotId);
        string snapPoolsDir = Path.Combine(snapDir, "pools");
        string snapSitesDir = Path.Combine(snapDir, "sites");
        Directory.CreateDirectory(snapPoolsDir);
        Directory.CreateDirectory(snapSitesDir);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        if (File.Exists(_layout.ServerConfigPath))
        {
            byte[] bytes = await File.ReadAllBytesAsync(_layout.ServerConfigPath, ct).ConfigureAwait(false);
            sha.AppendData(bytes);
            await File.WriteAllBytesAsync(Path.Combine(snapDir, "server.json"), bytes, ct).ConfigureAwait(false);
        }

        await CopyJsonFilesAsync(_layout.PoolsConfigDirectory, snapPoolsDir, sha, ct).ConfigureAwait(false);
        await CopyJsonFilesAsync(_layout.SitesConfigDirectory, snapSitesDir, sha, ct).ConfigureAwait(false);

        var summary = new ConfigSnapshotSummary
        {
            SnapshotId = snapshotId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Reason = string.IsNullOrWhiteSpace(reason) ? "Config update" : reason,
            ResourceKind = resourceKind,
            ResourceId = resourceId,
            Sha256 = Convert.ToHexString(sha.GetHashAndReset())[..16]
        };

        await AtomicJsonStore.WriteAtomicAsync(
            Path.Combine(snapDir, "meta.json"),
            summary,
            KiteJsonContext.Default.ConfigSnapshotSummary,
            ct).ConfigureAwait(false);

        PruneOldSnapshots();
        return summary;
    }

    public async ValueTask<IReadOnlyList<ConfigSnapshotSummary>> ListSnapshotsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_layout.HistoryDirectory))
        {
            return [];
        }

        var result = new List<ConfigSnapshotSummary>();
        foreach (string dir in Directory.EnumerateDirectories(_layout.HistoryDirectory)
                     .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string metaPath = Path.Combine(dir, "meta.json");
            ConfigSnapshotSummary? meta = await AtomicJsonStore
                .TryReadAsync(metaPath, KiteJsonContext.Default.ConfigSnapshotSummary, ct)
                .ConfigureAwait(false);
            if (meta is not null)
            {
                result.Add(meta);
            }
        }
        return result;
    }

    public async ValueTask<string> ComputeDiffAsync(string snapshotId, CancellationToken ct = default)
    {
        string snapDir = ResolveSnapshotDirectory(snapshotId);
        var sb = new StringBuilder();

        await AppendFileDiffAsync(sb, "server.json", Path.Combine(snapDir, "server.json"), _layout.ServerConfigPath, ct)
            .ConfigureAwait(false);

        foreach (string name in CollectRelativeJsonNames(Path.Combine(snapDir, "pools"), _layout.PoolsConfigDirectory))
        {
            await AppendFileDiffAsync(
                sb,
                $"pools/{name}",
                Path.Combine(snapDir, "pools", name),
                Path.Combine(_layout.PoolsConfigDirectory, name),
                ct).ConfigureAwait(false);
        }

        foreach (string name in CollectRelativeJsonNames(Path.Combine(snapDir, "sites"), _layout.SitesConfigDirectory))
        {
            await AppendFileDiffAsync(
                sb,
                $"sites/{name}",
                Path.Combine(snapDir, "sites", name),
                Path.Combine(_layout.SitesConfigDirectory, name),
                ct).ConfigureAwait(false);
        }

        return sb.Length == 0 ? "No differences detected between snapshot and current live configuration." : sb.ToString();
    }

    public async ValueTask<bool> RollbackToSnapshotAsync(string snapshotId, CancellationToken ct = default)
    {
        string snapDir = ResolveSnapshotDirectory(snapshotId);
        if (!Directory.Exists(snapDir))
        {
            return false;
        }

        await CaptureSnapshotAsync($"Pre-rollback backup before restoring {snapshotId}", "rollback", snapshotId, ct)
            .ConfigureAwait(false);

        string snapServer = Path.Combine(snapDir, "server.json");
        if (File.Exists(snapServer))
        {
            byte[] serverBytes = await File.ReadAllBytesAsync(snapServer, ct).ConfigureAwait(false);
            await AtomicJsonStore.WriteBytesAtomicAsync(_layout.ServerConfigPath, serverBytes, ct).ConfigureAwait(false);
        }

        await RestoreDirectoryJsonAsync(Path.Combine(snapDir, "pools"), _layout.PoolsConfigDirectory, ct).ConfigureAwait(false);
        await RestoreDirectoryJsonAsync(Path.Combine(snapDir, "sites"), _layout.SitesConfigDirectory, ct).ConfigureAwait(false);
        return true;
    }

    private string ResolveSnapshotDirectory(string snapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        string sanitized = KiteHomeLayout.SanitizeFileName(snapshotId);
        return Path.Combine(_layout.HistoryDirectory, sanitized);
    }

    private static async Task CopyJsonFilesAsync(string srcDir, string dstDir, IncrementalHash sha, CancellationToken ct)
    {
        if (!Directory.Exists(srcDir))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(srcDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (file.Contains(".tmp.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            byte[] bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            sha.AppendData(bytes);
            await File.WriteAllBytesAsync(Path.Combine(dstDir, Path.GetFileName(file)), bytes, ct).ConfigureAwait(false);
        }
    }

    private static async Task RestoreDirectoryJsonAsync(string snapSubDir, string liveSubDir, CancellationToken ct)
    {
        Directory.CreateDirectory(liveSubDir);
        var restoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(snapSubDir))
        {
            foreach (string file in Directory.EnumerateFiles(snapSubDir, "*.json"))
            {
                string name = Path.GetFileName(file);
                restoredNames.Add(name);
                byte[] bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
                await AtomicJsonStore.WriteBytesAtomicAsync(Path.Combine(liveSubDir, name), bytes, ct).ConfigureAwait(false);
            }
        }

        foreach (string liveFile in Directory.EnumerateFiles(liveSubDir, "*.json").ToList())
        {
            string name = Path.GetFileName(liveFile);
            if (!restoredNames.Contains(name))
            {
                try { File.Delete(liveFile); } catch { }
            }
        }
    }

    private static IEnumerable<string> CollectRelativeJsonNames(string dirA, string dirB)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(dirA))
        {
            foreach (string f in Directory.EnumerateFiles(dirA, "*.json")) set.Add(Path.GetFileName(f));
        }
        if (Directory.Exists(dirB))
        {
            foreach (string f in Directory.EnumerateFiles(dirB, "*.json")) set.Add(Path.GetFileName(f));
        }
        return set;
    }

    private static async Task AppendFileDiffAsync(
        StringBuilder sb,
        string label,
        string snapFile,
        string liveFile,
        CancellationToken ct)
    {
        string[] snapLines = File.Exists(snapFile) ? await File.ReadAllLinesAsync(snapFile, ct).ConfigureAwait(false) : [];
        string[] liveLines = File.Exists(liveFile) ? await File.ReadAllLinesAsync(liveFile, ct).ConfigureAwait(false) : [];
        if (snapLines.SequenceEqual(liveLines, StringComparer.Ordinal))
        {
            return;
        }

        sb.AppendLine($"--- snapshot/{label}");
        sb.AppendLine($"+++ live/{label}");
        int max = Math.Max(snapLines.Length, liveLines.Length);
        for (int i = 0; i < max; i++)
        {
            string? s = i < snapLines.Length ? snapLines[i] : null;
            string? l = i < liveLines.Length ? liveLines[i] : null;
            if (!string.Equals(s, l, StringComparison.Ordinal))
            {
                if (s is not null) sb.AppendLine($"- {s}");
                if (l is not null) sb.AppendLine($"+ {l}");
            }
        }
    }

    private void PruneOldSnapshots()
    {
        if (!Directory.Exists(_layout.HistoryDirectory))
        {
            return;
        }

        List<string> dirs = Directory.EnumerateDirectories(_layout.HistoryDirectory)
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (int i = MaxRetainedSnapshots; i < dirs.Count; i++)
        {
            try { Directory.Delete(dirs[i], recursive: true); } catch { }
        }
    }
}
