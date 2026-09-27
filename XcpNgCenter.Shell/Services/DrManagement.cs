using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XenAdmin.Actions.DR;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using Task = System.Threading.Tasks.Task;
using Network = XenAPI.Network;

namespace XcpNgCenter.Shell.Services;

/// <summary>Metadata recovery on existing replicated storage. Every operation remains bound to its original pool.</summary>
public sealed partial class DrManagement : IDrWorkflow
{
    private readonly IXenConnection _connection;
    private readonly string _poolReference;
    private readonly string _poolUuid;
    private readonly Action<string>? _status;
    public string PoolName { get; }
    public const string RecoveryNotice = "The last operation may have partly completed or its response may have been lost. Inspect the destination VMs, NICs and disks before another recovery. No automatic retry, rollback, power-on or disk deletion was performed.";

    public DrManagement(Pool pool, Action<string>? status = null)
    {
        _connection = pool.Connection;
        RequireIdentity(pool.opaque_ref, pool.uuid);
        _poolReference = pool.opaque_ref; _poolUuid = pool.uuid; PoolName = pool.Name(); _status = status;
    }

    public Task<IReadOnlyList<DrMetadataOption>> DiscoverAsync() => Task.Run<IReadOnlyList<DrMetadataOption>>(() =>
    {
        RequirePool();
        var session = _connection.DuplicateSession(60000);
        RequirePermissions(session, ReadMethods());
        var action = new GetMetadataVDIsAction(_connection, null);
        action.RunSync(session);
        return action.VDIs.OrderBy(vdi => vdi.Name()).Select(vdi => new DrMetadataOption(vdi.opaque_ref,
            vdi.uuid, vdi.Name(), _connection.Resolve(vdi.SR)?.Name() ?? vdi.SR.opaque_ref)).ToArray();
    });

    public Task<DrInspection> InspectAsync(DrMetadataOption metadata) => Task.Run(() =>
    {
        RequirePool();
        var session = _connection.DuplicateSession(60000);
        RequirePermissions(session, ReadMethods());
        return WithMetadata(session, metadata, source => Inspect(metadata, source, ReadTarget(session)));
    });

    public Task<DrReview> ReviewAsync(DrInspection inspection, DrRequest request) => Task.Run(() =>
    {
        RequirePool();
        var session = _connection.DuplicateSession(60000);
        RequirePermissions(session, RecoveryMethods());
        return WithMetadata(session, inspection.Metadata, source => Review(session, inspection, request, source));
    });

    public async Task<DrOutcome> RecoverAsync(DrInspection inspection, DrRequest request, DrReview review)
    {
        var action = new DrRecoveryAction(_connection, this, inspection, request, review);
        await ShellActionRunner.RunAndWaitAsync(action, _status);
        return action.Outcome ?? new(request.Mode, [], [], action.Exception?.Message ?? "Recovery did not run.");
    }

    public async Task<DrOutcome> CleanupAsync(DrOutcome outcome)
    {
        var action = new DrCleanupAction(_connection, this, outcome);
        await ShellActionRunner.RunAndWaitAsync(action, _status);
        return action.Outcome ?? new(outcome.Mode, [], outcome.Cleanup, action.Exception?.Message ?? "Cleanup did not run.");
    }

