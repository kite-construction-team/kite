using System.Buffers;
using System.IO.Pipelines;
using Microsoft.Extensions.ObjectPool;

namespace Kite.Core.Memory;

/// <summary>
/// A Large-Object-Heap (LOH)-free Stream backed by fixed-size chunks rented from <see cref="ArrayPool{T}.Shared"/>.
/// Guarantees that even multi-megabyte cached assets or buffered responses never allocate arrays >= 85,000 bytes.
/// </summary>
public sealed class SegmentedBufferStream : Stream, IResettable
{
    public const int DefaultSegmentSize = 16 * 1024; // 16 KB (well below 85,000 byte LOH threshold)

    private readonly int _segmentSize;
    private readonly List<byte[]> _segments = new(4);
    private long _length;
    private long _position;
    private bool _disposed;
    private bool _ownershipTransferred;

    public SegmentedBufferStream()
        : this(DefaultSegmentSize)
    {
    }

    public SegmentedBufferStream(int segmentSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(segmentSize, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(segmentSize, 85_000);
        _segmentSize = segmentSize;
    }

    public int SegmentCount => _segments.Count;
    public int SegmentSize => _segmentSize;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed && !_ownershipTransferred;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ownershipTransferred)
        {
            throw new InvalidOperationException("Cannot write after buffer ownership has been transferred.");
        }

        int remaining = buffer.Length;
        int sourceIndex = 0;

        while (remaining > 0)
        {
            int segmentIndex = (int)(_position / _segmentSize);
            int segmentOffset = (int)(_position % _segmentSize);

            while (_segments.Count <= segmentIndex)
            {
                _segments.Add(ArrayPool<byte>.Shared.Rent(_segmentSize));
            }

            byte[] segment = _segments[segmentIndex];
            int writableInSegment = _segmentSize - segmentOffset;
            int toCopy = Math.Min(remaining, writableInSegment);

            buffer.Slice(sourceIndex, toCopy).CopyTo(segment.AsSpan(segmentOffset, toCopy));

            _position += toCopy;
            if (_position > _length)
            {
                _length = _position;
            }

            sourceIndex += toCopy;
            remaining -= toCopy;
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long available = _length - _position;
        if (available <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        int totalToRead = (int)Math.Min(available, buffer.Length);
        int remaining = totalToRead;
        int destIndex = 0;

        while (remaining > 0)
        {
            int segmentIndex = (int)(_position / _segmentSize);
            int segmentOffset = (int)(_position % _segmentSize);
            int readableInSegment = _segmentSize - segmentOffset;
            int toCopy = Math.Min(remaining, readableInSegment);

            _segments[segmentIndex].AsSpan(segmentOffset, toCopy).CopyTo(buffer.Slice(destIndex, toCopy));

            _position += toCopy;
            destIndex += toCopy;
            remaining -= toCopy;
        }

        return totalToRead;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        ArgumentOutOfRangeException.ThrowIfNegative(target);
        _position = target;
        return _position;
    }

    public override void SetLength(long value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        int requiredSegments = value == 0 ? 0 : (int)((value + _segmentSize - 1) / _segmentSize);
        while (_segments.Count < requiredSegments)
        {
            _segments.Add(ArrayPool<byte>.Shared.Rent(_segmentSize));
        }

        while (_segments.Count > requiredSegments)
        {
            int lastIndex = _segments.Count - 1;
            ArrayPool<byte>.Shared.Return(_segments[lastIndex]);
            _segments.RemoveAt(lastIndex);
        }

        _length = value;
        if (_position > _length)
        {
            _position = _length;
        }
    }

    /// <summary>
    /// Copies the buffered segments directly to a destination stream from offset 0 without allocating intermediate arrays.
    /// </summary>
    public async ValueTask CopySegmentsToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long remaining = _length;
        int segmentIndex = 0;

        while (remaining > 0 && segmentIndex < _segments.Count)
        {
            int toWrite = (int)Math.Min(remaining, _segmentSize);
            await destination.WriteAsync(_segments[segmentIndex].AsMemory(0, toWrite), cancellationToken).ConfigureAwait(false);
            remaining -= toWrite;
            segmentIndex++;
        }
    }

