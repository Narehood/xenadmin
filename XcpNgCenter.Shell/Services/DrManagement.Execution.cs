using XenAdmin.Actions;
using XenAdmin.Actions.DR;
using XenAdmin.Network;
using XenAPI;
using Network = XenAPI.Network;

namespace XcpNgCenter.Shell.Services;

public sealed partial class DrManagement
{
    internal DrOutcome Recover(Session session, DrInspection inspection, DrRequest request, DrReview approval)
    {
        var results = new List<DrVmResult>();
        var cleanup = new List<DrCleanupItem>();
        var created = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string>? originalNetworks = null;
        string? error = null;
        try
        {
            RequirePool(); RequirePermissions(session, RecoveryMethods());
            if (approval.RequestFingerprint != request.Fingerprint || approval.SourceFingerprint != inspection.Fingerprint)
                throw new InvalidOperationException("The recovery draft changed after review. Review it again.");
            WithMetadata(session, inspection.Metadata, source =>
            {
                var review = Review(session, inspection, request, source);
                if (review.TargetFingerprint != approval.TargetFingerprint)
                    throw new InvalidOperationException("The destination storage or networks changed after review. Inspect and review again.");
                originalNetworks = ReadTarget(session).Networks.Keys.ToHashSet(StringComparer.Ordinal);
                foreach (var reference in request.VmReferences)
                {
                    var sourceVm = source.Vms[reference];
                    VM? recovered = null;
                    try
                    {
                        RequirePool(); RequirePermissions(session, RecoveryMethods());
                        var fresh = ReadTarget(session);
                        if (fresh.Pool.uuid != _poolUuid || fresh.Pool.ha_enabled || fresh.Pool.current_operations.Count != 0
                            || fresh.Vms.Values.Any(vm => vm.uuid == sourceVm.uuid))
                            throw new InvalidOperationException("The destination pool or VM identity changed before recovery.");
                        // Only this operation's successfully recovered VMs may account for changed NIC membership.
                        foreach (var network in fresh.Networks.Values.Where(network => request.Networks.Any(map => map.TargetReference == network.opaque_ref)))
                            network.VIFs = network.VIFs.Where(vif => !created.Contains(VIF.get_record(session, vif).VM.opaque_ref)).ToList();
                        fresh = fresh with { Vms = fresh.Vms.Where(pair => !created.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value) };
                        ValidateTarget(inspection, request, fresh);
                        if (TargetFingerprint(inspection, request, fresh) != approval.TargetFingerprint)
                            throw new InvalidOperationException("Reviewed storage or network configuration changed before the next VM recovery.");
                        var expectedDisks = ResolveDiskBindings(source, sourceVm, request, fresh);
                        // No mutation is automatically retried; false prevents forcing an older backup over a newer VM.
                        new DrRecoverAction(_connection, sourceVm, force: false, suppressHistory: true) { MetadataSession = source.Session }.RunSync(session);
                        var recoveredRef = VM.get_by_uuid(session, sourceVm.uuid);
                        recovered = VM.get_record(session, recoveredRef); recovered.opaque_ref = recoveredRef.opaque_ref;
                        RequireHalted(recovered, sourceVm.uuid);
                        created.Add(recovered.opaque_ref);
                        var nics = recovered.VIFs.Select(vif => (Reference: vif.opaque_ref, Record: VIF.get_record(session, vif))).ToArray();
                        var originalNics = sourceVm.VIFs.Select(vif => source.Vifs[vif.opaque_ref]).ToArray();
                        if (nics.Length != originalNics.Length || nics.Select(nic => nic.Record.device).Distinct().Count() != nics.Length)
                            throw new InvalidOperationException("The recovered NIC inventory does not match the reviewed VM.");
                        if (!MatchesImportedConfiguration(recovered, sourceVm))
                            throw new InvalidOperationException("The recovered VM configuration differs from the reviewed metadata. Inspect it manually.");
                        var expectedCleanup = ReadCleanupState(session, recovered.opaque_ref, sourceVm.uuid);
                        var originalDisks = sourceVm.VBDs.Select(vbd => source.Vbds[vbd.opaque_ref]).ToArray();
                        if (originalDisks.Length != expectedCleanup.Vbds.Length || expectedCleanup.Vbds.Any(vbd =>
                            !originalDisks.Any(original => original.userdevice == vbd.userdevice && original.type == vbd.type && original.mode == vbd.mode)))
                            throw new InvalidOperationException("Recovered disk attachments do not match the reviewed metadata.");
                        foreach (var diskAttachment in expectedCleanup.Vbds.Where(vbd => !vbd.empty && vbd.type == vbd_type.Disk))
                        {
                            var original = originalDisks.Single(vbd => vbd.userdevice == diskAttachment.userdevice);
                            var expectedDisk = expectedDisks[original.VDI.opaque_ref];
                            var actualDisk = VDI.get_record(session, diskAttachment.VDI); actualDisk.opaque_ref = diskAttachment.VDI.opaque_ref;
                            if (actualDisk.opaque_ref != expectedDisk.opaque_ref || Hash(DiskIdentity(actualDisk)) != Hash(DiskIdentity(expectedDisk)))
                                throw new InvalidOperationException("Recovery resolved a disk to an unreviewed VDI or storage location. Keep the VM halted and inspect its disks manually.");
                        }
                        foreach (var nic in nics)
                        {
                            RequireHalted(VM.get_record(session, recoveredRef), sourceVm.uuid);
                            var sourceNic = originalNics.Single(vif => vif.device == nic.Record.device);
                            if (nic.Record.VM.opaque_ref != recoveredRef.opaque_ref || nic.Record.currently_attached || nic.Record.MAC != sourceNic.MAC)
                                throw new InvalidOperationException("A recovered NIC changed owner or became attached.");
                            var mapping = request.Networks.Single(map => map.SourceReference == sourceNic.network.opaque_ref);
                            var expected = inspection.TargetNetworks.Single(network => network.Reference == mapping.TargetReference);
                            var targetNetwork = Network.get_record(session, mapping.TargetReference);
                            if (targetNetwork.uuid != expected.Uuid || targetNetwork.current_operations.Count != 0)
                                throw new InvalidOperationException("The reviewed target network changed before NIC mapping.");
                            if (request.Mode == DrMode.MetadataRehearsal && (targetNetwork.PIFs.Count != 0
                                || targetNetwork.VIFs.Any(vif => !created.Contains(VIF.get_record(session, vif).VM.opaque_ref))))
                                throw new InvalidOperationException("A rehearsal network lost its isolation. The VM remains halted; inspect its NICs.");
                            if (nic.Record.network.opaque_ref != mapping.TargetReference)
                                VIF.move(session, nic.Reference, mapping.TargetReference);
                            expectedCleanup.Vifs.Single(vif => vif.device == nic.Record.device).network = new(mapping.TargetReference);
                        }
                        if (request.Mode == DrMode.MetadataRehearsal)
                        {
                            var receipt = CaptureCleanup(session, recovered.opaque_ref, sourceVm.uuid);
                            if (receipt.Fingerprint != expectedCleanup.Fingerprint)
                                throw new InvalidOperationException("The recovered VM changed while NICs were mapped. Inspect it and perform cleanup manually.");
                            cleanup.Add(receipt);
                        }
                        results.Add(new(sourceVm.Name(), sourceVm.uuid, $"Recovered halted ({recovered.opaque_ref}); reviewed NIC mappings applied.", true));
                    }
                    catch (Exception failure)
                    {
                        results.Add(new(sourceVm.Name(), sourceVm.uuid, "Stopped: " + failure.Message
                            + (recovered == null ? "" : $" Observed VM [{recovered.uuid}] ({recovered.opaque_ref}), power state: {recovered.power_state}."
                                + " Its NICs may retain importer-chosen networks, including live networks. Do not start it until power state, automatic power-on and NIC isolation have been checked manually."), recovered != null));
                        throw;
                    }
                }
                return true;
            });
        }
        catch (Exception failure) { error = failure.Message + " " + RecoveryNotice; }
        if (originalNetworks != null)
        {
            try
            {
                foreach (var network in Records(Network.get_all_records(session)).Values.Where(network => !originalNetworks.Contains(network.opaque_ref)))
                    results.Add(new("Network inventory", network.uuid,
                        $"New network observed: '{network.Name()}' ({network.opaque_ref}). Metadata import or another administrator may have created it. Inspect it separately; rehearsal cleanup preserves network records.", false));
            }
            catch (Exception failure)
            {
                results.Add(new("Network inventory", _poolUuid, "Unable to check imported network records: " + failure.Message + " Inspect destination networks manually.", false));
            }
        }
        foreach (var reference in request.VmReferences)
        {
            var vm = inspection.Vms.SingleOrDefault(candidate => candidate.Reference == reference);
            if (vm != null && results.All(result => result.Uuid != vm.Uuid)) results.Add(new(vm.Name, vm.Uuid, "Not attempted.", false));
        }
        return new(request.Mode, results, cleanup, error);
    }

