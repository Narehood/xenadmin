#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using XenAdmin;
using XenAdmin.Actions;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.Tests
{
    [CollectionDefinition("Action session ownership", DisableParallelization = true)]
    public sealed class ActionSessionOwnershipCollection { }

    [Collection("Action session ownership")]
    public sealed class ActionSessionOwnershipTests : IDisposable
    {
        private readonly IXenAdminConfigProvider previous = XenAdminConfigManager.Provider;
        public ActionSessionOwnershipTests() => XenAdminConfigManager.Provider = new RbacProvider();
        public void Dispose() => XenAdminConfigManager.Provider = previous;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RbacAllowedActionPreservesMainTransportOnSuccessAndFailure(bool fail)
        {
            using var server = new JsonRpcTransportTests.RpcServer();
            using var connection = HeartbeatLifecycleTests.Connection(server);
            Authorize(connection);
            var main = connection.Session;
            var action = new ProbeAction(connection, true, fail: fail);
            if (fail) Assert.Throws<InvalidOperationException>(() => action.RunSync(null));
            else action.RunSync(null);
            Assert.Same(main, action.Used);
            Assert.Null(action.Session);
            Assert.Equal("ok", await main.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
            Assert.DoesNotContain(server.Requests, request => request["method"].Value<string>() == "session.logout");
        }

        [Fact]
        public async Task RetryCreatedTransportIsDisposedWhileBorrowedMainRemainsUsable()
        {
            using var server = new JsonRpcTransportTests.RpcServer();
            using var connection = HeartbeatLifecycleTests.Connection(server);
            Authorize(connection);
            var main = connection.Session;
            var action = new ProbeAction(connection, true, retry: true);
            action.RunSync(null);
            Assert.NotSame(main, action.Used);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => action.Used.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
            Assert.Equal("ok", await main.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
            Assert.DoesNotContain(server.Requests, request => request["method"].Value<string>() == "session.logout");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RepeatedRetryDisposesEveryReplacementAndPreservesBorrowedClient(bool callerSupplied)
        {
            using var server = new JsonRpcTransportTests.RpcServer();
            using var connection = HeartbeatLifecycleTests.Connection(server);
            Authorize(connection);
            var supplied = callerSupplied ? new Session(connection.Session) : connection.Session;
            try
            {
                var action = new ProbeAction(connection, !callerSupplied, retryCount: 2);
                action.RunSync(callerSupplied ? supplied : null);
                Assert.Equal(3, action.Attempts.Count);
                Assert.Same(supplied, action.Attempts[0]);
                foreach (var replaced in action.Attempts.GetRange(1, 2))
                    await Assert.ThrowsAsync<ObjectDisposedException>(() => replaced.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
                foreach (var cancel in action.CancelClients)
                    await Assert.ThrowsAsync<ObjectDisposedException>(() => cancel.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
                Assert.Equal("ok", await supplied.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
                Assert.Equal("ok", await connection.Session.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
                Assert.DoesNotContain(server.Requests, request => request["method"].Value<string>() == "session.logout");
            }
            finally { if (callerSupplied) supplied.JsonRpcClient.Dispose(); }
        }

        [Fact]
        public async Task ElevatedRetryLogsOutAndDisposesTheReplacedIndependentLogin()
        {
            using var server = new JsonRpcTransportTests.RpcServer();
            using var connection = HeartbeatLifecycleTests.Connection(server);
            var action = new ProbeAction(connection, false, retryCount: 1, elevated: true);
            action.RunSync(null);
            Assert.Equal(2, action.Attempts.Count);
            foreach (var owned in action.Attempts)
                await Assert.ThrowsAsync<ObjectDisposedException>(() => owned.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
            foreach (var cancel in action.CancelClients)
                await Assert.ThrowsAsync<ObjectDisposedException>(() => cancel.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
            var logoutTokens = server.Requests.Where(request => request["method"].Value<string>() == "session.logout")
                .Select(request => request["params"][0].Value<string>()).ToArray();
            Assert.Equal(new[] { "synthetic-elevated-1", "synthetic-elevated-2" }, logoutTokens);
            Assert.Equal("ok", await connection.Session.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FolderActionReleasesAllCachedTransportsOnSuccessAndFailure(bool fail)
        {
            using var server = new JsonRpcTransportTests.RpcServer();
            using var first = HeartbeatLifecycleTests.Connection(server);
            using var second = HeartbeatLifecycleTests.Connection(server);
            var action = new FolderProbeAction(new[] { first, second }, fail);
            if (fail) Assert.Throws<InvalidOperationException>(() => action.RunSync(null));
            else action.RunSync(null);
            Assert.Equal(2, action.Used.Count);
            foreach (var duplicate in action.Used)
                await Assert.ThrowsAsync<ObjectDisposedException>(() => duplicate.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
            foreach (var connection in new[] { first, second })
                Assert.Equal("ok", await connection.Session.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
            Assert.DoesNotContain(server.Requests, request => request["method"].Value<string>() == "session.logout");
        }

        [Fact]
        public async Task SudoResultWithoutSessionDisposesCreatedDuplicateWithoutPoolLogout()
        {
            using var server = new JsonRpcTransportTests.RpcServer();
            using var connection = HeartbeatLifecycleTests.Connection(server);
            var action = new ProbeAction(connection, false);
            action.RunAsync(new AsyncAction.SudoElevationResult(null, null, null));
            var until = DateTime.UtcNow.AddSeconds(5);
            while ((!action.IsCompleted || action.Session != null) && DateTime.UtcNow < until) await Task.Delay(10);
            Assert.True(action.Succeeded, action.Exception?.Message);
            Assert.Null(action.Session);
            Assert.NotSame(connection.Session, action.Used);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => action.Used.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
            Assert.Equal("ok", await connection.Session.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
            Assert.DoesNotContain(server.Requests, request => request["method"].Value<string>() == "session.logout");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CallerSuppliedSessionIsPreservedAndRetryReplacementIsOwned(bool retry)
        {
            using var server = new JsonRpcTransportTests.RpcServer();
            using var connection = HeartbeatLifecycleTests.Connection(server);
            var supplied = new Session(connection.Session);
            try
            {
                var action = new ProbeAction(connection, false, retry: retry);
                action.RunSync(supplied);
                Assert.Equal("ok", await supplied.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
                if (retry)
                    await Assert.ThrowsAsync<ObjectDisposedException>(() => action.Used.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()));
                else Assert.Same(supplied, action.Used);
            }
            finally { supplied.JsonRpcClient.Dispose(); }
        }

        private static void Authorize(XenConnection connection)
        {
            connection.Cache.UpdateFrom(connection, new[]
            {
                new ObjectChange(typeof(Role), "permission", new Role { name_label = "synthetic.read" }),
                new ObjectChange(typeof(Role), "operator", new Role
                {
                    name_label = Role.MR_ROLE_VM_OPERATOR,
                    subroles = new List<XenRef<Role>> { new XenRef<Role>("permission") }
                })
            });
            connection.Session.Roles.Add(connection.Resolve(new XenRef<Role>("operator")));
        }

        private sealed class ProbeAction : AsyncAction
        {
            private readonly bool fail;
            private readonly int retryCount;
            private readonly bool elevated;
            private int created;
            public readonly List<Session> Attempts = new List<Session>();
            public readonly List<Session> CancelClients = new List<Session>();
            public Session Used;
            public ProbeAction(IXenConnection connection, bool roleCheck, bool fail = false, bool retry = false,
                int retryCount = 0, bool elevated = false)
                : base(connection, "Synthetic ownership regression", true)
            {
                this.fail = fail;
                this.retryCount = retry ? 1 : retryCount;
                this.elevated = elevated;
                if (roleCheck) ApiMethodsToRoleCheck.Add("synthetic.read");
            }

            protected override void Run()
            {
                Used = Session;
                if (retryCount > 0)
                {
                    var current = Session;
                    var attempts = 0;
                    try
                    {
                        DoWithSessionRetry(ref current, (Func<Session, string>)(session =>
                        {
                            Attempts.Add(session);
                            Assert.Same(session, Session);
                            if (CancelClients.Count > 0)
                                Assert.Throws<ObjectDisposedException>(() => Read(CancelClients[CancelClients.Count - 1]));
                            var cancel = GetCancelSession();
                            Assert.Equal(session.opaque_ref, cancel.opaque_ref);
                            Assert.Equal("ok", Read(cancel));
                            CancelClients.Add(cancel);
                            Assert.Equal("ok", Read(session));
                            // Replacements must release the previous owned attempt
                            // before the next callback, while borrowed clients survive.
                            if (Attempts.Count > (elevated ? 1 : 2))
                                Assert.Throws<ObjectDisposedException>(() => Read(Attempts[Attempts.Count - 2]));
                            if (attempts++ < retryCount) throw new WebException("Synthetic transient failure");
                            return Read(session);
                        }));
                    }
                    finally { Session = Used = current; }
                }
                else Assert.Equal("ok", Read(Session));
                if (fail) throw new InvalidOperationException("Synthetic action failure");
            }

            protected override Session NewSession()
            {
                if (!elevated) return base.NewSession();
                return new Session(Connection.Session)
                {
                    opaque_ref = "synthetic-elevated-" + ++created,
                    IsElevatedSession = true
                };
            }

            private static string Read(Session session) =>
                session.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()).GetAwaiter().GetResult();
        }

        private sealed class FolderProbeAction : FolderAction
        {
            private readonly XenConnection[] connections;
            private readonly bool fail;
            public readonly List<Session> Used = new List<Session>();
            public FolderProbeAction(XenConnection[] connections, bool fail) : base(null, "Synthetic folder ownership")
            { this.connections = connections; this.fail = fail; }

            protected override void Run()
            {
                var getSession = typeof(FolderAction).GetMethod("GetSession",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                foreach (var connection in connections)
                {
                    var session = (Session)getSession.Invoke(this, new object[] { connection });
                    Assert.Same(session, getSession.Invoke(this, new object[] { connection }));
                    Used.Add(session);
                    Assert.Equal("ok", session.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()).GetAwaiter().GetResult());
                }
                if (fail) throw new InvalidOperationException("Synthetic folder failure");
            }
        }

        private sealed class RbacProvider : IXenAdminConfigProvider
        {
            public bool DontSudo => false;
            public int ConnectionTimeout => 5000;
            public bool Exiting => false;
            public bool ForcedExiting => false;
            public bool ShowHiddenVMs => false;
            public string XenCenterUUID => "synthetic-client";
            public string FileServiceUsername => "";
            public string FileServiceClientId => "";
            public Func<List<Role>, IXenConnection, string, AsyncAction.SudoElevationResult> ElevatedSessionDelegate =>
                (_, __, ___) => throw new InvalidOperationException("Authorized action must not prompt for elevation");
            public Session CreateActionSession(Session session, IXenConnection connection) => new Session(session, connection);
            public int GetProxyTimeout(bool timeout) => 5000;
            public IWebProxy GetProxyFromSettings(IXenConnection connection) => null;
            public IWebProxy GetProxyFromSettings(IXenConnection connection, bool isForXenServer) => null;
            public void ShowObject(string opaqueRef) { }
            public void HideObject(string opaqueRef) { }
            public bool ObjectIsHidden(string opaqueRef) => false;
            public string GetLogFile() => "";
            public void UpdateServerHistory(string hostnameWithPort) { }
            public void SaveSettingsIfRequired() { }
            public string GetXenCenterMetadata() => "{}";
            public string GetCustomClientUpdatesXmlLocation() => "";
            public string GetCustomCfuLocation() => "";
            public string GetClientUpdatesQueryParam() => "";
            public string GetCustomFileServicePrefix() => "";
            public string GetCustomTokenUrl() => "";
        }
    }
}
