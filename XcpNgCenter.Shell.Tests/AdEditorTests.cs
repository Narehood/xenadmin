using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class AdEditorTests
{
    [Fact]
    public async Task ReviewIsReadOnlyAndEditsInvalidateTheApprovedPlan()
    {
        var f = new Fixture(); f.Ready();
        await f.Editor.ReviewCommand.ExecuteAsync(null); Assert.True(f.Editor.CanApply); Assert.Equal(0, f.Applies);
        f.Editor.SubjectName = "DOMAIN\\other"; Assert.False(f.Editor.CanApply);
        await f.Editor.ReviewCommand.ExecuteAsync(null); Assert.True(f.Editor.CanApply);
        f.Editor.Roles[0].IsSelected = false; Assert.False(f.Editor.CanApply);
    }

    [Fact]
    public async Task SubjectSelectionCannotAccidentallyBlockAddingAnotherUserWithSameRoles()
    {
        var f = new Fixture(); f.Ready();
        f.Editor.SelectedSubject = f.Snapshot.Subjects[0];
        f.Editor.SelectedOperation = f.Editor.Operations.Single(o => o.Operation == AdOperation.SetRoles);
        Assert.Contains("unchanged", f.Editor.ValidationMessage);
        f.Editor.SelectedOperation = f.Editor.Operations.Single(o => o.Operation == AdOperation.AddSubject);
        Assert.True(f.Editor.CanReview, f.Editor.ValidationMessage);
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        await f.Editor.ApplyCommand.ExecuteAsync(null);
        Assert.Null(f.Applied!.SubjectReference);
        Assert.Equal(AdOperation.AddSubject, f.Applied.Operation);
    }

    [Fact]
    public async Task SuccessfulApplyClosesAfterBusyIsCleared()
    {
        var f = new Fixture(); f.Ready(); await f.Editor.ReviewCommand.ExecuteAsync(null);
        await f.Editor.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(1, f.Applies); Assert.Equal(1, f.Closes); Assert.False(f.BusyAtClose);
        Assert.False(f.Editor.CanEdit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PasswordNeverEntersReviewOrConfirmationAndCredentialsAreCleared(bool confirm)
    {
        var f = new Fixture(joined: false) { Confirm = confirm }; f.Ready();
        f.Editor.Username = "transient-user"; f.Editor.Password = "transient-password";
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        Assert.Equal("transient-user", f.Reviewed!.CredentialUsername);
        Assert.DoesNotContain("transient-password", System.Text.Json.JsonSerializer.Serialize(f.Reviewed));
        await f.Editor.ApplyCommand.ExecuteAsync(null);
        Assert.DoesNotContain("transient", f.Confirmation!.Message);
        Assert.Empty(f.Editor.Password); Assert.Empty(f.Editor.Username);
        Assert.Equal(confirm ? 1 : 0, f.Applies);
        if (confirm) { Assert.NotNull(f.Credentials); Assert.Empty(f.Credentials!.Password); }
    }

    [Fact]
    public async Task LeaveCleanupChoiceCannotChangeAfterReviewWithoutAnotherReview()
    {
        var f = new Fixture(); f.Ready();
        f.Editor.SelectedOperation = f.Editor.Operations.Single(option => option.Operation == AdOperation.Leave);
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        var withoutCleanup = f.Reviewed!.Fingerprint;
        Assert.False(f.Reviewed.LeaveMachineAccountCleanup); Assert.True(f.Editor.CanApply);

        f.Editor.Username = " DOMAIN\\administrator "; f.Editor.Password = "directory-secret";
        Assert.False(f.Editor.CanApply);
        await f.Editor.ApplyCommand.ExecuteAsync(null); Assert.Equal(0, f.Applies);
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        Assert.True(f.Reviewed!.LeaveMachineAccountCleanup); Assert.Equal("DOMAIN\\administrator", f.Reviewed.CredentialUsername);
        Assert.NotEqual(withoutCleanup, f.Reviewed.Fingerprint); Assert.True(f.Editor.CanApply);

        f.Editor.Username = ""; f.Editor.Password = "";
        Assert.False(f.Editor.CanApply);
        await f.Editor.ApplyCommand.ExecuteAsync(null); Assert.Equal(0, f.Applies);
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        Assert.False(f.Reviewed!.LeaveMachineAccountCleanup); Assert.True(f.Editor.CanApply);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingReviewedCredentialAccountInvalidatesJoinAndLeave(bool joined)
    {
        var f = new Fixture(joined); f.Ready();
        if (joined) f.Editor.SelectedOperation = f.Editor.Operations.Single(option => option.Operation == AdOperation.Leave);
        f.Editor.Username = "administrator-one"; f.Editor.Password = "secret";
        await f.Editor.ReviewCommand.ExecuteAsync(null); Assert.True(f.Editor.CanApply);
        f.Editor.Username = " administrator-one "; Assert.True(f.Editor.CanApply);
        f.Editor.Username = "administrator-two"; Assert.False(f.Editor.CanApply);
        await f.Editor.ApplyCommand.ExecuteAsync(null); Assert.Equal(0, f.Applies);
        await f.Editor.ReviewCommand.ExecuteAsync(null); Assert.Equal("administrator-two", f.Reviewed!.CredentialUsername);
        Assert.True(f.Editor.CanApply);
    }

    [Fact]
    public async Task PasswordReplacementKeepsReviewedAccountAndCleanupChoiceWithoutHashingSecrets()
    {
        var f = new Fixture(); f.Ready();
        f.Editor.SelectedOperation = f.Editor.Operations.Single(option => option.Operation == AdOperation.Leave);
        f.Editor.Username = "administrator"; f.Editor.Password = "first-secret";
        await f.Editor.ReviewCommand.ExecuteAsync(null); var review = f.Reviewed;
        f.Editor.Password = ""; Assert.False(f.Editor.CanApply);
        f.Editor.Password = "different-secret"; Assert.True(f.Editor.CanApply);
        Assert.Same(review, f.Reviewed);
        var serialized = System.Text.Json.JsonSerializer.Serialize(review);
        Assert.DoesNotContain("first-secret", serialized); Assert.DoesNotContain("different-secret", serialized);
        await f.Editor.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(1, f.Applies); Assert.Same(review, f.Reviewed);
        Assert.Equal("administrator", f.Applied!.CredentialUsername); Assert.True(f.Applied.LeaveMachineAccountCleanup);
    }

    [Theory]
    [InlineData("   ", "", true, false)]
    [InlineData("   ", "secret", false, false)]
    [InlineData(" administrator ", "", false, true)]
    [InlineData(" administrator ", "secret", true, true)]
    public async Task LeaveCredentialPairUsesTrimmedUsernameConsistently(string username, string password, bool canApply, bool cleanup)
    {
        var f = new Fixture(); f.Ready();
        f.Editor.SelectedOperation = f.Editor.Operations.Single(option => option.Operation == AdOperation.Leave);
        f.Editor.Username = username; f.Editor.Password = password;
        await f.Editor.ReviewCommand.ExecuteAsync(null);
        Assert.Equal(canApply, f.Editor.CanApply); Assert.Equal(cleanup, f.Reviewed!.LeaveMachineAccountCleanup);
        Assert.Equal(username.Trim(), f.Reviewed.CredentialUsername);
        await f.Editor.ApplyCommand.ExecuteAsync(null); Assert.Equal(canApply ? 1 : 0, f.Applies);
    }

    [Fact]
    public async Task CancelledCredentialConfirmationRequiresReviewAfterAccountReentry()
    {
        var f = new Fixture(false) { Confirm = false }; f.Ready();
        f.Editor.Username = "administrator"; f.Editor.Password = "secret";
        await f.Editor.ReviewCommand.ExecuteAsync(null); await f.Editor.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(f.Editor.Username); Assert.Empty(f.Editor.Password);
        f.Editor.Username = "administrator"; f.Editor.Password = "secret";
        Assert.False(f.Editor.CanApply); Assert.True(f.Editor.CanReview);
        await f.Editor.ReviewCommand.ExecuteAsync(null); Assert.True(f.Editor.CanApply);
    }

    [Fact]
    public async Task PartialFailureInvalidatesPlanAndRequiresReopening()
    {
        var f = new Fixture { ApplyFailure = true }; f.Ready(); await f.Editor.ReviewCommand.ExecuteAsync(null);
        await f.Editor.ApplyCommand.ExecuteAsync(null);
        Assert.Contains("partial", f.Editor.StatusMessage); Assert.False(f.Editor.CanApply); Assert.False(f.Editor.CanReview);
        Assert.False(f.Editor.CanEdit); Assert.True(f.Editor.CloseCommand.CanExecute(null)); Assert.Equal(0, f.Closes);
        await f.Editor.ApplyCommand.ExecuteAsync(null); Assert.Equal(1, f.Applies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownWorkerOutcomeControlsRecoveryNoticeAndWhetherEditorRequiresReopening(bool mutationAttempted)
    {
        var f = new Fixture(false) { TypedApplyFailure = new AdActionException("Safe worker outcome", mutationAttempted) };
        f.Ready(); f.Editor.Username = "administrator"; f.Editor.Password = "secret";
        await f.Editor.ReviewCommand.ExecuteAsync(null); await f.Editor.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(1, f.Applies); Assert.Equal(0, f.Closes); Assert.False(f.Editor.CanApply);
        Assert.Contains("Safe worker outcome", f.Editor.StatusMessage);
        Assert.Equal(!mutationAttempted, f.Editor.CanEdit); Assert.Equal(!mutationAttempted, f.Editor.CanReview);
        Assert.Equal(mutationAttempted, f.Editor.StatusMessage.Contains(AdManagement.RecoveryNotice, StringComparison.Ordinal));
        Assert.Empty(f.Editor.Username); Assert.Empty(f.Editor.Password);
        Assert.True(f.Editor.CloseCommand.CanExecute(null));
    }

    [Fact]
    public async Task BusyReviewBlocksEditsCloseAndApplyAndSupportsCancellation()
    {
        var pending = new TaskCompletionSource<AdReview>();
        var f = new Fixture { PendingReview = pending }; f.Ready();
        var task = f.Editor.ReviewCommand.ExecuteAsync(null);
        Assert.True(f.Editor.IsBusy); Assert.False(f.Editor.CloseCommand.CanExecute(null));
        f.Editor.SubjectName = "ignored"; Assert.Equal("DOMAIN\\new", f.Editor.SubjectName);
        f.Editor.CloseCommand.Execute(null); Assert.Equal(0, f.Closes);
        f.Editor.CancelReviewCommand.Execute(null);
        pending.SetResult(new(f.Snapshot.Fingerprint, f.Reviewed!.Fingerprint, "sid", "Reviewed"));
        await task; Assert.False(f.Editor.CanApply); Assert.Contains("cancelled", f.Editor.StatusMessage);
    }

    [Fact]
    public async Task WrongReviewFingerprintCannotAuthorizeApply()
    {
        var f = new Fixture { WrongReview = true }; f.Ready(); await f.Editor.ReviewCommand.ExecuteAsync(null);
        Assert.False(f.Editor.CanApply); Assert.Contains("did not match", f.Editor.StatusMessage);
    }

    [Fact]
    public void ClosingWithoutApplyClearsCredentialsAndDoesNotMutate()
    {
        var f = new Fixture(false); f.Editor.Username = "user"; f.Editor.Password = "secret";
        f.Editor.CloseCommand.Execute(null); Assert.Empty(f.Editor.Password); Assert.Empty(f.Editor.Username);
        Assert.Equal(0, f.Applies); Assert.Equal(1, f.Closes);
        f.Editor.CloseCommand.Execute(null); Assert.Equal(1, f.Closes);
    }

    [Fact]
    public void RestrictedSessionCanInspectButCannotReviewOrApplyChanges()
    {
        var snapshot = Snapshot(true, false); using var editor = new AdEditorViewModel(snapshot,
            (_, _) => throw new Exception("must not review"), (_, _, _) => throw new Exception("must not apply"), _ => Task.FromResult(true), () => { });
        Assert.Single(editor.Subjects); Assert.Contains("Read-only", editor.ValidationMessage);
        Assert.False(editor.CanReview); Assert.False(editor.CanApply);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootRecoveryAcknowledgmentIsRequiredForEveryMutation(bool joined)
    {
        var f = new Fixture(joined); f.Editor.Domain = "example.org"; f.Editor.SubjectName = "DOMAIN\\new";
        f.Editor.Roles[0].IsSelected = true;
        Assert.Contains("root recovery", f.Editor.ValidationMessage);
        Assert.False(f.Editor.CanReview);
    }

    private static AdSnapshot Snapshot(bool joined, bool root = true) => new("pool", "pool-uuid", "Test pool", "host",
        [new("host", "host-uuid", "Host", "192.0.2.1", joined ? "AD" : "", joined ? "example.org" : "", false, false, true, false)],
        [new("subject", "subject-uuid", "target-sid", "DOMAIN\\existing", false, "read-only", "read")],
        [new("read", "read-uuid", "read-only", "Inspect without changes", true)], root, []);

    private sealed class Fixture
    {
        public AdSnapshot Snapshot { get; }
        public AdEditorViewModel Editor { get; }
        public int Applies { get; private set; }
        public int Closes { get; private set; }
        public bool BusyAtClose { get; private set; }
        public bool Confirm { get; init; } = true;
        public bool ApplyFailure { get; init; }
        public AdActionException? TypedApplyFailure { get; init; }
        public bool WrongReview { get; init; }
        public TaskCompletionSource<AdReview>? PendingReview { get; init; }
        public AdRequest? Reviewed { get; private set; }
        public AdRequest? Applied { get; private set; }
        public AdCredentials? Credentials { get; private set; }
        public ShellConfirmRequest? Confirmation { get; private set; }
        public Fixture(bool joined = true)
        {
            Snapshot = AdEditorTests.Snapshot(joined);
            Editor = new(Snapshot, (request, _) =>
            {
                Reviewed = request;
                return PendingReview?.Task ?? Task.FromResult(new AdReview(Snapshot.Fingerprint, WrongReview ? "wrong" : request.Fingerprint, "sid", "Reviewed plan"));
            }, (request, _, credentials) =>
            {
                Applies++; Applied = request; Credentials = credentials;
                if (TypedApplyFailure != null) throw TypedApplyFailure;
                if (ApplyFailure) throw new InvalidOperationException("partial failure");
                return Task.CompletedTask;
            }, request => { Confirmation = request; return Task.FromResult(Confirm); }, () => { Closes++; BusyAtClose = Editor!.IsBusy; });
        }
        public void Ready()
        {
            Editor.Domain = "example.org"; Editor.SubjectName = "DOMAIN\\new";
            Editor.Roles[0].IsSelected = true; Editor.RecoveryConfirmed = true;
        }
    }
}
