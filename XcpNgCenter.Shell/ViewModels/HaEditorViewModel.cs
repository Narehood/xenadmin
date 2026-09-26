using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XcpNgCenter.Shell.Services;
using VM = XenAPI.VM;

namespace XcpNgCenter.Shell.ViewModels;

public sealed record HaOperationOption(HaOperation Operation, string Label);
public sealed record PoolHaPriorityOption(VM.HaRestartPriority Priority, string Label);

public sealed class HaVmDraft : ObservableObject
{
    private readonly Func<bool> _canEdit;
    private readonly Action _changed;
    private PoolHaPriorityOption? _selectedPriority;
    private string _reviewNotice = "Not reviewed.";

    public static IReadOnlyList<PoolHaPriorityOption> Priorities { get; } = Array.AsReadOnly(new[]
    {
        new PoolHaPriorityOption(VM.HaRestartPriority.Restart, "Restart"),
        new PoolHaPriorityOption(VM.HaRestartPriority.BestEffort, "Best effort"),
        new PoolHaPriorityOption(VM.HaRestartPriority.DoNotRestart, "Do not restart")
    });

    internal HaVmDraft(HaVmOption original, Func<bool> canEdit, Action changed)
    {
        Original = original;
        _canEdit = canEdit;
        _changed = changed;
        _selectedPriority = Priorities.FirstOrDefault(priority => priority.Priority == original.Priority);
    }

    internal HaVmOption Original { get; }
    public string Reference => Original.Reference;
    public string Name => Original.Name;
    public string CurrentPolicy => Priorities.FirstOrDefault(priority => priority.Priority == Original.Priority)?.Label
        ?? $"Unknown or legacy policy: '{(string.IsNullOrEmpty(Original.RawPriority) ? "not reported" : Original.RawPriority)}'";
    public string StartupSettings => $"Start order: {Original.Order}; start delay: {Original.StartDelay} seconds (preserved).";
    public bool RequiresExplicitPolicy => SelectedPriority == null;
    public string ReviewNotice => _reviewNotice;
    public PoolHaPriorityOption? SelectedPriority
    {
        get => _selectedPriority;
        set
        {
            if (!_canEdit() || value != null && !Priorities.Contains(value) || !SetProperty(ref _selectedPriority, value)) return;
            OnPropertyChanged(nameof(RequiresExplicitPolicy));
            _changed();
        }
    }

    internal HaVmSetting Setting() => new(Reference, SelectedPriority?.Priority, Original.Order, Original.StartDelay);
    internal void SetReview(HaVmAgility? result) => SetProperty(ref _reviewNotice, result == null
        ? "Not reviewed." : result.IsAgile ? "Server agility check passed."
        : result.Error ?? "The VM does not meet the server's restart prerequisites.", nameof(ReviewNotice));
}

/// <summary>Separates a local HA draft, read-only server review, confirmation, and mutation.</summary>
public sealed class HaEditorViewModel : ViewModelBase
{
    private readonly HaSnapshot _snapshot;
    private readonly Func<HaRequest, CancellationToken, Task<HaReview>> _reviewAsync;
    private readonly Func<HaRequest, HaReview, Task> _applyAsync;
    private readonly Func<ShellConfirmRequest, Task<bool>> _confirm;
    private readonly Action _close;
    private HaOperationOption _selectedOperation;
    private string _failuresToTolerate;
    private IReadOnlyList<HaSrOption> _heartbeatCandidates;
    private HaSrOption? _selectedHeartbeatSr;
    private HaReview? _review;
    private CancellationTokenSource? _reviewCancellation;
    private int _draftVersion;
    private int _reviewVersion = -1;
    private bool _isReviewing;
    private bool _isSaving;
    private bool _closed;
    private string _statusMessage = "";
    private string _validationMessage = "";

