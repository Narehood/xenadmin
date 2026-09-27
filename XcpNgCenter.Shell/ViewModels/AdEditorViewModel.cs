using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public sealed record AdOperationOption(AdOperation Operation, string Label);
public sealed class AdRoleDraft(AdRoleOption role, Func<bool> editable, Action changed) : ObservableObject
{
    private bool _selected;
    public AdRoleOption Role { get; } = role;
    public string Name => Role.Name;
    public string Description => Role.Description;
    public bool IsSelected
    {
        get => _selected;
        set { if (editable() && SetProperty(ref _selected, value)) changed(); }
    }
    internal void Select(bool value) => SetProperty(ref _selected, value, nameof(IsSelected));
}

public sealed class AdEditorViewModel : ViewModelBase, IDisposable
{
    private readonly AdSnapshot _snapshot;
    private readonly Func<AdRequest, CancellationToken, Task<AdReview>> _reviewAsync;
    private readonly Func<AdRequest, AdReview, AdCredentials, Task> _applyAsync;
    private readonly Func<ShellConfirmRequest, Task<bool>> _confirm;
    private readonly Action _close;
    private AdOperationOption _operation;
    private AdSubjectOption? _subject;
    private string _domain = "", _subjectName = "", _username = "", _password = "", _status = "";
    private bool _recoveryConfirmed, _busy, _saving, _closed, _requiresReopen;
    private AdReview? _review;
    private CancellationTokenSource? _cancellation;

