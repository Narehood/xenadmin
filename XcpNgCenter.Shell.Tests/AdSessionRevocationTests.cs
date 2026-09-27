using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XenAPI;
using XcpNgCenter.Shell.Services;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.Tests;

public sealed class AdSessionRevocationTests
{
    [Fact]
    public void DirectUserRevocationNeedsNoGroupEnumeration()
    {
        using var server = new RpcServer();
        AdSessionRevocation.Revoke(server.Session, "user-sid", false);
        var request = Assert.Single(server.Requests);
        Assert.Equal("session.logout_subject_identifier", Method(request)); Assert.Equal("user-sid", Identifier(request));
    }

    [Fact]
    public void GroupRevocationFindsDirectAndTransitiveUsersAndDeduplicatesReturnedIdentities()
    {
        using var server = new RpcServer
        {
            Subjects = ["direct-user", "nested-user", "unrelated-user", "direct-user", "", " ", "target-group", "nested-group"],
            Groups = new()
            {
                ["direct-user"] = ["target-group"],
                ["nested-user"] = ["nested-group", "target-group", "other-group"],
                ["unrelated-user"] = ["other-group"],
                ["nested-group"] = ["target-group"]
            }
        };
        AdSessionRevocation.Revoke(server.Session, "target-group", true);
        Assert.Equal(new[] { "target-group", "direct-user", "nested-user", "nested-group" }, server.Logouts);
        Assert.Equal(new[] { "direct-user", "nested-user", "unrelated-user", "nested-group" }, server.MembershipLookups);
        Assert.Single(server.Requests, request => Method(request) == "session.get_all_subject_identifiers");
        Assert.DoesNotContain(server.Requests, request => Method(request) == "session.logout");
    }

    [Fact]
    public void RemovalSweepsRemainingMembersWithoutRepeatingTheSharedDirectLogout()
    {
        using var server = new RpcServer { Subjects = ["target-group", "nested-user"], Groups = new() { ["nested-user"] = ["target-group"] } };
        AdSessionRevocation.Revoke(server.Session, "target-group", true, targetAlreadyLoggedOut: true);
        Assert.Equal(new[] { "nested-user" }, server.Logouts);
        Assert.Equal("session.get_all_subject_identifiers", Method(server.Requests[0]));
    }

    [Fact]
    public void EmptyExternalSessionInventoryOnlyLogsOutTheDirectGroup()
    {
        using var server = new RpcServer { Subjects = ["", " "] };
        AdSessionRevocation.Revoke(server.Session, "target-group", true);
        Assert.Equal(new[] { "target-group" }, server.Logouts); Assert.Empty(server.MembershipLookups);
    }

    [Theory]
    [InlineData("session.logout_subject_identifier", "target-group", 1, 0)]
    [InlineData("session.get_all_subject_identifiers", null, 1, 0)]
    [InlineData("auth.get_group_membership", "second-user", 2, 2)]
    [InlineData("session.logout_subject_identifier", "first-user", 2, 1)]
    public void RpcFailureStopsTheSweepWithoutRetriesOrFalseCompletion(string method, string? identifier, int logoutCalls, int membershipCalls)
    {
        using var server = new RpcServer
        {
            Subjects = ["first-user", "second-user", "third-user"],
            Groups = new() { ["first-user"] = ["target-group"], ["second-user"] = ["target-group"], ["third-user"] = ["target-group"] },
            FailMethod = method, FailIdentifier = identifier
        };
        Assert.Throws<Failure>(() => AdSessionRevocation.Revoke(server.Session, "target-group", true));
        Assert.Equal(logoutCalls, server.Logouts.Count); Assert.Equal(membershipCalls, server.MembershipLookups.Count);
        Assert.DoesNotContain("third-user", server.MembershipLookups);
        Assert.Single(server.Requests, request => Method(request) == method && (identifier == null || Identifier(request) == identifier));
    }

