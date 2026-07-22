using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions.VMActions;
using XenAPI;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class VmDeleteViewModel : ViewModelBase
{
    private readonly VM _vm;
    private readonly Action _close;
    private readonly Action<string>? _status;

    public VmDeleteViewModel(VM vm, Action close, Action<string>? status = null)
    {
        _vm = vm;
        _close = close;
        _status = status;
        ConfirmText = $"Delete VM “{vm.name_label}”? This cannot be undone.";
        SnapshotCount = vm.Connection.Cache.VMs.Count(s => s.is_a_snapshot && vm.Connection.Resolve(s.snapshot_of) == vm);
        DeleteSnapshots = SnapshotCount > 0;
    }

    public string ConfirmText { get; }

    public int SnapshotCount { get; }

    public bool HasSnapshots => SnapshotCount > 0;

    public string SnapshotsLabel => HasSnapshots
        ? $"Also delete {SnapshotCount} snapshot(s) of this VM"
        : "No snapshots";

    [ObservableProperty]
    private bool _deleteDisks = true;

    [ObservableProperty]
    private bool _deleteSnapshots;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private void Delete()
    {
        var disks = DeleteDisks
            ? _vm.Connection.ResolveAll(_vm.VBDs).Where(v => v.GetIsOwner()).ToList()
            : new List<VBD>();

        var snapshots = DeleteSnapshots
            ? _vm.Connection.Cache.VMs
                .Where(s => s.is_a_snapshot && _vm.Connection.Resolve(s.snapshot_of) == _vm)
                .ToList()
            : new List<VM>();

        var action = new VMDestroyAction(_vm, disks, snapshots);
        ShellActionRunner.Run(action, msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });
        StatusMessage = "Delete queued — see Logs.";
        _close();
    }
}
