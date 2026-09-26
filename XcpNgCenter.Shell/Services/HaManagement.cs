using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.Services;

public enum HaOperation { Enable, Configure, Disable }
public sealed record HaVmSetting(string Reference, VM.HaRestartPriority? Priority, long Order, long StartDelay);
public sealed record HaVmOption(string Reference, string Uuid, string Name, VM.HaRestartPriority? Priority,
    long Order, long StartDelay, string RawPriority);
public sealed record HaSrOption(string Reference, string Uuid, string Name, string? Error)
{
    public bool IsAvailable => Error == null;
}
public sealed record HaVmAgility(string Reference, bool IsAgile, string? Error);

/// <summary>An isolated draft; constructing or reviewing it never changes server settings.</summary>
public sealed class HaRequest
{
    public HaRequest(HaOperation operation, long failuresToTolerate, string? heartbeatSrReference,
        IReadOnlyList<HaVmSetting> vmSettings)
    {
        Operation = operation;
        FailuresToTolerate = failuresToTolerate;
        HeartbeatSrReference = heartbeatSrReference;
        VmSettings = Array.AsReadOnly(vmSettings.ToArray());
    }
    public HaOperation Operation { get; }
    public long FailuresToTolerate { get; }
    public string? HeartbeatSrReference { get; }
    public IReadOnlyList<HaVmSetting> VmSettings { get; }
}

/// <summary>Identity and configuration captured when the editor opens, including hidden real VMs.</summary>
public sealed class HaSnapshot
{
    public HaSnapshot(string poolReference, string poolUuid, string poolName, bool isEnabled, long failuresToTolerate,
        int hostCount, IReadOnlyList<HaVmOption> vms, IReadOnlyList<HaSrOption> heartbeatCandidates,
        string? currentHeartbeatSrReference = null)
    {
        PoolReference = poolReference; PoolUuid = poolUuid; PoolName = poolName; IsEnabled = isEnabled;
        FailuresToTolerate = failuresToTolerate; HostCount = hostCount;
        Vms = Array.AsReadOnly(vms.ToArray()); HeartbeatCandidates = Array.AsReadOnly(heartbeatCandidates.ToArray());
        CurrentHeartbeatSrReference = currentHeartbeatSrReference;
    }
    public string PoolReference { get; }
    public string PoolUuid { get; }
    public string PoolName { get; }
    public bool IsEnabled { get; }
    public long FailuresToTolerate { get; }
    public int HostCount { get; }
    public IReadOnlyList<HaVmOption> Vms { get; }
    public IReadOnlyList<HaSrOption> HeartbeatCandidates { get; }
    public string? CurrentHeartbeatSrReference { get; }
    internal string Fingerprint { get; init; } = "";
    internal string DisableFingerprint { get; init; } = "";
}

public sealed class HaReview
{
    public HaReview(HaRequest request, IReadOnlyList<HaSrOption> candidates, IReadOnlyList<HaVmAgility> agility,
        long? maxHostFailures, string? error)
    {
        RequestFingerprint = HaManagement.RequestFingerprint(request);
        HeartbeatCandidates = Array.AsReadOnly(candidates.ToArray()); VmAgility = Array.AsReadOnly(agility.ToArray());
        MaxHostFailures = maxHostFailures; Error = error;
    }
    public string RequestFingerprint { get; }
    public IReadOnlyList<HaSrOption> HeartbeatCandidates { get; }
    public IReadOnlyList<HaVmAgility> VmAgility { get; }
    public long? MaxHostFailures { get; }
    public string? Error { get; }
    public bool CanApply => Error == null;
    internal string SnapshotFingerprint { get; init; } = "";
}

/// <summary>Reviews HA prerequisites without enabling HA, changing VM policies, or modifying storage.</summary>
public static class HaManagement
{
    public static IReadOnlyList<VM.HaRestartPriority> Priorities { get; } = Array.AsReadOnly(new[]
        { VM.HaRestartPriority.Restart, VM.HaRestartPriority.BestEffort, VM.HaRestartPriority.DoNotRestart });

