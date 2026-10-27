#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XenAPI;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.Tests;

[CollectionDefinition("HTTP async transport", DisableParallelization = true)]
public sealed class HttpAsyncTransportCollection { }

[Collection("HTTP async transport")]
public sealed class HttpAsyncTransportTests
{
    [Fact]
    public async Task GetUsesAsyncIoAndLeavesBodyUnreadAndOwnedByCaller()
    {
        using var input = new AsyncOnlyStream("HTTP/1.0 200 OK\r\nContent-Length: 4\r\n\r\nbody");
        using var cancellation = new CancellationTokenSource();
        var uri = new Uri("http://example.invalid/rrd?session_id=synthetic");
        using (var stream = await HTTP.HttpGetStreamAsync(uri, new MockProxy(input), 500, cancellation.Token))
        {
            Assert.Same(input, stream);
            Assert.Equal("GET /rrd?session_id=synthetic HTTP/1.0\r\nHost: example.invalid\r\n\r\n", input.Request);
            // The setup deadline and token stop owning the stream after handoff.
            cancellation.Cancel();
            await Task.Delay(600, TestContext.Current.CancellationToken);
            Assert.False(input.Closed);
            var bytes = new byte[4];
            Assert.Equal(4, await stream.ReadAsync(bytes, 0, bytes.Length, TestContext.Current.CancellationToken));
            Assert.Equal("body", Encoding.ASCII.GetString(bytes));
        }
        Assert.True(input.Closed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CancellationAbortsPendingHeadersProxyAndTls(bool proxy, bool tls)
    {
        using var listener = new TestListener();
        listener.Start();
        var address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/rrd");
        using var cancellation = new CancellationTokenSource();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var destination = proxy ? new Uri("http://pool.invalid/rrd")
            : tls ? new UriBuilder(address) { Scheme = "https" }.Uri : address;
        var opening = HTTP.HttpGetStreamAsync(destination, proxy ? new WebProxy(address) : null, 0, cancellation.Token);
        using var peer = await Accept(listener, watchdog.Token);
        var received = new byte[4096];
        Assert.True(await peer.GetStream().ReadAsync(received, 0, received.Length, watchdog.Token) > 0);
        Assert.False(opening.IsCompleted);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(opening));
    }

    [Fact]
    public async Task SetupDeadlineClosesAStalledResponseWithoutCallerCancellation()
    {
        using var listener = new TestListener();
        listener.Start();
        var uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/rrd");
        var opening = HTTP.HttpGetStreamAsync(uri, null, 300, CancellationToken.None);
        using var peer = await Accept(listener, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => Within(opening));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedirectsRebuildRequestAndRejectOtherRecipients(bool external)
    {
        using var listener = new TestListener();
        using var recipient = new TestListener();
        listener.Start(); recipient.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/original?session_id=synthetic");
        var opening = HTTP.HttpGetStreamAsync(uri, null, 5000, deadline.Token);
        using (var first = await Accept(listener, deadline.Token))
        {
            await Headers(first.GetStream(), deadline.Token);
            var target = external ? $"http://127.0.0.1:{((IPEndPoint)recipient.LocalEndpoint).Port}/next" : "/next?changed=true";
            await Reply(first.GetStream(), $"HTTP/1.0 302 Found\r\nLocation: {target}\r\n\r\n", deadline.Token);
        }
        if (external)
        {
            await Assert.ThrowsAsync<HTTP.BadServerResponseException>(() => Within(opening));
            Assert.False(recipient.Pending());
            return;
        }
        using var next = await Accept(listener, deadline.Token);
        var request = await Headers(next.GetStream(), deadline.Token);
        Assert.StartsWith("GET /next?changed=true HTTP/1.0\r\n", request);
        Assert.Contains("Host: " + uri.Authority + "\r\n", request);
        await Reply(next.GetStream(), "HTTP/1.0 200 OK\r\n\r\n", deadline.Token);
        using var body = await Within(opening);
    }

    [Theory]
    [InlineData(HTTP.ProxyAuthenticationMethod.Basic, false, false)]
    [InlineData(HTTP.ProxyAuthenticationMethod.Basic, true, false)]
    [InlineData(HTTP.ProxyAuthenticationMethod.Digest, false, false)]
    [InlineData(HTTP.ProxyAuthenticationMethod.Digest, true, false)]
    [InlineData(HTTP.ProxyAuthenticationMethod.Digest, false, true)]
    [InlineData(HTTP.ProxyAuthenticationMethod.Digest, true, true)]
    public async Task ProxyAuthenticationStaysOnConnectAndSupportsReconnect(HTTP.ProxyAuthenticationMethod method, bool reconnect, bool quotedAlgorithm)
    {
        using var listener = new TestListener();
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        var proxy = new WebProxy(address) { Credentials = new NetworkCredential("synthetic-user", "synthetic-password") };
        var previous = HTTP.CurrentProxyAuthenticationMethod;
        HTTP.CurrentProxyAuthenticationMethod = method;
        try
        {
            var opening = HTTP.HttpGetStreamAsync(new Uri("http://pool.invalid/rrd"), proxy, 5000, deadline.Token);
            using var first = await Accept(listener, deadline.Token);
            var initial = await Headers(first.GetStream(), deadline.Token);
            Assert.StartsWith("CONNECT pool.invalid:80 HTTP/1.0\r\n", initial);
            Assert.DoesNotContain("synthetic-password", initial);
            var algorithm = quotedAlgorithm ? "\"SHA-256\"" : "SHA-256";
            var challenge = method == HTTP.ProxyAuthenticationMethod.Basic ? "Basic realm=\"pool\""
                : $"Digest realm=\"pool\", nonce=\"synthetic-nonce\", algorithm={algorithm}, qop=\"auth\"";
            await Reply(first.GetStream(), $"HTTP/1.0 407 Authentication Required\r\nProxy-Authenticate: {challenge}\r\n"
                + (reconnect ? "Proxy-Connection: Close\r\n" : "") + "\r\n", deadline.Token);
            using var replacement = reconnect ? await Accept(listener, deadline.Token) : null;
            var stream = (replacement ?? first).GetStream();
            var authenticated = await Headers(stream, deadline.Token);
            Assert.Contains("Proxy-Authorization: " + method, authenticated);
            if (method == HTTP.ProxyAuthenticationMethod.Digest)
            {
                Assert.Contains("algorithm=SHA-256", authenticated);
                Assert.Matches("response=\"[a-f0-9]{64}\"", authenticated);
            }
            await Reply(stream, "HTTP/1.0 200 Connected\r\n\r\n", deadline.Token);
            var request = await Headers(stream, deadline.Token);
            Assert.StartsWith("GET /rrd HTTP/1.0\r\n", request);
            Assert.DoesNotContain("Proxy-Authorization:", request);
            await Reply(stream, "HTTP/1.0 200 OK\r\n\r\n", deadline.Token);
            using var body = await Within(opening);
        }
        finally { HTTP.CurrentProxyAuthenticationMethod = previous; }
    }

    private static Task Reply(Stream stream, string response, CancellationToken token)
    {
        var bytes = Encoding.ASCII.GetBytes(response);
        return stream.WriteAsync(bytes, 0, bytes.Length, token);
    }

    private static async Task<TcpClient> Accept(TcpListener listener, CancellationToken token)
    {
        using var stop = token.Register(listener.Stop);
        return await listener.AcceptTcpClientAsync();
    }

    private sealed class TestListener() : TcpListener(IPAddress.Loopback, 0), IDisposable
    {
        void IDisposable.Dispose() => Stop();
    }

    private static async Task<string> Headers(Stream stream, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new byte[1];
        while (!text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            Assert.Equal(1, await stream.ReadAsync(buffer, 0, 1, token));
            text.Append((char)buffer[0]);
        }
        return text.ToString();
    }

    private static async Task<Stream> Within(Task<Stream> work)
    {
        Assert.Same(work, await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)));
        return await work;
    }

    private sealed class MockProxy(Stream stream) : IMockWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => destination;
        public bool IsBypassed(Uri host) => false;
        public Stream GetStream(Uri uri) => stream;
    }

    private sealed class AsyncOnlyStream(string response) : Stream
    {
        private readonly MemoryStream input = new(Encoding.ASCII.GetBytes(response));
        private readonly MemoryStream output = new();
        public bool Closed { get; private set; }
        public string Request => Encoding.ASCII.GetString(output.ToArray());
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(input.Read(buffer, offset, count));
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            output.Write(buffer, offset, count);
            return Task.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        protected override void Dispose(bool disposing) { Closed = true; if (disposing) { input.Dispose(); output.Dispose(); } base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous header reads are forbidden.");
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous header writes are forbidden.");
        public override void Flush() => throw new InvalidOperationException("Synchronous flush is forbidden.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
