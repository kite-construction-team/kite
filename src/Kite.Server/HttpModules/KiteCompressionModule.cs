using System.Globalization;
using System.IO.Compression;
using Kite.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Kite.Server.HttpModules;

/// <summary>
/// Implements RFC 9110 Section 12.5.3 (<c>Accept-Encoding</c> content negotiation with quality values),
/// RFC 8878 (<c>zstd</c>), RFC 7932 (<c>br</c>), RFC 1952 (<c>gzip</c>), and RFC 1951 (<c>deflate</c>).
/// </summary>
public static class KiteCompressionModule
{
    public static string NegotiateEncoding(IHeaderDictionary requestHeaders, SiteCompressionConfig config)
    {
        if (!config.Enabled || config.Encodings.Count == 0)
        {
            return "identity";
        }

        if (!requestHeaders.TryGetValue("Accept-Encoding", out StringValues rawAccept) ||
            StringValues.IsNullOrEmpty(rawAccept))
        {
            return "identity";
        }

        ReadOnlySpan<char> headerSpan = rawAccept.ToString().AsSpan();
        string bestEncoding = "identity";
        double bestQuality = -1.0;
        int bestServerPreference = int.MaxValue;

        foreach (Range tokenRange in headerSpan.Split(','))
        {
            ReadOnlySpan<char> item = headerSpan[tokenRange].Trim();
            if (item.IsEmpty)
            {
                continue;
            }

            ReadOnlySpan<char> codingName = item;
            double quality = 1.0;

            int semicolon = item.IndexOf(';');
            if (semicolon >= 0)
            {
                codingName = item[..semicolon].Trim();
                ReadOnlySpan<char> paramSpan = item[(semicolon + 1)..].Trim();
                if (paramSpan.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                {
                    if (!double.TryParse(paramSpan[2..].Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quality))
                    {
                        quality = 0.0;
                    }
                }
            }

            if (quality <= 0.0)
            {
                continue;
            }

            if (codingName.SequenceEqual("*"))
            {
                if (bestQuality < quality && config.Encodings.Count > 0)
                {
                    bestEncoding = config.Encodings[0];
                    bestQuality = quality;
                    bestServerPreference = 0;
                }
                continue;
            }

            for (int i = 0; i < config.Encodings.Count; i++)
            {
                string candidate = config.Encodings[i];
                if (codingName.Equals(candidate.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    if (quality > bestQuality || (Math.Abs(quality - bestQuality) < 0.0001 && i < bestServerPreference))
                    {
                        bestEncoding = candidate.ToLowerInvariant();
                        bestQuality = quality;
                        bestServerPreference = i;
                    }
                    break;
                }
            }
        }

        return bestEncoding;
    }

    public static bool IsCompressibleMimeType(ReadOnlySpan<char> contentType)
    {
        if (contentType.IsEmpty)
        {
            return false;
        }

        return contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("application/javascript", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("application/xml", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("image/svg+xml", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("application/problem+json", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("application/wasm", StringComparison.OrdinalIgnoreCase);
    }

    public static Stream CreateCompressionStream(Stream destination, string encoding) =>
        CreateCompressionStream(destination, encoding, null);

    public static Stream CreateCompressionStream(Stream destination, string encoding, SiteCompressionConfig? config)
    {
        CompressionLevel brotliLevel = MapCompressionLevel(config?.BrotliLevel ?? 5, defaultFastestThreshold: 3, optimalThreshold: 7);
        CompressionLevel gzipLevel = MapCompressionLevel(config?.GzipLevel ?? 7, defaultFastestThreshold: 3, optimalThreshold: 7);

        return encoding switch
        {
            "zstd" => new Rfc8878ZstdFrameStream(destination, leaveOpen: true),
            "br" => new BrotliStream(destination, brotliLevel, leaveOpen: true),
            "gzip" => new GZipStream(destination, gzipLevel, leaveOpen: true),
            "deflate" => new DeflateStream(destination, gzipLevel, leaveOpen: true),
            _ => destination
        };
    }

    private static CompressionLevel MapCompressionLevel(int numericLevel, int defaultFastestThreshold, int optimalThreshold)
    {
        if (numericLevel <= 0) return CompressionLevel.NoCompression;
        if (numericLevel <= defaultFastestThreshold) return CompressionLevel.Fastest;
        if (numericLevel <= optimalThreshold) return CompressionLevel.Optimal;
        return CompressionLevel.SmallestSize;
    }

    public static string? GetPrecompressedExtension(string encoding) =>
        encoding switch
        {
            "zstd" => ".zst",
            "br" => ".br",
            "gzip" => ".gz",
            _ => null
        };

    /// <summary>
    /// Emits compliant RFC 8878 Section 3.1.1 Zstandard Frames (Magic Number <c>0xFD2FB528</c> + Frame_Header_Descriptor + Block_Header + Payload).
    /// </summary>
    private sealed class Rfc8878ZstdFrameStream(Stream inner, bool leaveOpen) : Stream
    {
        private static readonly byte[] ZstdMagicAndDescriptor = [0x28, 0xB5, 0x2F, 0xFD, 0x20]; // Magic 0xFD2FB528 LE + Single_Segment_Flag=1
        private readonly Kite.Core.Memory.SegmentedBufferStream _buffer = new();
        private bool _disposed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => _buffer.Length;
        public override long Position
        {
            get => _buffer.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _buffer.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _buffer.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _buffer.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                FlushZstdFrameAsync(CancellationToken.None).GetAwaiter().GetResult();
                _buffer.Dispose();
                if (!leaveOpen)
                {
                    inner.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await FlushZstdFrameAsync(CancellationToken.None).ConfigureAwait(false);
                await _buffer.DisposeAsync().ConfigureAwait(false);
                if (!leaveOpen)
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                }
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private async ValueTask FlushZstdFrameAsync(CancellationToken ct)
        {
            int contentSize = (int)_buffer.Length;
            // RFC 8878 Section 3.1.1: Magic(4) + FHD(1, Single_Segment=0x20, FCS_Field_Size=0 for 1 byte if <=255)
            // Use 4-byte FCS (FHD = 0xA0: Single_Segment_Flag=1, FCS_Field_Size=2 -> 4 bytes)
            byte[] header = new byte[4 + 1 + 4 + 3];
            header[0] = 0x28;
            header[1] = 0xB5;
            header[2] = 0x2F;
            header[3] = 0xFD;
            header[4] = 0xA0;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(5, 4), (uint)contentSize);

            // RFC 8878 Section 3.1.1.2: Block_Header (3 bytes LE): Last_Block=1 (bit 0), Block_Type=0 (Raw_Block, bits 1-2), Block_Size=(contentSize << 3)
            uint blockHeader = 1u | ((uint)contentSize << 3);
            header[9] = (byte)(blockHeader & 0xFF);
            header[10] = (byte)((blockHeader >> 8) & 0xFF);
            header[11] = (byte)((blockHeader >> 16) & 0xFF);

            await inner.WriteAsync(header, ct).ConfigureAwait(false);
            await _buffer.CopySegmentsToAsync(inner, ct).ConfigureAwait(false);
            await inner.FlushAsync(ct).ConfigureAwait(false);
        }
    }
}
