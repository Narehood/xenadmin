using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class DrVmDraft : ObservableObject
{
    public DrVmDraft(DrVmOption option) { Option = option; }
    public DrVmOption Option { get; }
    public string Name => $"{Option.Name} [{Option.Uuid}]";
    public string? UnavailableReason => Option.UnavailableReason;
    public bool IsAvailable => UnavailableReason == null;
    [ObservableProperty] private bool _isSelected;
}

public partial class DrStorageDraft : ObservableObject
{
    public DrStorageDraft(DrSourceStorage source, IEnumerable<DrStorageOption> targets)
    {
        Source = source; Targets = targets.Where(target => target.Uuid == source.Uuid).ToArray();
        _selectedTarget = Targets.Count == 1 ? Targets[0] : null;
    }
    public DrSourceStorage Source { get; }
    public string Name => $"{Source.Name} [{Source.Uuid}]";
    public IReadOnlyList<DrStorageOption> Targets { get; }
    public bool IsMissing => Targets.Count == 0;
    [ObservableProperty] private DrStorageOption? _selectedTarget;
}

public partial class DrNetworkDraft : ObservableObject
{
    public DrNetworkDraft(DrSourceNetwork source, IEnumerable<DrNetworkOption> targets)
    { Source = source; Targets = targets.ToArray(); }
    public DrSourceNetwork Source { get; }
    public string Name => $"{Source.Name} [{Source.Uuid}]";
    public IReadOnlyList<DrNetworkOption> Targets { get; }
    public bool IsMissing => Targets.Count == 0;
    [ObservableProperty] private DrNetworkOption? _selectedTarget;
}

public partial class DrEditorViewModel : ObservableObject
{
    private readonly IDrWorkflow _workflow;
    private readonly Func<ShellConfirmRequest, Task<bool>> _confirm;
    private readonly Action _close;
    private DrInspection? _inspection;
    private DrReview? _review;
    private DrOutcome? _outcome;
    private bool _executionAttempted;
    private bool _cleanupAttempted;
    public DrEditorViewModel(IDrWorkflow workflow, Func<ShellConfirmRequest, Task<bool>> confirm, Action close)
    { _workflow = workflow; _confirm = confirm; _close = close; }

    public string PoolName => _workflow.PoolName;
    public ObservableCollection<DrMetadataOption> Metadata { get; } = [];
    public ObservableCollection<DrVmDraft> Vms { get; } = [];
    public ObservableCollection<DrStorageDraft> Storage { get; } = [];
    public ObservableCollection<DrNetworkDraft> Networks { get; } = [];
    [ObservableProperty] private DrMetadataOption? _selectedMetadata;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isRehearsal = true;
    [ObservableProperty] private string _sourceSummary = "Discover metadata on the destination's attached shared storage, then select and inspect a database.";
    [ObservableProperty] private string _reviewSummary = "Select VMs and destinations, then review the current server state.";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _report = "";
    public bool HasError => Error.Length > 0;
    public bool HasReport => Report.Length > 0;
    public bool HasInspection => _inspection != null;
    public bool CanEdit => !IsBusy && !_executionAttempted;
    public bool CanInspect => CanEdit && SelectedMetadata != null;
    public bool CanReview => CanEdit && _inspection != null && Vms.Any(vm => vm.IsSelected);
    public bool CanRecover => CanReview && _review != null;
    public bool CanCleanup => !IsBusy && !_cleanupAttempted && _outcome is { Mode: DrMode.MetadataRehearsal, Cleanup.Count: > 0 };
    public bool CanClose => !IsBusy;
    public string ModeNotice => IsRehearsal
        ? "Metadata rehearsal creates halted VM records on empty internal networks. It does not boot guests or prove failover. Cleanup preserves virtual disks; remove the rehearsal records when finished."
        : "Recovery restores VM records halted. Fence the original workloads and verify replicated disks before starting recovered VMs separately. This operation does not copy disks.";

