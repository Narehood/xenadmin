#nullable disable
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;
using XenAdmin;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.Tests
{
    public sealed class HeartbeatLifecycleTests
    {
        [Fact]
        public async Task ConsoleElevationCanCancelDuringSilentTlsNegotiation()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var connection = new XenConnection
                {
                    Hostname = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port
                };
                using var cancellation = new CancellationTokenSource();
                var accept = listener.AcceptTcpClientAsync();
                var login = Task.Run(() => connection.ElevatedSession("synthetic-user", "synthetic-password", cancellation.Token));
                await Within(accept);
                using var peer = await accept;
                var buffer = new byte[1024];
                var hello = peer.GetStream().ReadAsync(buffer, 0, buffer.Length);
                await Within(hello);
                Assert.True(await hello > 0);
                cancellation.Cancel();
                Assert.Same(login, await Task.WhenAny(login, Task.Delay(5000)));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await login);
            }
            finally { listener.Stop(); }
        }
        [Theory]
        [InlineData(JsonRpcVersion.v1)]
        [InlineData(JsonRpcVersion.v2)]
        public async Task AsyncHeartbeatPreservesXapiDateConverterAndWireShape(JsonRpcVersion version)
        {
            using var server = new JsonRpcTransportTests.RpcServer { Result = new JValue("20261002T13:14:15Z") };
            using var client = new JsonRpcClient(server.Url) { JsonRpcVersion = version, Timeout = 5000 };
            var result = await client.HostGetServerTimeAsync("synthetic-token", "synthetic-host", CancellationToken.None);
            Assert.Equal(new DateTime(2026, 10, 2, 13, 14, 15, DateTimeKind.Utc), result);
            var request = Assert.Single(server.Requests);
            Assert.Equal("host.get_servertime", request["method"].Value<string>());
            Assert.Equal(new[] { "synthetic-token", "synthetic-host" }, request["params"].ToObject<string[]>());
        }

        [Fact]
        public async Task StopCancelsInFlightHeartbeatWithoutDisconnectingOrLoggingOutPool()
        {
            using var server = new JsonRpcTransportTests.RpcServer { StallHeaders = true };
            using var connection = Connection(server);
            var heartbeat = new Heartbeat(connection, 30000);
            heartbeat.Start();
            try
            {
                await Within(server.FirstRequest.Task);
                heartbeat.Start(); // Never start a second poller.
                await Within(heartbeat.StopAsync());
                Assert.True(connection.IsConnected);
                Assert.Single(server.Requests);
                Assert.Equal("host.get_servertime", server.Requests.ToArray()[0]["method"].Value<string>());
                server.StallHeaders = false;
                Assert.Equal("ok", await connection.Session.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
                Assert.DoesNotContain(server.Requests, request => request["method"].Value<string>() == "session.logout");
                heartbeat.Stop();
                heartbeat.Start();
                Assert.True(heartbeat.Completion.IsCompleted);
            }
            finally { await Within(heartbeat.StopAsync()); }
        }

        [Fact]
        public async Task StopBeforeStartAndStopDuringIdleWaitCompletePromptly()
        {
            using var connection = new XenConnection();
            var stopped = new Heartbeat(connection, 20000);
            stopped.Stop();
            stopped.Start();
            await Within(stopped.Completion);
            var idle = new Heartbeat(connection, 20000);
            idle.Start();
            await Task.Delay(100);
            await Within(idle.StopAsync());
        }

        internal static XenConnection Connection(JsonRpcTransportTests.RpcServer server)
        {
            var connection = new XenConnection { Hostname = "127.0.0.1" };
            var session = new Session(server.Url) { opaque_ref = "synthetic-token", APIVersion = API_Version.LATEST };
            var field = typeof(XenConnection).GetField("connectTask", BindingFlags.Instance | BindingFlags.NonPublic);
            var state = Activator.CreateInstance(field.FieldType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { "127.0.0.1", 1 }, null);
            field.FieldType.GetField("Connected").SetValue(state, true);
            field.FieldType.GetField("Session").SetValue(state, session);
            field.SetValue(connection, state);
            connection.Cache.UpdateFrom(connection, new[]
            {
                new ObjectChange(typeof(Host), "synthetic-host", new Host { uuid = "synthetic-host-uuid" }),
                new ObjectChange(typeof(Pool), "synthetic-pool", new Pool { master = new XenRef<Host>("synthetic-host") })
            });
            return connection;
        }

        private static async Task Within(Task work)
        {
            Assert.Same(work, await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(5))));
            await work;
        }
    }
}
