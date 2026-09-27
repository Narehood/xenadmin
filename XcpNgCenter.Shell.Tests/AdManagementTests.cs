using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XenAdmin;
using XenAdmin.Actions;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.Tests;

[Collection("HA shared action runtime")]
public sealed class AdManagementTests : IDisposable
{
    private readonly IXenAdminConfigProvider? _provider = XenAdminConfigManager.Provider;
    public AdManagementTests() => XenAdminConfigManager.Provider = new ShellConfigProvider(new ShellAppSettings { ConnectionTimeoutSeconds = 3 });
    public void Dispose() => XenAdminConfigManager.Provider = _provider;

    [Theory]
    [InlineData(AdOperation.Join)]
    [InlineData(AdOperation.Leave)]
    [InlineData(AdOperation.AddSubject)]
    [InlineData(AdOperation.SetRoles)]
    [InlineData(AdOperation.RemoveSubject)]
    public async Task ReviewedSharedOperationsCompleteWithoutRetry(AdOperation operation)
    {
        var f = new Fixture(operation != AdOperation.Join);
        using var server = new RpcServer(f); f.Connect(server.Session);
        var snapshot = await AdManagement.LoadAsync(f.Connection, "pool", "pool-uuid");
        var request = f.Request(operation);
        var review = await AdManagement.ReviewAsync(f.Connection, snapshot, request);
        Assert.DoesNotContain(server.Requests, IsMutation);
        Assert.DoesNotContain("directory-password", JsonConvert.SerializeObject(review));
        using var credentials = new AdCredentials("directory-user", "directory-password");
        var action = new AdAction(f.Connection, snapshot, request, review, credentials);
        action.RunSync(server.Session);
        Assert.True(action.Succeeded, action.Exception?.Message);
        Assert.Empty(credentials.Password);
        Assert.Null(typeof(AdAction).GetField("_credentials", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(action));
        var mutations = server.Requests.Where(IsMutation).Select(Method).ToArray();
        switch (operation)
        {
            case AdOperation.Join: Assert.Equal(new[] { "pool.disable_external_auth", "pool.enable_external_auth" }, mutations); break;
            case AdOperation.Leave: Assert.Equal(new[] { "pool.disable_external_auth" }, mutations); break;
            case AdOperation.AddSubject: Assert.Equal(new[] { "subject.create", "subject.add_to_roles", "subject.remove_from_roles", "session.logout_subject_identifier" }, mutations); break;
            case AdOperation.SetRoles: Assert.Equal(new[] { "subject.add_to_roles", "subject.remove_from_roles", "session.logout_subject_identifier" }, mutations); break;
            case AdOperation.RemoveSubject: Assert.Equal(new[] { "session.logout_subject_identifier", "subject.destroy" }, mutations); break;
        }
    }

    [Theory]
    [InlineData("pool")]
    [InlineData("host")]
    [InlineData("domain")]
    [InlineData("subject")]
    [InlineData("role")]
    [InlineData("role-tree")]
    [InlineData("license")]
    [InlineData("members")]
    public async Task ReplacedOrChangedReviewedInventoryBlocksMutation(string change)
    {
        var f = new Fixture(); using var server = new RpcServer(f); f.Connect(server.Session);
        var snapshot = await AdManagement.LoadAsync(f.Connection, "pool", "pool-uuid"); var request = f.Request(AdOperation.SetRoles);
        var review = await AdManagement.ReviewAsync(f.Connection, snapshot, request);
        switch (change)
        {
            case "pool": f.Pool.uuid = "replacement"; break;
            case "host": f.Host.uuid = "replacement"; break;
            case "domain": f.Host.external_auth_service_name = "other.org"; break;
            case "subject": f.Subject.uuid = "replacement"; break;
            case "role": f.Admin.uuid = "replacement"; break;
            case "role-tree": f.Admin.subroles = [new("different-permission")]; break;
            case "license": f.Host.license_params["restrict_rbac"] = "true"; break;
            case "members": f.Add("new-host", new Host { uuid = "new-host-uuid" }); break;
        }
        var action = new AdAction(f.Connection, snapshot, request, review, new("", ""));
        Assert.Throws<InvalidOperationException>(() => action.RunSync(server.Session));
        Assert.DoesNotContain(server.Requests, IsMutation);
    }

    [Fact]
    public async Task ServerOnlyChangeIsNotHiddenByStaleCache()
    {
        var f = new Fixture(); using var server = new RpcServer(f); f.Connect(server.Session);
        var snapshot = await AdManagement.LoadAsync(f.Connection, "pool", "pool-uuid"); var request = f.Request(AdOperation.RemoveSubject);
        var review = await AdManagement.ReviewAsync(f.Connection, snapshot, request);
        server.ReplaceServerSubject = true;
        Assert.Throws<InvalidOperationException>(() => new AdAction(f.Connection, snapshot, request, review, new("", "")).RunSync(server.Session));
        Assert.DoesNotContain(server.Requests, IsMutation);
    }

    [Fact]
    public async Task ConcurrentCacheAndServerRoleChangeCannotReplaceTheReviewedBaseline()
    {
        var f = new Fixture();
        f.Add("operator", new Role { uuid = "operator-uuid", name_label = "vm-operator", subroles = [new("permission")] });
        using var server = new RpcServer(f); f.Connect(server.Session);
        var snapshot = await AdManagement.LoadAsync(f.Connection, "pool", "pool-uuid");
        var request = f.Request(AdOperation.SetRoles);
        var review = await AdManagement.ReviewAsync(f.Connection, snapshot, request);
        var inventoryReads = 0;
        var changed = false;
        server.BeforeReply = method =>
        {
            // Apply reviews the inventory twice before SetRoles performs its
            // final read. Simulate another admin's event during that final read.
            if (method != "pool.get_record" || ++inventoryReads != 3) return;
            var updated = f.Add("subject", new Subject
            {
                uuid = f.Subject.uuid, subject_identifier = f.Subject.subject_identifier,
                other_config = f.Subject.other_config, roles = [new("read"), new("operator")]
            });
            Assert.Same(f.Subject, updated); // Real cache events update objects in place.
            changed = true;
        };

        var action = new AdAction(f.Connection, snapshot, request, review, new("", ""));
        Assert.Throws<InvalidOperationException>(() => action.RunSync(server.Session));
        Assert.True(changed);
        Assert.Equal("read", snapshot.Subjects.Single().RoleReferences);
        Assert.Contains(f.Subject.roles, role => role.opaque_ref == "operator");
        Assert.DoesNotContain(server.Requests, IsMutation);
    }

    [Theory]
    [InlineData(AdOperation.Join)]
    [InlineData(AdOperation.Leave)]
    public async Task DomainChangesRequireAnIndependentLocalRootSession(AdOperation operation)
    {
        var f = new Fixture(operation != AdOperation.Join); using var server = new RpcServer(f) { LocalRoot = false }; f.Connect(server.Session);
        var snapshot = await AdManagement.LoadAsync(f.Connection, "pool", "pool-uuid");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AdManagement.ReviewAsync(f.Connection, snapshot, f.Request(operation)));
        Assert.Contains("local root", error.Message);
        Assert.DoesNotContain(server.Requests, IsMutation);
    }