    internal DrOutcome Cleanup(Session session, DrOutcome outcome)
    {
        var results = new List<DrVmResult>();
        var remaining = outcome.Cleanup.ToList();
        try
        {
            RequirePool(); RequirePermissions(session, CleanupMethods());
            var pool = Pool.get_record(session, _poolReference);
            if (pool.uuid != _poolUuid) throw new InvalidOperationException("The destination pool identity changed.");
            if (outcome.Mode != DrMode.MetadataRehearsal || outcome.Cleanup.Count == 0)
                throw new InvalidOperationException("Cleanup is available only for VM records created by this metadata rehearsal.");
            // Validate the entire receipt before the first deletion. Never shut down a VM or delete a VDI.
            foreach (var item in outcome.Cleanup)
                if (CaptureCleanup(session, item.Reference, item.Uuid).Fingerprint != item.Fingerprint)
                    throw new InvalidOperationException($"'{item.Name}' changed since recovery. Inspect it and clean it up manually.");
            foreach (var item in outcome.Cleanup)
            {
                RequirePool(); RequirePermissions(session, CleanupMethods());
                var expected = ReadCleanupState(session, item.Reference, item.Uuid);
                if (expected.Fingerprint != item.Fingerprint)
                    throw new InvalidOperationException($"'{item.Name}' changed before cleanup. No deletion was attempted for it.");
                foreach (var reference in expected.Vm.VIFs.ToArray())
                {
                    RequireUnchangedCleanup(session, item, expected);
                    VIF.destroy(session, reference);
                    // Only our confirmed deletion changes the expected receipt. Never adopt fresh server edits.
                    expected.Vm.VIFs.RemoveAll(vif => vif.opaque_ref == reference.opaque_ref);
                    expected = expected with { Vifs = expected.Vifs.Where(vif => vif.opaque_ref != reference.opaque_ref).ToArray() };
                }
                foreach (var reference in expected.Vm.VBDs.ToArray())
                {
                    RequireUnchangedCleanup(session, item, expected);
                    VBD.destroy(session, reference);
                    expected.Vm.VBDs.RemoveAll(vbd => vbd.opaque_ref == reference.opaque_ref);
                    expected = expected with { Vbds = expected.Vbds.Where(vbd => vbd.opaque_ref != reference.opaque_ref).ToArray() };
                }
                RequireUnchangedCleanup(session, item, expected);
                VM.destroy(session, item.Reference);
                remaining.Remove(item);
                results.Add(new(item.Name, item.Uuid, "Rehearsal VM record and attachments removed. Disks preserved.", true));
            }
            return new(outcome.Mode, results, remaining);
        }
        catch (Exception error)
        {
            return new(outcome.Mode, results, remaining, error.Message
                + " Cleanup stopped and may be partial. Inspect remaining VM records and attachments before manual cleanup. No disk deletion or automatic retry was performed.");
        }
    }

