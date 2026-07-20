using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions.VMActions;
using XenAPI;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class VmCloneViewModel : ViewModelBase
{
    private readonly VM _vm;
    private readonly Action _close;
    private readonly Action<string>? _status;

    public VmCloneViewModel(VM vm, Action close, Action<string>? status = null)
    {
        _vm = vm;
        _close = close;
        _status = status;
        CloneName = $"{vm.name_label} (copy)";
        CloneDescription = vm.name_description ?? string.Empty;
    }

    [ObservableProperty]
    private string _cloneName = string.Empty;

    [ObservableProperty]
    private string _cloneDescription = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private void Clone()
    {
        if (string.IsNullOrWhiteSpace(CloneName))
        {
            StatusMessage = "Enter a name for the cloned VM.";
            return;
        }

        var action = new VMCloneAction(_vm, CloneName.Trim(), CloneDescription?.Trim() ?? string.Empty);
        ShellActionRunner.Run(action, msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });
        StatusMessage = "Clone queued — see Logs.";
        _close();
    }
}
