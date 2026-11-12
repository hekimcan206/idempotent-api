namespace IdempotentApi;

/// <summary>
/// Passes every write straight to the real response body and keeps a copy for the store. Streaming is unaffected
/// (the client sees bytes as the handler writes them); only the copy is bounded.
/// </summary>
internal sealed class ResponseCapture(Stream inner, int limit) : Stream
{
    private readonly MemoryStream _copy = new();

    /// <summary>The handler wrote more than the limit; the copy was dropped.</summary>
    public bool Overflowed { get; private set; }

    public ReadOnlyMemory<byte> Captured => Overflowed ? ReadOnlyMemory<byte>.Empty : _copy.GetBuffer().AsMemory(0, (int)_copy.Length);

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Keep(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Keep(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        Keep(buffer.Span);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Keep(ReadOnlySpan<byte> data)
    {
        if (Overflowed)
        {
            return;
        }

        if (_copy.Length + data.Length > limit)
        {
            Overflowed = true;
            _copy.SetLength(0);
            _copy.Capacity = 0;
            return;
        }

        _copy.Write(data);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _copy.Dispose();
        }

        base.Dispose(disposing);
    }
}