    private static void RequireHalted(VM vm, string uuid)
    {
        if (vm.uuid != uuid || vm.power_state != vm_power_state.Halted || vm.current_operations.Count != 0 || vm.is_a_snapshot
            || vm.is_control_domain || vm.is_a_template || vm.snapshots.Count != 0)
            throw new InvalidOperationException("The recovered VM identity, halted state or snapshot inventory changed. Inspect it before proceeding.");
        RequireManualPowerOn(vm);
    }

    private static bool HasAutomaticPowerOn(VM vm) => vm.other_config.TryGetValue("auto_poweron", out var value)
        && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static void RequireManualPowerOn(VM vm)
    {
        if (HasAutomaticPowerOn(vm))
            throw new InvalidOperationException("The VM has automatic power-on enabled. Clear it and verify the VM is halted before any host reboot or recovery attempt.");
    }

    private static DrCleanupItem CaptureCleanup(Session session, string reference, string uuid)
    {
        var state = ReadCleanupState(session, reference, uuid);
        return new(reference, uuid, state.Vm.Name(), state.Fingerprint);
    }

    private static void RequireUnchangedCleanup(Session session, DrCleanupItem item, DrCleanupState expected)
    {
        if (ReadCleanupState(session, item.Reference, item.Uuid).Fingerprint != expected.Fingerprint)
            throw new InvalidOperationException($"'{item.Name}' changed during cleanup. Inspect its remaining records before manual cleanup.");
    }

