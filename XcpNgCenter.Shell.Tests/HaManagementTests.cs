using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XenAdmin;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.Tests;

[CollectionDefinition("HA shared action runtime", DisableParallelization = true)]
public sealed class HaSharedActionRuntimeCollection;

[Collection("HA shared action runtime")]
public sealed class HaManagementTests : IDisposable
{
    private readonly IXenAdminConfigProvider? _originalProvider = XenAdminConfigManager.Provider;

    public HaManagementTests()
    {
        // The application bootstrap supplies task metadata and cloned action
        // sessions. Exercise the same provider without initializing the UI.
        XenAdminConfigManager.Provider = new ShellConfigProvider(new ShellAppSettings { ConnectionTimeoutSeconds = 3 });
    }

    public void Dispose() => XenAdminConfigManager.Provider = _originalProvider;

    [Fact]
    public void CaptureRetainsUnknownPoliciesInvalidToleranceHiddenVmsAndImmutableDrafts()
    {
        var f = new Fixture();
        f.Pool.ha_host_failures_to_tolerate = -5;
        f.Vms[0].ha_restart_priority = "future-policy";
        f.Hosts[0].license_params.Clear();
        var snapshot = HaManagement.Capture(f.Pool);
        Assert.Equal(-5, snapshot.FailuresToTolerate);
        Assert.Equal(2, snapshot.Vms.Count);
        Assert.Null(snapshot.Vms[0].Priority);
        Assert.Equal("future-policy", snapshot.Vms[0].RawPriority);
        var settings = snapshot.Vms.Select(vm => new HaVmSetting(vm.Reference, vm.Priority, vm.Order, vm.StartDelay)).ToList();
        var request = new HaRequest(HaOperation.Enable, 1, f.Sr.opaque_ref, settings);
        settings.Clear();
        Assert.Equal(2, request.VmSettings.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<HaVmSetting>)request.VmSettings).Clear());
        Assert.Equal(5, request.VmSettings[0].Order);
        Assert.Equal(9, request.VmSettings[0].StartDelay);
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("host-offline")]
    [InlineData("host-disabled")]
    [InlineData("unlicensed")]
    [InlineData("management-dhcp")]
    [InlineData("management-unknown-address-type")]
    [InlineData("missing-management")]
    [InlineData("mixed-versions")]
    [InlineData("secret-rotation")]
    [InlineData("pool-busy")]
    public async Task InvalidEnablePrerequisitesFailBeforeAnyRpc(string condition)
    {
        var f = new Fixture();
        switch (condition)
        {
            case "host-offline": f.Metrics[1].live = false; break;
            case "host-disabled": f.Hosts[1].enabled = false; break;
            case "unlicensed": f.Hosts[1].license_params.Clear(); break;
            case "management-dhcp": f.Pifs[1].ip_configuration_mode = ip_configuration_mode.DHCP; break;
            case "management-unknown-address-type": f.Pifs[1].primary_address_type = primary_address_type.unknown; break;
            case "missing-management": f.Pifs[1].management = false; break;
            case "mixed-versions": f.Hosts[1].software_version["product_version"] = "8.2.0"; break;
            case "secret-rotation": f.Pool.is_psr_pending = true; break;
            case "pool-busy": f.Pool.Locked = true; break;
        }
        using var server = new RpcServer(f);
        if (condition != "disconnected") f.MarkConnected(server.Session);
        var snapshot = HaManagement.Capture(f.Pool);
        await Assert.ThrowsAsync<InvalidOperationException>(() => HaManagement.ReviewAsync(f.Connection, snapshot, f.Request(snapshot)));
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("pool-uuid")]
    [InlineData("host-uuid")]
    [InlineData("sr-uuid")]
    [InlineData("vm-uuid")]
    [InlineData("priority")]
    [InlineData("start-delay")]
    [InlineData("vgpu")]
    [InlineData("sriov")]
    [InlineData("pbd-detached")]
    public async Task ChangedReviewedConfigurationIsRejectedBeforeRpc(string change)
    {
        var f = new Fixture();
        using var server = new RpcServer(f);
        f.MarkConnected(server.Session);
        var snapshot = HaManagement.Capture(f.Pool);
        var request = f.Request(snapshot);
        switch (change)
        {
            case "pool-uuid": f.Pool.uuid = "replacement"; break;
            case "host-uuid": f.Hosts[1].uuid = "replacement"; break;
            case "sr-uuid": f.Sr.uuid = "replacement"; break;
            case "vm-uuid": f.Vms[0].uuid = "replacement"; break;
            case "priority": f.Vms[0].ha_restart_priority = "best-effort"; break;
            case "start-delay": f.Vms[0].start_delay++; break;
            case "vgpu": f.Vms[0].VGPUs = [new("vgpu")]; break;
            case "sriov": f.Pifs[0].sriov_physical_PIF_of = [new("sriov")]; break;
            case "pbd-detached": f.Pbds[1].currently_attached = false; break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => HaManagement.ReviewAsync(f.Connection, snapshot, request));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task ReviewDiscoversHeartbeatCandidatesWithoutChangingVmsOrPool()
    {
        var f = new Fixture();
        using var server = new RpcServer(f);
        f.MarkConnected(server.Session);
        var snapshot = HaManagement.Capture(f.Pool);
        var original = snapshot.Fingerprint;
        var request = new HaRequest(HaOperation.Enable, 1, null, f.Request(snapshot).VmSettings);
        var review = await HaManagement.ReviewAsync(f.Connection, snapshot, request);
        Assert.False(review.CanApply);
        Assert.Contains("Select a heartbeat", review.Error);
        Assert.True(Assert.Single(review.HeartbeatCandidates).IsAvailable);
        Assert.Equal(2, review.VmAgility.Count);
        Assert.Equal(1, review.MaxHostFailures);
        Assert.Equal(original, HaManagement.Capture(f.Pool).Fingerprint);
        Assert.DoesNotContain(server.Requests, IsMutation);
        Assert.DoesNotContain(server.Requests, request => Method(request) is "session.logout" or "session.login_with_password");
    }

    [Theory]
    [InlineData(vm_power_state.Paused)]
    [InlineData(vm_power_state.Suspended)]
    public async Task UnchangedUnprotectedPausedOrSuspendedVmDoesNotBlockPoolReview(vm_power_state state)
    {
        var f = new Fixture();
        f.Vms[1].power_state = state;
        using var server = new RpcServer(f);
        f.MarkConnected(server.Session);
        var snapshot = HaManagement.Capture(f.Pool);
        var review = await HaManagement.ReviewAsync(f.Connection, snapshot, f.Request(snapshot));
        Assert.True(review.CanApply, review.Error);
        Assert.Equal(state, f.Vms[1].power_state);
        Assert.DoesNotContain(server.Requests, IsMutation);
    }

    [Fact]
    public async Task ServerRejectedHeartbeatStorageRemainsVisibleAndCannotBeApplied()
    {
        var f = new Fixture();
        using var server = new RpcServer(f) { SrFailure = "SR_OPERATION_NOT_SUPPORTED" };
        f.MarkConnected(server.Session);
        var snapshot = HaManagement.Capture(f.Pool);
        var review = await HaManagement.ReviewAsync(f.Connection, snapshot, f.Request(snapshot));
        Assert.False(review.CanApply);
        Assert.False(Assert.Single(review.HeartbeatCandidates).IsAvailable);
        Assert.NotEmpty(review.HeartbeatCandidates[0].Error!);
        Assert.DoesNotContain(server.Requests, IsMutation);
    }

    [Theory]
    [InlineData(0L, null)]
    [InlineData(1L, "0")]
    [InlineData(-1L, null)]
    public async Task UnknownOrInsufficientCapacityBlocksApply(long capacity, string? hciLimit)
    {
        var f = new Fixture();
        if (hciLimit != null) f.Pool.other_config["hci-limit-fault-tolerance"] = hciLimit;
        using var server = new RpcServer(f) { Capacity = capacity };
        f.MarkConnected(server.Session);
        var snapshot = HaManagement.Capture(f.Pool);
        var request = f.Request(snapshot);
        var review = await HaManagement.ReviewAsync(f.Connection, snapshot, request);
        Assert.False(review.CanApply);
        var action = new ShellHaAction(f.Connection, snapshot, request, review);
        var calls = server.Requests.Count;
        Assert.Throws<InvalidOperationException>(() => action.RunSync(server.Session));
        Assert.Equal(calls, server.Requests.Count);
        Assert.DoesNotContain(server.Requests, IsMutation);
    }

    [Theory]
    [InlineData(VM.HaRestartPriority.Restart, false)]
    [InlineData(VM.HaRestartPriority.BestEffort, true)]
    [InlineData(VM.HaRestartPriority.DoNotRestart, true)]
    public async Task OnlyProtectedVmRequiresSuccessfulAgility(VM.HaRestartPriority priority, bool applicable)
    {
        var f = new Fixture();
        using var server = new RpcServer(f) { AgilityFailure = "VM_HAS_VUSB" };
        f.MarkConnected(server.Session);
        var snapshot = HaManagement.Capture(f.Pool);
        var initial = f.Request(snapshot);
        var request = new HaRequest(initial.Operation, 1, initial.HeartbeatSrReference,
            initial.VmSettings.Select(setting => setting with { Priority = priority }).ToArray());
        var review = await HaManagement.ReviewAsync(f.Connection, snapshot, request);
        Assert.Equal(applicable, review.CanApply);
        Assert.All(review.VmAgility, result => Assert.False(result.IsAgile));
        var map = server.Requests.Single(request => Method(request) == "pool.ha_compute_hypothetical_max_host_failures_to_tolerate")["params"]![1]!.ToObject<Dictionary<string, string>>()!;
        Assert.Equal(priority == VM.HaRestartPriority.Restart ? 2 : 0, map.Count);
    }

    [Theory]
    [InlineData("SESSION_INVALID")]
    [InlineData("RBAC_PERMISSION_DENIED")]
    [InlineData("INTERNAL_ERROR")]
    public async Task UnknownServerReviewFailureStopsScanAndNeverProducesApproval(string failure)
    {
        var f = new Fixture();
        using var server = new RpcServer(f) { AgilityFailure = failure };
        f.MarkConnected(server.Session);
        var snapshot = HaManagement.Capture(f.Pool);
        await Assert.ThrowsAsync<Failure>(() => HaManagement.ReviewAsync(f.Connection, snapshot, f.Request(snapshot)));
        Assert.Single(server.Requests, request => Method(request) == "VM.assert_agile");
        Assert.DoesNotContain(server.Requests, request => Method(request).Contains("hypothetical", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Requests, IsMutation);
    }

    [Fact]
    public async Task ServerChangeDuringLongReviewIsRejectedEvenWhenCacheDidNotChange()
    {
        var f = new Fixture();
        using var server = new RpcServer(f) { AddServerVgpuAfterCapacity = true };
        f.MarkConnected(server.Session);
        var snapshot = HaManagement.Capture(f.Pool);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HaManagement.ReviewAsync(f.Connection, snapshot, f.Request(snapshot)));
        Assert.Contains("changed", error.Message);
        Assert.Empty(f.Vms[0].VGPUs);
        Assert.Equal(2, server.Requests.Count(request => Method(request) == "VM.get_all_records"));
        Assert.DoesNotContain(server.Requests, IsMutation);
    }

    [Fact]
    public async Task RestrictedSessionCannotReviewOrMutateHa()
    {
        var f = new Fixture();
        using var server = new RpcServer(f);
        typeof(Session).GetProperty(nameof(Session.IsLocalSuperuser))!.SetValue(server.Session, false);
        typeof(Session).GetProperty(nameof(Session.Permissions))!.SetValue(server.Session, new[] { "pool.get_record" });
        f.MarkConnected(server.Session);
        var snapshot = HaManagement.Capture(f.Pool);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HaManagement.ReviewAsync(f.Connection, snapshot, f.Request(snapshot)));
        Assert.Contains("permissions", error.Message);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData(HaOperation.Enable, "OPERATION_NOT_ALLOWED")]
    [InlineData(HaOperation.Configure, "OPERATION_NOT_ALLOWED")]
    [InlineData(HaOperation.Enable, "SESSION_INVALID")]
    [InlineData(HaOperation.Configure, "SESSION_INVALID")]
    public async Task SharedWorkerReceivesOnlyChangedPoliciesAndPreservedStartupOptions(HaOperation operation, string mutationFailure)
    {
        var f = new Fixture(operation == HaOperation.Configure);
        using var server = new RpcServer(f) { MutationFailure = mutationFailure };
        f.MarkConnected(server.Session);
        var snapshot = HaManagement.Capture(f.Pool);
        var request = f.Request(snapshot, operation);
        var review = await HaManagement.ReviewAsync(f.Connection, snapshot, request);
        Assert.True(review.CanApply, review.Error);
        var action = new ShellHaAction(f.Connection, snapshot, request, review);
        var permissions = action.GetApiMethodsToRoleCheck.Select(method => method.Method).ToArray();
        Assert.Contains("vm.set_ha_restart_priority", permissions);
        Assert.Contains("vm.set_order", permissions);
        Assert.Contains("vm.set_start_delay", permissions);
        Assert.Contains("pool.set_ha_host_failures_to_tolerate", permissions);
        Assert.Contains("task.destroy", permissions);
        Assert.Contains(operation == HaOperation.Enable ? "pool.enable_ha" : "pool.sync_database", permissions);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => action.RunSync(server.Session)));
        Assert.Contains("partially changed", error.Message);
        Assert.Equal(mutationFailure, Assert.IsType<Failure>(error.InnerException).ErrorDescription[0]);
        var mutations = server.Requests.Where(IsMutation).ToArray();
        Assert.Single(mutations, call => Method(call) == "VM.set_ha_restart_priority");
        Assert.Equal("restart", mutations.Single(call => Method(call) == "VM.set_ha_restart_priority")["params"]![2]!.Value<string>());
        Assert.Equal(5, mutations.Single(call => Method(call) == "VM.set_order")["params"]![2]!.Value<long>());
        Assert.Equal(9, mutations.Single(call => Method(call) == "VM.set_start_delay")["params"]![2]!.Value<long>());
        Assert.Equal(1, mutations.Single(call => Method(call) == "pool.set_ha_host_failures_to_tolerate")["params"]![2]!.Value<long>());
        var final = Assert.Single(mutations, call => Method(call) == (operation == HaOperation.Enable ? "Async.pool.enable_ha" : "Async.pool.sync_database"));
        if (operation == HaOperation.Enable)
        {
            Assert.Equal(new[] { f.Sr.opaque_ref }, final["params"]![1]!.ToObject<string[]>());
            Assert.Empty((JObject)final["params"]![2]!);
        }
        Assert.Equal(snapshot.Fingerprint, HaManagement.Capture(f.Pool).Fingerprint);
        Assert.Equal("", f.Vms[0].ha_restart_priority);
        Assert.DoesNotContain(mutations, call => Method(call).Contains("rollback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DisableDoesNotRequireHealthyHostsLicenseHeartbeatOrValidDraftPolicies()
    {
        var f = new Fixture(true);
        f.Metrics[1].live = false;
        f.Hosts[1].enabled = false;
        f.Hosts[0].license_params.Clear();
        f.Pool.ha_statefiles = ["missing-statefile"];
        f.Pool.ha_host_failures_to_tolerate = -7;
        f.Vms[0].ha_restart_priority = "unknown";
        using var server = new RpcServer(f);
        f.MarkConnected(server.Session);
        var snapshot = HaManagement.Capture(f.Pool);
        var request = new HaRequest(HaOperation.Disable, -7, null, []);
        var review = await HaManagement.ReviewAsync(f.Connection, snapshot, request);
        Assert.True(review.CanApply, review.Error);
        var action = new ShellHaAction(f.Connection, snapshot, request, review);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => action.RunSync(server.Session)));
        Assert.Equal(new[] { "pool.get_record", "pool.get_record", "Async.pool.disable_ha" }, server.Requests.Select(Method));
        var permission = action.GetApiMethodsToRoleCheck.Select(method => method.Method).ToArray();
        Assert.Contains("pool.disable_ha", permission);
        Assert.DoesNotContain("vm.set_ha_restart_priority", permission);
        Assert.DoesNotContain("pool.enable_ha", permission);
        Assert.Single(server.Requests, IsMutation);
    }

    [Fact]
    public async Task WorkerRecomputesCapacityAndRejectsChangedDraftOrServerCapacityBeforeMutation()
    {
        var f = new Fixture();
        using var server = new RpcServer(f);
        f.MarkConnected(server.Session);
        var snapshot = HaManagement.Capture(f.Pool);
        var request = f.Request(snapshot);
        var review = await HaManagement.ReviewAsync(f.Connection, snapshot, request);
        var changedRequest = new HaRequest(request.Operation, 0, request.HeartbeatSrReference, request.VmSettings);
        var calls = server.Requests.Count;
        Assert.Throws<InvalidOperationException>(() => new ShellHaAction(f.Connection, snapshot, changedRequest, review).RunSync(server.Session));
        Assert.Equal(calls, server.Requests.Count);
        server.Capacity = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => new ShellHaAction(f.Connection, snapshot, request, review).RunSync(server.Session)));
        Assert.Equal(2, server.Requests.Count(call => Method(call) == "pool.ha_compute_hypothetical_max_host_failures_to_tolerate"));
        Assert.DoesNotContain(server.Requests, IsMutation);
    }

    [Theory]
    [InlineData("vm.set_order")]
    [InlineData("pool.sync_database")]
    [InlineData("task.get_record")]
    [InlineData("task.remove_from_other_config/key:applies_to")]
    public async Task WorkerRejectsLostNestedPermissionBeforeAnyNewRpc(string revokedPermission)
    {
        var f = new Fixture(true);
        using var server = new RpcServer(f);
        f.MarkConnected(server.Session);
        var snapshot = HaManagement.Capture(f.Pool);
        var request = f.Request(snapshot, HaOperation.Configure);
        var review = await HaManagement.ReviewAsync(f.Connection, snapshot, request);
        Assert.True(review.CanApply, review.Error);
        typeof(Session).GetProperty(nameof(Session.IsLocalSuperuser))!.SetValue(server.Session, false);
        typeof(Session).GetProperty(nameof(Session.Permissions))!.SetValue(server.Session,
            HaManagement.Methods(request.Operation).ToStringArray().Where(permission => permission != revokedPermission).ToArray());
        var calls = server.Requests.Count;
        var error = Assert.Throws<InvalidOperationException>(() => new ShellHaAction(f.Connection, snapshot, request, review).RunSync(server.Session));
        Assert.Contains("permissions", error.Message);
        Assert.Equal(calls, server.Requests.Count);
        Assert.DoesNotContain(server.Requests, IsMutation);
    }

    [Theory]
    [InlineData(HaOperation.Enable, "Async.pool.enable_ha")]
    [InlineData(HaOperation.Configure, "Async.pool.sync_database")]
    [InlineData(HaOperation.Disable, "Async.pool.disable_ha")]
    public async Task SharedTaskSuccessCompletesWrapperAndCleansUpWithoutLoggingOutOrChangingCache(HaOperation operation, string expectedMutation)
    {
        var f = new Fixture(operation != HaOperation.Enable);
        using var server = new RpcServer(f) { MutationFailure = null };
        typeof(Session).GetProperty(nameof(Session.IsLocalSuperuser))!.SetValue(server.Session, false);
        typeof(Session).GetProperty(nameof(Session.Permissions))!.SetValue(server.Session,
            HaManagement.Methods(operation).ToStringArray());
        f.MarkConnected(server.Session);
        var snapshot = HaManagement.Capture(f.Pool);
        var request = operation == HaOperation.Disable ? new HaRequest(operation, snapshot.FailuresToTolerate, null, []) : f.Request(snapshot, operation);
        var review = await HaManagement.ReviewAsync(f.Connection, snapshot, request);
        Assert.True(review.CanApply, review.Error);
        var action = new ShellHaAction(f.Connection, snapshot, request, review);
        await Task.Run(() => action.RunSync(server.Session));
        Assert.True(action.Succeeded, action.Exception?.ToString());
        Assert.Contains(operation == HaOperation.Disable ? "disabled" : "applied", action.Description);
        Assert.Single(server.Requests, call => Method(call) == expectedMutation);
        Assert.Single(server.Requests, call => Method(call) == "task.get_record");
        Assert.Single(server.Requests, call => Method(call) == "task.destroy");
        Assert.Equal(2, server.Requests.Count(call => Method(call) == "task.remove_from_other_config"));
        Assert.Equal(2, server.Requests.Count(call => Method(call) == "task.add_to_other_config"));
        Assert.DoesNotContain(server.Requests, call => Method(call) == "session.logout");
        Assert.Equal(snapshot.Fingerprint, HaManagement.Capture(f.Pool).Fingerprint);
        Assert.Contains("task.remove_from_other_config", action.GetApiMethodsToRoleCheck.Select(method => method.Method));
    }

    [Fact]
    public async Task CancellationStopsFurtherReviewCallsWithoutLoggingOutBorrowedSession()
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        using var server = new RpcServer(f) { OnFirstRequest = cancellation.Cancel };
        f.MarkConnected(server.Session);
        var snapshot = HaManagement.Capture(f.Pool);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HaManagement.ReviewAsync(f.Connection, snapshot, f.Request(snapshot), cancellation.Token));
        Assert.Single(server.Requests);
        Assert.Equal("pool.get_record", Method(server.Requests[0]));
    }

    private static string Method(JObject request) => request["method"]!.Value<string>()!;
    private static bool IsMutation(JObject request) => Method(request).StartsWith("Async.pool.", StringComparison.Ordinal)
        || Method(request).Contains(".set_", StringComparison.Ordinal);

    private sealed class RpcServer : IDisposable
    {
        private readonly Fixture _fixture;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));
        private readonly List<JObject> _requests = [];
        private readonly Task _server;
        private bool _computed;
        public long Capacity { get; set; } = 1;
        public string? AgilityFailure { get; init; }
        public string? SrFailure { get; init; }
        public string? MutationFailure { get; init; } = "OPERATION_NOT_ALLOWED";
        public bool AddServerVgpuAfterCapacity { get; init; }
        public Action? OnFirstRequest { get; init; }
        public Session Session { get; }
        public IReadOnlyList<JObject> Requests { get { lock (_requests) return _requests.ToArray(); } }

        public RpcServer(Fixture fixture)
        {
            _fixture = fixture; _listener.Start();
            Session = new Session($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/")
                { APIVersion = API_Version.API_2_16, Timeout = 3000, opaque_ref = "OpaqueRef:ha-test-session", Connection = fixture.Connection };
            typeof(Session).GetProperty(nameof(Session.IsLocalSuperuser))!.SetValue(Session, true);
            _server = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_timeout.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_timeout.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    var length = 0;
                    while (await reader.ReadLineAsync(_timeout.Token) is { Length: > 0 } header)
                        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header.Split(':', 2)[1]);
                    var body = new char[length];
                    var offset = 0;
                    while (offset < length)
                    {
                        var read = await reader.ReadAsync(body.AsMemory(offset), _timeout.Token);
                        if (read == 0) throw new EndOfStreamException();
                        offset += read;
                    }
                    var request = JObject.Parse(new string(body));
                    lock (_requests) _requests.Add(request);
                    if (Requests.Count == 1) OnFirstRequest?.Invoke();
                    var method = Method(request);
                    var failure = method.StartsWith("Async.pool.", StringComparison.Ordinal) ? MutationFailure
                        : method == "VM.assert_agile" ? AgilityFailure : method == "SR.assert_can_host_ha_statefile" ? SrFailure : null;
                    var result = failure == null ? Reply(method) : null;
                    var response = new JObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone() };
                    if (failure == null) response["result"] = result;
                    else response["error"] = new JObject { ["code"] = 1, ["message"] = failure, ["data"] = new JArray("loopback regression") };
                    var bytes = Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _timeout.Token);
                    await stream.WriteAsync(bytes, _timeout.Token);
                }
            }
            catch (OperationCanceledException) when (_timeout.IsCancellationRequested) { }
        }

        private JToken? Reply(string method)
        {
            switch (method)
            {
                case "pool.get_record": return _fixture.Pool.ToJObject();
                case "host.get_all_records": return Map(_fixture.Hosts);
                case "host_metrics.get_all_records": return Map(_fixture.Metrics);
                case "VM.get_all_records":
                    var vms = Map(_fixture.Vms);
                    if (_computed && AddServerVgpuAfterCapacity) vms[_fixture.Vms[0].opaque_ref]!["VGPUs"] = new JArray("new-vgpu");
                    return vms;
                case "SR.get_all_records": return Map(new[] { _fixture.Sr });
                case "PBD.get_all_records": return Map(_fixture.Pbds);
                case "PIF.get_all_records": return Map(_fixture.Pifs);
                case "VDI.get_all_records": return Map(_fixture.Vdis);
                case "VBD.get_all_records": case "VIF.get_all_records": return new JObject();
                case "pool.ha_compute_hypothetical_max_host_failures_to_tolerate": _computed = true; return new JValue(Capacity);
                case "SR.assert_can_host_ha_statefile": case "VM.assert_agile": return null;
                case "VM.set_ha_restart_priority": case "VM.set_order": case "VM.set_start_delay": case "pool.set_ha_host_failures_to_tolerate": return null;
                case "Async.pool.enable_ha": case "Async.pool.sync_database": case "Async.pool.disable_ha": return new JValue("ha-task");
                case "task.remove_from_other_config": case "task.add_to_other_config": case "task.destroy": return null;
                case "task.get_allowed_operations": return new JArray();
                case "task.get_record": return new JObject { ["uuid"] = "task-uuid", ["status"] = "success", ["progress"] = 1.0,
                    ["result"] = "", ["error_info"] = new JArray() };
                default: throw new InvalidOperationException($"Unexpected RPC {method}");
            }
        }
        private static JObject Map<T>(IEnumerable<T> records) where T : XenObject<T>
        {
            var result = new JObject();
            foreach (var record in records) result[record.opaque_ref] = record.ToJObject();
            return result;
        }
        public void Dispose()
        {
            _timeout.Cancel(); _listener.Stop();
            try { _server.GetAwaiter().GetResult(); } catch (SocketException) when (_timeout.IsCancellationRequested) { }
            _timeout.Dispose();
        }
    }

    private sealed class Fixture
    {
        public XenConnection Connection { get; } = new();
        public Pool Pool { get; }
        public Host[] Hosts { get; }
        public Host_metrics[] Metrics { get; }
        public PIF[] Pifs { get; }
        public PBD[] Pbds { get; }
        public VM[] Vms { get; }
        public VDI[] Vdis { get; }
        public SR Sr { get; }

        public Fixture(bool enabled = false)
        {
            Metrics = Enumerable.Range(0, 2).Select(i => Add($"metrics{i}", new Host_metrics { uuid = $"metrics-uuid{i}", live = true })).ToArray();
            Hosts = Enumerable.Range(0, 2).Select(i => Add($"host{i}", new Host
            {
                uuid = $"host-uuid{i}", name_label = $"Host {i}", enabled = true, metrics = new($"metrics{i}"), address = $"192.0.2.{i + 10}",
                license_params = new() { ["enable_xha"] = "true" }, software_version = new() { ["product_version"] = "8.3.0" }
            })).ToArray();
            Pifs = Enumerable.Range(0, 2).Select(i => Add($"pif{i}", new PIF { uuid = $"pif-uuid{i}", host = new($"host{i}"), device = "eth0",
                managed = true, management = true, currently_attached = true, primary_address_type = primary_address_type.IPv4,
                ip_configuration_mode = ip_configuration_mode.Static, IP = $"192.0.2.{i + 10}", network = new("network") })).ToArray();
            foreach (var host in Hosts) host.PIFs = [new(Pifs.Single(pif => pif.host.opaque_ref == host.opaque_ref).opaque_ref)];
            Pbds = Enumerable.Range(0, 2).Select(i => Add($"pbd{i}", new PBD { uuid = $"pbd-uuid{i}", host = new($"host{i}"), SR = new("sr"), currently_attached = true })).ToArray();
            Sr = Add("sr", new SR { uuid = "sr-uuid", name_label = "Shared heartbeat", shared = true, type = "nfs", PBDs = Pbds.Select(pbd => new XenRef<PBD>(pbd.opaque_ref)).ToList() });
            Vdis = enabled ? [Add("statefile", new VDI { uuid = "statefile-uuid", SR = new("sr") })] : [];
            Pool = Add("pool", new Pool { uuid = "pool-uuid", name_label = "Pool", master = new("host0"), ha_enabled = enabled,
                ha_host_failures_to_tolerate = 0, ha_statefiles = enabled ? ["statefile"] : [] });
            Vms = Enumerable.Range(0, 2).Select(i => Add($"vm{i}", new VM { uuid = $"vm-uuid{i}", name_label = $"VM {i}",
                power_state = vm_power_state.Running, resident_on = new("host0"), order = 5, start_delay = 9,
                ha_restart_priority = i == 0 ? "" : "best-effort", other_config = i == 1 ? new() { ["HideFromXenCenter"] = "true" } : [] })).ToArray();
        }
        public HaRequest Request(HaSnapshot snapshot, HaOperation operation = HaOperation.Enable) => new(operation, 1,
            operation == HaOperation.Enable ? Sr.opaque_ref : null, snapshot.Vms.Select((vm, index) => new HaVmSetting(vm.Reference,
                index == 0 ? VM.HaRestartPriority.Restart : VM.HaRestartPriority.BestEffort, vm.Order, vm.StartDelay)).ToArray());
        public void MarkConnected(Session session)
        {
            var field = typeof(XenConnection).GetField("connectTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var state = Activator.CreateInstance(field.FieldType, BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: ["127.0.0.1", 1], culture: null)!;
            field.FieldType.GetField("Connected")!.SetValue(state, true);
            field.FieldType.GetField("Session")!.SetValue(state, session);
            field.SetValue(Connection, state);
        }
        private T Add<T>(string reference, T value) where T : XenObject<T>
        {
            Connection.Cache.UpdateFrom(Connection, [new ObjectChange(typeof(T), reference, value)]);
            return Connection.Resolve(new XenRef<T>(reference));
        }
    }
}
