namespace NotedX.Core.Storage;

/// <summary>Read-only, seekable window over a region of an inner stream.</summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _innerStartOffset;
    private readonly long _length;
    private long _position;

    public BoundedReadStream(Stream inner, long innerStartOffset, long length)
    {
        ArgumentNullException.ThrowIfNull(inner);

        if (!inner.CanRead)
        {
            throw new ArgumentException("The inner stream must be readable.", nameof(inner));
        }

        if (innerStartOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(innerStartOffset));
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _inner = inner;
        _innerStartOffset = innerStartOffset;
        _length = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var allowed = (int)Math.Min(buffer.Length, _length - _position);
        if (allowed <= 0)
        {
            return 0;
        }

        var read = _inner.Read(buffer[..allowed]);
        _position += read;
        return read;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var allowed = (int)Math.Min(buffer.Length, _length - _position);
        if (allowed <= 0)
        {
            return ValueTask.FromResult(0);
        }

        return ReadFromInnerAsync(buffer[..allowed], cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (!CanSeek)
        {
            throw new NotSupportedException("The inner stream is not seekable.");
        }

        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (target < 0 || target > _length)
        {
            throw new IOException("The requested position lies outside the blob window.");
        }

        _inner.Seek(_innerStartOffset + target, SeekOrigin.Begin);
        _position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async ValueTask<int> ReadFromInnerAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }
}