    [Theory]
    [InlineData(AdOperation.AddSubject)]
    [InlineData(AdOperation.SetRoles)]
    [InlineData(AdOperation.RemoveSubject)]
    public void SubjectOperationsDeclarePermissionsForTransitiveSessionRevocation(AdOperation operation)
    {
        var methods = AdManagement.Methods(operation).Select(method => method.Method).ToArray();
        Assert.Contains("session.get_all_subject_identifiers", methods);
        Assert.Contains("auth.get_group_membership", methods);
        Assert.Contains("session.logout_subject_identifier", methods);
    }

    [Theory]
    [InlineData("session.get_all_subject_identifiers")]
    [InlineData("auth.get_group_membership")]
    [InlineData("session.logout_subject_identifier")]
    public void MissingRevocationPermissionRejectsTheOperationBeforeSessionMutation(string missing)
    {
        using var server = new RpcServer { Permissions = AdManagement.Methods(AdOperation.AddSubject).ToStringArray().Where(method => method != missing).ToArray() };
        Assert.Throws<AdValidationException>(() => AdManagement.RequirePermissions(server.Session, AdOperation.AddSubject));
        Assert.Empty(server.Logouts); Assert.Empty(server.MembershipLookups);
    }

    private static string Method(JObject request) => request["method"]!.Value<string>()!;
    private static string? Identifier(JObject request) => request["params"] is JArray { Count: > 1 } parameters ? parameters[1]!.Value<string>() : null;

    private sealed class RpcServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        private readonly List<JObject> _requests = [];
        private readonly Task _server;
        public Session Session { get; }
        public string[] Subjects { get; init; } = [];
        public Dictionary<string, string[]> Groups { get; init; } = [];
        public string[] Permissions { get; init; } = [];
        public string? FailMethod { get; init; }
        public string? FailIdentifier { get; init; }
        public IReadOnlyList<JObject> Requests { get { lock (_requests) return _requests.ToArray(); } }
        public IReadOnlyList<string> Logouts => Requests.Where(request => Method(request) == "session.logout_subject_identifier").Select(request => Identifier(request)!).ToArray();
        public IReadOnlyList<string> MembershipLookups => Requests.Where(request => Method(request) == "auth.get_group_membership").Select(request => Identifier(request)!).ToArray();
        public RpcServer()
        {
            _listener.Start();
            Session = new Session($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/")
                { APIVersion = API_Version.API_2_16, opaque_ref = "administrator-session", Timeout = 3000 };
            _server = ServeAsync();
        }
        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    var length = 0;
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } header)
                        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header.Split(':', 2)[1]);
                    var body = new char[length]; var offset = 0;
                    while (offset < length)
                    { var read = await reader.ReadAsync(body.AsMemory(offset), _stop.Token); if (read == 0) throw new EndOfStreamException(); offset += read; }
                    var request = JObject.Parse(new string(body)); lock (_requests) _requests.Add(request);
                    var response = new JObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone() };
                    if (Method(request) == FailMethod && (FailIdentifier == null || Identifier(request) == FailIdentifier))
                        response["error"] = new JObject { ["code"] = 1, ["message"] = "RBAC_PERMISSION_DENIED", ["data"] = new JArray("injected failure") };
                    else response["result"] = Method(request) switch
                    {
                        "session.get_is_local_superuser" => new JValue(false),
                        "session.get_rbac_permissions" => new JArray(Permissions),
                        "session.get_all_subject_identifiers" => new JArray(Subjects),
                        "auth.get_group_membership" => new JArray(Groups.GetValueOrDefault(Identifier(request)!, [])),
                        "session.logout_subject_identifier" => null,
                        _ => throw new InvalidOperationException("Unexpected RPC " + Method(request))
                    };
                    var bytes = Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await stream.WriteAsync(bytes, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); _server.GetAwaiter().GetResult(); _stop.Dispose(); }
    }
}
