#nullable disable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using XenAPI;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.Tests
{
    public sealed class JsonRpcTransportTests
    {
        [Theory]
        [InlineData(JsonRpcVersion.v1)]
        [InlineData(JsonRpcVersion.v2)]
        public async Task GeneratedCallsPreserveWireShapeAndReuseConnection(JsonRpcVersion version)
        {
            using var server = new RpcServer();
            using var rpc = Client(server);
            rpc.JsonRpcVersion = version;
            Assert.Equal("ok", await rpc.CallAsync<string>("synthetic.read", new JArray("synthetic-token", "value")));
            Assert.Equal("ok", await rpc.CallAsync<string>("synthetic.read", new JArray("synthetic-token", "value")));
            Assert.Equal(2, server.Requests.Count);
            Assert.Equal(1, server.Connections);
            var requests = server.Requests.ToArray();
            Assert.Equal("synthetic.read", requests[0]["method"]!.Value<string>());
            Assert.Equal("synthetic-token", requests[0]["params"]![0]!.Value<string>());
            Assert.NotEqual(requests[0]["id"]!.Value<int>(), requests[1]["id"]!.Value<int>());
            Assert.Equal(version == JsonRpcVersion.v2, requests[0]["jsonrpc"] != null);
        }

        [Theory]
        [InlineData(JsonRpcVersion.v1)]
        [InlineData(JsonRpcVersion.v2)]
        public async Task ServerFailurePreservesFailureDescription(JsonRpcVersion version)
        {
            using var server = new RpcServer { Failure = true };
            using var rpc = Client(server);
            rpc.JsonRpcVersion = version;
            var error = await Assert.ThrowsAsync<Failure>(() => rpc.CallAsync<string>("synthetic.mutation", new JArray()));
            Assert.Equal(new[] { "PERMISSION_DENIED", "synthetic.mutation" }, error.ErrorDescription);
            Assert.Single(server.Requests);
        }

        [Theory]
        [InlineData(407)]
        [InlineData(500)]
        public async Task HttpRejectionPreservesStatusAndDoesNotReplayMutation(int status)
        {
            using var server = new RpcServer { Status = status };
            using var rpc = Client(server);
            var error = await Assert.ThrowsAsync<WebException>(() => rpc.CallAsync<string>("synthetic.mutation", new JArray()));
            Assert.Equal(WebExceptionStatus.ProtocolError, error.Status);
            Assert.Equal((HttpStatusCode)status, JsonRpcClient.GetHttpStatus(error));
            Assert.Single(server.Requests);
        }

        [Fact]
        public async Task TimeoutIncludesResponseBodyAndLeavesAnotherCallUsable()
        {
            using var server = new RpcServer { StallBody = true };
            using var rpc = Client(server);
            rpc.Timeout = 150;
            var error = await Assert.ThrowsAsync<WebException>(() => rpc.CallAsync<string>("synthetic.mutation", new JArray()));
            Assert.Equal(WebExceptionStatus.Timeout, error.Status);
            Assert.Single(server.Requests);
            server.StallBody = false;
            rpc.Timeout = 5000;
            Assert.Equal("ok", await rpc.CallAsync<string>("synthetic.read", new JArray()));
            Assert.Equal(2, server.Requests.Count);
        }

        [Fact]
        public async Task ExplicitCancellationReleasesBlockedRequestWithoutReplay()
        {
            using var server = new RpcServer { StallHeaders = true };
            using var rpc = Client(server);
            using var cancellation = new CancellationTokenSource();
            var pending = rpc.CallAsync<string>("synthetic.mutation", new JArray(), cancellation.Token);
            Assert.Same(server.FirstRequest.Task,
                await Task.WhenAny(server.FirstRequest.Task, pending, Task.Delay(TimeSpan.FromSeconds(5))));
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Single(server.Requests);
        }

        [Fact]
        public async Task DuplicatesUseIndependentConnectionsAndDisposeOnlyOwnedTransport()
        {
            using var server = new RpcServer();
            var original = new Session(server.Url) { APIVersion = API_Version.API_2_8 };
            original.ConnectionGroupName = Guid.NewGuid().ToString();
            var duplicate = new Session(original);
            try
            {
                Assert.Equal("ok", await original.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
                Assert.Equal("ok", await duplicate.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
                Assert.Equal(2, server.Connections);
                duplicate.JsonRpcClient.Dispose();
                Assert.Equal("ok", await original.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
                await Assert.ThrowsAsync<ObjectDisposedException>(() => duplicate.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
                Assert.Equal(3, server.Requests.Count);
            }
            finally { original.JsonRpcClient.Dispose(); duplicate.JsonRpcClient.Dispose(); }
        }

        [Fact]
        public async Task ExplicitCookiesPersistButIndependentClientsDoNotShareThem()
        {
            using var server = new RpcServer { Cookie = true };
            using var one = Client(server);
            one.Cookies = new CookieContainer();
            await one.CallAsync<string>("synthetic.read", new JArray());
            await one.CallAsync<string>("synthetic.read", new JArray());
            using var two = Client(server);
            await two.CallAsync<string>("synthetic.read", new JArray());
            var headers = server.Headers.ToArray();
            Assert.DoesNotContain("Cookie:", headers[0]);
            Assert.Contains("synthetic-cookie=1", headers[1]);
            Assert.DoesNotContain("Cookie:", headers[2]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task HttpsCallsPassHostnameToCertificatePolicyBeforeSendingCredentials(bool accept)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=synthetic-rpc", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
#if NET8_0_OR_GREATER
            using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
#else
            using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx), (string)null, X509KeyStorageFlags.Exportable);
#endif
            using var server = new RpcServer(certificate);
            using var rpc = Client(server);
            string hostname = null;
            rpc.ServerCertificateValidationCallback = (sender, presented, _, errors) =>
            {
                hostname = sender is HttpWebRequest legacy ? legacy.Address.Host : sender as string;
                Assert.Equal(certificate.GetCertHashString(), presented.GetCertHashString());
                Assert.NotEqual(SslPolicyErrors.None, errors);
                return accept;
            };
            var failure = await Record.ExceptionAsync(() => rpc.CallAsync<string>("synthetic.read", new JArray("synthetic-credential")));
            Assert.Equal("127.0.0.1", hostname);
            if (accept)
            {
                Assert.Null(failure);
                Assert.Single(server.Requests);
            }
            else
            {
                Assert.IsType<WebException>(failure);
                Assert.Empty(server.Requests);
            }
        }

        [Fact]
        public async Task ExplicitProxyAuthenticatesWithProxyCredentials()
        {
            using var proxy = new RpcServer { RequireProxyAuth = true };
            using var rpc = new JsonRpcClient("http://synthetic-target.invalid/")
            {
                Timeout = 5000, KeepAlive = true, JsonRpcVersion = JsonRpcVersion.v2,
                WebProxy = new WebProxy(proxy.Url) { Credentials = new NetworkCredential("synthetic-user", "synthetic-proxy-password") }
            };
            Assert.Equal("ok", await rpc.CallAsync<string>("synthetic.read", new JArray()));
            var headers = proxy.Headers.ToArray();
            Assert.Equal(2, headers.Length);
            Assert.DoesNotContain("Proxy-Authorization:", headers[0]);
            Assert.Contains("Proxy-Authorization: Basic", headers[1]);
            Assert.Contains(Convert.ToBase64String(Encoding.ASCII.GetBytes("synthetic-user:synthetic-proxy-password")), headers[1]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task RedirectPolicyPreservesReviewedSetting(bool allow)
        {
            using var destination = new RpcServer();
            using var source = new RpcServer { Status = 307, RedirectTo = destination.Url + "jsonrpc" };
            using var rpc = Client(source);
            rpc.AllowAutoRedirect = allow;
            if (allow)
            {
                Assert.Equal("ok", await rpc.CallAsync<string>("synthetic.read", new JArray("synthetic-token")));
                Assert.Single(destination.Requests);
                Assert.Equal("synthetic-token", destination.Requests.ToArray()[0]["params"]![0]!.Value<string>());
            }
            else
            {
                var error = await Assert.ThrowsAsync<WebException>(() => rpc.CallAsync<string>("synthetic.read", new JArray()));
                Assert.Equal((HttpStatusCode)307, JsonRpcClient.GetHttpStatus(error));
                Assert.Empty(destination.Requests);
            }
            Assert.Single(source.Requests);
        }

        private static JsonRpcClient Client(RpcServer server) => new(server.Url)
        { Timeout = 5000, KeepAlive = true, JsonRpcVersion = JsonRpcVersion.v2 };

        internal sealed class RpcServer : IDisposable
        {
            private readonly TcpListener listener = new(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
            private readonly ConcurrentBag<TcpClient> peers = new();
            private int connections;
            private readonly X509Certificate2 certificate;
            public int Connections => Volatile.Read(ref connections);
            public string Url { get; }
            public readonly ConcurrentQueue<JObject> Requests = new();
            public readonly ConcurrentQueue<string> Headers = new();
            public readonly TaskCompletionSource<bool> FirstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Status = 200;
            public bool Failure;
            public bool StallHeaders;
            public bool StallBody;
            public bool Cookie;
            public bool RequireProxyAuth;
            public JToken Result = new JValue("ok");
            public string RedirectTo;

            public RpcServer(X509Certificate2 certificate = null)
            {
                this.certificate = certificate;
                listener.Start();
                Url = (certificate == null ? "http" : "https") + "://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
                _ = Accept();
            }

            private async Task Accept()
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var peer = await listener.AcceptTcpClientAsync();
                        peers.Add(peer);
                        Interlocked.Increment(ref connections);
                        _ = Serve(peer);
                    }
                }
                catch (Exception error) when (error is ObjectDisposedException || error is SocketException) { }
            }

            private async Task Serve(TcpClient peer)
            {
                try
                {
                    using (peer)
                    using (var stream = await OpenStream(peer))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            var first = await reader.ReadLineAsync();
                            if (first == null) return;
                            var length = 0;
                            var headers = new StringBuilder(first);
                            string header;
                            while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync()))
                            {
                                headers.AppendLine(header);
                                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header.Substring(15).Trim());
                            }
                            var chars = new char[length];
                            var read = 0;
                            while (read < length)
                            {
                                var count = await reader.ReadAsync(chars, read, length - read);
                                if (count == 0) return;
                                read += count;
                            }
                            var request = JObject.Parse(new string(chars));
                            Requests.Enqueue(request);
                            Headers.Enqueue(headers.ToString());
                            FirstRequest.TrySetResult(true);
                            if (StallHeaders) { await Task.Delay(Timeout.Infinite, stop.Token); return; }
                            var response = new JObject { ["id"] = request["id"], ["result"] = Result.DeepClone() };
                            if (request["jsonrpc"] != null) response["jsonrpc"] = "2.0";
                            if (Failure) response["error"] = request["jsonrpc"] == null
                                ? (JToken)new JArray("PERMISSION_DENIED", "synthetic.mutation")
                                : new JObject { ["code"] = 1, ["message"] = "PERMISSION_DENIED", ["data"] = new JArray("synthetic.mutation") };
                            else if (request["jsonrpc"] == null) response["error"] = null;
                            var body = response.ToString(Newtonsoft.Json.Formatting.None);
                            var status = RequireProxyAuth && !headers.ToString().Contains("Proxy-Authorization:") ? 407 : Status;
                            var reply = "HTTP/1.1 " + status + " Response\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body)
                                + "\r\n" + (Cookie ? "Set-Cookie: synthetic-cookie=1; Path=/\r\n" : "")
                                + (RequireProxyAuth && status == 407 ? "Proxy-Authenticate: Basic realm=\"synthetic\"\r\n" : "")
                                + (RedirectTo == null ? "" : "Location: " + RedirectTo + "\r\n") + "\r\n";
                            var bytes = Encoding.ASCII.GetBytes(reply);
                            await stream.WriteAsync(bytes, 0, bytes.Length);
                            if (StallBody) { await Task.Delay(Timeout.Infinite, stop.Token); return; }
                            bytes = Encoding.UTF8.GetBytes(body);
                            await stream.WriteAsync(bytes, 0, bytes.Length);
                        }
                    }
                }
                catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is OperationCanceledException || error is AuthenticationException) { }
            }

            private async Task<Stream> OpenStream(TcpClient peer)
            {
                if (certificate == null) return peer.GetStream();
                var tls = new SslStream(peer.GetStream(), false);
                try
                {
                    await tls.AuthenticateAsServerAsync(certificate, false, SslProtocols.Tls12, false);
                    return tls;
                }
                catch { tls.Dispose(); throw; }
            }

            public void Dispose()
            {
                stop.Cancel();
                listener.Stop();
                foreach (var peer in peers) peer.Dispose();
                stop.Dispose();
            }
        }
    }
}
