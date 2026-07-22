using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions.VMActions;
using XenAdmin.Core;
using XenAPI;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public sealed class MoveSrOption
{
    public MoveSrOption(SR sr, string label)
    {
        Sr = sr;
        Label = label;
    }

    public SR Sr { get; }
    public string Label { get; }
    public override string ToString() => Label;
}

public partial class VmMoveViewModel : ViewModelBase
{
    private readonly VM _vm;
    private readonly Action _close;
    private readonly Action<string>? _status;
    private readonly IReadOnlyList<VDI> _movableDisks;

    public VmMoveViewModel(VM vm, Action close, Action<string>? status = null)
    {
        _vm = vm;
        _close = close;
        _status = status;
        _movableDisks = ShellStoragePicker.GetMovableDisks(vm);

        foreach (var sr in vm.Connection.Cache.SRs
                     .Where(sr => ShellStoragePicker.IsUsableDestination(sr, _movableDisks, requireStorageMigration: false))
                     .OrderByDescending(sr => sr.shared)
                     .ThenBy(sr => Helpers.GetName(sr), StringComparer.OrdinalIgnoreCase))
        {
            StorageRepositories.Add(new MoveSrOption(sr, ShellStoragePicker.FormatSrLabel(sr)));
        }

        SelectedStorage = StorageRepositories.FirstOrDefault(o => o.Sr.shared)
                          ?? StorageRepositories.FirstOrDefault();
        Hint = "Moves owned disks with VDI copy + destroy (VM must be shut down). Used when migrate_send Move is unavailable (single host, license, or CBT). Local SRs are labeled with their host.";
    }

    public ObservableCollection<MoveSrOption> StorageRepositories { get; } = new();

    public string Hint { get; }

    [ObservableProperty]
    private MoveSrOption? _selectedStorage;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private void Move()
    {
        if (SelectedStorage == null)
        {
            StatusMessage = "Select a destination SR.";
            return;
        }

        if (ShellStoragePicker.IsCurrentLocation(SelectedStorage.Sr, _movableDisks))
        {
            StatusMessage = "Choose a different SR — disks are already on that storage.";
            return;
        }

        if (!ShellStoragePicker.CanFitDisks(SelectedStorage.Sr, _movableDisks))
        {
            StatusMessage = "Not enough free space on that SR for the VM disks.";
            return;
        }

        var host = ShellStoragePicker.ResolveTargetHostForSr(_vm, SelectedStorage.Sr);
        if (host == null)
        {
            StatusMessage = "No host available for the move.";
            return;
        }

        // Local destination must belong to a live host that can see the SR.
        if (!SelectedStorage.Sr.shared && !ShellStoragePicker.SrVisibleToHost(SelectedStorage.Sr, host))
        {
            StatusMessage = "That local SR is not attached to a usable host.";
            return;
        }

        var action = new VMMoveAction(_vm, SelectedStorage.Sr, host);
        ShellActionRunner.Run(action, msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });
        StatusMessage = "Move queued — see Logs.";
        _close();
    }
}