    public AdEditorViewModel(AdSnapshot snapshot, Func<AdRequest, CancellationToken, Task<AdReview>> review,
        Func<AdRequest, AdReview, AdCredentials, Task> apply, Func<ShellConfirmRequest, Task<bool>> confirm, Action close)
    {
        _snapshot = snapshot; _reviewAsync = review; _applyAsync = apply; _confirm = confirm; _close = close;
        Operations = snapshot.HasExternalAuth
            ? [new(AdOperation.AddSubject, "Add user or group"), new(AdOperation.SetRoles, "Change roles"), new(AdOperation.RemoveSubject, "Remove access"), new(AdOperation.Leave, "Leave domain")]
            : [new(AdOperation.Join, "Join domain")];
        _operation = Operations[0];
        Roles = snapshot.Roles.Where(role => role.Assignable).OrderBy(role => role.Name, StringComparer.Ordinal)
            .Select(role => new AdRoleDraft(role, () => CanEdit, Changed)).ToArray();
        ReviewCommand = new AsyncRelayCommand(ReviewAsync, () => CanReview);
        CancelReviewCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsBusy && !IsSaving);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => CanApply);
        CloseCommand = new RelayCommand(Close, () => !IsBusy && !_closed);
    }

    public string PoolName => _snapshot.PoolName;
    public string HostSummary => string.Join('\n', _snapshot.Hosts.Select(host => $"{host.Name} ({host.Address}): {(host.AuthType.Length == 0 ? "local authentication" : $"{host.AuthType} / {host.Domain}")}{(host.Live ? "" : " — offline")}"));
    public string RecoveryInstructions => AdManagement.RecoveryInstructions;
    public string SessionNotice => _snapshot.IsLocalSuperuser ? "Connected as local root. Keep this recovery session available."
        : "Directory session: subject management requires pool administrator permissions. Join/leave and changes to your own user or groups require local root.";
    public IReadOnlyList<AdOperationOption> Operations { get; }
    public IReadOnlyList<AdSubjectOption> Subjects => _snapshot.Subjects;
    public IReadOnlyList<AdRoleDraft> Roles { get; }
    public bool HasSubjects => Subjects.Count > 0;
    public bool IsJoining => SelectedOperation.Operation == AdOperation.Join;
    public bool IsLeaving => SelectedOperation.Operation == AdOperation.Leave;
    public bool IsAdding => SelectedOperation.Operation == AdOperation.AddSubject;
    public bool IsSubjectOperation => !IsJoining && !IsLeaving;
    public bool ShowRoles => SelectedOperation.Operation is AdOperation.AddSubject or AdOperation.SetRoles;
    public bool ShowCredentials => IsJoining || IsLeaving;
    public bool IsBusy => _busy;
    public bool IsSaving => _saving;
    public bool CanEdit => !IsBusy && !_closed && !_requiresReopen;
    public bool CanReview => CanEdit && ValidationMessage.Length == 0;
    public bool CanApply => CanReview && _review is { CanApply: true } && _review.RequestFingerprint == BuildRequest().Fingerprint
        && (!IsJoining || Username.Trim().Length > 0 && Password.Length > 0)
        && (!IsLeaving || (Username.Trim().Length == 0) == (Password.Length == 0));
    public string StatusMessage => _status;
    public string ReviewSummary => _review?.Summary ?? "Review the operation against the server before applying it.";
    public string ValidationMessage
    {
        get
        {
            if (_requiresReopen) return "Close and reopen this editor to inspect actual server state before another attempt.";
            if ((IsJoining || IsLeaving) && !_snapshot.IsLocalSuperuser) return "Reconnect using local root to join or leave a domain.";
            if (!_snapshot.IsLocalSuperuser && !AdManagement.Methods(SelectedOperation.Operation).All(method => _snapshot.Permissions.Any(permission =>
                string.Equals(permission, method.Method, StringComparison.OrdinalIgnoreCase)
                || permission.EndsWith('*') && method.Method.StartsWith(permission[..^1], StringComparison.OrdinalIgnoreCase))))
                return "Read-only for this operation: this session does not have all required pool administrator permissions.";
            return AdManagement.ValidationError(_snapshot, BuildRequest()) ?? "";
        }
    }
    public AdOperationOption SelectedOperation
    {
        get => _operation;
        set { if (CanEdit && value != null && Operations.Contains(value) && SetProperty(ref _operation, value)) { SelectSubjectRoles(); Changed(); } }
    }
    public AdSubjectOption? SelectedSubject
    {
        get => _subject;
        set { if (CanEdit && (value == null || Subjects.Contains(value)) && SetProperty(ref _subject, value)) { SelectSubjectRoles(); Changed(); } }
    }
    public string Domain { get => _domain; set { if (CanEdit && SetProperty(ref _domain, value ?? "")) Changed(); } }
    public string SubjectName { get => _subjectName; set { if (CanEdit && SetProperty(ref _subjectName, value ?? "")) Changed(); } }
    public string Username
    {
        get => _username;
        set
        {
            var previous = _username.Trim();
            if (!CanEdit || !SetProperty(ref _username, value ?? "")) return;
            if (ShowCredentials && !string.Equals(previous, _username.Trim(), StringComparison.Ordinal)) Changed();
            else Refresh();
        }
    }
    public string Password { get => _password; set { if (CanEdit && SetProperty(ref _password, value ?? "")) Refresh(); } }
    public bool RecoveryConfirmed { get => _recoveryConfirmed; set { if (CanEdit && SetProperty(ref _recoveryConfirmed, value)) Changed(); } }
    public IAsyncRelayCommand ReviewCommand { get; }
    public IRelayCommand CancelReviewCommand { get; }
    public IAsyncRelayCommand ApplyCommand { get; }
    public IRelayCommand CloseCommand { get; }

    private AdRequest BuildRequest() => new(SelectedOperation.Operation, Domain, SubjectName,
        SelectedOperation.Operation is AdOperation.SetRoles or AdOperation.RemoveSubject ? SelectedSubject?.Reference : null,
        Roles.Where(role => role.IsSelected).Select(role => role.Role.Reference).ToArray(), RecoveryConfirmed, Username);
    private void SelectSubjectRoles()
    {
        if (SelectedOperation.Operation != AdOperation.SetRoles) return;
        var selected = SelectedSubject?.RoleReferences.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];
        foreach (var role in Roles) role.Select(selected.Contains(role.Role.Reference, StringComparer.Ordinal));
    }
    private void Changed() { _review = null; _status = ""; Refresh(); }
    private void Refresh()
    {
        foreach (var property in new[] { nameof(IsJoining), nameof(IsLeaving), nameof(IsAdding), nameof(IsSubjectOperation), nameof(ShowRoles), nameof(ShowCredentials),
            nameof(IsBusy), nameof(IsSaving), nameof(CanEdit), nameof(CanReview), nameof(CanApply), nameof(ValidationMessage), nameof(StatusMessage), nameof(ReviewSummary) })
            OnPropertyChanged(property);
        ReviewCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); CancelReviewCommand.NotifyCanExecuteChanged(); CloseCommand.NotifyCanExecuteChanged();
    }
    private async Task ReviewAsync()
    {
        if (!CanReview) return;
        _busy = true; _review = null; _status = "Reading current server configuration and permissions…";
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation; Refresh();
        try
        {
            var request = BuildRequest();
            var result = await _reviewAsync(request, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (result.RequestFingerprint != request.Fingerprint || result.SnapshotFingerprint != _snapshot.Fingerprint)
                throw new InvalidOperationException("The review did not match this draft. Reopen access management.");
            _review = result;
            _status = result.Error ?? "Review complete. Inspect the plan before applying it.";
        }
        catch (OperationCanceledException) { _status = "Review cancelled. No changes were made."; }
        catch (Exception error) { _status = error.Message; }
        finally { _cancellation = null; _busy = false; Refresh(); }
    }
    private async Task ApplyAsync()
    {
        if (!CanApply || _review == null) return;
        var request = BuildRequest(); var review = _review;
        _busy = _saving = true; Refresh();
        try
        {
            if (!await _confirm(new ShellConfirmRequest { Title = "Confirm directory access change", Message = review.Summary,
                AcceptLabel = "Apply access change" })) return;
            using var credentials = new AdCredentials(_username.Trim(), _password);
            ClearCredentials();
            await _applyAsync(request, review, credentials);
            _closed = true; _busy = _saving = false; Refresh(); _close();
        }
        catch (Exception error)
        {
            if (error is AdActionException actionError)
            {
                _status = actionError.Message;
                _requiresReopen = actionError.MutationAttempted;
            }
            else
            {
                _status = error.Message.Contains(AdManagement.RecoveryNotice, StringComparison.Ordinal)
                    ? error.Message : error.Message + "\n" + AdManagement.RecoveryNotice;
                _requiresReopen = true;
            }
            _review = null;
        }
        finally { ClearCredentials(); _busy = _saving = false; Refresh(); }
    }
    private void ClearCredentials()
    { _username = ""; _password = ""; OnPropertyChanged(nameof(Username)); OnPropertyChanged(nameof(Password)); }
    private void Close() { if (IsBusy || _closed) return; Dispose(); _close(); }
    public void Dispose() { _closed = true; _cancellation?.Cancel(); ClearCredentials(); }
}