    public static HaSnapshot Capture(Pool pool)
    {
        RequireIdentity(pool.opaque_ref, pool.uuid, "pool");
        var inventory = HaInventory.FromCache(pool);
        var current = inventory.HeartbeatSrs();
        return new(pool.opaque_ref, pool.uuid, pool.Name(), pool.ha_enabled, pool.ha_host_failures_to_tolerate,
            inventory.Hosts.Length,
            inventory.Vms.Where(vm => vm.IsRealVm()).OrderBy(vm => vm.opaque_ref, StringComparer.Ordinal)
                .Select(vm => new HaVmOption(vm.opaque_ref, vm.uuid, vm.Name(), Priority(vm.ha_restart_priority),
                    vm.order, vm.start_delay, vm.ha_restart_priority)).ToArray(),
            Candidates(inventory), current.Count == 1 ? current[0] : null)
        { Fingerprint = inventory.Fingerprint(), DisableFingerprint = inventory.DisableFingerprint() };
    }

    public static string RequestFingerprint(HaRequest request) => Hash(new
    {
        request.Operation, request.FailuresToTolerate, request.HeartbeatSrReference,
        Vms = request.VmSettings.OrderBy(vm => vm.Reference, StringComparer.Ordinal)
    });

    public static Task<HaReview> ReviewAsync(IXenConnection connection, HaSnapshot snapshot, HaRequest request,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!connection.IsConnected) throw new InvalidOperationException("Reconnect the pool before reviewing HA.");
        // DuplicateSession borrows the same server login handle. It has a separate
        // transport but must not be logged out, which would disconnect the user.
        var session = connection.DuplicateSession(60000);
        return Review(connection, session, snapshot, request, cancellationToken);
    }, cancellationToken);

    internal static HaReview Review(IXenConnection connection, Session session, HaSnapshot snapshot, HaRequest request,
        CancellationToken cancellationToken = default)
    {
        var cached = RequireCurrent(connection, snapshot, request.Operation);
        RequireCachedDraft(cached, request);
        RequirePermissions(session, Methods(request.Operation));
        var inventory = HaInventory.FromServer(session, snapshot.PoolReference, request.Operation == HaOperation.Disable, cancellationToken);
        RequireSnapshot(inventory, snapshot, request.Operation);
        ValidatePool(inventory, request.Operation);
        if (request.Operation == HaOperation.Disable)
            return Reviewed(snapshot, request, [], [], null, null);

        ValidateRequest(inventory, request);
        var candidates = Candidates(inventory).ToList();
        for (var index = 0; index < candidates.Count; index++)
        {
            if (!candidates[index].IsAvailable) continue;
            cancellationToken.ThrowIfCancellationRequested();
            try { SR.assert_can_host_ha_statefile(session, candidates[index].Reference); }
            catch (Failure failure) when (!IsUnknownFailure(failure))
            { candidates[index] = candidates[index] with { Error = failure.Message }; }
        }
        var agility = new List<HaVmAgility>();
        foreach (var vm in request.VmSettings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { VM.assert_agile(session, vm.Reference); agility.Add(new(vm.Reference, true, null)); }
            catch (Failure failure) when (KnownAgilityFailure(failure))
            { agility.Add(new(vm.Reference, false, failure.Message)); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var priorities = request.VmSettings.ToDictionary(setting => cached.Vms.Single(vm => vm.opaque_ref == setting.Reference), setting => setting.Priority!.Value);
        // This helper intentionally excludes best-effort/unprotected VMs from
        // the hypothetical configuration, as required by xapi.
        var capacity = Pool.GetMaximumTolerableHostFailures(session, Helpers.GetVmHaRestartPrioritiesForApi(priorities));
        // Also enforce the freshly read HCI limit if it changed before cache delivery.
        if (inventory.Pool.other_config.TryGetValue("hci-limit-fault-tolerance", out var limitText))
        {
            if (!long.TryParse(limitText, out var limit) || limit < 0)
                throw new InvalidOperationException("The pool's HA failure limit is invalid. Review it with the server administrator.");
            capacity = Math.Min(capacity, limit);
        }
        cancellationToken.ThrowIfCancellationRequested();
        RequireCachedDraft(RequireCurrent(connection, snapshot, request.Operation), request);
        // Prerequisite scans can be lengthy. Re-read server state as well as the
        // cache so a delayed event cannot hide changes made during that scan.
        RequireSnapshot(HaInventory.FromServer(session, snapshot.PoolReference, false, cancellationToken), snapshot, request.Operation);
        string? error = capacity < 0 ? "The server could not establish HA failover capacity." :
            request.FailuresToTolerate > capacity ? $"The requested tolerance exceeds the server's current maximum of {capacity}." : null;
        var nonAgile = agility.Where(vm => !vm.IsAgile && request.VmSettings.Single(setting => setting.Reference == vm.Reference).Priority == VM.HaRestartPriority.Restart).ToArray();
        if (nonAgile.Length > 0) error = "Every VM with Restart protection must pass the server agility check. Review the VM reasons or select a different policy.";
        if (request.Operation == HaOperation.Enable)
        {
            if (string.IsNullOrWhiteSpace(request.HeartbeatSrReference)) error = "Select a heartbeat SR that passed the server review.";
            else if (candidates.SingleOrDefault(sr => sr.Reference == request.HeartbeatSrReference)?.IsAvailable != true)
                error = "The selected heartbeat SR did not pass the server review. Review its reason and select a suitable SR.";
        }
        else
        {
            var heartbeat = inventory.HeartbeatSrs();
            if (heartbeat.Count == 0 || heartbeat.Any(reference => candidates.SingleOrDefault(sr => sr.Reference == reference)?.IsAvailable != true))
                error = "The current HA heartbeat storage is missing, incomplete, or unsuitable. Repair it before changing protection settings.";
        }
        return Reviewed(snapshot, request, candidates, agility, capacity, error);
    }

    internal static HaInventory RequireCurrent(IXenConnection connection, HaSnapshot snapshot, HaOperation operation)
    {
        if (!connection.IsConnected) throw new InvalidOperationException("The pool disconnected. Reconnect and reopen the HA editor.");
        var pool = Helpers.GetPoolOfOne(connection);
        if (pool == null || pool.opaque_ref != snapshot.PoolReference || pool.uuid != snapshot.PoolUuid)
            throw new InvalidOperationException("The pool identity changed. Reopen the HA editor.");
        if (pool.Locked) throw new InvalidOperationException("Another pool action is in progress. Wait and review HA again.");
        var inventory = HaInventory.FromCache(pool);
        RequireSnapshot(inventory, snapshot, operation);
        ValidatePool(inventory, operation);
        return inventory;
    }

    internal static void RequireSnapshot(HaInventory inventory, HaSnapshot snapshot, HaOperation operation)
    {
        var expected = operation == HaOperation.Disable ? snapshot.DisableFingerprint : snapshot.Fingerprint;
        var actual = operation == HaOperation.Disable ? inventory.DisableFingerprint() : inventory.Fingerprint();
        if (expected.Length == 0 || expected != actual)
            throw new InvalidOperationException("The pool, hosts, storage, or VM configuration changed after the editor opened. Reopen it and review the current HA settings.");
    }

    private static HaReview Reviewed(HaSnapshot snapshot, HaRequest request, IReadOnlyList<HaSrOption> candidates,
        IReadOnlyList<HaVmAgility> agility, long? capacity, string? error)
        => new(request, candidates, agility, capacity, error)
        { SnapshotFingerprint = request.Operation == HaOperation.Disable ? snapshot.DisableFingerprint : snapshot.Fingerprint };

    internal static void ValidateRequest(HaInventory inventory, HaRequest request)
    {
        if (request.Operation is not (HaOperation.Enable or HaOperation.Configure)) throw new InvalidOperationException("Select a supported HA operation.");
        if (request.FailuresToTolerate < 0 || request.FailuresToTolerate >= inventory.Hosts.Length)
            throw new InvalidOperationException("Failure tolerance must be zero or greater and less than the number of pool hosts.");
        var real = inventory.Vms.Where(vm => vm.IsRealVm()).ToArray();
        if (request.VmSettings.Count != real.Length || request.VmSettings.Select(vm => vm.Reference).Distinct(StringComparer.Ordinal).Count() != real.Length
            || !request.VmSettings.Select(vm => vm.Reference).ToHashSet(StringComparer.Ordinal).SetEquals(real.Select(vm => vm.opaque_ref)))
            throw new InvalidOperationException("The HA draft must include every current real VM, including hidden VMs. Reopen the editor.");
        foreach (var setting in request.VmSettings)
        {
            var vm = real.Single(candidate => candidate.opaque_ref == setting.Reference);
            RequireIdentity(vm.opaque_ref, vm.uuid, "VM");
            if (setting.Priority == null || !Priorities.Contains(setting.Priority.Value))
                throw new InvalidOperationException($"Select a supported restart policy for '{vm.name_label}'. Its current policy is not changed automatically.");
            if (vm.Locked || vm.current_operations.Count > 0 || vm.power_state == vm_power_state.unknown)
                throw new InvalidOperationException($"VM '{vm.name_label}' is busy or has an unsupported power state. Review it before changing HA.");
            if (setting.Order != vm.order || setting.StartDelay != vm.start_delay)
                throw new InvalidOperationException("This HA editor preserves VM start order and delay. Reopen it after changing startup options elsewhere.");
            if (setting.Order < 0 || setting.StartDelay < 0) throw new InvalidOperationException("A VM has invalid startup options. Correct them before reviewing HA.");
        }
    }

    internal static void RequireCachedDraft(HaInventory inventory, HaRequest request)
    {
        if (request.Operation == HaOperation.Disable) return;
        ValidateRequest(inventory, request);
        IReadOnlyList<string> storage = request.Operation == HaOperation.Enable
            ? request.HeartbeatSrReference is { } reference ? [reference] : [] : inventory.HeartbeatSrs();
        if (storage.Any(reference => inventory.Srs.Any(sr => sr.opaque_ref == reference && sr.Locked)))
            throw new InvalidOperationException("Heartbeat storage is busy. Wait for its current action before reviewing HA.");
    }

    private static void ValidatePool(HaInventory inventory, HaOperation operation)
    {
        var pool = inventory.Pool;
        RequireIdentity(pool.opaque_ref, pool.uuid, "pool");
        if (pool.current_operations.Count > 0) throw new InvalidOperationException("The pool is busy. Wait for its current operation to finish.");
        if (operation == HaOperation.Disable)
        {
            if (!pool.ha_enabled) throw new InvalidOperationException("HA is already disabled. Reopen the editor.");
            return;
        }
        if (operation == HaOperation.Enable && pool.ha_enabled || operation == HaOperation.Configure && !pool.ha_enabled)
            throw new InvalidOperationException("The pool's HA state changed. Reopen the editor.");
        if (operation is not (HaOperation.Enable or HaOperation.Configure)) throw new InvalidOperationException("Select a supported HA operation.");
        if (inventory.Hosts.Length < 2 || inventory.Hosts.All(host => host.opaque_ref != pool.master.opaque_ref))
            throw new InvalidOperationException("HA setup requires at least two pool hosts and a known coordinator.");
        if (pool.RollingUpgrade() || pool.is_psr_pending)
            throw new InvalidOperationException("Complete the pool upgrade or secret rotation before changing HA protection.");
        if (inventory.Hosts.Select(host => host.software_version.GetValueOrDefault("platform_version", host.software_version.GetValueOrDefault("product_version", ""))).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new InvalidOperationException("All hosts must run matching platform versions before changing HA protection.");
        foreach (var host in inventory.Hosts)
        {
            RequireIdentity(host.opaque_ref, host.uuid, "host");
            if (Host.RestrictHA(host)) throw new InvalidOperationException($"HA is unsupported or restricted on '{host.name_label}'.");
            if (host.software_version.GetValueOrDefault("platform_version", host.software_version.GetValueOrDefault("product_version", "")).Length == 0)
                throw new InvalidOperationException("Host version information is incomplete. Refresh the pool.");
            if (host.Locked || !host.enabled || host.current_operations.Count > 0 || inventory.HostMetrics.SingleOrDefault(metrics => metrics.opaque_ref == host.metrics.opaque_ref)?.live != true)
                throw new InvalidOperationException($"The shell requires host '{host.name_label}' to be online, enabled, and idle before changing HA protection.");
            var management = inventory.Pifs.Where(pif => pif.host.opaque_ref == host.opaque_ref && pif.management).ToArray();
            if (management.Length != 1 || !management[0].currently_attached || !management[0].managed
                || management[0].primary_address_type is not (primary_address_type.IPv4 or primary_address_type.IPv6)
                || (management[0].primary_address_type == primary_address_type.IPv6
                    ? management[0].ipv6_configuration_mode != ipv6_configuration_mode.Static || management[0].IPv6.Length == 0
                    : management[0].ip_configuration_mode != ip_configuration_mode.Static || string.IsNullOrWhiteSpace(management[0].IP)))
                throw new InvalidOperationException($"Host '{host.name_label}' needs a connected, static management interface before changing HA protection.");
            RequireIdentity(management[0].opaque_ref, management[0].uuid, "management interface");
        }
        if (inventory.Pifs.Any(pif => pif.disallow_unplug && !pif.currently_attached))
            throw new InvalidOperationException("A protected physical interface is detached. Restore it before changing HA protection.");
    }

    private static IReadOnlyList<HaSrOption> Candidates(HaInventory inventory) => inventory.Srs
        .Where(sr => sr.shared && !sr.IsToolsSR()).OrderBy(sr => sr.opaque_ref, StringComparer.Ordinal)
        .Select(sr => new HaSrOption(sr.opaque_ref, sr.uuid, sr.name_label, SrError(inventory, sr))).ToArray();

    private static string? SrError(HaInventory inventory, SR sr)
    {
        if (string.IsNullOrWhiteSpace(sr.uuid) || sr.Locked || sr.current_operations.Count > 0) return "SR identity is incomplete or the SR is busy.";
        var pbds = inventory.Pbds.Where(pbd => pbd.SR.opaque_ref == sr.opaque_ref).ToArray();
        if (pbds.Length != inventory.Hosts.Length || sr.PBDs.Count != pbds.Length
            || !sr.PBDs.Select(reference => reference.opaque_ref).ToHashSet(StringComparer.Ordinal).SetEquals(pbds.Select(pbd => pbd.opaque_ref))
            || pbds.Any(pbd => !pbd.currently_attached || string.IsNullOrWhiteSpace(pbd.uuid))
            || !pbds.Select(pbd => pbd.host.opaque_ref).ToHashSet(StringComparer.Ordinal).SetEquals(inventory.Hosts.Select(host => host.opaque_ref)))
            return "The heartbeat SR must have one attached, fully identified storage connection on every pool host.";
        return null;
    }

    internal static RbacMethodList Methods(HaOperation operation)
    {
        var methods = new RbacMethodList(operation switch
        {
            HaOperation.Disable => ["pool.async_disable_ha"],
            HaOperation.Enable => ["pool.set_ha_host_failures_to_tolerate", "pool.async_enable_ha", "VM.set_ha_restart_priority", "VM.set_order", "VM.set_start_delay"],
            _ => ["pool.set_ha_host_failures_to_tolerate", "pool.async_sync_database", "VM.set_ha_restart_priority", "VM.set_order", "VM.set_start_delay"]
        });
        if (operation != HaOperation.Disable)
        {
            methods.AddRange("SR.assert_can_host_ha_statefile", "VM.assert_agile", "pool.ha_compute_hypothetical_max_host_failures_to_tolerate");
            methods.AddRange("host.get_all_records", "host_metrics.get_all_records", "VM.get_all_records", "SR.get_all_records",
                "PBD.get_all_records", "PIF.get_all_records", "VDI.get_all_records", "VBD.get_all_records", "VIF.get_all_records");
        }
        methods.AddRange("pool.get_record", "task.get_record", "task.get_allowed_operations");
        methods.AddRange(Role.CommonTaskApiList);
        // The shared RelatedTask setter removes these entries before adding them.
        methods.AddWithKey("task.remove_from_other_config", "XenCenterUUID");
        methods.AddWithKey("task.remove_from_other_config", "applies_to");
        methods.AddRange(Role.CommonSessionApiList);
        return methods;
    }

    internal static void RequirePermissions(Session session, RbacMethodList methods)
    {
        if (session.IsLocalSuperuser) return;
        var permissions = session.Permissions?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (permissions == null || methods.Any(method => !permissions.Contains(method.Method) && !permissions.Contains(method.ToString())
            && !permissions.Any(permission => permission.EndsWith('*') && method.ToString().StartsWith(permission[..^1], StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("The current session does not have all permissions required to review and apply this HA operation.");
    }

    private static VM.HaRestartPriority? Priority(string raw) => raw switch
    {
        "restart" => VM.HaRestartPriority.Restart, "best-effort" => VM.HaRestartPriority.BestEffort,
        "" => VM.HaRestartPriority.DoNotRestart, _ => null
    };
    internal static void RequireIdentity(string reference, string uuid, string kind)
    { if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(uuid)) throw new InvalidOperationException($"The {kind} identity is incomplete. Reconnect and reopen the HA editor."); }
    internal static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static bool IsUnknownFailure(Failure failure) => failure.ErrorDescription.Count == 0 || failure.ErrorDescription[0] is
        "SESSION_INVALID" or "RBAC_PERMISSION_DENIED" or "HANDLE_INVALID" or "MESSAGE_METHOD_UNKNOWN" or "INTERNAL_ERROR";
    private static bool KnownAgilityFailure(Failure failure) => failure.ErrorDescription.Count > 0 && failure.ErrorDescription[0] is
        "VM_REQUIRES_SR" or "VM_REQUIRES_NETWORK" or "VM_HAS_VGPU" or "VM_HAS_PCI_ATTACHED" or "VM_HAS_VUSBS" or "VM_HAS_VUSB"
        or "VM_HAS_SRIOV_VIF" or "VM_REQUIRES_GPU" or "VM_HAS_CHECKPOINT" or "HA_CONSTRAINT_VIOLATION_SR_NOT_SHARED"
        or "HA_CONSTRAINT_VIOLATION_NETWORK_NOT_SHARED" or "HA_CONSTRAINT_VIOLATION_NON_AGILE";
}

/// <summary>Only nonsecret HA-relevant fields are compared; live capacity is recomputed by xapi.</summary>
internal sealed class HaInventory
{
    public required Pool Pool { get; init; }
    public Host[] Hosts { get; init; } = [];
    public Host_metrics[] HostMetrics { get; init; } = [];
    public VM[] Vms { get; init; } = [];
    public SR[] Srs { get; init; } = [];
    public PBD[] Pbds { get; init; } = [];
    public PIF[] Pifs { get; init; } = [];
    public VDI[] Vdis { get; init; } = [];
    public VBD[] Vbds { get; init; } = [];
    public VIF[] Vifs { get; init; } = [];

    public static HaInventory FromCache(Pool pool)
    {
        T[] All<T>() where T : XenObject<T>
        {
            var values = new List<T>(); pool.Connection.Cache.AddAll(values, _ => true);
            return values.OrderBy(value => value.opaque_ref, StringComparer.Ordinal).ToArray();
        }
        return new() { Pool = pool, Hosts = All<Host>(), HostMetrics = All<Host_metrics>(), Vms = All<VM>(), Srs = All<SR>(),
            Pbds = All<PBD>(), Pifs = All<PIF>(), Vdis = All<VDI>(), Vbds = All<VBD>(), Vifs = All<VIF>() };
    }

    public static HaInventory FromServer(Session session, string poolReference, bool disable, CancellationToken token)
    {
        T[] Read<T>(Func<Session, Dictionary<XenRef<T>, T>> read) where T : XenObject<T>
        {
            token.ThrowIfCancellationRequested();
            return read(session).Select(pair => { pair.Value.opaque_ref = pair.Key.opaque_ref; return pair.Value; })
                .OrderBy(value => value.opaque_ref, StringComparer.Ordinal).ToArray();
        }
        token.ThrowIfCancellationRequested();
        var pool = Pool.get_record(session, poolReference); pool.opaque_ref = poolReference;
        if (disable) return new() { Pool = pool };
        return new() { Pool = pool, Hosts = Read(Host.get_all_records), HostMetrics = Read(Host_metrics.get_all_records),
            Vms = Read(VM.get_all_records), Srs = Read(SR.get_all_records), Pbds = Read(PBD.get_all_records), Pifs = Read(PIF.get_all_records),
            Vdis = Read(VDI.get_all_records), Vbds = Read(VBD.get_all_records), Vifs = Read(VIF.get_all_records) };
    }

    public IReadOnlyList<string> HeartbeatSrs()
    {
        var result = new List<string>();
        foreach (var reference in Pool.ha_statefiles)
        {
            var vdi = Vdis.SingleOrDefault(value => value.opaque_ref == reference);
            if (vdi == null || string.IsNullOrWhiteSpace(vdi.uuid) || Srs.All(sr => sr.opaque_ref != vdi.SR.opaque_ref)) return [];
            result.Add(vdi.SR.opaque_ref);
        }
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }
    public string DisableFingerprint() => HaManagement.Hash(new { Pool.opaque_ref, Pool.uuid, Pool.ha_enabled,
        Coordinator = Pool.master.opaque_ref, Pool.ha_host_failures_to_tolerate, Statefiles = Pool.ha_statefiles.Order(StringComparer.Ordinal) });
    public string Fingerprint() => HaManagement.Hash(new
    {
        PoolState = DisableFingerprint(), Pool.is_psr_pending,
        PoolConfig = Pool.other_config.Where(pair => pair.Key is "hci-limit-fault-tolerance" or "rolling_upgrade_in_progress").OrderBy(pair => pair.Key),
        HaConfiguration = Pool.ha_configuration.OrderBy(pair => pair.Key), PoolOperations = Pool.current_operations.OrderBy(pair => pair.Key),
        Hosts = Hosts.Select(host => new { host.opaque_ref, host.uuid, host.enabled, host.address, Metrics = host.metrics.opaque_ref,
            Live = HostMetrics.SingleOrDefault(metrics => metrics.opaque_ref == host.metrics.opaque_ref)?.live,
            HaLicensed = host.license_params.GetValueOrDefault("enable_xha"), Version = host.software_version.GetValueOrDefault("platform_version", host.software_version.GetValueOrDefault("product_version", "")),
            Pifs = Refs(host.PIFs), Operations = host.current_operations.OrderBy(pair => pair.Key) }),
        Vms = Vms.Where(vm => vm.IsRealVm()).Select(vm => new { vm.opaque_ref, vm.uuid, vm.ha_restart_priority, vm.ha_always_run, vm.order, vm.start_delay,
            vm.power_state, Resident = vm.resident_on.opaque_ref, Affinity = vm.affinity.opaque_ref, vm.memory_static_max, vm.memory_dynamic_min, vm.memory_dynamic_max,
            Vbds = Refs(vm.VBDs), Vifs = Refs(vm.VIFs), Vgpus = Refs(vm.VGPUs), Vusbs = Refs(vm.VUSBs), Pcis = Refs(vm.attached_PCIs),
            Operations = vm.current_operations.OrderBy(pair => pair.Key) }),
        Srs = Srs.Select(sr => new { sr.opaque_ref, sr.uuid, sr.shared, sr.type, sr.is_tools_sr, Pbds = Refs(sr.PBDs), Operations = sr.current_operations.OrderBy(pair => pair.Key) }),
        Pbds = Pbds.Select(pbd => new { pbd.opaque_ref, pbd.uuid, Sr = pbd.SR.opaque_ref, Host = pbd.host.opaque_ref, pbd.currently_attached }),
        Pifs = Pifs.Select(pif => new { pif.opaque_ref, pif.uuid, Host = pif.host.opaque_ref, Network = pif.network.opaque_ref, pif.device, pif.management,
            pif.managed, pif.primary_address_type, pif.ip_configuration_mode, pif.IP, pif.netmask, pif.gateway, pif.ipv6_configuration_mode,
            Addresses = pif.IPv6.Order(StringComparer.Ordinal), pif.ipv6_gateway, pif.currently_attached, pif.disallow_unplug,
            BondSlave = pif.bond_slave_of.opaque_ref, BondMasters = Refs(pif.bond_master_of),
            SriovPhysical = Refs(pif.sriov_physical_PIF_of), SriovLogical = Refs(pif.sriov_logical_PIF_of) }),
        Vdis = Vdis.Select(vdi => new { vdi.opaque_ref, vdi.uuid, Sr = vdi.SR.opaque_ref, vdi.sharable, vdi.read_only }),
        Vbds = Vbds.Select(vbd => new { vbd.opaque_ref, vbd.uuid, Vm = vbd.VM.opaque_ref, Vdi = vbd.VDI.opaque_ref, vbd.type, vbd.empty, vbd.currently_attached }),
        Vifs = Vifs.Select(vif => new { vif.opaque_ref, vif.uuid, Vm = vif.VM.opaque_ref, Network = vif.network.opaque_ref, vif.currently_attached })
    });
    private static string[] Refs<T>(IEnumerable<XenRef<T>> refs) where T : XenObject<T> => refs.Select(reference => reference.opaque_ref).Order(StringComparer.Ordinal).ToArray();
}
