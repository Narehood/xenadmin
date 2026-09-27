using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XenAdmin;
using XenAdmin.Actions;
using XenAdmin.Actions.DR;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using Xunit;
using Task = System.Threading.Tasks.Task;
using Network = XenAPI.Network;

namespace XcpNgCenter.Shell.Tests;

[Collection("HA shared action runtime")]
public sealed class DrManagementTests : IDisposable
{
    private readonly IXenAdminConfigProvider? _original = XenAdminConfigManager.Provider;
    public DrManagementTests() => XenAdminConfigManager.Provider = new ShellConfigProvider(new ShellAppSettings { ConnectionTimeoutSeconds = 3 });
    public void Dispose() => XenAdminConfigManager.Provider = _original;

    [Fact]
    public async Task DiscoveryAndInspectionReadMetadataAndLogoutOnlyOwnedSessions()
    {
        using var f = new Fixture();
        var candidates = await f.Workflow.DiscoverAsync();
        var metadata = Assert.Single(candidates);
        var inspection = await f.Workflow.InspectAsync(metadata);
        Assert.Equal(2, inspection.Vms.Count);
        Assert.Equal("source-pool-uuid", inspection.SourcePoolUuid);
        Assert.Single(inspection.TargetStorage);
        Assert.Contains(inspection.TargetNetworks, network => network.IsIsolated);
        Assert.DoesNotContain(f.Requests, request => Mutation(Method(request)));
        Assert.All(f.Requests.Where(request => Method(request) == "session.logout"), request => Assert.Equal("metadata-session", request["params"]![0]!.Value<string>()));
        Assert.Single(f.Requests, request => Method(request) == "session.logout");
    }

    [Theory]
    [InlineData(vm_power_state.Suspended)]
    [InlineData(vm_power_state.unknown)]
    public async Task UnsupportedPowerStateIsVisibleAndRejectedBeforeRecovery(vm_power_state state)
    {
        using var f = new Fixture(); f.SourceVms[0].power_state = state;
        var inspection = await f.Inspect(); Assert.NotNull(inspection.Vms[0].UnavailableReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Workflow.ReviewAsync(inspection, f.Request(inspection)));
        Assert.DoesNotContain(f.Requests, rpc => Mutation(Method(rpc)));
    }

    [Theory]
    [InlineData("VM.get_all_records")]
    [InlineData("host.get_record")]
    public async Task MetadataFailureIsSurfacedAndOpenedSessionIsClosed(string method)
    {
        using var f = new Fixture { FailMethod = method, FailOnlyMetadata = true };
        await Assert.ThrowsAnyAsync<Exception>(() => f.Inspect());
        Assert.Single(f.Requests, request => Method(request) == "session.logout");
        Assert.DoesNotContain(f.Requests, request => Mutation(Method(request)));
    }