    [Fact]
    public async Task AuthorizedDirectoryAdministratorCanManageAnotherSubject()
    {
        var f = new Fixture(); using var server = new RpcServer(f) { LocalRoot = false }; f.Connect(server.Session);
        var snapshot = await AdManagement.LoadAsync(f.Connection, "pool", "pool-uuid"); var request = f.Request(AdOperation.SetRoles);
        var review = await AdManagement.ReviewAsync(f.Connection, snapshot, request);
        new AdAction(f.Connection, snapshot, request, review, new("", "")).RunSync(server.Session);
        Assert.Contains(server.Requests, r => Method(r) == "subject.add_to_roles");
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("user")]
    [InlineData("group")]
    public async Task DirectorySessionCannotRemoveOrChangeItsOwnAuthority(string authority)
    {
        var f = new Fixture(); using var server = new RpcServer(f) { LocalRoot = false, SelfAuthority = authority }; f.Connect(server.Session);
        var snapshot = await AdManagement.LoadAsync(f.Connection, "pool", "pool-uuid");
        foreach (var operation in new[] { AdOperation.SetRoles, AdOperation.RemoveSubject })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AdManagement.ReviewAsync(f.Connection, snapshot, f.Request(operation)));
            Assert.Contains("current account", error.Message);
        }
        Assert.DoesNotContain(server.Requests, IsMutation);
    }

    [Theory]
    [InlineData("subject.add_to_roles")]
    [InlineData("subject.remove_from_roles")]
    [InlineData("session.logout_subject_identifier")]
    [InlineData("auth.get_group_membership")]
    public async Task RevokedNestedPermissionAfterReviewBlocksAllMutation(string permission)
    {
        var f = new Fixture(); using var server = new RpcServer(f) { LocalRoot = false }; f.Connect(server.Session);
        var snapshot = await AdManagement.LoadAsync(f.Connection, "pool", "pool-uuid"); var request = f.Request(AdOperation.SetRoles);
        var review = await AdManagement.ReviewAsync(f.Connection, snapshot, request);
        server.DeniedPermission = permission;
        Assert.Throws<InvalidOperationException>(() => new AdAction(f.Connection, snapshot, request, review, new("", "")).RunSync(server.Session));
        Assert.DoesNotContain(server.Requests, IsMutation);
    }

    [Theory]
    [InlineData("subject.add_to_roles")]
    [InlineData("subject.remove_from_roles")]
    [InlineData("session.logout_subject_identifier")]
    public async Task PartialRoleFailureStopsAtFirstUnconfirmedStep(string failureMethod)
    {
        var f = new Fixture(); using var server = new RpcServer(f); f.Connect(server.Session);
        var snapshot = await AdManagement.LoadAsync(f.Connection, "pool", "pool-uuid"); var request = f.Request(AdOperation.SetRoles);
        var review = await AdManagement.ReviewAsync(f.Connection, snapshot, request); server.FailureMethod = failureMethod;
        var error = Assert.Throws<InvalidOperationException>(() => new AdAction(f.Connection, snapshot, request, review, new("", "")).RunSync(server.Session));
        Assert.Contains("partial", error.Message);
        var writes = server.Requests.Where(IsMutation).Select(Method).ToArray();
        Assert.Equal(failureMethod, writes[^1]); Assert.Single(writes, m => m == failureMethod);
    }

    [Fact]
    public async Task DirectoryIdentityChangingAfterReviewCannotCreateAnotherUser()
    {
        var f = new Fixture(); using var server = new RpcServer(f); f.Connect(server.Session);
        var snapshot = await AdManagement.LoadAsync(f.Connection, "pool", "pool-uuid"); var request = f.Request(AdOperation.AddSubject);
        var review = await AdManagement.ReviewAsync(f.Connection, snapshot, request); server.ResolvedSid = "replacement-sid";
        Assert.Throws<InvalidOperationException>(() => new AdAction(f.Connection, snapshot, request, review, new("", "")).RunSync(server.Session));
        Assert.DoesNotContain(server.Requests, IsMutation);
    }

    [Fact]
    public void SharedSubjectResolutionPinsReviewedIdentifierAtItsOwnLookup()
    {
        var f = new Fixture(); using var server = new RpcServer(f); f.Connect(server.Session);
        var action = new AddRemoveSubjectsAction(f.Connection, ["DOMAIN\\new"], [], new() { ["DOMAIN\\new"] = "other-sid" });
        Assert.Throws<InvalidOperationException>(() => action.RunSync(server.Session));
        Assert.DoesNotContain(server.Requests, IsMutation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CredentialEchoesNeverReachSharedActionExceptionsOrLogs(bool join)
    {
        var f = new Fixture(!join); using var server = new RpcServer(f) { FailureMethod = join ? "pool.enable_external_auth" : "pool.disable_external_auth", Echo = "distinct-password-and-user" }; f.Connect(server.Session);
        var hierarchy = (Hierarchy)log4net.LogManager.GetRepository(typeof(AsyncAction).Assembly);
        var appender = new MemoryAppender(); appender.ActivateOptions(); hierarchy.Root.AddAppender(appender);
        var old = hierarchy.Root.Level; hierarchy.Root.Level = Level.All; hierarchy.Configured = true;
        try
        {
            AsyncAction action = join ? new EnableAdAction(f.Connection, "example.org", server.Echo, server.Echo, true)
                : new DisableAdAction(f.Connection, new() { ["user"] = server.Echo, ["pass"] = server.Echo });
            var error = Assert.ThrowsAny<Exception>(() => action.RunSync(server.Session));
            Assert.DoesNotContain(server.Echo, error.ToString());
            Assert.All(appender.GetEvents(), e => Assert.DoesNotContain(server.Echo, (e.RenderedMessage ?? "") + e.GetExceptionString()));
            if (join) Assert.Null(typeof(EnableAdAction).GetField("password", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(action));
            else Assert.Empty((Dictionary<string, string>)typeof(DisableAdAction).GetField("creds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(action)!);
        }
        finally { hierarchy.Root.RemoveAppender(appender); hierarchy.Root.Level = old; appender.Close(); }
    }

    [Fact]
    public async Task CancelBeforeStartReleasesSharedActionCredentials()
    {
        var f = new Fixture(false);
        var action = new EnableAdAction(f.Connection, "example.org", "user", "secret", true);
        action.Cancel();
        var field = typeof(EnableAdAction).GetField("password", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var limit = DateTime.UtcNow.AddSeconds(5);
        while (field.GetValue(action) != null && DateTime.UtcNow < limit) await Task.Delay(10);
        Assert.Null(field.GetValue(action));
    }

    [Fact]
    public async Task FailedPreparatoryLeaveDoesNotContinueIntoJoin()
    {
        var f = new Fixture(false); using var server = new RpcServer(f) { FailureMethod = "pool.disable_external_auth" }; f.Connect(server.Session);
        var snapshot = await AdManagement.LoadAsync(f.Connection, "pool", "pool-uuid"); var request = f.Request(AdOperation.Join);
        var review = await AdManagement.ReviewAsync(f.Connection, snapshot, request);
        Assert.Throws<InvalidOperationException>(() => new AdAction(f.Connection, snapshot, request, review, new("user", "secret")).RunSync(server.Session));
        Assert.Equal("pool.disable_external_auth", Method(Assert.Single(server.Requests, IsMutation)));
    }

    [Fact]
    public async Task DomainOrRoleChangesDuringSubjectCreationStopSubsequentRoleAssignment()
    {
        var f = new Fixture(); using var server = new RpcServer(f) { ChangeDomainOnCreate = true }; f.Connect(server.Session);
        var snapshot = await AdManagement.LoadAsync(f.Connection, "pool", "pool-uuid"); var request = f.Request(AdOperation.AddSubject);
        var review = await AdManagement.ReviewAsync(f.Connection, snapshot, request);
        Assert.Throws<InvalidOperationException>(() => new AdAction(f.Connection, snapshot, request, review, new("", "")).RunSync(server.Session));
        Assert.Equal("subject.create", Method(Assert.Single(server.Requests, IsMutation)));
    }

    [Theory]
    [InlineData("host-offline")]
    [InlineData("ad-restricted")]
    [InlineData("rbac-restricted")]
    [InlineData("unknown-role")]
    [InlineData("pool-busy")]
    [InlineData("no-recovery")]
    public async Task UnsupportedPrerequisitesFailWithoutMutation(string condition)
    {
        var f = new Fixture();
        switch (condition)
        {
            case "host-offline": f.Metrics.live = false; break;
            case "ad-restricted": f.Host.license_params["restrict_ad"] = "true"; break;
            case "rbac-restricted": f.Host.license_params["restrict_rbac"] = "true"; break;
            case "unknown-role": f.Subject.roles.Add(new("unknown")); break;
            case "pool-busy": f.Pool.is_psr_pending = true; break;
        }
        using var server = new RpcServer(f); f.Connect(server.Session);
        var snapshot = await AdManagement.LoadAsync(f.Connection, "pool", "pool-uuid");
        var request = new AdRequest(AdOperation.SetRoles, "", "", "subject", ["admin"], condition != "no-recovery");
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdManagement.ReviewAsync(f.Connection, snapshot, request));
        Assert.DoesNotContain(server.Requests, IsMutation);
    }

    [Fact]
    public async Task RecoveryLeaveRemainsAvailableWhenDomainCapabilitiesAreRestricted()
    {
        var f = new Fixture(); f.Metrics.live = false; f.Host.license_params["restrict_ad"] = "true";
        using var server = new RpcServer(f); f.Connect(server.Session);
        var snapshot = await AdManagement.LoadAsync(f.Connection, "pool", "pool-uuid"); var request = f.Request(AdOperation.Leave);
        var review = await AdManagement.ReviewAsync(f.Connection, snapshot, request);
        new AdAction(f.Connection, snapshot, request, review, new("", "")).RunSync(server.Session);
        var mutation = Assert.Single(server.Requests, IsMutation);
        Assert.Equal("pool.disable_external_auth", Method(mutation));
        Assert.Empty((JObject)mutation["params"]![2]!);
    }

    private static string Method(JObject request) => request["method"]!.Value<string>()!;
    private static bool IsMutation(JObject request) => Method(request) is "pool.enable_external_auth" or "pool.disable_external_auth"
        or "subject.create" or "subject.destroy" or "subject.add_to_roles" or "subject.remove_from_roles" or "session.logout_subject_identifier";

    private sealed class Fixture
    {
        public XenConnection Connection { get; } = new();
        public Pool Pool { get; }
        public Host Host { get; }
        public Host_metrics Metrics { get; }
        public Subject Subject { get; }
        public Role Admin { get; }
        public Role ReadOnly { get; }
        public Subject? Created { get; set; }
        public Fixture(bool joined = true)
        {
            Metrics = Add("metrics", new Host_metrics { uuid = "metrics-uuid", live = true });
            Host = Add("host", new Host { uuid = "host-uuid", name_label = "Host", address = "192.0.2.10", metrics = new("metrics"), enabled = true,
                external_auth_type = joined ? "AD" : "", external_auth_service_name = joined ? "example.org" : "",
                license_params = new() { ["restrict_ad"] = "false", ["restrict_rbac"] = "false" }, software_version = new() { ["product_version"] = "8.3.0" } });
            Pool = Add("pool", new Pool { uuid = "pool-uuid", name_label = "Pool", master = new("host") });
            Admin = Add("admin", new Role { uuid = "admin-uuid", name_label = "pool-admin", subroles = [new("permission")] });
            ReadOnly = Add("read", new Role { uuid = "read-uuid", name_label = "read-only", subroles = [new("permission")] });
            Subject = Add("subject", new Subject { uuid = "subject-uuid", subject_identifier = "target-sid", other_config = new() { ["subject-name"] = "DOMAIN\\target" }, roles = [new("read")] });
        }
        public AdRequest Request(AdOperation operation) => new(operation, "example.org", "DOMAIN\\new", "subject", ["admin"], true);
        public T Add<T>(string reference, T value) where T : XenObject<T>
        { Connection.Cache.UpdateFrom(Connection, [new ObjectChange(typeof(T), reference, value)]); return Connection.Resolve(new XenRef<T>(reference)); }
        public void Connect(Session session)
        {
            var field = typeof(XenConnection).GetField("connectTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var state = Activator.CreateInstance(field.FieldType, BindingFlags.Instance | BindingFlags.NonPublic, null, ["127.0.0.1", 1], null)!;
            field.FieldType.GetField("Connected")!.SetValue(state, true); field.FieldType.GetField("Session")!.SetValue(state, session); field.SetValue(Connection, state);
        }
    }

    private sealed class RpcServer : IDisposable
    {
        private readonly Fixture _fixture;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(60));
        private readonly List<JObject> _requests = [];
        private readonly Task _server;
        public Session Session { get; }
        public bool LocalRoot { get; set; } = true;
        public bool ReplaceServerSubject { get; set; }
        public bool ChangeDomainOnCreate { get; set; }
        public Action<string>? BeforeReply { get; set; }
        public string? DeniedPermission { get; set; }
        public string? SelfAuthority { get; set; }
        public string? FailureMethod { get; set; }
        public string Echo { get; set; } = "loopback regression";
        public string ResolvedSid { get; set; } = "new-sid";
        public IReadOnlyList<JObject> Requests { get { lock (_requests) return _requests.ToArray(); } }
        public RpcServer(Fixture fixture)
        {
            _fixture = fixture; _listener.Start();
            Session = new Session($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/") { APIVersion = API_Version.API_2_16, Timeout = 3000,
                opaque_ref = "OpaqueRef:ad-test-session", Connection = fixture.Connection };
            typeof(Session).GetProperty(nameof(Session.IsLocalSuperuser))!.SetValue(Session, true);
            _server = ServeAsync();
        }
        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    var length = 0;
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } header)
                        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header.Split(':', 2)[1]);
                    var body = new char[length]; var offset = 0;
                    while (offset < length) { var read = await reader.ReadAsync(body.AsMemory(offset), _stop.Token); if (read == 0) throw new EndOfStreamException(); offset += read; }
                    var request = JObject.Parse(new string(body)); lock (_requests) _requests.Add(request);
                    var method = Method(request); var response = new JObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone() };
                    if (method == FailureMethod) response["error"] = new JObject { ["code"] = 1, ["message"] = "OPERATION_NOT_ALLOWED", ["data"] = new JArray(Echo) };
                    else response["result"] = Reply(method);
                    var bytes = Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await stream.WriteAsync(bytes, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        private JToken? Reply(string method)
        {
            BeforeReply?.Invoke(method);
            switch (method)
            {
                case "session.get_is_local_superuser": return new JValue(LocalRoot);
                case "session.get_rbac_permissions": return new JArray(Enum.GetValues<AdOperation>().SelectMany(AdManagement.Methods).Select(m => m.Method).Distinct().Where(m => m != DeniedPermission));
                case "session.get_subject": return new JValue(SelfAuthority == "subject" ? "subject" : "current-subject");
                case "session.get_auth_user_sid": return new JValue(SelfAuthority == "user" ? "target-sid" : "current-sid");
                case "auth.get_group_membership": return new JArray(SelfAuthority == "group" ? new[] { "target-sid" } : []);
                case "pool.get_record": return _fixture.Pool.ToJObject();
                case "host.get_all_records": return Map(_fixture.Connection.Cache.Hosts);
                case "host_metrics.get_all_records": return Map(new[] { _fixture.Metrics });
                case "role.get_all_records": return Map(_fixture.Connection.Cache.Roles);
                case "subject.get_all_records":
                    var map = Map(_fixture.Created == null ? new[] { _fixture.Subject } : new[] { _fixture.Subject, _fixture.Created });
                    if (ReplaceServerSubject) map["subject"]!["uuid"] = "server-replacement";
                    return map;
                case "auth.get_subject_identifier": return new JValue(ResolvedSid);
                case "auth.get_subject_information_from_identifier": return new JObject { ["subject-name"] = "DOMAIN\\new", ["subject-is-group"] = "true" };
                case "subject.create":
                    _fixture.Created = new Subject { opaque_ref = "created", uuid = "created-uuid", subject_identifier = ResolvedSid, roles = [new("read")] };
                    if (ChangeDomainOnCreate) _fixture.Host.external_auth_service_name = "replacement.org";
                    return new JValue("created");
                case "subject.add_to_roles": case "subject.remove_from_roles": case "subject.destroy": case "session.logout_subject_identifier":
                case "pool.enable_external_auth": case "pool.disable_external_auth": return null;
                default: throw new InvalidOperationException($"Unexpected RPC {method}");
            }
        }
        private static JObject Map<T>(IEnumerable<T> records) where T : XenObject<T>
        { var result = new JObject(); foreach (var record in records) result[record.opaque_ref] = record.ToJObject(); return result; }
        public void Dispose()
        {
            _stop.Cancel(); _listener.Stop();
            try { _server.GetAwaiter().GetResult(); } catch (SocketException) when (_stop.IsCancellationRequested) { }
            _stop.Dispose();
        }
    }
}
