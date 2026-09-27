using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Xunit;
using VM = XenAPI.VM;

namespace XcpNgCenter.Shell.Tests;

public sealed class HaEditorTests
{
    private static readonly HaSrOption Shared = new("sr", "sr-uuid", "Shared storage", null);
    private static readonly HaSrOption OtherShared = new("sr2", "sr2-uuid", "Other shared storage", null);
    private static readonly HaSrOption Ineligible = new("local", "local-uuid", "Local storage", "Storage is not shared.");
    private static PoolHaPriorityOption Policy(VM.HaRestartPriority value) => HaVmDraft.Priorities.Single(option => option.Priority == value);
    private static HaSnapshot Snapshot(bool enabled = false, VM.HaRestartPriority? priority = VM.HaRestartPriority.Restart) => new(
        "pool", "pool-uuid", "Test pool", enabled, 1, 3,
        [new("vm", "vm-uuid", "Protected VM", priority, 17, 43, priority == null ? "legacy-value" : "restart")],
        [Shared, OtherShared, Ineligible], enabled ? Shared.Reference : null);

    private static HaReview Successful(HaRequest request) => new(request, [Shared, OtherShared, Ineligible],
        [new("vm", true, null)], request.Operation == HaOperation.Disable ? null : 2,
        request.Operation == HaOperation.Enable && request.HeartbeatSrReference == null ? "Select heartbeat storage." : null);

    [Fact]
    public void DraftEditsAndCancelLeaveCapturedPoliciesAndStartupSettingsUntouched()
    {
        var snapshot = Snapshot();
        var f = new Fixture(snapshot);
        f.Editor.FailuresToTolerate = "2";
        f.Editor.SelectedHeartbeatSr = OtherShared;
        f.Editor.Vms[0].SelectedPriority = Policy(VM.HaRestartPriority.DoNotRestart);
        f.Editor.CancelCommand.Execute(null);
        Assert.Equal(1, snapshot.FailuresToTolerate);
        Assert.Equal(VM.HaRestartPriority.Restart, snapshot.Vms[0].Priority);
        Assert.Equal(17, snapshot.Vms[0].Order);
        Assert.Equal(43, snapshot.Vms[0].StartDelay);
        Assert.Empty(f.Reviews);
        Assert.Empty(f.Applies);
        Assert.Equal(1, f.Closes);
        Assert.False(f.Editor.CanClose);
        f.Editor.CancelCommand.Execute(null);
        Assert.Equal(1, f.Closes);
    }

    [Fact]
    public async Task EnableRequiresDiscoveryThenAnExplicitReviewedHeartbeatSelection()
    {
        var f = new Fixture();
        Assert.True(f.Editor.CanReview);
        Assert.Null(f.Editor.SelectedHeartbeatSr);
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        Assert.Null(Assert.Single(f.Reviews).HeartbeatSrReference);
        Assert.False(f.Editor.CanApply);
        Assert.Contains("Select heartbeat", f.Editor.ReviewError);
        Assert.Contains(Ineligible, f.Editor.HeartbeatCandidates);
        f.Editor.SelectedHeartbeatSr = Shared;
        Assert.False(f.Editor.HasReview);
        Assert.False(f.Editor.CanApply);
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        Assert.True(f.Editor.CanApply);
        await f.Editor.ApplyCommand.ExecuteAsync(null);
        var applied = Assert.Single(f.Applies);
        Assert.Equal(HaOperation.Enable, applied.Request.Operation);
        Assert.Equal(Shared.Reference, applied.Request.HeartbeatSrReference);
        Assert.Equal(17, applied.Request.VmSettings[0].Order);
        Assert.Equal(43, applied.Request.VmSettings[0].StartDelay);
        Assert.Contains("Shared storage", Assert.Single(f.Confirmations).Message);
        Assert.Equal(1, f.Closes);
    }

