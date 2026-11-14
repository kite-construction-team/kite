using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Kite.Core.Memory;

namespace Kite.Core.Storage;

/// <summary>
/// Provides atomic, crash-safe JSON persistence using temporary files and atomic OS rename/move operations,
/// coupled with retry backoff for Windows file-sharing locks and SHA-256 content hashing.
/// </summary>
public static class AtomicJsonStore
{
    private const int MaxRetries = 6;

    public static async ValueTask<string> WriteAtomicAsync<T>(
        string filePath,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{filePath}.tmp.{Guid.NewGuid():N}";
        string contentHash;

        try
        {
            await using var bufferStream = new SegmentedBufferStream();
            await JsonSerializer.SerializeAsync(bufferStream, value, typeInfo, cancellationToken).ConfigureAwait(false);
            contentHash = ComputeSha256Hex(bufferStream);

            await using (var fileStream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                await bufferStream.CopySegmentsToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            await MoveWithRetryAsync(tempPath, filePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* Best-effort cleanup */ }
            }
        }

        return contentHash;
    }

    public static async ValueTask WriteBytesAtomicAsync(
        string filePath,
        byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{filePath}.tmp.{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            await MoveWithRetryAsync(tempPath, filePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    public static async ValueTask<T?> TryReadAsync<T>(
        string filePath,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        (T? value, _) = await ReadWithHashAsync(filePath, typeInfo, cancellationToken).ConfigureAwait(false);
        return value;
    }

    private static async ValueTask MoveWithRetryAsync(string tempPath, string destPath, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, destPath, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < MaxRetries)
            {
                await Task.Delay(attempt * 15, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static async ValueTask<(T? Value, string? ContentHash)> ReadWithHashAsync<T>(
        string filePath,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return (default, null);
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var fileStream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                await using var memoryStream = new SegmentedBufferStream();
                await fileStream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
                if (memoryStream.Length == 0)
                {
                    return (default, null);
                }

                string hash = ComputeSha256Hex(memoryStream);
                memoryStream.Position = 0;
                T? result = await JsonSerializer.DeserializeAsync(memoryStream, typeInfo, cancellationToken).ConfigureAwait(false);
                return (result, hash);
            }
            catch (FileNotFoundException)
            {
                return (default, null);
            }
            catch (DirectoryNotFoundException)
            {
                return (default, null);
            }
            catch (Exception ex) when ((ex is IOException or JsonException) && attempt < MaxRetries)
            {
                await Task.Delay(attempt * 15, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static string ComputeSha256Hex(SegmentedBufferStream stream)
    {
        long previousPos = stream.Position;
        stream.Position = 0;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.AppendData(buffer.AsSpan(0, bytesRead));
            }

            Span<byte> hashBytes = stackalloc byte[32];
            sha.GetHashAndReset(hashBytes);
            return Convert.ToHexString(hashBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            stream.Position = previousPos;
        }
    }
}
