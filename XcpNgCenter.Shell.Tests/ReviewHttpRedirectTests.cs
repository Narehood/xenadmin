using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using XenAPI;
using XenAdmin;
using XenAdmin.Core;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.Tests;

[Collection("Action session ownership")]
public sealed class ReviewHttpRedirectTests
{
    /// <summary>Accepts canonical IPv6/IDN pool members only for the owning token and rejects plaintext or unrelated destinations.</summary>
    /// <param name="hostname">The connection hostname spelling stored in inventory.</param>
    /// <param name="member">The inventoried member address spelling.</param>
    /// <param name="origin">The initial request URI.</param>
    /// <param name="target">The candidate redirected URI.</param>
    [Theory]
    [InlineData("2001:db8::1", "2001:db8::2", "https://[2001:db8::1]/console", "https://[2001:db8::2]/console")]
    [InlineData("pool.example", "m\u00fcnchen.example", "https://pool.example/console", "https://xn--mnchen-3ya.example/console")]
    [InlineData("m\u00fcnchen.example", "member.example", "https://xn--mnchen-3ya.example/console", "https://member.example/console")]
    public void PoolRedirectCanonicalizesIpv6AndIdnForTheMatchingToken(string hostname, string member, string origin, string target)
    {
        using var server = new JsonRpcTransportTests.RpcServer();
        using var connection = HeartbeatLifecycleTests.Connection(server);
        connection.Hostname = hostname;
        connection.Cache.UpdateFrom(connection, [new ObjectChange(typeof(Host), "member", new Host { address = member })]);
        lock (ConnectionsManager.ConnectionsLock) ConnectionsManager.XenConnections.Add(connection);
        try
        {
            foreach (string token in new[] { "synthetic-token", "wrong-token" })
            {
                var policy = (Func<Uri, bool>)typeof(HTTP).GetMethod("CreateRedirectPolicy", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [new Uri(origin), new[] { "CONNECT /console HTTP/1.0", "Cookie: session_id=" + token }])!;
                Assert.Equal(token == "synthetic-token", policy(new Uri(target)));
                Assert.False(policy(new Uri(target.Replace("https:", "http:", StringComparison.Ordinal))));
                Assert.False(policy(new Uri("https://unrelated.example/console")));
            }
        }
        finally { lock (ConnectionsManager.ConnectionsLock) ConnectionsManager.XenConnections.Remove(connection); }
    }

    /// <summary>Requires the owning session token and captured pool inventory before permitting a cross-authority HTTPS redirect.</summary>
    [Fact]
    public void PoolRedirectRequiresTheOwningTokenAndAuthenticatedInventory()
    {
        using var server = new JsonRpcTransportTests.RpcServer();
        using var connection = HeartbeatLifecycleTests.Connection(server);
        connection.Cache.UpdateFrom(connection, [new ObjectChange(typeof(Host), "member", new Host { address = "pool-member.example" })]);
        lock (ConnectionsManager.ConnectionsLock) ConnectionsManager.XenConnections.Add(connection);
        try
        {
            // Capture a fresh policy for each synthetic owning/non-owning token.
            Func<Uri, bool> Policy(string token) => (Func<Uri, bool>)typeof(HTTP).GetMethod("CreateRedirectPolicy", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [new Uri("https://127.0.0.1/console"), new[] { "CONNECT /console HTTP/1.0", "Cookie: session_id=" + token }])!;
            var allowed = Policy("synthetic-token");
            Assert.True(allowed(new Uri("https://pool-member.example/console")));
            Assert.False(Policy("different-token")(new Uri("https://pool-member.example/console")));
            Assert.False(allowed(new Uri("http://pool-member.example/console")));
            Assert.False(allowed(new Uri("https://pool-member.example:8443/console")));
            connection.Cache.UpdateFrom(connection, [new ObjectChange(typeof(Host), "later", new Host { address = "new-member.example" })]);
            Assert.False(allowed(new Uri("https://new-member.example/console")));
        }
        finally { lock (ConnectionsManager.ConnectionsLock) ConnectionsManager.XenConnections.Remove(connection); }
    }