    [Theory]
    [InlineData("tolerance")]
    [InlineData("storage")]
    [InlineData("policy")]
    public async Task EachDraftChangeInvalidatesThePreviousCapacityApproval(string field)
    {
        var f = new Fixture();
        f.Editor.SelectedHeartbeatSr = Shared;
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        Assert.True(f.Editor.CanApply);
        switch (field)
        {
            case "tolerance": f.Editor.FailuresToTolerate = "2"; break;
            case "storage": f.Editor.SelectedHeartbeatSr = OtherShared; break;
            default: f.Editor.Vms[0].SelectedPriority = Policy(VM.HaRestartPriority.BestEffort); break;
        }
        Assert.False(f.Editor.HasReview);
        Assert.False(f.Editor.CanApply);
        Assert.Equal("Not reviewed.", f.Editor.Vms[0].ReviewNotice);
        await f.Editor.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(f.Applies);
        Assert.Empty(f.Confirmations);
    }

    [Fact]
    public async Task UnchangedEnabledPoolCanBeReviewedWithoutApplyingANoOp()
    {
        var f = new Fixture(Snapshot(enabled: true));
        Assert.False(f.Editor.HasChanges);
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        Assert.True(f.Editor.HasReview);
        Assert.False(f.Editor.CanApply);
        await f.Editor.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(f.Applies);
    }

    [Fact]
    public async Task ConfigureKeepsHeartbeatAndStartupSettingsWhileChangingPolicy()
    {
        var f = new Fixture(Snapshot(enabled: true));
        f.Editor.Vms[0].SelectedPriority = Policy(VM.HaRestartPriority.BestEffort);
        f.Editor.FailuresToTolerate = "2";
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        await f.Editor.ApplyCommand.ExecuteAsync(null);
        var request = Assert.Single(f.Applies).Request;
        Assert.Equal(HaOperation.Configure, request.Operation);
        Assert.Null(request.HeartbeatSrReference);
        Assert.Equal(2, request.FailuresToTolerate);
        Assert.Equal(new HaVmSetting("vm", VM.HaRestartPriority.BestEffort, 17, 43), Assert.Single(request.VmSettings));
        Assert.Contains("Protected VM: Restart → Best effort", Assert.Single(f.Confirmations).Message);
    }