    partial void OnIsBusyChanged(bool value) => RefreshCommands();
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnReportChanged(string value) => OnPropertyChanged(nameof(HasReport));
    partial void OnIsRehearsalChanged(bool value)
    { Invalidate(); RebuildMappings(); OnPropertyChanged(nameof(ModeNotice)); }
    partial void OnSelectedMetadataChanged(DrMetadataOption? value)
    {
        _inspection = null; ClearDrafts(); Invalidate(); OnPropertyChanged(nameof(HasInspection)); RefreshCommands();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task DiscoverAsync()
    {
        if (!CanEdit) return;
        IsBusy = true; Error = "";
        try
        {
            SelectedMetadata = null; _inspection = null; ClearDrafts(); Invalidate(); Metadata.Clear();
            foreach (var metadata in await _workflow.DiscoverAsync()) Metadata.Add(metadata);
            SourceSummary = Metadata.Count == 0
                ? "No latest metadata VDIs were found on attached shared storage. Attach the replicated metadata SR, then discover again."
                : $"Found {Metadata.Count} metadata database(s). Select one and inspect its contents.";
        }
        catch (Exception error) { Error = error.Message; }
        finally { IsBusy = false; OnPropertyChanged(nameof(HasInspection)); }
    }

    [RelayCommand(CanExecute = nameof(CanInspect))]
    private async Task InspectAsync()
    {
        if (!CanInspect || SelectedMetadata is not { } metadata) return;
        IsBusy = true; Error = ""; _inspection = null; ClearDrafts(); Invalidate();
        try
        {
            var inspection = await _workflow.InspectAsync(metadata);
            if (SelectedMetadata != metadata) throw new InvalidOperationException("The metadata selection changed during inspection.");
            _inspection = inspection;
            foreach (var vm in inspection.Vms)
            {
                var draft = new DrVmDraft(vm); draft.PropertyChanged += VmChanged; Vms.Add(draft);
            }
            SourceSummary = $"Source pool: {inspection.SourcePoolName} [{inspection.SourcePoolUuid}]. {Vms.Count} VM(s) found. Unsupported VMs show their reason.";
        }
        catch (Exception error) { Error = error.Message; }
        finally { IsBusy = false; OnPropertyChanged(nameof(HasInspection)); }
    }

    [RelayCommand(CanExecute = nameof(CanReview))]
    private async Task ReviewAsync()
    {
        if (!CanReview || _inspection == null) return;
        IsBusy = true; Error = ""; Invalidate();
        try
        {
            var request = Request(); var inspection = _inspection;
            var review = await _workflow.ReviewAsync(inspection, request);
            if (_inspection != inspection || request.Fingerprint != Request().Fingerprint)
                throw new InvalidOperationException("The draft changed during review. Review it again.");
            _review = review; ReviewSummary = review.Summary;
        }
        catch (Exception error) { Error = error.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRecover))]
    private async Task RecoverAsync()
    {
        if (!CanRecover || _inspection == null || _review == null) return;
        IsBusy = true; Error = "";
        try
        {
            var request = Request(); var review = _review; var inspection = _inspection;
            if (!await _confirm(new ShellConfirmRequest
            {
                Title = IsRehearsal ? "Create metadata rehearsal?" : "Recover VMs?",
                Message = review.Summary + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine, Vms.Where(vm => vm.IsSelected).Select(vm => vm.Name))
                    + Environment.NewLine + "VMs remain halted. Recovery stops at the first failure; partial results require inspection.",
                AcceptLabel = IsRehearsal ? "Create rehearsal" : "Recover halted"
            })) return;
            if (_review != review || _inspection != inspection || Request().Fingerprint != request.Fingerprint)
                throw new InvalidOperationException("The draft changed during confirmation. Review it again.");
            _executionAttempted = true;
            _outcome = await _workflow.RecoverAsync(inspection, request, review);
            Report = _outcome.Report; Error = _outcome.Error ?? "";
            ReviewSummary = _outcome.Succeeded ? "Recovery finished. Inspect the VM records and mapped NICs before any start operation."
                : "Recovery stopped. Inspect partial results before opening a new recovery session.";
            _review = null;
        }
        catch (Exception error) { Error = error.Message; _review = null; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanCleanup))]
    private async Task CleanupAsync()
    {
        if (!CanCleanup || _outcome == null) return;
        IsBusy = true; Error = "";
        try
        {
            if (!await _confirm(new ShellConfirmRequest
            {
                Title = "Remove rehearsal VM records?", AcceptLabel = "Clean up rehearsal",
                Message = "Remove these halted rehearsal VM records and their NIC/disk attachments. The virtual disks are preserved. Changed or running VMs are rejected."
                    + Environment.NewLine + string.Join(Environment.NewLine, _outcome.Cleanup.Select(vm => $"{vm.Name} [{vm.Uuid}]"))
            })) return;
            _cleanupAttempted = true;
            var result = await _workflow.CleanupAsync(_outcome);
            _outcome = result; Report += Environment.NewLine + Environment.NewLine + result.Report; Error = result.Error ?? "";
        }
        catch (Exception error) { Error = error.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close() { if (CanClose) _close(); }

    private DrRequest Request()
    {
        if (Storage.Any(row => row.SelectedTarget == null) || Networks.Any(row => row.SelectedTarget == null))
            throw new InvalidOperationException("Select a destination for every required storage repository and network.");
        return new(IsRehearsal ? DrMode.MetadataRehearsal : DrMode.Recovery,
            Vms.Where(vm => vm.IsSelected).Select(vm => vm.Option.Reference),
            Storage.Select(row => new DrStorageMapping(row.Source.Reference, row.SelectedTarget!.Reference)),
            Networks.Select(row => new DrNetworkMapping(row.Source.Reference, row.SelectedTarget!.Reference)));
    }
    private void VmChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(DrVmDraft.IsSelected)) { Invalidate(); RebuildMappings(); RefreshCommands(); } }
    private void MappingChanged(object? sender, PropertyChangedEventArgs args) => Invalidate();
    private void RebuildMappings()
    {
        var oldStorage = Storage.ToDictionary(row => row.Source.Reference, row => row.SelectedTarget);
        var oldNetworks = Networks.ToDictionary(row => row.Source.Reference, row => row.SelectedTarget);
        foreach (var row in Storage) row.PropertyChanged -= MappingChanged;
        foreach (var row in Networks) row.PropertyChanged -= MappingChanged;
        Storage.Clear(); Networks.Clear();
        if (_inspection == null) return;
        var selected = Vms.Where(vm => vm.IsSelected).Select(vm => vm.Option).ToArray();
        foreach (var reference in selected.SelectMany(vm => vm.StorageReferences).Distinct())
        {
            var row = new DrStorageDraft(_inspection.SourceStorage.Single(sr => sr.Reference == reference), _inspection.TargetStorage);
            if (oldStorage.TryGetValue(reference, out var previous) && previous != null && row.Targets.Contains(previous)) row.SelectedTarget = previous;
            row.PropertyChanged += MappingChanged; Storage.Add(row);
        }
        foreach (var reference in selected.SelectMany(vm => vm.NetworkReferences).Distinct())
        {
            var row = new DrNetworkDraft(_inspection.SourceNetworks.Single(network => network.Reference == reference),
                _inspection.TargetNetworks.Where(network => !IsRehearsal || network.IsIsolated));
            if (oldNetworks.TryGetValue(reference, out var previous) && previous != null && row.Targets.Contains(previous)) row.SelectedTarget = previous;
            row.PropertyChanged += MappingChanged; Networks.Add(row);
        }
    }
    private void ClearDrafts()
    {
        foreach (var vm in Vms) vm.PropertyChanged -= VmChanged;
        Vms.Clear(); RebuildMappings();
    }
    private void Invalidate()
    { _review = null; ReviewSummary = "The current draft needs a server review."; RefreshCommands(); }
    private void RefreshCommands()
    {
        foreach (var name in new[] { nameof(CanEdit), nameof(CanInspect), nameof(CanReview), nameof(CanRecover), nameof(CanCleanup), nameof(CanClose) }) OnPropertyChanged(name);
        DiscoverCommand.NotifyCanExecuteChanged(); InspectCommand.NotifyCanExecuteChanged(); ReviewCommand.NotifyCanExecuteChanged();
        RecoverCommand.NotifyCanExecuteChanged(); CleanupCommand.NotifyCanExecuteChanged(); CloseCommand.NotifyCanExecuteChanged();
    }
}
