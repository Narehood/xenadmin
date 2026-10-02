using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using XenAPI;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace XenCenterLib.Tests
{
    public sealed class ConsoleCancellationTests
    {
        [Fact]
        public async Task DeadlineClosesHandshakeButCompletedConnectionRemainsIdleSafe()
        {
            using var parent = new CancellationTokenSource();
            using var stream = new ClosingStream();
            using var guard = new ConsoleStartupGuard(parent.Token, TimeSpan.FromMilliseconds(100));
            guard.AttachTransport(stream);
            Assert.True(guard.Complete());
            await Task.Delay(250);
            Assert.False(stream.Closed);
            Assert.False(guard.Token.IsCancellationRequested);
            parent.Cancel();
            Assert.True(stream.Closed);
        }

        [Fact]
        public async Task ExpiredHandshakeClosesTransportAndCannotReportSuccess()
        {
            using var stream = new ClosingStream();
            using var guard = new ConsoleStartupGuard(CancellationToken.None, TimeSpan.FromMilliseconds(100));
            guard.AttachTransport(stream);
            Assert.True(await Task.Run(() => stream.ClosedSignal.Wait(TimeSpan.FromSeconds(5))));
            Assert.False(guard.Complete());
            Assert.Contains("handshake", guard.FailureMessage);
        }

        [Fact]
        public async Task CancellingHttpGetAbortsSilentResponseHeaders()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var cancellation = new CancellationTokenSource();
                var url = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/rrd");
                var accept = listener.AcceptTcpClientAsync();
                var get = Task.Run(() => HTTP.HttpGetStream(url, null, 0, cancellation.Token));
                Assert.Same(accept, await Task.WhenAny(accept, Task.Delay(5000)));
                using var peer = await accept;
                var buffer = new byte[1024];
                var read = peer.GetStream().ReadAsync(buffer, 0, buffer.Length);
                Assert.Same(read, await Task.WhenAny(read, Task.Delay(5000)));
                Assert.True(await read > 0);
                cancellation.Cancel();
                Assert.Same(get, await Task.WhenAny(get, Task.Delay(5000)));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await get);
            }
            finally { listener.Stop(); }
        }

        private sealed class ClosingStream : MemoryStream
        {
            public readonly ManualResetEventSlim ClosedSignal = new ManualResetEventSlim();
            public bool Closed => ClosedSignal.IsSet;
            protected override void Dispose(bool disposing) { ClosedSignal.Set(); base.Dispose(disposing); }
        }
    }
}