    [Fact]
    public void UnknownPolicyIsDisplayedWithoutSelectingAReplacement()
    {
        var f = new Fixture(Snapshot(priority: null));
        var row = Assert.Single(f.Editor.Vms);
        Assert.Null(row.SelectedPriority);
        Assert.True(row.RequiresExplicitPolicy);
        Assert.Contains("legacy-value", row.CurrentPolicy);
        Assert.False(f.Editor.CanReview);
        row.SelectedPriority = Policy(VM.HaRestartPriority.DoNotRestart);
        Assert.True(f.Editor.CanReview);
        Assert.False(row.RequiresExplicitPolicy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("3")]
    [InlineData("bad")]
    public async Task InvalidToleranceCannotStartAReviewOrMutation(string tolerance)
    {
        var f = new Fixture();
        f.Editor.FailuresToTolerate = tolerance;
        Assert.False(f.Editor.CanReview);
        Assert.True(f.Editor.HasValidationMessage);
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        await f.Editor.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(f.Reviews);
        Assert.Empty(f.Applies);
    }

    [Fact]
    public async Task DisableBypassesUnrelatedInvalidDraftAndKeepsOriginalPolicies()
    {
        var f = new Fixture(Snapshot(enabled: true, priority: null));
        f.Editor.FailuresToTolerate = "bad";
        f.Editor.Vms[0].SelectedPriority = Policy(VM.HaRestartPriority.DoNotRestart);
        f.Editor.SelectedOperation = f.Editor.Operations.Single(option => option.Operation == HaOperation.Disable);
        Assert.True(f.Editor.CanReview);
        Assert.False(f.Editor.HasValidationMessage);
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        Assert.True(f.Editor.CanApply);
        await f.Editor.ApplyCommand.ExecuteAsync(null);
        var request = Assert.Single(f.Applies).Request;
        Assert.Equal(HaOperation.Disable, request.Operation);
        Assert.Equal(1, request.FailuresToTolerate);
        Assert.Null(request.HeartbeatSrReference);
        Assert.Null(request.VmSettings[0].Priority);
        Assert.Equal(17, request.VmSettings[0].Order);
        Assert.Equal(43, request.VmSettings[0].StartDelay);
        Assert.Contains("retained", Assert.Single(f.Confirmations).Message);
    }

    [Fact]
    public async Task ServerCapacityAndAgilityFindingsBlockApplyAndRemainVisible()
    {
        var f = new Fixture(review: (request, _) => Task.FromResult(new HaReview(request, [Ineligible],
            [new("vm", false, "VM uses a local disk.")], 0, "Requested tolerance exceeds capacity.")));
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        Assert.False(f.Editor.CanApply);
        Assert.Contains("exceeds capacity", f.Editor.ReviewError);
        Assert.Contains("local disk", f.Editor.Vms[0].ReviewNotice);
        Assert.Contains("up to 0", f.Editor.ReviewSummary);
        f.Editor.SelectedHeartbeatSr = Ineligible;
        Assert.Contains("not shared", f.Editor.HeartbeatNotice);
        Assert.False(f.Editor.HasReview);
    }

    [Fact]
    public async Task ReviewForAnotherDraftCannotEnableApply()
    {
        var f = new Fixture(review: (request, _) => Task.FromResult(Successful(new HaRequest(
            request.Operation, request.FailuresToTolerate + 1, request.HeartbeatSrReference, request.VmSettings))));
        f.Editor.SelectedHeartbeatSr = Shared;
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        Assert.False(f.Editor.HasReview);
        Assert.False(f.Editor.CanApply);
        Assert.Contains("does not match", f.Editor.StatusMessage);
    }

    [Fact]
    public async Task DeclinedConfirmationDoesNotMutateAndRetainsMatchingReview()
    {
        var f = new Fixture(confirm: _ => Task.FromResult(false));
        f.Editor.SelectedHeartbeatSr = Shared;
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        await f.Editor.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(f.Applies);
        Assert.Equal(0, f.Closes);
        Assert.True(f.Editor.HasReview);
        Assert.True(f.Editor.CanApply);
        Assert.False(f.Editor.IsBusy);
    }

    [Fact]
    public async Task FailedApplyKeepsDraftButRequiresFreshReviewBeforeRetry()
    {
        var f = new Fixture(apply: (_, _) => throw new InvalidOperationException("Connection was lost."));
        f.Editor.SelectedHeartbeatSr = Shared;
        f.Editor.Vms[0].SelectedPriority = Policy(VM.HaRestartPriority.BestEffort);
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        await f.Editor.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(0, f.Closes);
        Assert.Equal(VM.HaRestartPriority.BestEffort, f.Editor.Vms[0].SelectedPriority!.Priority);
        Assert.False(f.Editor.HasReview);
        Assert.False(f.Editor.CanApply);
        Assert.True(f.Editor.CanReview);
        Assert.Contains("Some settings may have applied", f.Editor.StatusMessage);
        Assert.Contains("reopen this editor", f.Editor.StatusMessage);
        await f.Editor.ApplyCommand.ExecuteAsync(null);
        Assert.Single(f.Applies);
    }

    [Fact]
    public async Task BusyConfirmationAndMutationRejectEditsCloseAndDuplicateApply()
    {
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = new Fixture(confirm: _ => confirmation.Task, apply: (_, _) => mutation.Task);
        f.Editor.SelectedHeartbeatSr = Shared;
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        var applying = f.Editor.ApplyCommand.ExecuteAsync(null);
        Assert.True(f.Editor.IsSaving);
        Assert.False(f.Editor.CanClose);
        f.Editor.FailuresToTolerate = "2";
        f.Editor.SelectedHeartbeatSr = OtherShared;
        f.Editor.Vms[0].SelectedPriority = Policy(VM.HaRestartPriority.DoNotRestart);
        f.Editor.CancelCommand.Execute(null);
        await f.Editor.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("1", f.Editor.FailuresToTolerate);
        Assert.Equal(Shared.Reference, f.Editor.SelectedHeartbeatSr!.Reference);
        Assert.Equal(VM.HaRestartPriority.Restart, f.Editor.Vms[0].SelectedPriority!.Priority);
        Assert.Single(f.Confirmations);
        Assert.Empty(f.Applies);
        confirmation.SetResult(true);
        await f.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(f.Editor.IsSaving);
        Assert.False(f.Editor.CanReview);
        await f.Editor.ApplyCommand.ExecuteAsync(null);
        Assert.Single(f.Applies);
        Assert.Equal(0, f.Closes);
        mutation.SetResult();
        await applying;
        Assert.False(f.Editor.IsBusy);
        Assert.Equal(1, f.Closes);
    }

    [Fact]
    public async Task CancelReviewWaitsForTheActiveReadAndDiscardsItsResult()
    {
        var response = new TaskCompletionSource<HaReview>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        HaRequest? pending = null;
        var f = new Fixture(review: (request, cancellation) => { pending = request; token = cancellation; return response.Task; });
        f.Editor.SelectedHeartbeatSr = Shared;
        var reviewing = f.Editor.ReviewCommand.ExecuteAsync(null);
        Assert.True(f.Editor.IsReviewing);
        Assert.True(f.Editor.CanCancelReview);
        f.Editor.CancelReviewCommand.Execute(null);
        Assert.True(token.IsCancellationRequested);
        Assert.True(f.Editor.IsBusy);
        Assert.False(f.Editor.CanClose);
        Assert.False(f.Editor.CanCancelReview);
        f.Editor.FailuresToTolerate = "2";
        f.Editor.CancelCommand.Execute(null);
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        Assert.Equal("1", f.Editor.FailuresToTolerate);
        Assert.Single(f.Reviews);
        Assert.Equal(0, f.Closes);
        response.SetResult(Successful(pending!));
        await reviewing;
        Assert.False(f.Editor.HasReview);
        Assert.False(f.Editor.CanApply);
        Assert.True(f.Editor.CanReview);
        Assert.True(f.Editor.CanClose);
        Assert.Contains("cancelled", f.Editor.StatusMessage);
    }

    [Fact]
    public async Task FailedDisableReviewDoesNotClaimTheDisableWasReviewedSuccessfully()
    {
        var f = new Fixture(Snapshot(enabled: true), review: (request, _) => Task.FromResult(
            new HaReview(request, [], [], null, "Permission denied.")));
        f.Editor.SelectedOperation = f.Editor.Operations.Single(option => option.Operation == HaOperation.Disable);
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        Assert.False(f.Editor.CanApply);
        Assert.Contains("did not pass", f.Editor.ReviewSummary);
        Assert.Contains("Permission denied", f.Editor.ReviewError);
    }

    private sealed class Fixture
    {
        public HaEditorViewModel Editor { get; }
        public List<HaRequest> Reviews { get; } = [];
        public List<(HaRequest Request, HaReview Review)> Applies { get; } = [];
        public List<ShellConfirmRequest> Confirmations { get; } = [];
        public TaskCompletionSource ApplyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Closes { get; private set; }

        public Fixture(HaSnapshot? snapshot = null, Func<HaRequest, CancellationToken, Task<HaReview>>? review = null,
            Func<HaRequest, HaReview, Task>? apply = null, Func<ShellConfirmRequest, Task<bool>>? confirm = null)
        {
            Editor = new(snapshot ?? Snapshot(), (request, cancellation) =>
            {
                Reviews.Add(request);
                return review?.Invoke(request, cancellation) ?? Task.FromResult(Successful(request));
            }, (request, checkedReview) =>
            {
                Applies.Add((request, checkedReview));
                ApplyStarted.TrySetResult();
                return apply?.Invoke(request, checkedReview) ?? Task.CompletedTask;
            }, request =>
            {
                Confirmations.Add(request);
                return confirm?.Invoke(request) ?? Task.FromResult(true);
            }, () => Closes++);
        }
    }
}
