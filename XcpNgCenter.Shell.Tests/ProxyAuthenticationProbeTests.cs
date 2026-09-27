using System.Net;
using System.Net.Sockets;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class ProxyAuthenticationProbeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingAcceptCancellationCompletesCleanly(bool stopListenerDuringCancellation)
    {
        // Exercise the actual listener and cancellation/close race that failed Windows CI.
        for (var iteration = 0; iteration < 64; iteration++)
        {
            using var cancellation = new CancellationTokenSource();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var schemes = new List<string>();
            var server = ProxyAuthenticationProbe.Serve(listener, "Basic", schemes, cancellation.Token);
            Assert.False(server.IsCompleted);

            var cancel = cancellation.CancelAsync();
            if (stopListenerDuringCancellation) listener.Stop();
            await cancel;
            await server.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(schemes);
        }
    }

    [Fact]
    public async Task ListenerAbortWithoutCancellationStillFailsTheProbe()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = ProxyAuthenticationProbe.Serve(listener, "Basic", [], CancellationToken.None);
        Assert.False(server.IsCompleted);

        listener.Stop();

        var error = await Record.ExceptionAsync(() => server.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(error is SocketException or ObjectDisposedException,
            $"An unexpected listener shutdown must fail; observed {error?.GetType().Name ?? "success"}.");
    }
}