    internal DrInspection Inspect(DrMetadataOption metadata, DrSource source, DrTarget target)
    {
        RequireIdentity(source.Pool.opaque_ref, source.Pool.uuid);
        if (source.Pool.uuid == _poolUuid)
            throw new InvalidOperationException("This metadata belongs to the destination pool. Select a different recovery pool.");
        return new(metadata, string.IsNullOrWhiteSpace(source.Pool.Name()) ? source.Pool.uuid : source.Pool.Name(), source.Pool.uuid,
            source.Vms.Values.Where(vm => vm.IsRealVm()).OrderBy(vm => vm.Name()).Select(vm =>
            {
                RequireIdentity(vm.opaque_ref, vm.uuid);
                var vbds = vm.VBDs.Select(reference => Require(source.Vbds, reference.opaque_ref, "source disk attachment")).ToArray();
                var vifs = vm.VIFs.Select(reference => Require(source.Vifs, reference.opaque_ref, "source NIC")).ToArray();
                var disks = vbds.Where(vbd => !vbd.empty && vbd.type == vbd_type.Disk)
                    .Select(vbd => Require(source.Vdis, vbd.VDI.opaque_ref, "source disk")).ToArray();
                string? reason = HasAutomaticPowerOn(vm) ? "This VM has automatic power-on enabled. Disable it in the source and refresh the recovery metadata before using this halted workflow." :
                    vm.power_state == vm_power_state.Suspended ? "Suspended VMs preserve suspended state during recovery. Use the existing WinForms recovery workflow to inspect their saved state." :
                    vm.power_state == vm_power_state.unknown ? "This metadata has an unknown VM power state; inspect it with the server administrator." :
                    vm.snapshots.Count > 0 ? "Snapshot chains require the existing WinForms recovery wizard." :
                    vm.IsAssignedToVapp() ? "Appliance members require the existing WinForms appliance recovery workflow." :
                    vm.VTPMs.Count > 0 || vm.VGPUs.Count > 0 || vm.VUSBs.Count > 0 || vm.attached_PCIs.Count > 0
                        ? "This VM uses hardware or security devices that require a dedicated recovery procedure." : null;
                return new DrVmOption(vm.opaque_ref, vm.uuid, vm.Name(), reason,
                    Array.AsReadOnly(disks.Select(vdi => vdi.SR.opaque_ref).Distinct().ToArray()),
                    Array.AsReadOnly(vifs.Select(vif => vif.network.opaque_ref).Distinct().ToArray()));
            }).ToArray(),
            source.Srs.Values.Select(sr => new DrSourceStorage(sr.opaque_ref, sr.uuid, sr.Name())).ToArray(),
            source.Networks.Values.Select(network => new DrSourceNetwork(network.opaque_ref, network.uuid, network.Name())).ToArray(),
            target.Srs.Values.Where(sr => sr.shared && sr.content_type != "iso").Select(sr => new DrStorageOption(sr.opaque_ref, sr.uuid, sr.Name())).ToArray(),
            target.Networks.Values.Select(network => new DrNetworkOption(network.opaque_ref, network.uuid, network.Name(),
                network.PIFs.Count == 0 && network.VIFs.Count == 0, network.PIFs.Count > 0)).ToArray()) { Fingerprint = source.Fingerprint };
    }

    internal static void ValidateRequest(DrInspection inspection, DrRequest request)
    {
        if (request.Mode is not (DrMode.Recovery or DrMode.MetadataRehearsal)) throw new InvalidOperationException("Select a supported recovery mode.");
        if (request.VmReferences.Count == 0 || request.VmReferences.Distinct().Count() != request.VmReferences.Count)
            throw new InvalidOperationException("Select one or more distinct VMs to recover.");
        var vms = request.VmReferences.Select(reference => inspection.Vms.SingleOrDefault(vm => vm.Reference == reference)
            ?? throw new InvalidOperationException("A selected VM is not part of the inspected metadata.")).ToArray();
        if (vms.Any(vm => vm.UnavailableReason != null)) throw new InvalidOperationException("A selected VM needs a recovery procedure that this editor cannot perform.");
        var storage = vms.SelectMany(vm => vm.StorageReferences).ToHashSet(StringComparer.Ordinal);
        var networks = vms.SelectMany(vm => vm.NetworkReferences).ToHashSet(StringComparer.Ordinal);
        if (request.Storage.Count != storage.Count || !storage.SetEquals(request.Storage.Select(map => map.SourceReference))
            || request.Networks.Count != networks.Count || !networks.SetEquals(request.Networks.Select(map => map.SourceReference)))
            throw new InvalidOperationException("Map every required storage repository and network exactly once.");
        foreach (var map in request.Storage)
        {
            var source = inspection.SourceStorage.Single(sr => sr.Reference == map.SourceReference);
            RequireIdentity(source.Reference, source.Uuid);
            var target = inspection.TargetStorage.SingleOrDefault(sr => sr.Reference == map.TargetReference);
            if (target == null || target.Uuid != source.Uuid)
                throw new InvalidOperationException($"Storage '{source.Name}' needs its attached replicated SR with UUID {source.Uuid}. Recovery cannot copy disks or remap to an unrelated SR.");
        }
        foreach (var map in request.Networks)
        {
            var target = inspection.TargetNetworks.SingleOrDefault(network => network.Reference == map.TargetReference);
            if (target == null) throw new InvalidOperationException("Select an existing target for every required network.");
            if (request.Mode == DrMode.MetadataRehearsal && (!target.IsIsolated || target.HasPhysicalInterfaces))
                throw new InvalidOperationException("Metadata rehearsal requires empty internal networks without physical interfaces or existing VMs.");
        }
    }

