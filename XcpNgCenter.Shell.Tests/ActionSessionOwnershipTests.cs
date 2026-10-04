#nullable disable
using System;
using System.Collections.Generic;
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
            private readonly bool retry;
            public Session Used;
            public ProbeAction(IXenConnection connection, bool roleCheck, bool fail = false, bool retry = false)
                : base(connection, "Synthetic ownership regression", true)
            {
                this.fail = fail;
                this.retry = retry;
                if (roleCheck) ApiMethodsToRoleCheck.Add("synthetic.read");
            }

            protected override void Run()
            {
                Used = Session;
                if (retry)
                {
                    var current = Session;
                    var attempts = 0;
                    try
                    {
                        DoWithSessionRetry(ref current, (Func<Session, string>)(session =>
                        {
                            if (attempts++ == 0) throw new WebException("Synthetic transient failure");
                            return Read(session);
                        }));
                    }
                    finally { Session = Used = current; }
                }
                else Assert.Equal("ok", Read(Session));
                if (fail) throw new InvalidOperationException("Synthetic action failure");
            }

            private static string Read(Session session) =>
                session.JsonRpcClient.CallAsync<string>("synthetic.read", new JArray()).GetAwaiter().GetResult();
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