    public HaEditorViewModel(HaSnapshot snapshot, Func<HaRequest, CancellationToken, Task<HaReview>> review,
        Func<HaRequest, HaReview, Task> apply, Func<ShellConfirmRequest, Task<bool>> confirm, Action close)
    {
        _snapshot = snapshot;
        _reviewAsync = review;
        _applyAsync = apply;
        _confirm = confirm;
        _close = close;
        Operations = snapshot.IsEnabled
            ? Array.AsReadOnly(new[] { new HaOperationOption(HaOperation.Configure, "Update HA settings"), new HaOperationOption(HaOperation.Disable, "Disable HA") })
            : Array.AsReadOnly(new[] { new HaOperationOption(HaOperation.Enable, "Enable HA") });
        _selectedOperation = Operations[0];
        _failuresToTolerate = snapshot.FailuresToTolerate.ToString(CultureInfo.InvariantCulture);
        _heartbeatCandidates = Array.AsReadOnly(snapshot.HeartbeatCandidates.ToArray());
        _selectedHeartbeatSr = _heartbeatCandidates.FirstOrDefault(sr => sr.Reference == snapshot.CurrentHeartbeatSrReference);
        Vms = Array.AsReadOnly(snapshot.Vms.Select(vm => new HaVmDraft(vm, () => CanEdit && !IsDisabling, DraftChanged)).ToArray());
        ReviewCommand = new AsyncRelayCommand(ReviewAsync, () => CanReview);
        CancelReviewCommand = new RelayCommand(CancelReview, () => CanCancelReview);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => CanApply);
        CancelCommand = new RelayCommand(Cancel, () => CanClose);
        RefreshState();
    }

    public string PoolName => _snapshot.PoolName;
    public string CurrentState => _snapshot.IsEnabled ? "Pool HA is currently enabled." : "Pool HA is currently disabled.";
    public string HostSummary => $"Pool hosts: {_snapshot.HostCount}. Three or more hosts are recommended for HA.";
    public IReadOnlyList<HaOperationOption> Operations { get; }
    public IReadOnlyList<HaVmDraft> Vms { get; }
    public IReadOnlyList<HaSrOption> HeartbeatCandidates => _heartbeatCandidates;
    public bool HasHeartbeatCandidates => HeartbeatCandidates.Count > 0;
    public bool IsEnabling => SelectedOperation.Operation == HaOperation.Enable;
    public bool IsDisabling => SelectedOperation.Operation == HaOperation.Disable;
    public bool IsConfiguring => SelectedOperation.Operation == HaOperation.Configure;
    public bool IsReviewing => _isReviewing;
    public bool IsSaving => _isSaving;
    public bool IsBusy => IsReviewing || IsSaving;
    public bool CanEdit => !IsBusy && !_closed;
    public bool CanClose => CanEdit;
    public bool CanReview => CanEdit && ValidationMessage.Length == 0;
    public bool CanCancelReview => IsReviewing && _reviewCancellation is { IsCancellationRequested: false };
    public bool CanApply => CanReview && HasChanges && _review is { CanApply: true } && _reviewVersion == _draftVersion
        && _review.RequestFingerprint == HaManagement.RequestFingerprint(BuildRequest());
    public bool HasReview => _review != null;
    public bool HasReviewError => !string.IsNullOrEmpty(_review?.Error);
    public bool HasValidationMessage => ValidationMessage.Length > 0;
    public bool HasStatusMessage => StatusMessage.Length > 0;
    public string ValidationMessage => _validationMessage;
    public string StatusMessage => _statusMessage;
    public string ReviewError => _review?.Error ?? "";
    public string HeartbeatNotice => SelectedHeartbeatSr?.Error ?? "";
    public bool HasHeartbeatNotice => HeartbeatNotice.Length > 0;
    public string ApplyLabel => IsEnabling ? "Enable HA" : IsDisabling ? "Disable HA" : "Apply HA settings";
    public string ReviewSummary => _review == null ? "Review the current draft before applying changes."
        : IsDisabling ? _review.CanApply ? "Reviewed for normal HA disable. Existing VM restart policies will be retained."
            : "The normal HA disable review did not pass. Resolve the reported finding before applying it."
        : _review.MaxHostFailures is { } maximum
            ? $"Server capacity for this draft: up to {maximum} host failure(s). Requested tolerance: {FailuresToTolerate}."
            : "The server could not confirm failover capacity for this draft.";
    public bool HasChanges => !IsConfiguring || !long.TryParse(FailuresToTolerate, NumberStyles.None, CultureInfo.InvariantCulture, out var failures)
        || failures != _snapshot.FailuresToTolerate || Vms.Any(vm => vm.SelectedPriority?.Priority != vm.Original.Priority);

    public HaOperationOption SelectedOperation
    {
        get => _selectedOperation;
        set
        {
            if (!CanEdit || value == null || !Operations.Contains(value) || !SetProperty(ref _selectedOperation, value)) return;
            DraftChanged();
        }
    }
    public string FailuresToTolerate
    {
        get => _failuresToTolerate;
        set { if (CanEdit && SetProperty(ref _failuresToTolerate, value ?? "")) DraftChanged(); }
    }
    public HaSrOption? SelectedHeartbeatSr
    {
        get => _selectedHeartbeatSr;
        set
        {
            if (!CanEdit || value != null && !HeartbeatCandidates.Contains(value) || !SetProperty(ref _selectedHeartbeatSr, value)) return;
            DraftChanged();
        }
    }

    public IAsyncRelayCommand ReviewCommand { get; }
    public IRelayCommand CancelReviewCommand { get; }
    public IAsyncRelayCommand ApplyCommand { get; }
    public IRelayCommand CancelCommand { get; }

    private HaRequest BuildRequest() => IsDisabling
        ? new(HaOperation.Disable, _snapshot.FailuresToTolerate, null, _snapshot.Vms.Select(vm =>
            new HaVmSetting(vm.Reference, vm.Priority, vm.Order, vm.StartDelay)).ToArray())
        : new(SelectedOperation.Operation, long.Parse(FailuresToTolerate, NumberStyles.None, CultureInfo.InvariantCulture),
            IsEnabling ? SelectedHeartbeatSr?.Reference : null, Vms.Select(vm => vm.Setting()).ToArray());

    private void DraftChanged()
    {
        _draftVersion++;
        ClearReview();
        SetProperty(ref _statusMessage, "The draft changed. Run a fresh server review before applying it.", nameof(StatusMessage));
        RefreshState();
    }
    private void ClearReview()
    {
        _review = null;
        _reviewVersion = -1;
        foreach (var vm in Vms) vm.SetReview(null);
    }
    private void RefreshState()
    {
        var validation = "";
        if (!IsDisabling)
        {
            if (!long.TryParse(FailuresToTolerate, NumberStyles.None, CultureInfo.InvariantCulture, out var failures)
                || failures < 0 || failures >= _snapshot.HostCount)
                validation = "Enter a whole number of host failures from 0 to one fewer than the pool's host count.";
            else if (Vms.Any(vm => vm.SelectedPriority == null))
                validation = "Choose a supported restart policy explicitly for every VM with an unknown or legacy policy.";
        }
        SetProperty(ref _validationMessage, validation, nameof(ValidationMessage));
        foreach (var property in new[] { nameof(IsEnabling), nameof(IsDisabling), nameof(IsConfiguring), nameof(IsBusy),
                     nameof(CanEdit), nameof(CanClose), nameof(CanReview), nameof(CanCancelReview), nameof(CanApply), nameof(HasChanges),
                     nameof(HasReview), nameof(HasReviewError), nameof(ReviewError), nameof(ReviewSummary), nameof(ApplyLabel),
                     nameof(HasHeartbeatCandidates), nameof(HeartbeatNotice), nameof(HasHeartbeatNotice),
                     nameof(HasValidationMessage), nameof(HasStatusMessage) })
            OnPropertyChanged(property);
        ReviewCommand.NotifyCanExecuteChanged();
        CancelReviewCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private async Task ReviewAsync()
    {
        if (!CanReview) return;
        var request = BuildRequest();
        var version = _draftVersion;
        using var cancellation = new CancellationTokenSource();
        _reviewCancellation = cancellation;
        ClearReview();
        SetProperty(ref _isReviewing, true, nameof(IsReviewing));
        SetProperty(ref _statusMessage, "Reading server prerequisites and failover capacity. No HA settings are being changed.", nameof(StatusMessage));
        RefreshState();
        try
        {
            var result = await _reviewAsync(request, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (result.RequestFingerprint != HaManagement.RequestFingerprint(request) || version != _draftVersion)
                throw new InvalidOperationException("The server review does not match this draft. Review again before applying changes.");
            _review = result;
            _reviewVersion = version;
            var selectedReference = _selectedHeartbeatSr?.Reference;
            SetProperty(ref _heartbeatCandidates, Array.AsReadOnly(result.HeartbeatCandidates.ToArray()), nameof(HeartbeatCandidates));
            // Publish a real transition after changing ItemsSource so the chooser
            // restores its visual selection without interpreting binding feedback as an edit.
            SetProperty(ref _selectedHeartbeatSr, null, nameof(SelectedHeartbeatSr));
            SetProperty(ref _selectedHeartbeatSr, HeartbeatCandidates.FirstOrDefault(sr => sr.Reference == selectedReference), nameof(SelectedHeartbeatSr));
            foreach (var vm in Vms) vm.SetReview(result.VmAgility.FirstOrDefault(check => check.Reference == vm.Reference));
            SetProperty(ref _statusMessage, result.CanApply ? "Server review complete. Review the result and confirm the change to apply it."
                : "Resolve the review findings, then review the draft again.", nameof(StatusMessage));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ClearReview();
            SetProperty(ref _statusMessage, "The server review was cancelled. No HA settings were changed.", nameof(StatusMessage));
        }
        catch (Exception error)
        {
            ClearReview();
            SetProperty(ref _statusMessage, $"The server review did not complete: {error.Message}", nameof(StatusMessage));
        }
        finally
        {
            _reviewCancellation = null;
            SetProperty(ref _isReviewing, false, nameof(IsReviewing));
            RefreshState();
        }
    }

    private void CancelReview()
    {
        if (!CanCancelReview) return;
        SetProperty(ref _statusMessage, "Cancelling server review. Waiting for the active server request to finish...", nameof(StatusMessage));
        _reviewCancellation!.Cancel();
        RefreshState();
    }

    private ShellConfirmRequest Confirmation(HaRequest request)
    {
        var change = request.Operation switch
        {
            HaOperation.Enable => $"Enable HA for '{PoolName}' with heartbeat storage '{SelectedHeartbeatSr?.Name}' and tolerance for {request.FailuresToTolerate} host failure(s)?",
            HaOperation.Disable => $"Disable HA for '{PoolName}'? Automatic VM recovery after host failure will stop. Existing VM restart policy settings will be retained.",
            _ => $"Update HA settings for '{PoolName}' with tolerance for {request.FailuresToTolerate} host failure(s)?"
        };
        if (request.Operation != HaOperation.Disable)
        {
            var changes = Vms.Where(vm => vm.SelectedPriority?.Priority != vm.Original.Priority)
                .Select(vm => $"{vm.Name}: {vm.CurrentPolicy} → {vm.SelectedPriority?.Label}").ToArray();
            change += changes.Length == 0 ? " VM restart policies are unchanged."
                : "\n\nVM restart policy changes:\n" + string.Join("\n", changes);
            change += "\n\nStart order and start delay are preserved. HA can fence/restart hosts when heartbeats are lost; VM recovery includes downtime.";
        }
        change += "\n\nA failed or interrupted change can leave partial settings. Refresh the pool and review actual HA state before retrying.";
        return new ShellConfirmRequest { Title = ApplyLabel, Message = change, AcceptLabel = ApplyLabel };
    }

    private async Task ApplyAsync()
    {
        if (!CanApply) return;
        var request = BuildRequest();
        var review = _review!;
        var mutationStarted = false;
        SetProperty(ref _isSaving, true, nameof(IsSaving));
        SetProperty(ref _statusMessage, "Waiting for confirmation.", nameof(StatusMessage));
        RefreshState();
        try
        {
            if (!await _confirm(Confirmation(request)))
            {
                SetProperty(ref _statusMessage, "The change was not applied.", nameof(StatusMessage));
                return;
            }
            ClearReview();
            mutationStarted = true;
            SetProperty(ref _statusMessage, "Applying the reviewed HA change...", nameof(StatusMessage));
            RefreshState();
            await _applyAsync(request, review);
            _closed = true;
            SetProperty(ref _isSaving, false, nameof(IsSaving));
            RefreshState();
            _close();
        }
        catch (Exception error)
        {
            ClearReview();
            SetProperty(ref _statusMessage, mutationStarted
                ? $"The HA change did not complete or could not be confirmed: {error.Message} Some settings may have applied. Refresh the pool and reopen this editor to review actual state before retrying."
                : $"Confirmation did not complete: {error.Message} Review the current settings before trying again.", nameof(StatusMessage));
        }
        finally
        {
            SetProperty(ref _isSaving, false, nameof(IsSaving));
            RefreshState();
        }
    }
    private void Cancel()
    {
        if (!CanClose) return;
        _closed = true;
        RefreshState();
        _close();
    }
}
