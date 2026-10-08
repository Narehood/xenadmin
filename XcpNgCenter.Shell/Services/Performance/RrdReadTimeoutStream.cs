namespace XcpNgCenter.Shell.Services.Performance;

/// <summary>
/// Preserves the HTTP stream's receive timeout for each asynchronous body read.
/// Progress starts a fresh window; expiry closes this fetch without cancelling the poller.
/// </summary>
internal sealed class RrdReadTimeoutStream(Stream input, int timeout) : Stream
{
    public static Stream Wrap(Stream input) => input.CanTimeout && input.ReadTimeout > 0
        ? new RrdReadTimeoutStream(input, input.ReadTimeout)
        : input;

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // XmlReader supplies no cancellation token. Closing the transport also aborts
        // reads on streams that do not implement cancellation themselves.
        using var close = deadline.Token.Register(input.Dispose);
        deadline.CancelAfter(timeout);
        try
        {
            var count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return count;
        }
        catch (Exception error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The RRD body read timed out.", error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) input.Dispose();
        base.Dispose(disposing);
    }

    public override bool CanRead => input.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