    private static DrCleanupState ReadCleanupState(Session session, string reference, string uuid)
    {
        var vm = VM.get_record(session, reference); vm.opaque_ref = reference; RequireHalted(vm, uuid);
        var vifs = vm.VIFs.OrderBy(vif => vif.opaque_ref).Select(vif =>
        {
            var record = VIF.get_record(session, vif); record.opaque_ref = vif.opaque_ref; return record;
        }).ToArray();
        var vbds = vm.VBDs.OrderBy(vbd => vbd.opaque_ref).Select(vbd =>
        {
            var record = VBD.get_record(session, vbd); record.opaque_ref = vbd.opaque_ref; return record;
        }).ToArray();
        if (vifs.Any(vif => vif.VM.opaque_ref != reference || vif.currently_attached)
            || vbds.Any(vbd => vbd.VM.opaque_ref != reference || vbd.currently_attached))
            throw new InvalidOperationException("Rehearsal VM attachments changed or became active.");
        return new(vm, vifs, vbds);
    }

    private sealed record DrCleanupState(VM Vm, VIF[] Vifs, VBD[] Vbds)
    {
        public string Fingerprint => Hash(new { Vm = Vm.ToJObject(), Vifs = Vifs.Select(vif => vif.ToJObject()), Vbds = Vbds.Select(vbd => vbd.ToJObject()) });
    }

    private static bool MatchesImportedConfiguration(VM recovered, VM source)
    {
        var actual = ConfigurationFingerprint(recovered);
        if (actual == ConfigurationFingerprint(source)) return true;
        // XAPI import's ensure_device_model_profile_present upgrades only this platform entry.
        // Compare in the source -> import direction; never accept arbitrary or reverse platform edits.
        var platform = new Dictionary<string, string>(source.platform, StringComparer.Ordinal);
        var hasDeviceModel = platform.TryGetValue("device-model", out var deviceModel);
        if (source.is_a_template || (deviceModel != "qemu-trad" && (hasDeviceModel || EffectiveDomainType(source) != domain_type.hvm)))
            return false;
        platform["device-model"] = "qemu-upstream-compat";
        return actual == ConfigurationFingerprint(source, platform);
    }

    private static domain_type EffectiveDomainType(VM vm) => vm.domain_type == domain_type.unspecified
        ? (string.IsNullOrEmpty(vm.HVM_boot_policy) ? domain_type.pv : domain_type.hvm) : vm.domain_type;

    private static string ConfigurationFingerprint(VM vm, IReadOnlyDictionary<string, string>? platform = null) => Hash(new
    {
        vm.uuid, vm.name_label, vm.name_description, vm.memory_static_min, vm.memory_static_max,
        vm.memory_dynamic_min, vm.memory_dynamic_max, vm.VCPUs_max, vm.VCPUs_at_startup, vm.VCPUs_params,
        vm.HVM_boot_policy, vm.HVM_boot_params, vm.PV_bootloader, vm.PV_kernel, vm.PV_ramdisk, vm.PV_args,
        DomainType = EffectiveDomainType(vm), Platform = (platform ?? vm.platform).OrderBy(pair => pair.Key, StringComparer.Ordinal)
    });
}

internal sealed class DrRecoveryAction : AsyncAction
{
    private readonly DrManagement _workflow;
    private readonly DrInspection _inspection;
    private readonly DrRequest _request;
    private readonly DrReview _review;
    public DrOutcome? Outcome { get; private set; }
    public DrRecoveryAction(IXenConnection connection, DrManagement workflow, DrInspection inspection, DrRequest request, DrReview review)
        : base(connection, request.Mode == DrMode.MetadataRehearsal ? "Rehearse metadata recovery" : "Recover VM metadata")
    {
        _workflow = workflow; _inspection = inspection; _request = request; _review = review;
        ApiMethodsToRoleCheck.AddRange(DrManagement.RecoveryMethods());
    }
    protected override void Run()
    {
        Outcome = _workflow.Recover(Session, _inspection, _request, _review);
        Description = Outcome.Report;
        if (!Outcome.Succeeded) throw new InvalidOperationException(Outcome.Error);
    }
}

internal sealed class DrCleanupAction : AsyncAction
{
    private readonly DrManagement _workflow;
    private readonly DrOutcome _outcome;
    public DrOutcome? Outcome { get; private set; }
    public DrCleanupAction(IXenConnection connection, DrManagement workflow, DrOutcome outcome) : base(connection, "Clean up metadata rehearsal")
    { _workflow = workflow; _outcome = outcome; ApiMethodsToRoleCheck.AddRange(DrManagement.CleanupMethods()); }
    protected override void Run()
    {
        Outcome = _workflow.Cleanup(Session, _outcome); Description = Outcome.Report;
        if (!Outcome.Succeeded) throw new InvalidOperationException(Outcome.Error);
    }
}
