namespace SqlHarness.Mcp;

/// <summary>
/// Explicit stdin-EOF to host-shutdown binding (T5 fix R1). A scratch probe
/// against the real transport (SDK ModelContextProtocol 2.2.0) showed that
/// completing the server's stdin while a call is in flight neither cancels
/// the handler token nor ends the server loop: without this binding the call
/// would hang until the process time budget expires (default 900 s, up to
/// 86400 s). This stream observes the transport's own reads and cancels the
/// host-shutdown source on the first EOF (a non-empty read returning 0 bytes,
/// or ReadByte returning -1). The host links that source into every handler
/// execution and into the server loop, so an EOF cancels the in-flight Core
/// call (a stable cancelled result, never the natural watch exit 7),
/// releases the gate, and ends the loop. No thread, no polling, no timing:
/// cancellation is synchronous with the EOF read. Zero-length reads are not
/// EOF and never signal.
/// </summary>
internal sealed class EofShutdownInput(Stream inner, CancellationTokenSource shutdown) : Stream
{
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly CancellationTokenSource _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (count > 0 && read == 0)
            SignalEof();
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        if (!buffer.IsEmpty && read == 0)
            SignalEof();
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (!buffer.IsEmpty && read == 0)
            SignalEof();
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        if (count > 0 && read == 0)
            SignalEof();
        return read;
    }

    public override int ReadByte()
    {
        var value = _inner.ReadByte();
        if (value < 0)
            SignalEof();
        return value;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    private void SignalEof()
    {
        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The host already tore down; a late EOF read signals nothing.
        }
    }
}