    internal DrReview Review(Session session, DrInspection inspection, DrRequest request, DrSource source)
    {
        RequirePool();
        ValidateRequest(inspection, request);
        if (inspection.Fingerprint.Length == 0 || inspection.Fingerprint != source.Fingerprint)
            throw new InvalidOperationException("The source metadata changed. Inspect it again and review a new draft.");
        var target = ReadTarget(session);
        ValidateTarget(inspection, request, target);
        foreach (var reference in request.VmReferences)
        {
            RequireManualPowerOn(source.Vms[reference]);
            ResolveDiskBindings(source, source.Vms[reference], request, target);
        }
        foreach (var reference in request.VmReferences)
            VM.assert_can_be_recovered(source.Session, reference, session.opaque_ref);
        return new(request.Fingerprint, source.Fingerprint, TargetFingerprint(inspection, request, target),
            $"{request.VmReferences.Count} VM(s) can be restored halted to {PoolName}. Replicated storage identities and NIC destinations passed review. Recovery does not copy disks. "
            + (request.Mode == DrMode.MetadataRehearsal
                ? "Metadata rehearsal creates temporary VM records on isolated networks and never boots them. It does not validate guest boot, disk replication or failover."
                : "Fence the original workloads and confirm replicated disks are ready before starting the recovered VMs separately."))
        {
            MappingSummary = DescribeMappings(inspection, request,
                target.Srs.Values.Select(sr => new DrStorageOption(sr.opaque_ref, sr.uuid, sr.Name())).ToArray(),
                target.Networks.Values.Select(network => new DrNetworkOption(network.opaque_ref, network.uuid, network.Name(),
                    network.PIFs.Count == 0 && network.VIFs.Count == 0, network.PIFs.Count > 0)).ToArray())
        };
    }