    /// <summary>Rejects new credential recipients, authority changes and TLS downgrades before opening a redirected transport.</summary>
    /// <param name="target">The candidate redirected URI.</param>
    /// <param name="accepted">Whether the fixture expects the policy to accept the target.</param>
    [Theory]
    [InlineData("https://pool.example/next", true)]
    [InlineData("http://pool.example/next", false)]
    [InlineData("http://pool.example:443/next", false)]
    [InlineData("https://other.example/next", false)]
    [InlineData("https://pool.example:8443/next", false)]
    [InlineData("https://user@pool.example/next", false)]
    public void RedirectPolicyRejectsNewRecipientsAndTlsDowngrades(string target, bool accepted)
    {
        var policy = (Func<Uri, bool>)typeof(HTTP).GetMethod("CreateRedirectPolicy", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new Uri("https://pool.example/console"), new[] { "CONNECT /console HTTP/1.0", "Cookie: session_id=synthetic" }])!;
        Assert.Equal(accepted, policy(new Uri(target)));
    }

    /// <summary>Exercises CONNECT, GET and PUT over loopback, checking redirected path/Host headers and absence of connections to rejected recipients.</summary>
    /// <param name="method">The HTTP method exercised by the fixture.</param>
    /// <param name="external">Whether the response names a different, rejected authority.</param>
    [Theory]
    [InlineData("CONNECT", false)]
    [InlineData("GET", false)]
    [InlineData("PUT", false)]
    [InlineData("CONNECT", true)]
    [InlineData("GET", true)]
    [InlineData("PUT", true)]
    public async Task RealRequestsFollowRelativeRedirectsAndRejectOtherAuthorities(string method, bool external)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var recipient = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); recipient.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int otherPort = ((IPEndPoint)recipient.LocalEndpoint).Port;
        string location = external ? $"http://127.0.0.1:{otherPort}/next" : "/next?value=changed";
        var serving = Task.Run(async () =>
        {
            using (var first = await listener.AcceptTcpClientAsync(deadline.Token))
            {
                await Headers(first.GetStream(), deadline.Token);
                await first.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.0 302 Found\r\nLocation: {location}\r\n\r\n"), deadline.Token);
            }
            if (external) return "";
            using var next = await listener.AcceptTcpClientAsync(deadline.Token);
            var request = await Headers(next.GetStream(), deadline.Token);
            await next.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.0 200 OK\r\n\r\n"), deadline.Token);
            return request;
        }, deadline.Token);
        var uri = new Uri($"http://127.0.0.1:{port}/original?session_id=synthetic-token");
        // Exercise the production stream boundary, including recipient checks before reconnecting.
        void Open()
        {
            using var stream = method switch
            {
                "CONNECT" => HTTP.HttpConnectStream(uri, null, "synthetic-token", 3000, deadline.Token),
                "GET" => HTTP.HttpGetStream(uri, null, 3000, deadline.Token),
                _ => HTTP.HttpPutStream(uri, null, 0, 3000)
            };
        }
        if (external) await Assert.ThrowsAsync<HTTP.BadServerResponseException>(() => Task.Run(Open, deadline.Token));
        else await Task.Run(Open, deadline.Token);
        string received = await serving;
        Assert.False(recipient.Pending());
        if (!external)
        {
            Assert.StartsWith(method + " /next?value=changed HTTP/1.0", received);
            Assert.Contains($"Host: 127.0.0.1:{port}", received);
            if (method == "CONNECT") Assert.Contains("Cookie: session_id=synthetic-token", received);
        }
    }

    /// <summary>Reads one bounded fixture header block with cancellation and rejects truncation or oversized headers.</summary>
    /// <param name="input">The fixture response stream.</param>
    /// <param name="token">Cancellation for the fixture read.</param>
    /// <returns>One complete HTTP header block.</returns>
    private static async Task<string> Headers(Stream input, CancellationToken token)
    {
        var result = new StringBuilder();
        var one = new byte[1];
        while (result.Length < 16000)
        {
            if (await input.ReadAsync(one, token) != 1) throw new EndOfStreamException();
            result.Append((char)one[0]);
            if (result.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) return result.ToString();
        }
        throw new InvalidDataException();
    }
}
