using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class DrEditorTests
{
    [Fact]
    public async Task InspectionBuildsExplicitVmAndNicSelectionAndOnlyMatchingStorage()
    {
        var f = new FakeWorkflow(); var vm = Editor(f);
        await vm.DiscoverCommand.ExecuteAsync(null); vm.SelectedMetadata = Assert.Single(vm.Metadata);
        await vm.InspectCommand.ExecuteAsync(null);
        Assert.True(vm.HasInspection); Assert.False(vm.CanReview); Assert.True(vm.IsRehearsal);
        vm.Vms[0].IsSelected = true;
        Assert.Single(vm.Storage); Assert.Equal("target-sr", vm.Storage[0].SelectedTarget!.Reference);
        Assert.Single(vm.Networks); Assert.Null(vm.Networks[0].SelectedTarget);
        Assert.Single(vm.Networks[0].Targets);
        await vm.ReviewCommand.ExecuteAsync(null);
        Assert.False(vm.CanRecover); Assert.Equal(0, f.Reviews); Assert.Contains("destination", vm.Error);
    }

    [Fact]
    public async Task MappingAndModeChangesInvalidateSuccessfulReview()
    {
        var f = new FakeWorkflow(); var vm = await Ready(f);
        await vm.ReviewCommand.ExecuteAsync(null); Assert.True(vm.CanRecover);
        vm.IsRehearsal = false; Assert.False(vm.CanRecover); Assert.Equal(2, vm.Networks[0].Targets.Count);
        vm.Networks[0].SelectedTarget = vm.Networks[0].Targets.Single(network => !network.IsIsolated);
        await vm.ReviewCommand.ExecuteAsync(null); Assert.True(vm.CanRecover);
        vm.IsRehearsal = true; Assert.Null(vm.Networks[0].SelectedTarget); Assert.False(vm.CanRecover);
    }

    [Fact]
    public async Task SelectingDifferentMetadataDropsInspectedVmAndReview()
    {
        var f = new FakeWorkflow(); var vm = await Ready(f);
        await vm.ReviewCommand.ExecuteAsync(null); vm.SelectedMetadata = null;
        Assert.False(vm.HasInspection); Assert.Empty(vm.Vms); Assert.False(vm.CanRecover); Assert.Empty(vm.Networks);
    }

    [Fact]
    public async Task FailedInspectionCannotLeavePreviousApprovalVisible()
    {
        var f = new FakeWorkflow(); var vm = await Ready(f); await vm.ReviewCommand.ExecuteAsync(null);
        f.InspectionFailure = true; await vm.InspectCommand.ExecuteAsync(null);
        Assert.False(vm.HasInspection); Assert.False(vm.CanRecover); Assert.Empty(vm.Vms); Assert.Contains("metadata unavailable", vm.Error);
    }

    [Fact]
    public async Task ChangedDraftDuringConfirmationNeverExecutes()
    {
        var f = new FakeWorkflow(); DrEditorViewModel? vm = null;
        vm = await Ready(f, _ => { vm!.Vms[0].IsSelected = false; return Task.FromResult(true); });
        await vm.ReviewCommand.ExecuteAsync(null); await vm.RecoverCommand.ExecuteAsync(null);
        Assert.Equal(0, f.Recoveries); Assert.False(vm.CanRecover); Assert.Contains("changed", vm.Error);
    }

    [Fact]
    public async Task DecliningConfirmationDoesNotFreezeDraftOrRecover()
    {
        var f = new FakeWorkflow(); var vm = await Ready(f, _ => Task.FromResult(false));
        await vm.ReviewCommand.ExecuteAsync(null); await vm.RecoverCommand.ExecuteAsync(null);
        Assert.True(vm.CanEdit); Assert.True(vm.CanRecover); Assert.Equal(0, f.Recoveries);
    }

    [Theory]
    [InlineData(true, "Isolated rehearsal", "isolated-uuid", "isolated")]
    [InlineData(false, "Production", "production-uuid", "not isolated")]
    public async Task ConfirmationRestatesPerVmStorageAndNetworkMappings(bool rehearsal, string networkName, string networkUuid, string isolation)
    {
        ShellConfirmRequest? confirmation = null;
        var f = new FakeWorkflow(); var vm = await Ready(f, request => { confirmation = request; return Task.FromResult(false); });
        vm.IsRehearsal = rehearsal;
        vm.Networks[0].SelectedTarget = vm.Networks[0].Targets.Single(target => target.Uuid == networkUuid);
        await vm.ReviewCommand.ExecuteAsync(null); await vm.RecoverCommand.ExecuteAsync(null);

        Assert.NotNull(confirmation);
        Assert.Contains("VM [vm-uuid]", confirmation.Message);
        Assert.Contains("Original storage [sr-uuid]", confirmation.Message);
        Assert.Contains("Replicated storage [sr-uuid]", confirmation.Message);
        Assert.Contains("Original network [network-uuid]", confirmation.Message);
        Assert.Contains($"{networkName} [{networkUuid}]", confirmation.Message);
        Assert.Contains(isolation, confirmation.Message);
        Assert.Contains("VMs remain halted", confirmation.Message);
        Assert.Contains("does not copy disks", confirmation.Message);
        Assert.Equal(0, f.Recoveries);
    }

    [Theory]
    [InlineData("storage")]
    [InlineData("network")]
    public async Task MissingConfirmationTargetFailsClosedEvenIfWorkflowReturnsReview(string missing)
    {
        var confirmations = 0;
        var f = new FakeWorkflow(); var vm = await Ready(f, _ => { confirmations++; return Task.FromResult(true); });
        if (missing == "storage") vm.Storage[0].SelectedTarget = new("missing", "sr-uuid", "Missing storage");
        else vm.Networks[0].SelectedTarget = new("missing", "missing-uuid", "Missing network", true);
        await vm.ReviewCommand.ExecuteAsync(null); await vm.RecoverCommand.ExecuteAsync(null);
        Assert.Equal(0, f.Recoveries); Assert.Equal(0, confirmations); Assert.False(vm.CanRecover); Assert.True(vm.HasError);
    }

    [Fact]
    public async Task PendingRecoveryPreventsCloseAndEditingThenRetainsCleanupReceipt()
    {
        var f = new FakeWorkflow { RecoveryCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = await Ready(f); await vm.ReviewCommand.ExecuteAsync(null);
        var running = vm.RecoverCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy); Assert.False(vm.CanEdit); Assert.False(vm.CloseCommand.CanExecute(null)); Assert.False(vm.CleanupCommand.CanExecute(null));
        f.RecoveryCompletion.SetResult(FakeWorkflow.Success()); await running;
        Assert.False(vm.IsBusy); Assert.False(vm.CanEdit); Assert.True(vm.CanCleanup); Assert.True(vm.CanClose);
    }

    [Fact]
    public async Task RecoveryFailureKeepsReportAndRequiresReopeningBeforeAnotherAttempt()
    {
        var f = new FakeWorkflow { RecoveryError = true }; var vm = await Ready(f); await vm.ReviewCommand.ExecuteAsync(null);
        await vm.RecoverCommand.ExecuteAsync(null);
        Assert.Equal(1, f.Recoveries); Assert.False(vm.CanEdit); Assert.False(vm.CanRecover); Assert.True(vm.HasReport);
        Assert.Contains("lost response", vm.Error); Assert.True(vm.CanClose);
        await vm.RecoverCommand.ExecuteAsync(null); Assert.Equal(1, f.Recoveries);
    }

    [Fact]
    public async Task CleanupFailurePreservesPartialReportAndDisablesRetry()
    {
        var f = new FakeWorkflow { CleanupError = true }; var vm = await Ready(f); await vm.ReviewCommand.ExecuteAsync(null);
        await vm.RecoverCommand.ExecuteAsync(null); Assert.True(vm.CanCleanup);
        await vm.CleanupCommand.ExecuteAsync(null);
        Assert.False(vm.CanCleanup); Assert.Equal(1, f.Cleanups); Assert.Contains("partial cleanup", vm.Error);
        Assert.Contains("Recovered halted", vm.Report); Assert.Contains("partial cleanup", vm.Report);
        await vm.CleanupCommand.ExecuteAsync(null); Assert.Equal(1, f.Cleanups);
    }

    [Fact]
    public async Task ReviewErrorCannotAuthorizeRecovery()
    {
        var f = new FakeWorkflow { ReviewError = true }; var vm = await Ready(f); await vm.ReviewCommand.ExecuteAsync(null);
        Assert.False(vm.CanRecover); Assert.True(vm.CanEdit); Assert.Contains("server review failed", vm.Error);
    }

    [Theory]
    [InlineData("wrong-storage")]
    [InlineData("production-network")]
    [InlineData("duplicate-vm")]
    [InlineData("unsupported-vm")]
    [InlineData("missing-mapping")]
    public void BackendRejectsTamperedOrUnsupportedRequests(string change)
    {
        var inspection = FakeWorkflow.Inspection();
        var selected = change == "duplicate-vm" ? new[] { "vm", "vm" } : change == "unsupported-vm" ? ["unsupported"] : new[] { "vm" };
        var request = new DrRequest(DrMode.MetadataRehearsal, selected,
            change == "missing-mapping" ? [] : [new("source-sr", change == "wrong-storage" ? "unrelated-sr" : "target-sr")],
            [new("source-network", change == "production-network" ? "production" : "isolated")]);
        Assert.Throws<InvalidOperationException>(() => DrManagement.ValidateRequest(inspection, request));
    }

    [Fact]
    public void RequestOwnsImmutableCopiesOfCallerLists()
    {
        var vms = new List<string> { "vm" }; var storage = new List<DrStorageMapping> { new("s", "t") };
        var request = new DrRequest(DrMode.Recovery, vms, storage, []); vms.Clear(); storage.Clear();
        Assert.Single(request.VmReferences); Assert.Single(request.Storage);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)request.VmReferences).Clear());
    }

    private static DrEditorViewModel Editor(FakeWorkflow f, Func<ShellConfirmRequest, Task<bool>>? confirm = null) =>
        new(f, confirm ?? (_ => Task.FromResult(true)), () => { });
    private static async Task<DrEditorViewModel> Ready(FakeWorkflow f, Func<ShellConfirmRequest, Task<bool>>? confirm = null)
    {
        var vm = Editor(f, confirm); await vm.DiscoverCommand.ExecuteAsync(null); vm.SelectedMetadata = vm.Metadata[0];
        await vm.InspectCommand.ExecuteAsync(null); vm.Vms[0].IsSelected = true; vm.Networks[0].SelectedTarget = vm.Networks[0].Targets[0]; return vm;
    }
    private sealed class FakeWorkflow : IDrWorkflow
    {
        public string PoolName => "Recovery pool";
        public int Reviews, Recoveries, Cleanups;
        public bool InspectionFailure, ReviewError, RecoveryError, CleanupError;
        public TaskCompletionSource<DrOutcome>? RecoveryCompletion;
        public static DrInspection Inspection() => new(new("metadata", "metadata-uuid", "Metadata", "Shared storage"), "Original pool", "source-pool-uuid",
            [new("vm", "vm-uuid", "VM", null, ["source-sr"], ["source-network"]), new("unsupported", "unsupported-uuid", "Appliance member", "Use appliance workflow", ["source-sr"], ["source-network"])],
            [new("source-sr", "sr-uuid", "Original storage")], [new("source-network", "network-uuid", "Original network")],
            [new("target-sr", "sr-uuid", "Replicated storage"), new("unrelated-sr", "other-uuid", "Unrelated storage")],
            [new("isolated", "isolated-uuid", "Isolated rehearsal", true), new("production", "production-uuid", "Production", false)]);
        public static DrOutcome Success() => new(DrMode.MetadataRehearsal, [new("VM", "vm-uuid", "Recovered halted", true)], [new("target-vm", "vm-uuid", "VM", "fingerprint")]);
        public Task<IReadOnlyList<DrMetadataOption>> DiscoverAsync() => Task.FromResult<IReadOnlyList<DrMetadataOption>>([Inspection().Metadata]);
        public Task<DrInspection> InspectAsync(DrMetadataOption metadata) => InspectionFailure ? Task.FromException<DrInspection>(new InvalidOperationException("metadata unavailable")) : Task.FromResult(Inspection());
        public Task<DrReview> ReviewAsync(DrInspection inspection, DrRequest request)
        { Reviews++; return ReviewError ? Task.FromException<DrReview>(new InvalidOperationException("server review failed")) : Task.FromResult(new DrReview(request.Fingerprint, inspection.Fingerprint, "target", "Reviewed")); }
        public Task<DrOutcome> RecoverAsync(DrInspection inspection, DrRequest request, DrReview review)
        { Recoveries++; return RecoveryCompletion?.Task ?? Task.FromResult(RecoveryError ? new DrOutcome(request.Mode, [new("VM", "vm-uuid", "unknown", false)], [], "lost response") : Success()); }
        public Task<DrOutcome> CleanupAsync(DrOutcome outcome)
        { Cleanups++; return Task.FromResult(CleanupError ? new DrOutcome(outcome.Mode, [], outcome.Cleanup, "partial cleanup") : new DrOutcome(outcome.Mode, [new("VM", "vm-uuid", "Removed; disks retained", true)], [])); }
    }
}