    /// <summary>
    /// Transfers ownership of the underlying pooled segments into a thread-safe <see cref="PooledBytePayload"/>
    /// suitable for storing in Output Cache without copying or allocating on the LOH.
    /// </summary>
    public PooledBytePayload DetachPayload()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ownershipTransferred)
        {
            throw new InvalidOperationException("Payload ownership was already detached.");
        }

        _ownershipTransferred = true;
        byte[][] ownedSegments = _segments.ToArray();
        _segments.Clear();
        return new PooledBytePayload(ownedSegments, _segmentSize, _length);
    }

    public bool TryReset()
    {
        if (_disposed)
        {
            return false;
        }

        if (!_ownershipTransferred)
        {
            // Keep at most 1 segment warm to avoid ArrayPool rent/return churn on small payloads
            while (_segments.Count > 1)
            {
                int last = _segments.Count - 1;
                ArrayPool<byte>.Shared.Return(_segments[last]);
                _segments.RemoveAt(last);
            }
        }
        else
        {
            _segments.Clear();
            _ownershipTransferred = false;
        }

        _length = 0;
        _position = 0;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (!_ownershipTransferred)
            {
                for (int i = 0; i < _segments.Count; i++)
                {
                    ArrayPool<byte>.Shared.Return(_segments[i]);
                }
            }
            _segments.Clear();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Holds pooled byte segments for an Output Cache entry or in-memory response body.
/// Can be concurrently read by multiple HTTP requests and returns segments to <see cref="ArrayPool{T}.Shared"/> when disposed.
/// </summary>
public sealed class PooledBytePayload : IDisposable
{
    private byte[][]? _segments;
    private readonly int _segmentSize;
    private readonly long _length;

    internal PooledBytePayload(byte[][] segments, int segmentSize, long length)
    {
        _segments = segments;
        _segmentSize = segmentSize;
        _length = length;
    }

    public long Length => _length;
    public int SegmentCount => _segments?.Length ?? 0;

    public Stream CreateReadStream()
    {
        byte[][]? segments = Volatile.Read(ref _segments);
        ObjectDisposedException.ThrowIf(segments is null, this);
        return new PooledPayloadReadStream(segments, _segmentSize, _length);
    }

    public async ValueTask WriteToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        byte[][]? segments = Volatile.Read(ref _segments);
        ObjectDisposedException.ThrowIf(segments is null, this);

        long remaining = _length;
        for (int i = 0; i < segments.Length && remaining > 0; i++)
        {
            int count = (int)Math.Min(remaining, _segmentSize);
            await destination.WriteAsync(segments[i].AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            remaining -= count;
        }
    }

    public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken = default)
    {
        byte[][]? segments = Volatile.Read(ref _segments);
        ObjectDisposedException.ThrowIf(segments is null, this);

        long remaining = _length;
        for (int i = 0; i < segments.Length && remaining > 0; i++)
        {
            int count = (int)Math.Min(remaining, _segmentSize);
            FlushResult result = await writer.WriteAsync(segments[i].AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (result.IsCanceled || result.IsCompleted)
            {
                break;
            }
            remaining -= count;
        }
    }

    public void Dispose()
    {
        byte[][]? segments = Interlocked.Exchange(ref _segments, null);
        if (segments is not null)
        {
            for (int i = 0; i < segments.Length; i++)
            {
                ArrayPool<byte>.Shared.Return(segments[i]);
            }
        }
    }

    private sealed class PooledPayloadReadStream(byte[][] segments, int segmentSize, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, length);
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long available = length - _position;
            if (available <= 0 || buffer.IsEmpty)
            {
                return 0;
            }

            int totalToRead = (int)Math.Min(available, buffer.Length);
            int remaining = totalToRead;
            int destOffset = 0;

            while (remaining > 0)
            {
                int segmentIndex = (int)(_position / segmentSize);
                int segmentOffset = (int)(_position % segmentSize);
                int toCopy = Math.Min(remaining, segmentSize - segmentOffset);

                segments[segmentIndex].AsSpan(segmentOffset, toCopy).CopyTo(buffer.Slice(destOffset, toCopy));
                _position += toCopy;
                destOffset += toCopy;
                remaining -= toCopy;
            }

            return totalToRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            _position = Math.Clamp(target, 0, length);
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