    internal static string DescribeMappings(DrInspection inspection, DrRequest request,
        IReadOnlyList<DrStorageOption>? storage = null, IReadOnlyList<DrNetworkOption>? networks = null)
    {
        ValidateRequest(inspection, request);
        storage ??= inspection.TargetStorage; networks ??= inspection.TargetNetworks;
        var lines = new List<string>();
        foreach (var reference in request.VmReferences)
        {
            var vm = inspection.Vms.Single(candidate => candidate.Reference == reference);
            lines.Add($"{vm.Name} [{vm.Uuid}]");
            foreach (var sourceReference in vm.StorageReferences)
            {
                var source = inspection.SourceStorage.Single(sr => sr.Reference == sourceReference);
                var mapping = request.Storage.Single(map => map.SourceReference == sourceReference);
                var target = storage.SingleOrDefault(sr => sr.Reference == mapping.TargetReference)
                    ?? throw new InvalidOperationException("A reviewed storage target is missing. Inspect and review again.");
                if (target.Uuid != source.Uuid) throw new InvalidOperationException("A reviewed storage identity changed. Inspect and review again.");
                lines.Add($"  Storage: {source.Name} [{source.Uuid}] -> {target.Name} [{target.Uuid}]");
            }
            foreach (var sourceReference in vm.NetworkReferences)
            {
                var source = inspection.SourceNetworks.Single(network => network.Reference == sourceReference);
                var mapping = request.Networks.Single(map => map.SourceReference == sourceReference);
                var target = networks.SingleOrDefault(network => network.Reference == mapping.TargetReference)
                    ?? throw new InvalidOperationException("A reviewed network target is missing. Inspect and review again.");
                if (target.Uuid != inspection.TargetNetworks.Single(network => network.Reference == mapping.TargetReference).Uuid)
                    throw new InvalidOperationException("A reviewed network identity changed. Inspect and review again.");
                var isolation = target.HasPhysicalInterfaces ? "has physical interfaces; not isolated"
                    : target.IsIsolated ? "isolated: no physical interfaces or existing VM NICs" : "internal network with existing VM NICs; not isolated";
                lines.Add($"  NIC network: {source.Name} [{source.Uuid}] -> {target.Name} [{target.Uuid}] ({isolation})");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void ValidateTarget(DrInspection inspection, DrRequest request, DrTarget target)
    {
        if (target.Pool.uuid != _poolUuid || target.Pool.opaque_ref != _poolReference)
            throw new InvalidOperationException("The destination pool identity changed. Reopen recovery.");
        if (target.Pool.ha_enabled) throw new InvalidOperationException("Disable destination pool HA before disaster recovery.");
        if (target.Pool.current_operations.Count > 0) throw new InvalidOperationException("A destination pool operation is in progress.");
        var selected = request.VmReferences.Select(reference => inspection.Vms.Single(vm => vm.Reference == reference)).ToArray();
        if (selected.Any(vm => target.Vms.Values.Any(existing => existing.uuid == vm.Uuid)))
            throw new InvalidOperationException("A selected VM UUID already exists in the destination. Recovery never approves replacing existing VMs.");
        foreach (var map in request.Storage)
        {
            var expected = inspection.TargetStorage.Single(sr => sr.Reference == map.TargetReference);
            var actual = Require(target.Srs, map.TargetReference, "replicated target storage");
            RequireIdentity(actual.opaque_ref, actual.uuid);
            if (actual.introduced_by?.opaque_ref is { Length: > 0 } introduced && introduced != "OpaqueRef:NULL")
                throw new InvalidOperationException("A selected SR is still owned by a DR task. Complete its permanent attachment with the existing WinForms recovery workflow before using this editor.");
            var pbds = actual.PBDs.Select(reference => Require(target.Pbds, reference.opaque_ref, "storage connection")).ToArray();
            if (actual.uuid != expected.Uuid || !actual.shared || actual.current_operations.Count > 0 || pbds.Length == 0 || pbds.Any(pbd => !pbd.currently_attached))
                throw new InvalidOperationException($"Replicated storage '{expected.Name}' changed, is busy, or is detached. Inspect storage and review again.");
        }
        foreach (var map in request.Networks)
        {
            var expected = inspection.TargetNetworks.Single(network => network.Reference == map.TargetReference);
            var actual = Require(target.Networks, map.TargetReference, "target network");
            RequireIdentity(actual.opaque_ref, actual.uuid);
            if (actual.uuid != expected.Uuid || actual.current_operations.Count > 0)
                throw new InvalidOperationException("A target network changed or is busy. Inspect and review again.");
            if (request.Mode == DrMode.MetadataRehearsal && (actual.PIFs.Count != 0 || actual.VIFs.Count != 0))
                throw new InvalidOperationException("A rehearsal network is no longer empty and isolated. Inspect and review again.");
        }
    }

    private static string TargetFingerprint(DrInspection inspection, DrRequest request, DrTarget target) => Hash(new
    {
        Pool = new { target.Pool.uuid, target.Pool.ha_enabled },
        Storage = request.Storage.OrderBy(map => map.TargetReference, StringComparer.Ordinal).Select(map =>
            StorageIdentity(target.Srs[map.TargetReference], target.Pbds)),
        Networks = request.Networks.OrderBy(map => map.TargetReference, StringComparer.Ordinal).Select(map => NetworkIdentity(target.Networks[map.TargetReference])),
        Disks = target.Vdis.Values.Where(vdi => request.Storage.Any(map => map.TargetReference == vdi.SR.opaque_ref))
            .OrderBy(vdi => vdi.opaque_ref, StringComparer.Ordinal).Select(DiskIdentity),
        Existing = target.Vms.Values.Where(vm => inspection.Vms.Any(source => source.Uuid == vm.uuid)).Select(vm => vm.uuid).OrderBy(uuid => uuid, StringComparer.Ordinal)
    });

    // Capacity/accounting, unrelated VDI membership and SR annotations do not identify the reviewed replica.
    // Backend/attachment configuration and every relevant network setting remain part of approval.
    private static object StorageIdentity(SR sr, IReadOnlyDictionary<string, PBD> pbds) => new
    {
        sr.opaque_ref, sr.uuid, sr.name_label, sr.type, sr.content_type, sr.shared, sr.clustered, sr.local_cache_enabled,
        IntroducedBy = sr.introduced_by?.opaque_ref, sr.sm_config,
        Pbds = sr.PBDs.OrderBy(reference => reference.opaque_ref, StringComparer.Ordinal).Select(reference =>
        {
            var pbd = pbds[reference.opaque_ref];
            return new { pbd.opaque_ref, pbd.uuid, Host = pbd.host.opaque_ref, Sr = pbd.SR.opaque_ref, pbd.currently_attached, pbd.device_config, pbd.other_config };
        })
    };

    private static object NetworkIdentity(Network network) => new
    {
        network.opaque_ref, network.uuid, network.name_label, network.bridge, network.MTU, network.managed,
        network.default_locking_mode, network.other_config,
        Purpose = network.purpose.OrderBy(purpose => purpose),
        Pifs = network.PIFs.Select(reference => reference.opaque_ref).OrderBy(reference => reference, StringComparer.Ordinal),
        Vifs = network.VIFs.Select(reference => reference.opaque_ref).OrderBy(reference => reference, StringComparer.Ordinal)
    };

    private DrTarget ReadTarget(Session session)
    {
        var pool = Pool.get_record(session, _poolReference); pool.opaque_ref = _poolReference;
        return new(pool, Records(SR.get_all_records(session)), Records(Network.get_all_records(session)),
            Records(PBD.get_all_records(session)), Records(VM.get_all_records(session)), Records(VDI.get_all_records(session)));
    }

    private Pool RequirePool()
    {
        if (!_connection.IsConnected) throw new InvalidOperationException("Reconnect the destination pool before recovery.");
        var pool = Helpers.GetPoolOfOne(_connection);
        if (pool == null || pool.opaque_ref != _poolReference || pool.uuid != _poolUuid)
            throw new InvalidOperationException("The destination pool changed. Reopen disaster recovery.");
        if (pool.Locked) throw new InvalidOperationException("Another pool action is in progress.");
        return pool;
    }

    private T WithMetadata<T>(Session session, DrMetadataOption metadata, Func<DrSource, T> work)
    {
        RequirePool();
        var cached = _connection.Resolve(new XenRef<VDI>(metadata.Reference));
        if (cached == null || cached.uuid != metadata.Uuid) throw new InvalidOperationException("The metadata VDI changed or disappeared. Discover metadata again.");
        var current = VDI.get_record(session, metadata.Reference);
        if (current.uuid != metadata.Uuid || current.type != vdi_type.metadata || !current.metadata_latest)
            throw new InvalidOperationException("The selected VDI is no longer current recovery metadata. Discover metadata again.");
        var open = new VdiOpenDatabaseAction(_connection, cached);
        Exception? failure = null;
        try
        {
            open.RunSync(session);
            var database = open.MetadataSession ?? throw new InvalidOperationException("The server did not open the metadata database.");
            database.Timeout = 60000;
            var pools = Records(Pool.get_all_records(database));
            if (pools.Count != 1) throw new InvalidOperationException("The metadata does not identify one source pool.");
            var source = new DrSource(database, pools.Values.Single(), Records(VM.get_all_records(database)),
                Records(SR.get_all_records(database)), Records(Network.get_all_records(database)), Records(VDI.get_all_records(database)),
                Records(VBD.get_all_records(database)), Records(VIF.get_all_records(database)));
            return work(source);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            // A failure after open_database but before Session.get_record must also release the metadata session.
            if (open.MetadataSessionRef is { } reference && !string.IsNullOrEmpty(reference.opaque_ref) && reference.opaque_ref != "OpaqueRef:NULL")
            {
                try { session.logout(reference.opaque_ref); }
                catch (Exception error)
                {
                    throw new InvalidOperationException((failure == null ? "" : failure.Message + " ")
                        + "The metadata session could not be closed. It may remain open until server expiry. " + error.Message, failure ?? error);
                }
            }
        }
    }

    internal static Dictionary<string, T> Records<T>(Dictionary<XenRef<T>, T> records) where T : XenObject<T>
    {
        foreach (var pair in records) pair.Value.opaque_ref = pair.Key.opaque_ref;
        return records.ToDictionary(pair => pair.Key.opaque_ref, pair => pair.Value, StringComparer.Ordinal);
    }
    private static T Require<T>(IReadOnlyDictionary<string, T> records, string reference, string kind) =>
        records.TryGetValue(reference, out var item) ? item : throw new InvalidOperationException($"Missing {kind}; inspect metadata and inventory again.");
    private static void RequireIdentity(string reference, string uuid)
    { if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(uuid)) throw new InvalidOperationException("Recovery requires complete object identities."); }
    internal static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson(JToken.FromObject(value)).ToString(Formatting.None))));
    private static JToken CanonicalJson(JToken value) => value switch
    {
        JObject obj => new JObject(obj.Properties().OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => new JProperty(property.Name, CanonicalJson(property.Value)))),
        JArray array => new JArray(array.Select(CanonicalJson)),
        _ => value.DeepClone()
    };

    internal static RbacMethodList ReadMethods() => new(["pool.get_record", "pool.get_all_records", "VDI.get_record", "VDI.open_database",
        "session.get_record", "session.logout", "VM.get_all_records", "SR.get_all_records", "network.get_all_records",
        "PBD.get_all_records", "VDI.get_all_records", "VBD.get_all_records", "VIF.get_all_records", "host.get_record"]);

    internal static RbacMethodList RecoveryMethods()
    {
        var methods = ReadMethods();
        methods.AddRange("VM.assert_can_be_recovered", "VM.async_recover", "VM.get_by_uuid", "VM.get_record", "VIF.get_record", "VIF.move", "VBD.get_record",
            "network.get_record", "task.get_record", "task.get_allowed_operations");
        methods.AddRange(Role.CommonTaskApiList); methods.AddRange(Role.CommonSessionApiList);
        methods.AddWithKey("task.remove_from_other_config", "XenCenterUUID"); methods.AddWithKey("task.remove_from_other_config", "applies_to");
        return methods;
    }
    internal static RbacMethodList CleanupMethods() => new(["pool.get_record", "VM.get_record", "VBD.get_record", "VIF.get_record", "VM.destroy", "VBD.destroy", "VIF.destroy"]);
    internal static void RequirePermissions(Session session, RbacMethodList methods)
    {
        if (Session.get_is_local_superuser(session, session.opaque_ref)) return;
        var permissions = Session.get_rbac_permissions(session, session.opaque_ref)?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (permissions == null || methods.Any(method => !permissions.Contains(method.Method) && !permissions.Contains(method.ToString())
            && !permissions.Any(permission => permission.EndsWith('*') && method.ToString().StartsWith(permission[..^1], StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Your session does not have all permissions required for this disaster recovery operation.");
    }

    internal sealed record DrTarget(Pool Pool, Dictionary<string, SR> Srs, Dictionary<string, Network> Networks,
        Dictionary<string, PBD> Pbds, Dictionary<string, VM> Vms, Dictionary<string, VDI> Vdis);

    private static object DiskIdentity(VDI vdi) => new { vdi.opaque_ref, vdi.uuid, Sr = vdi.SR.opaque_ref, vdi.location, vdi.type, vdi.managed, vdi.read_only, vdi.missing, vdi.virtual_size };

    private static Dictionary<string, VDI> ResolveDiskBindings(DrSource source, VM vm, DrRequest request, DrTarget target)
    {
        var bindings = new Dictionary<string, VDI>(StringComparer.Ordinal);
        foreach (var reference in vm.VBDs)
        {
            var vbd = Require(source.Vbds, reference.opaque_ref, "source disk attachment");
            if (vbd.empty || vbd.type != vbd_type.Disk) continue;
            var disk = Require(source.Vdis, vbd.VDI.opaque_ref, "source virtual disk");
            var map = request.Storage.Single(mapping => mapping.SourceReference == disk.SR.opaque_ref);
            var replicas = target.Vdis.Values.Where(vdi => vdi.SR.opaque_ref == map.TargetReference && vdi.location == disk.location).ToArray();
            if (string.IsNullOrWhiteSpace(disk.location) || replicas.Length != 1 || replicas[0].missing
                || string.IsNullOrWhiteSpace(replicas[0].uuid) || replicas[0].virtual_size != disk.virtual_size)
                throw new InvalidOperationException($"Disk '{disk.name_label}' has no unique available replica at its reviewed SR and storage location.");
            bindings[disk.opaque_ref] = replicas[0];
        }
        return bindings;
    }
    internal sealed record DrSource(Session Session, Pool Pool, Dictionary<string, VM> Vms, Dictionary<string, SR> Srs,
        Dictionary<string, Network> Networks, Dictionary<string, VDI> Vdis, Dictionary<string, VBD> Vbds, Dictionary<string, VIF> Vifs)
    {
        public string Fingerprint => Hash(new
        {
            Pool = Pool.ToJObject(), Vms = Ordered(Vms), Srs = Ordered(Srs), Networks = Ordered(Networks),
            Vdis = Ordered(Vdis), Vbds = Ordered(Vbds), Vifs = Ordered(Vifs)
        });
        private static object Ordered<T>(Dictionary<string, T> records) where T : XenObject<T> =>
            records.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { pair.Key, Record = pair.Value.ToJObject() }).ToArray();
    }
}