    [Fact]
    public async Task LogoutFailureIsReportedWithoutLoggingOutDestination()
    {
        using var f = new Fixture { FailMethod = "session.logout" };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => f.Inspect());
        Assert.Contains("could not be closed", error.Message);
        Assert.All(f.Requests.Where(request => Method(request) == "session.logout"), request => Assert.Equal("metadata-session", request["params"]![0]!.Value<string>()));
    }

    [Fact]
    public void MissingMetadataSessionFailsSharedActionInsteadOfClaimingRecovery()
    {
        using var f = new Fixture();
        var action = new DrRecoverAction(f.Connection, f.SourceVms[0], false);
        Assert.Throws<InvalidOperationException>(() => action.RunSync(f.Session));
        Assert.False(action.Succeeded);
        Assert.DoesNotContain(f.Requests, request => Method(request) == "Async.VM.recover");
    }

    [Theory]
    [InlineData("source-change")]
    [InlineData("existing-vm")]
    [InlineData("pool-ha")]
    [InlineData("sr-uuid")]
    [InlineData("detached-storage")]
    [InlineData("network-uuid")]
    [InlineData("network-pif")]
    [InlineData("network-vif")]
    [InlineData("metadata-not-latest")]
    [InlineData("disk-location")]
    [InlineData("disk-missing")]
    [InlineData("dr-owned-sr")]
    public async Task ReviewRejectsStaleOrUnsafeInventoryBeforeRecovery(string change)
    {
        using var f = new Fixture(); var inspection = await f.Inspect(); var request = f.Request(inspection);
        switch (change)
        {
            case "source-change": f.SourceVms[0].name_label = "changed"; break;
            case "existing-vm": f.Recovered.Add("existing", new VM { uuid = f.SourceVms[0].uuid }); break;
            case "pool-ha": f.Pool.ha_enabled = true; break;
            case "sr-uuid": f.TargetSr.uuid = "different"; break;
            case "detached-storage": f.Pbd.currently_attached = false; break;
            case "network-uuid": f.Isolated.uuid = "different"; break;
            case "network-pif": f.Isolated.PIFs = [new("pif")]; break;
            case "network-vif": f.Isolated.VIFs = [new("other-vif")]; break;
            case "metadata-not-latest": f.Metadata.metadata_latest = false; break;
            case "disk-location": f.TargetDisk.location = "different"; break;
            case "disk-missing": f.TargetDisk.missing = true; break;
            case "dr-owned-sr": f.TargetSr.introduced_by = new("dr-task"); break;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => f.Workflow.ReviewAsync(inspection, request));
        Assert.DoesNotContain(f.Requests, rpc => Mutation(Method(rpc)));
    }

    [Fact]
    public async Task FreshServerPermissionsOverrideCachedSuperuserBeforeMutation()
    {
        using var f = new Fixture(); var inspection = await f.Inspect(); var request = f.Request(inspection);
        var review = await f.Workflow.ReviewAsync(inspection, request);
        f.ServerSuperuser = false; f.Permissions = DrManagement.RecoveryMethods().ToStringArray().Where(method => method != "vif.move").ToArray();
        var outcome = f.Workflow.Recover(f.Session, inspection, request, review);
        Assert.False(outcome.Succeeded); Assert.Contains("permissions", outcome.Error);
        Assert.DoesNotContain(f.Requests, rpc => Mutation(Method(rpc)));
        Assert.Contains(f.Requests, rpc => Method(rpc) == "session.get_rbac_permissions");
    }

    [Fact]
    public async Task RecoveryUsesSharedTasksMapsNicsStaysHaltedAndCleansOnlyRehearsalRecords()
    {
        using var f = new Fixture(); var inspection = await f.Inspect(); var request = f.Request(inspection);
        var review = await f.Workflow.ReviewAsync(inspection, request);
        var outcome = f.Workflow.Recover(f.Session, inspection, request, review);
        Assert.True(outcome.Succeeded, outcome.Error); Assert.Equal(2, outcome.Cleanup.Count);
        Assert.Equal(2, f.Requests.Count(rpc => Method(rpc) == "Async.VM.recover"));
        Assert.All(f.Requests.Where(rpc => Method(rpc) == "Async.VM.recover"), rpc => Assert.False(rpc["params"]![3]!.Value<bool>()));
        Assert.Equal(2, f.Requests.Count(rpc => Method(rpc) == "VIF.move"));
        Assert.Equal(2, f.Requests.Count(rpc => Method(rpc) == "task.destroy"));
        Assert.All(f.Recovered.Values, vm => Assert.Equal(vm_power_state.Halted, vm.power_state));
        var cleanup = f.Workflow.Cleanup(f.Session, outcome);
        Assert.True(cleanup.Succeeded, cleanup.Error); Assert.Empty(cleanup.Cleanup); Assert.Empty(f.Recovered);
        Assert.Equal(2, f.Requests.Count(rpc => Method(rpc) == "VM.destroy"));
        Assert.DoesNotContain(f.Requests, rpc => Method(rpc) is "VDI.destroy" or "VM.start" or "Async.VM.start" or "VM.hard_shutdown");
    }

    [Theory]
    [InlineData(domain_type.hvm, null)]
    [InlineData(domain_type.hvm, "qemu-trad")]
    [InlineData(domain_type.unspecified, null)]
    [InlineData(domain_type.pv, "qemu-trad")]
    public async Task ExpectedImportDeviceModelUpgradeStillMapsNicsAndIssuesCleanup(domain_type domainType, string? deviceModel)
    {
        using var f = new Fixture();
        foreach (var vm in f.SourceVms)
        {
            vm.domain_type = domainType; vm.HVM_boot_policy = domainType == domain_type.pv ? "" : "BIOS order";
            vm.platform["acpi"] = "1";
            if (deviceModel != null) vm.platform["device-model"] = deviceModel;
        }
        f.ImportNetwork.PIFs = [new("physical-interface")];
        f.AfterRecover = vm => { vm.platform["device-model"] = "qemu-upstream-compat"; };
        var inspection = await f.Inspect(); var request = f.Request(inspection);

        var outcome = f.Workflow.Recover(f.Session, inspection, request, await f.Workflow.ReviewAsync(inspection, request));

        Assert.True(outcome.Succeeded, outcome.Error); Assert.Equal(2, outcome.Cleanup.Count);
        Assert.Equal(2, f.Requests.Count(rpc => Method(rpc) == "VIF.move"));
        Assert.All(f.TargetVifs.Values, vif => Assert.Equal("isolated", vif.network.opaque_ref));
        Assert.All(f.SourceVms, vm => Assert.Equal(deviceModel, vm.platform.GetValueOrDefault("device-model")));
        Assert.All(f.Recovered.Values, vm => Assert.Equal(vm_power_state.Halted, vm.power_state));
        Assert.True(f.Workflow.Cleanup(f.Session, outcome).Succeeded);
        Assert.DoesNotContain(f.Requests, rpc => Method(rpc) is "VDI.destroy" or "VM.start" or "Async.VM.start");
    }

    [Theory]
    [InlineData("unrecognized-device-model")]
    [InlineData("reverse-device-model")]
    [InlineData("pv-added-device-model")]
    [InlineData("other-platform")]
    public async Task UnexpectedImportConfigurationNeverRemapsOrAuthorizesCleanup(string change)
    {
        using var f = new Fixture();
        foreach (var vm in f.SourceVms)
        {
            vm.domain_type = change == "pv-added-device-model" ? domain_type.pv : domain_type.hvm;
            vm.platform["acpi"] = "1";
            if (change == "reverse-device-model") vm.platform["device-model"] = "qemu-upstream-compat";
        }
        f.AfterRecover = vm =>
        {
            vm.platform["device-model"] = change == "unrecognized-device-model" ? "qemu-upstream-uefi"
                : change == "reverse-device-model" ? "qemu-trad" : "qemu-upstream-compat";
            if (change == "other-platform") vm.platform["acpi"] = "0";
        };
        var inspection = await f.Inspect(); var request = f.Request(inspection);

        var outcome = f.Workflow.Recover(f.Session, inspection, request, await f.Workflow.ReviewAsync(inspection, request));

        Assert.False(outcome.Succeeded); Assert.Empty(outcome.Cleanup);
        Assert.Contains("configuration differs", outcome.Error);
        Assert.Contains("importer-chosen", outcome.Report);
        Assert.Single(f.Recovered);
        Assert.DoesNotContain(f.Requests, rpc => Method(rpc) is "VIF.move" or "VM.destroy" or "VDI.destroy" or "VM.start" or "Async.VM.start");
    }

    [Fact]
    public async Task OnlyOuterRecoveryActionPublishesHistoryWhenPostImportValidationFails()
    {
        using var f = new Fixture { RecoverWrongDisk = true }; var inspection = await f.Inspect(); var request = f.Request(inspection);
        var review = await f.Workflow.ReviewAsync(inspection, request);
        var published = new List<ActionBase>();
        void Capture(ActionBase action) { if (action is DrRecoverAction or DrRecoveryAction) published.Add(action); }
        ActionBase.NewAction += Capture;
        try
        {
            var standalone = new DrRecoverAction(f.Connection, f.SourceVms[0]);
            Assert.Same(standalone, Assert.Single(published)); published.Clear();
            var outer = new DrRecoveryAction(f.Connection, f.Workflow, inspection, request, review);
            Assert.Throws<InvalidOperationException>(() => outer.RunSync(f.Session));
            Assert.Same(outer, Assert.Single(published));
            Assert.False(outer.Succeeded); Assert.Contains("unreviewed VDI", outer.Description);
        }
        finally { ActionBase.NewAction -= Capture; }
    }

    [Fact]
    public async Task ReviewDescribesFreshChosenStorageAndNetworkIdentitiesForEachVm()
    {
        using var f = new Fixture(); var inspection = await f.Inspect(); var request = f.Request(inspection, DrMode.Recovery);
        f.TargetSr.name_label = "Reviewed replica"; f.Isolated.name_label = "Reviewed destination";
        f.Isolated.PIFs = [new("physical-interface")];
        var review = await f.Workflow.ReviewAsync(inspection, request);

        Assert.Contains("VM 0 [vm-uuid0]", review.MappingSummary);
        Assert.Contains("VM 1 [vm-uuid1]", review.MappingSummary);
        Assert.Contains("Original storage [replica-sr-uuid]", review.MappingSummary);
        Assert.Contains("Reviewed replica [replica-sr-uuid]", review.MappingSummary);
        Assert.Contains("Production [source-network-uuid]", review.MappingSummary);
        Assert.Contains("Reviewed destination [isolated-uuid]", review.MappingSummary);
        Assert.Contains("physical interfaces", review.MappingSummary);
        Assert.Contains("does not copy disks", review.Summary);
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("boot")]
    [InlineData("cpu")]
    public async Task ImportMapOrderDoesNotChangeConfigurationIdentity(string field)
    {
        using var f = new Fixture();
        foreach (var vm in f.SourceVms)
        {
            vm.platform = new() { ["acpi"] = "1", ["nx"] = "1" };
            vm.HVM_boot_params = new() { ["order"] = "cd", ["firmware"] = "bios" };
            vm.VCPUs_params = new() { ["weight"] = "256", ["cap"] = "0" };
        }
        f.AfterRecover = vm =>
        {
            if (field == "platform") ReverseMapOrder(vm.platform);
            if (field == "boot") ReverseMapOrder(vm.HVM_boot_params);
            if (field == "cpu") ReverseMapOrder(vm.VCPUs_params);
        };
        var inspection = await f.Inspect(); var request = f.Request(inspection);
        var outcome = f.Workflow.Recover(f.Session, inspection, request, await f.Workflow.ReviewAsync(inspection, request));
        Assert.True(outcome.Succeeded, outcome.Error); Assert.Equal(2, outcome.Cleanup.Count);
        Assert.Equal(2, f.Requests.Count(rpc => Method(rpc) == "VIF.move"));
    }

    [Theory]
    [InlineData("network-order")]
    [InlineData("sr-accounting")]
    [InlineData("map-order")]
    public async Task LaterRecoveryIgnoresOrderingAndSrAccountingWithDistinctDisksAndExistingNics(string change)
    {
        using var f = new Fixture(); f.UseDistinctVmDisks();
        f.Isolated.PIFs = [new("physical-b"), new("physical-a")];
        f.Isolated.other_config = new() { ["network-setting-a"] = "a", ["network-setting-b"] = "b" };
        f.Pbd.device_config = new() { ["server"] = "storage", ["path"] = "/replica" };
        foreach (var reference in new[] { "foreign-b", "foreign-a" })
        {
            f.TargetVifs.Add(reference, new VIF { opaque_ref = reference, uuid = reference, VM = new("existing-vm"), network = new("isolated") });
            f.Isolated.VIFs.Add(new(reference));
        }
        var inspection = await f.Inspect(); var request = f.Request(inspection, DrMode.Recovery);
        var review = await f.Workflow.ReviewAsync(inspection, request);
        f.AfterFirstMove = () =>
        {
            if (change == "network-order") { f.Isolated.VIFs.Reverse(); f.Isolated.PIFs.Reverse(); }
            if (change == "sr-accounting")
            {
                f.TargetSr.virtual_allocation += 4096; f.TargetSr.physical_utilisation += 4096;
                f.TargetSr.VDIs.Add(new("unrelated-inventory-entry")); f.TargetSr.other_config["last-scan"] = "now";
            }
            if (change == "map-order")
            {
                ReverseMapOrder(f.Isolated.other_config);
                ReverseMapOrder(f.Pbd.device_config);
            }
        };
        var outcome = f.Workflow.Recover(f.Session, inspection, request, review);
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(2, f.Requests.Count(rpc => Method(rpc) == "Async.VM.recover"));
        Assert.Equal(2, f.Requests.Count(rpc => Method(rpc) == "VIF.move"));
        Assert.Equal("target-disk-second", f.TargetDiskForVm("recovered1"));
        Assert.All(f.TargetVifs.Values, vif => Assert.Equal("isolated", vif.network.opaque_ref));
    }

    [Theory]
    [InlineData("sr-type")]
    [InlineData("sr-shared")]
    [InlineData("sr-backend")]
    [InlineData("dr-task")]
    [InlineData("pbd-device")]
    [InlineData("network-mtu")]
    [InlineData("network-bridge")]
    [InlineData("network-locking")]
    [InlineData("network-config")]
    [InlineData("network-pif")]
    public async Task LaterRecoveryStillRejectsRelevantStorageAndNetworkChanges(string change)
    {
        using var f = new Fixture(); var inspection = await f.Inspect(); var request = f.Request(inspection, DrMode.Recovery);
        var review = await f.Workflow.ReviewAsync(inspection, request);
        f.AfterFirstMove = () =>
        {
            switch (change)
            {
                case "sr-type": f.TargetSr.type = "different"; break;
                case "sr-shared": f.TargetSr.shared = false; break;
                case "sr-backend": f.TargetSr.sm_config["backend"] = "different"; break;
                case "dr-task": f.TargetSr.introduced_by = new("dr-task"); break;
                case "pbd-device": f.Pbd.device_config["server"] = "different"; break;
                case "network-mtu": f.Isolated.MTU = 9000; break;
                case "network-bridge": f.Isolated.bridge = "different"; break;
                case "network-locking": f.Isolated.default_locking_mode = network_default_locking_mode.disabled; break;
                case "network-config": f.Isolated.other_config["network-setting"] = "different"; break;
                case "network-pif": f.Isolated.PIFs.Add(new("new-physical-interface")); break;
            }
        };
        var outcome = f.Workflow.Recover(f.Session, inspection, request, review);
        Assert.False(outcome.Succeeded); Assert.Single(f.Requests, rpc => Method(rpc) == "Async.VM.recover");
        Assert.Single(f.Recovered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticPowerOnInMetadataIsUnsupportedBeforeImport(bool poolAutoPowerOn)
    {
        using var f = new Fixture(); f.Pool.other_config["auto_poweron"] = poolAutoPowerOn.ToString().ToLowerInvariant();
        f.SourceVms[0].other_config["auto_poweron"] = "true";
        var inspection = await f.Inspect();
        Assert.Contains("automatic power-on", inspection.Vms.Single(vm => vm.Uuid == f.SourceVms[0].uuid).UnavailableReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Workflow.ReviewAsync(inspection, f.Request(inspection)));
        Assert.DoesNotContain(f.Requests, rpc => Mutation(Method(rpc)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedImportedAutoPowerOnFailsBeforeMappingAndReportsObservedIdentity(bool poolAutoPowerOn)
    {
        using var f = new Fixture(); f.Pool.other_config["auto_poweron"] = poolAutoPowerOn.ToString().ToLowerInvariant();
        f.AfterRecover = vm => vm.other_config["auto_poweron"] = "true";
        var inspection = await f.Inspect(); var request = f.Request(inspection);
        var outcome = f.Workflow.Recover(f.Session, inspection, request, await f.Workflow.ReviewAsync(inspection, request));
        Assert.False(outcome.Succeeded); Assert.Empty(outcome.Cleanup);
        Assert.Contains("automatic power-on", outcome.Error);
        Assert.Contains("recovered0", outcome.Report); Assert.Contains("vm-uuid0", outcome.Report); Assert.Contains("Halted", outcome.Report);
        Assert.DoesNotContain(f.Requests, rpc => Method(rpc) is "VIF.move" or "VM.destroy" or "VDI.destroy" or "VM.start" or "Async.VM.start");
    }

    [Fact]
    public async Task RealRecoveryCannotInvokeRehearsalCleanup()
    {
        using var f = new Fixture(); var inspection = await f.Inspect(); var request = f.Request(inspection, DrMode.Recovery);
        var review = await f.Workflow.ReviewAsync(inspection, request);
        var outcome = f.Workflow.Recover(f.Session, inspection, request, review);
        Assert.True(outcome.Succeeded, outcome.Error); Assert.Empty(outcome.Cleanup);
        Assert.False(f.Workflow.Cleanup(f.Session, outcome).Succeeded);
        Assert.DoesNotContain(f.Requests, rpc => Method(rpc) == "VM.destroy");
    }

    [Theory]
    [InlineData("second-recovery")]
    [InlineData("second-storage")]
    [InlineData("second-permission")]
    public async Task LaterFailureRetainsFirstResultAndNeverRetriesOrRollsBack(string failure)
    {
        using var f = new Fixture(); var inspection = await f.Inspect(); var request = f.Request(inspection);
        var review = await f.Workflow.ReviewAsync(inspection, request);
        f.AfterFirstMove = () =>
        {
            if (failure == "second-recovery") f.FailSecondRecover = true;
            if (failure == "second-storage") f.Pbd.currently_attached = false;
            if (failure == "second-permission") { f.ServerSuperuser = false; f.Permissions = []; }
        };
        var outcome = f.Workflow.Recover(f.Session, inspection, request, review);
        Assert.False(outcome.Succeeded); Assert.Single(outcome.Cleanup);
        Assert.Contains(outcome.Results, result => result.Uuid == f.SourceVms[0].uuid && result.Recovered);
        Assert.Contains(DrManagement.RecoveryNotice, outcome.Error!);
        Assert.Equal(failure == "second-recovery" ? 2 : 1, f.Requests.Count(rpc => Method(rpc) == "Async.VM.recover"));
        Assert.DoesNotContain(f.Requests, rpc => Method(rpc) == "VM.destroy");
    }

    [Fact]
    public async Task MappingFailureNeverBlessesPartialOrForeignConfigurationForCleanup()
    {
        using var f = new Fixture(); var inspection = await f.Inspect(); var request = f.Request(inspection);
        var review = await f.Workflow.ReviewAsync(inspection, request);
        f.FailMethod = "VIF.move";
        var outcome = f.Workflow.Recover(f.Session, inspection, request, review);
        Assert.False(outcome.Succeeded); Assert.Empty(outcome.Cleanup);
        Assert.Single(f.Requests, rpc => Method(rpc) == "Async.VM.recover");
        Assert.Single(f.Recovered);
    }

    [Fact]
    public async Task ServerDiskFallbackToUnreviewedVdiStopsBeforeNicMappingOrCleanupReceipt()
    {
        using var f = new Fixture { RecoverWrongDisk = true }; var inspection = await f.Inspect(); var request = f.Request(inspection);
        var outcome = f.Workflow.Recover(f.Session, inspection, request, await f.Workflow.ReviewAsync(inspection, request));
        Assert.False(outcome.Succeeded); Assert.Contains("unreviewed VDI", outcome.Error); Assert.Empty(outcome.Cleanup);
        Assert.DoesNotContain(f.Requests, rpc => Method(rpc) == "VIF.move");
    }

    [Fact]
    public async Task RejectedResponseAfterServerCreationIsNotRetriedOrAssumedOwned()
    {
        using var f = new Fixture { LostRecoverResponse = true }; var inspection = await f.Inspect(); var request = f.Request(inspection);
        var outcome = f.Workflow.Recover(f.Session, inspection, request, await f.Workflow.ReviewAsync(inspection, request));
        Assert.False(outcome.Succeeded); Assert.Empty(outcome.Cleanup); Assert.Single(f.Recovered);
        Assert.Single(f.Requests, rpc => Method(rpc) == "Async.VM.recover");
        Assert.Contains(DrManagement.RecoveryNotice, outcome.Error);
    }

    [Fact]
    public async Task NewImportNetworksAreReportedAndPreservedByRehearsalCleanup()
    {
        using var f = new Fixture { IncludeImportNetwork = false }; var inspection = await f.Inspect(); var request = f.Request(inspection);
        var outcome = f.Workflow.Recover(f.Session, inspection, request, await f.Workflow.ReviewAsync(inspection, request));
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Contains(outcome.Results, result => result.Status.Contains("New network observed") && result.Uuid == "source-network-uuid");
        Assert.True(f.Workflow.Cleanup(f.Session, outcome).Succeeded);
        Assert.DoesNotContain(f.Requests, rpc => Method(rpc) == "network.destroy");
    }

    [Fact]
    public async Task ConcurrentVmEditDuringMappingIsNotAcceptedAsCleanupBaseline()
    {
        using var f = new Fixture(); var inspection = await f.Inspect(); var request = f.Request(inspection);
        var review = await f.Workflow.ReviewAsync(inspection, request);
        f.AfterFirstMove = () => f.Recovered.Values.Single().name_label = "operator edited";
        var outcome = f.Workflow.Recover(f.Session, inspection, request, review);
        Assert.False(outcome.Succeeded); Assert.Empty(outcome.Cleanup);
        Assert.Contains("changed while NICs", outcome.Error);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("uuid")]
    [InlineData("renamed")]
    [InlineData("nic-attached")]
    public async Task CleanupRejectsChangedVmWithoutDeletingAnyRecord(string change)
    {
        using var f = new Fixture(); var inspection = await f.Inspect(); var request = f.Request(inspection);
        var outcome = f.Workflow.Recover(f.Session, inspection, request, await f.Workflow.ReviewAsync(inspection, request));
        Assert.True(outcome.Succeeded, outcome.Error);
        var vm = f.Recovered.Values.First();
        switch (change)
        {
            case "running": vm.power_state = vm_power_state.Running; break;
            case "uuid": vm.uuid = "different"; break;
            case "renamed": vm.name_label = "edited"; break;
            case "nic-attached": f.TargetVifs[vm.VIFs[0].opaque_ref].currently_attached = true; break;
        }
        var cleanup = f.Workflow.Cleanup(f.Session, outcome);
        Assert.False(cleanup.Succeeded);
        Assert.All(outcome.Cleanup, item => { Assert.Contains(item.Name, cleanup.Report); Assert.Contains(item.Uuid, cleanup.Report); Assert.Contains(item.Reference, cleanup.Report); });
        Assert.DoesNotContain(f.Requests, rpc => Method(rpc).EndsWith(".destroy") && !Method(rpc).StartsWith("task."));
    }

    [Fact]
    public async Task CleanupFailureReportsPartialOutcomeAndPreservesDisks()
    {
        using var f = new Fixture(); var inspection = await f.Inspect(); var request = f.Request(inspection);
        var outcome = f.Workflow.Recover(f.Session, inspection, request, await f.Workflow.ReviewAsync(inspection, request));
        Assert.True(outcome.Succeeded, outcome.Error); f.FailMethod = "VBD.destroy";
        var cleanup = f.Workflow.Cleanup(f.Session, outcome);
        Assert.False(cleanup.Succeeded); Assert.Equal(2, cleanup.Cleanup.Count);
        Assert.Contains("partial", cleanup.Error); Assert.Single(f.Requests, rpc => Method(rpc) == "VIF.destroy");
        Assert.All(outcome.Cleanup, item => { Assert.Contains(item.Name, cleanup.Report); Assert.Contains(item.Uuid, cleanup.Report); Assert.Contains(item.Reference, cleanup.Report); });
        Assert.DoesNotContain(f.Requests, rpc => Method(rpc) is "VDI.destroy" or "VM.destroy");
    }

    [Theory]
    [InlineData("VIF.destroy", "new-vif")]
    [InlineData("VIF.destroy", "new-vbd")]
    [InlineData("VIF.destroy", "rename")]
    [InlineData("VIF.destroy", "config")]
    [InlineData("VIF.destroy", "remaining-vbd")]
    [InlineData("VBD.destroy", "new-vif")]
    [InlineData("VBD.destroy", "new-vbd")]
    [InlineData("VBD.destroy", "rename")]
    [InlineData("VBD.destroy", "config")]
    public async Task CleanupStopsWhenConfigurationChangesBetweenItsDeletions(string afterMethod, string change)
    {
        using var f = new Fixture(); var inspection = await f.Inspect(); var request = f.Request(inspection);
        var outcome = f.Workflow.Recover(f.Session, inspection, request, await f.Workflow.ReviewAsync(inspection, request));
        Assert.True(outcome.Succeeded, outcome.Error);
        string? editedVm = null;
        f.AfterAttachmentDestroy = (method, vmReference) =>
        {
            if (method != afterMethod || editedVm != null) return;
            editedVm = vmReference;
            f.EditDuringCleanup(vmReference, change);
        };

        var cleanup = f.Workflow.Cleanup(f.Session, outcome);

        Assert.NotNull(editedVm);
        Assert.False(cleanup.Succeeded);
        Assert.Contains("changed during cleanup", cleanup.Error);
        Assert.Equal(2, cleanup.Cleanup.Count);
        Assert.True(f.Recovered.ContainsKey(editedVm!));
        Assert.DoesNotContain(f.Requests, rpc => Method(rpc) is "VM.destroy" or "VDI.destroy");
        var deletions = f.Requests.Where(rpc => Method(rpc) is "VIF.destroy" or "VBD.destroy").ToArray();
        Assert.Equal(afterMethod == "VIF.destroy" ? 1 : 2, deletions.Length);
        Assert.Equal(afterMethod, Method(deletions[^1]));
        if (change == "new-vif") Assert.Contains(new XenRef<VIF>("operator-vif"), f.Recovered[editedVm!].VIFs);
        if (change == "new-vbd") Assert.Contains(new XenRef<VBD>("operator-vbd"), f.Recovered[editedVm!].VBDs);
    }

    private static string Method(JObject rpc) => rpc["method"]!.Value<string>()!;
    private static bool Mutation(string method) => method is "Async.VM.recover" or "VIF.move" or "VM.destroy" or "VIF.destroy" or "VBD.destroy";
    private static void ReverseMapOrder(Dictionary<string, string> map)
    {
        // XenObject property setters ignore value-equal maps; mutate the fixture's wire order explicitly.
        var reversed = map.Reverse().ToArray(); map.Clear();
        foreach (var pair in reversed) map.Add(pair.Key, pair.Value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(90));
        private readonly List<JObject> _requests = [];
        private readonly Task _server;
        public XenConnection Connection { get; } = new();
        public Session Session { get; }
        public Pool Pool { get; }
        public Pool SourcePool { get; } = new() { opaque_ref = "source-pool", uuid = "source-pool-uuid", name_label = "Original pool", master = new("host") };
        public VDI Metadata { get; }
        public SR TargetSr { get; }
        public VDI TargetDisk { get; } = new() { opaque_ref = "target-disk", uuid = "replica-disk-uuid", SR = new("target-sr"), location = "disk-location" };
        public PBD Pbd { get; }
        public Network Isolated { get; } = new() { opaque_ref = "isolated", uuid = "isolated-uuid", name_label = "Rehearsal" };
        public VM[] SourceVms { get; }
        public Dictionary<string, VM> Recovered { get; } = [];
        public Dictionary<string, VIF> TargetVifs { get; } = [];
        private readonly Dictionary<string, VBD> _targetVbds = [];
        private readonly Dictionary<string, VIF> _sourceVifs = [];
        private readonly Dictionary<string, VBD> _sourceVbds = [];
        private readonly Network _sourceNetwork = new() { opaque_ref = "source-network", uuid = "source-network-uuid", name_label = "Production" };
        private readonly Network _importNetwork = new() { opaque_ref = "import-network", uuid = "source-network-uuid", name_label = "Imported production" };
        public Network ImportNetwork => _importNetwork;
        private readonly SR _sourceSr = new() { opaque_ref = "source-sr", uuid = "replica-sr-uuid", name_label = "Original storage", shared = true, type = "nfs" };
        private readonly VDI _sourceDisk = new() { opaque_ref = "source-disk", uuid = "disk-uuid", SR = new("source-sr"), location = "disk-location" };
        private readonly List<VDI> _extraSourceDisks = [];
        private readonly List<VDI> _extraTargetDisks = [];
        public DrManagement Workflow { get; }
        public bool ServerSuperuser { get; set; } = true;
        public string[] Permissions { get; set; } = [];
        public string? FailMethod { get; set; }
        public bool FailOnlyMetadata { get; set; }
        public bool FailSecondRecover { get; set; }
        public bool RecoverWrongDisk { get; set; }
        public bool LostRecoverResponse { get; set; }
        public bool IncludeImportNetwork { get; set; } = true;
        public Action? AfterFirstMove { get; set; }
        public Action<VM>? AfterRecover { get; set; }
        public Action<string, string>? AfterAttachmentDestroy { get; set; }
        public IReadOnlyList<JObject> Requests { get { lock (_requests) return _requests.ToArray(); } }

        public Fixture()
        {
            TargetSr = Add("target-sr", new SR { uuid = "replica-sr-uuid", name_label = "Replicated storage", shared = true, type = "nfs", PBDs = [new("pbd")], VDIs = [new("metadata")] });
            Pbd = new() { opaque_ref = "pbd", uuid = "pbd-uuid", SR = new("target-sr"), host = new("host"), currently_attached = true };
            Metadata = Add("metadata", new VDI { uuid = "metadata-uuid", name_label = "Recovery metadata", SR = new("target-sr"), type = vdi_type.metadata, metadata_latest = true });
            Pool = Add("pool", new Pool { uuid = "target-pool-uuid", name_label = "Recovery pool", master = new("host") });
            SourceVms = Enumerable.Range(0, 2).Select(i => new VM { opaque_ref = $"source-vm{i}", uuid = $"vm-uuid{i}", name_label = $"VM {i}",
                power_state = vm_power_state.Halted, VBDs = [new($"source-vbd{i}")], VIFs = [new($"source-vif{i}")] }).ToArray();
            for (var i = 0; i < 2; i++)
            {
                _sourceVifs.Add($"source-vif{i}", new VIF { opaque_ref = $"source-vif{i}", uuid = $"vif-uuid{i}", VM = new($"source-vm{i}"), network = new("source-network"), device = "0", MAC = $"00:16:3e:00:00:0{i}" });
                _sourceVbds.Add($"source-vbd{i}", new VBD { opaque_ref = $"source-vbd{i}", uuid = $"vbd-uuid{i}", VM = new($"source-vm{i}"), VDI = new("source-disk"), userdevice = "0", type = vbd_type.Disk, mode = vbd_mode.RW });
            }
            _listener.Start();
            Session = new Session($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/")
                { APIVersion = API_Version.API_2_16, Timeout = 3000, opaque_ref = "target-session", Connection = Connection };
            typeof(Session).GetProperty(nameof(Session.IsLocalSuperuser))!.SetValue(Session, true);
            var field = typeof(XenConnection).GetField("connectTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var state = Activator.CreateInstance(field.FieldType, BindingFlags.Instance | BindingFlags.NonPublic, null, ["127.0.0.1", 1], null)!;
            field.FieldType.GetField("Connected")!.SetValue(state, true); field.FieldType.GetField("Session")!.SetValue(state, Session); field.SetValue(Connection, state);
            Workflow = new(Pool); _server = ServeAsync();
        }
        public Task<DrInspection> Inspect() => Workflow.InspectAsync(new("metadata", Metadata.uuid, Metadata.Name(), TargetSr.Name()));
        public DrRequest Request(DrInspection inspection, DrMode mode = DrMode.MetadataRehearsal) => new(mode,
            inspection.Vms.Select(vm => vm.Reference), [new("source-sr", "target-sr")], [new("source-network", "isolated")]);

        public void UseDistinctVmDisks()
        {
            _extraSourceDisks.Add(new VDI { opaque_ref = "source-disk-second", uuid = "disk-uuid-second", SR = new("source-sr"), location = "disk-location-second" });
            _extraTargetDisks.Add(new VDI { opaque_ref = "target-disk-second", uuid = "replica-disk-uuid-second", SR = new("target-sr"), location = "disk-location-second" });
            _sourceVbds["source-vbd1"].VDI = new("source-disk-second");
        }
        public string TargetDiskForVm(string reference) => _targetVbds[Recovered[reference].VBDs[0].opaque_ref].VDI.opaque_ref;

        public void EditDuringCleanup(string reference, string change)
        {
            var vm = Recovered[reference];
            switch (change)
            {
                case "new-vif":
                    TargetVifs.Add("operator-vif", new VIF { opaque_ref = "operator-vif", uuid = "operator-vif-uuid", VM = new(reference), network = new("isolated"), device = "7" });
                    vm.VIFs.Add(new("operator-vif"));
                    break;
                case "new-vbd":
                    _targetVbds.Add("operator-vbd", new VBD { opaque_ref = "operator-vbd", uuid = "operator-vbd-uuid", VM = new(reference), VDI = new("target-disk"), userdevice = "7", type = vbd_type.Disk });
                    vm.VBDs.Add(new("operator-vbd"));
                    break;
                case "rename": vm.name_label = "Operator adopted this VM"; break;
                case "config": vm.other_config["operator-owned"] = "retain"; break;
                case "remaining-vbd": _targetVbds[vm.VBDs[0].opaque_ref].VDI = new("operator-disk"); break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
        }

        private T Add<T>(string reference, T record) where T : XenObject<T>
        { Connection.Cache.UpdateFrom(Connection, [new ObjectChange(typeof(T), reference, record)]); return Connection.Resolve(new XenRef<T>(reference)); }
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
                    var rpc = JObject.Parse(new string(body)); lock (_requests) _requests.Add(rpc);
                    var response = new JObject { ["jsonrpc"] = "2.0", ["id"] = rpc["id"]!.DeepClone() };
                    try
                    {
                        if (Method(rpc) == FailMethod && (!FailOnlyMetadata || rpc["params"]![0]!.Value<string>() == "metadata-session")
                            || FailSecondRecover && Method(rpc) == "Async.VM.recover" && Recovered.Count == 1)
                            response["error"] = new JObject { ["code"] = 1, ["message"] = "OPERATION_NOT_ALLOWED", ["data"] = new JArray("injected failure") };
                        else
                        {
                            var result = Reply(rpc);
                            if (LostRecoverResponse && Method(rpc) == "Async.VM.recover")
                                response["error"] = new JObject { ["code"] = 1, ["message"] = "INTERNAL_ERROR", ["data"] = new JArray("server completed but response unavailable") };
                            else response["result"] = result;
                        }
                    }
                    catch (Exception error) { response["error"] = new JObject { ["code"] = 1, ["message"] = "INTERNAL_ERROR", ["data"] = new JArray(error.Message) }; }
                    var bytes = Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await stream.WriteAsync(bytes, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }
        private JToken? Reply(JObject rpc)
        {
            var p = (JArray)rpc["params"]!; var metadata = p[0]!.Value<string>() == "metadata-session";
            var reference = p.Count > 1 ? p[1]!.Value<string>()! : "";
            switch (Method(rpc))
            {
                case "session.get_is_local_superuser": return new JValue(ServerSuperuser);
                case "session.get_rbac_permissions": return new JArray(Permissions);
                case "session.logout": return null;
                case "VDI.open_database": return new JValue("metadata-session");
                case "pool.get_record": return Pool.ToJObject();
                case "pool.get_all_records": return Map(metadata ? [SourcePool] : new[] { Pool });
                case "host.get_record": return new Host { uuid = "host-uuid", API_version_major = 2, API_version_minor = 16 }.ToJObject();
                case "VM.get_all_records": return Map<VM>(metadata ? SourceVms : Recovered.Values);
                case "SR.get_all_records": return Map(metadata ? [_sourceSr] : new[] { TargetSr });
                case "network.get_all_records": return Map(metadata ? [_sourceNetwork] : IncludeImportNetwork ? new[] { Isolated, _importNetwork } : [Isolated]);
                case "PBD.get_all_records": return Map(new[] { Pbd });
                case "VDI.get_all_records": return Map(metadata ? new[] { _sourceDisk }.Concat(_extraSourceDisks) : new[] { Metadata, TargetDisk }.Concat(_extraTargetDisks));
                case "VBD.get_all_records": return Map(metadata ? _sourceVbds.Values : _targetVbds.Values);
                case "VIF.get_all_records": return Map(metadata ? _sourceVifs.Values : TargetVifs.Values);
                case "VDI.get_record": return reference == "metadata" ? Metadata.ToJObject() : reference == "target-disk" ? TargetDisk.ToJObject()
                    : _extraTargetDisks.SingleOrDefault(disk => disk.opaque_ref == reference)?.ToJObject()
                    ??
                    new VDI { uuid = "unreviewed-disk", SR = new("target-sr"), location = "unreviewed-location" }.ToJObject();
                case "VM.assert_can_be_recovered": return null;
                case "VM.get_by_uuid": return new JValue(Recovered.Single(pair => pair.Value.uuid == reference).Key);
                case "VM.get_record": return Recovered[reference].ToJObject();
                case "VIF.get_record": return TargetVifs[reference].ToJObject();
                case "VBD.get_record": return _targetVbds[reference].ToJObject();
                case "network.get_record": return (reference == "isolated" ? Isolated : _importNetwork).ToJObject();
                case "Async.VM.recover":
                    IncludeImportNetwork = true;
                    var source = SourceVms.Single(vm => vm.opaque_ref == reference); var index = Array.IndexOf(SourceVms, source); var vmRef = $"recovered{index}";
                    var vm = source.ToJObject().ToObject<VM>()!; vm.opaque_ref = vmRef; vm.VIFs = [new($"target-vif{index}")]; vm.VBDs = [new($"target-vbd{index}")]; Recovered.Add(vmRef, vm);
                    var vif = _sourceVifs[$"source-vif{index}"].ToJObject().ToObject<VIF>()!; vif.opaque_ref = $"target-vif{index}"; vif.VM = new(vmRef); vif.network = new("import-network"); TargetVifs.Add(vif.opaque_ref, vif); _importNetwork.VIFs.Add(new(vif.opaque_ref));
                    var vbd = _sourceVbds[$"source-vbd{index}"].ToJObject().ToObject<VBD>()!; vbd.opaque_ref = $"target-vbd{index}"; vbd.VM = new(vmRef);
                    vbd.VDI = new(RecoverWrongDisk ? "unreviewed-disk" : vbd.VDI.opaque_ref == "source-disk-second" ? "target-disk-second" : "target-disk"); _targetVbds.Add(vbd.opaque_ref, vbd);
                    AfterRecover?.Invoke(vm);
                    return new JValue("recovery-task" + index);
                case "VIF.move":
                    var moved = TargetVifs[reference]; _importNetwork.VIFs.RemoveAll(item => item.opaque_ref == reference); moved.network = new(p[2]!.Value<string>()!); Isolated.VIFs.Add(new(reference));
                    if (Requests.Count(request => Method(request) == "VIF.move") == 1) AfterFirstMove?.Invoke();
                    return null;
                case "VIF.destroy":
                    var vifVm = TargetVifs[reference].VM.opaque_ref;
                    Recovered[vifVm].VIFs.RemoveAll(item => item.opaque_ref == reference); TargetVifs.Remove(reference); Isolated.VIFs.RemoveAll(item => item.opaque_ref == reference);
                    AfterAttachmentDestroy?.Invoke("VIF.destroy", vifVm); return null;
                case "VBD.destroy":
                    var vbdVm = _targetVbds[reference].VM.opaque_ref;
                    Recovered[vbdVm].VBDs.RemoveAll(item => item.opaque_ref == reference); _targetVbds.Remove(reference);
                    AfterAttachmentDestroy?.Invoke("VBD.destroy", vbdVm); return null;
                case "VM.destroy": Recovered.Remove(reference); return null;
                case "task.remove_from_other_config": case "task.add_to_other_config": case "task.destroy": return null;
                case "task.get_allowed_operations": return new JArray();
                case "task.get_record": return new JObject { ["uuid"] = "task-uuid", ["status"] = "success", ["progress"] = 1.0, ["result"] = "", ["error_info"] = new JArray() };
                default: throw new InvalidOperationException("Unexpected RPC " + Method(rpc));
            }
        }
        private static JObject Map<T>(IEnumerable<T> records) where T : XenObject<T>
        { var map = new JObject(); foreach (var record in records) map[record.opaque_ref] = record.ToJObject(); return map; }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); _server.GetAwaiter().GetResult(); _stop.Dispose(); }
    }
}
