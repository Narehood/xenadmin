using System.Net;
using System.Net.Sockets;
using XcpNgCenter.Shell.Services;
using XenAPI;
using XenAdmin.Network;
using XenCenterLib;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.Tests;

public sealed class ConsoleStartupTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task CancellationAbortsSilentConnectProxyAndTlsNegotiation(bool useProxy, bool useTls)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/console");
        using var cancellation = new CancellationTokenSource();
        var accept = listener.AcceptTcpClientAsync();
        var destination = useProxy ? new Uri("https://console.invalid/console")
            : useTls ? new UriBuilder(endpoint) { Scheme = "https" }.Uri : endpoint;
        var work = Task.Run(() => HTTP.HttpConnectStream(destination,
            useProxy ? new WebProxy(endpoint) : null, "synthetic-session", 0, cancellation.Token));
        using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(5));
        var received = new byte[1024];
        var receivedCount = await peer.GetStream().ReadAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(receivedCount > 0);
        if (!useTls || useProxy)
        {
            // TCP may split the CONNECT headers across packets. Wait until the
            // worker is waiting for the response before exercising cancellation.
            var headers = new System.Text.StringBuilder(System.Text.Encoding.ASCII.GetString(received, 0, receivedCount));
            while (!headers.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                receivedCount = await peer.GetStream().ReadAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(receivedCount > 0);
                headers.Append(System.Text.Encoding.ASCII.GetString(received, 0, receivedCount));
            }
        }
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await work.WaitAsync(TimeSpan.FromSeconds(5)));
        // Cancellation closes the underlying socket rather than leaving a worker behind.
        try
        {
            // An in-flight TLS ClientHello may have buffered bytes after abort.
            while (await peer.GetStream().ReadAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) > 0) { }
        }
        catch (IOException error) when (error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
        {
            // Aborting a socket may reset it instead of sending an orderly FIN.
        }
    }

    [Fact]
    public async Task StartupDeadlineClosesBlockedHandshake()
    {
        using var parent = new CancellationTokenSource();
        using var guard = new ConsoleStartupGuard(parent.Token, TimeSpan.FromMilliseconds(150));
        using var stream = new BlockingStream();
        guard.AttachTransport(stream);
        var read = Task.Run(() => stream.ReadByte());
        await stream.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(guard.Token.IsCancellationRequested);
        Assert.Contains("handshake", guard.FailureMessage);
        Assert.False(guard.Complete());
    }

    [Fact]
    public async Task SuccessfulHandshakeDisarmsDeadlineAndParentStillClosesTransport()
    {
        using var parent = new CancellationTokenSource();
        using var guard = new ConsoleStartupGuard(parent.Token, TimeSpan.FromMilliseconds(100));
        using var stream = new BlockingStream();
        guard.AttachTransport(stream);
        Assert.True(guard.Complete());
        await Task.Delay(250);
        Assert.False(guard.Token.IsCancellationRequested);
        Assert.False(stream.Closed);
        parent.Cancel();
        Assert.True(stream.Closed);
    }

    [Fact]
    public void CancelledGenerationCannotAdoptNewTransport()
    {
        using var parent = new CancellationTokenSource();
        using var guard = new ConsoleStartupGuard(parent.Token, TimeSpan.FromSeconds(5));
        parent.Cancel();
        using var stream = new BlockingStream();
        Assert.ThrowsAny<OperationCanceledException>(() => guard.AttachTransport(stream));
        Assert.True(stream.Closed);
    }

    [Fact]
    public async Task HostedConsoleLeavesConnectingAndReportsHandshakeDeadline()
    {
        using var connection = new XenConnection { Hostname = "synthetic-host" };
        using var stream = new BlockingStream();
        using var session = new HostedConsoleSession((_, _) => stream, TimeSpan.FromMilliseconds(150));
        session.Start(new LiveRfbTarget(connection, new XenAPI.Console { location = "http://synthetic.invalid/console" },
            "Synthetic guest", "Synthetic guest", "synthetic-uuid", false, 1));
        await stream.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.IsConnecting && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.False(session.IsConnecting);
        Assert.False(session.IsConnected);
        Assert.Contains("timed out", session.StatusMessage);
        Assert.Contains("handshake", session.StatusMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeRfbStartsClearsConnectingWithoutUpdatingStoppedGeneration(bool stop)
    {
        using var connection = new XenConnection { Hostname = "synthetic-host" };
        using var stream = new PreConnectStream();
        using var session = new HostedConsoleSession((_, _) => stream, TimeSpan.FromMilliseconds(150));
        var target = new LiveRfbTarget(connection, new XenAPI.Console { location = "http://synthetic.invalid/console" },
            "Synthetic guest", "Synthetic guest", "synthetic-uuid", false, 1);
        session.Start(target);
        try
        {
            // BufferedStream checks CanRead inside the RFB constructor, after the
            // worker's first token check and before it can start the RFB thread.
            await stream.BeforeConnect.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var worker = (Task)typeof(HostedConsoleSession).GetField("_connectWorker",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(session)!;
            if (stop) session.Stop();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!stream.Closed && DateTime.UtcNow < until) await Task.Delay(10);
            Assert.True(stream.Closed);
            var stoppedStatus = session.StatusMessage;
            stream.ContinueConnect.Set();
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(session.IsConnected);
            Assert.False(session.IsConnecting);
            Assert.False(stream.Reading.Task.IsCompleted);
            if (stop) Assert.Equal(stoppedStatus, session.StatusMessage);
            else
            {
                Assert.Contains("timed out", session.StatusMessage);
                // The same target can start a fresh attempt after this failure.
                session.Start(target);
                Assert.True(session.IsConnecting);
                session.Stop();
            }
        }
        finally { stream.ContinueConnect.Set(); }
    }

    private sealed class PreConnectStream : BlockingStream
    {
        private int entered;
        public readonly TaskCompletionSource BeforeConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim ContinueConnect = new();
        public override bool CanRead
        {
            get
            {
                if (Interlocked.Exchange(ref entered, 1) == 0)
                {
                    BeforeConnect.TrySetResult();
                    if (!ContinueConnect.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test did not release RFB construction");
                }
                return true;
            }
        }
    }

    private class BlockingStream : Stream
    {
        private readonly ManualResetEventSlim closed = new();
        public readonly TaskCompletionSource Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed => closed.IsSet;
        public override int Read(byte[] buffer, int offset, int count) { Reading.TrySetResult(); closed.Wait(); return 0; }
        protected override void Dispose(bool disposing) { closed.Set(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
