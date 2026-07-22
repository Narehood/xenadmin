using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions;
using XenAdmin.Actions.VMActions;
using XenAdmin.Core;
using XenAPI;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class VmCopyViewModel : ViewModelBase
{
    private readonly VM _vm;
    private readonly Action _close;
    private readonly Action<string>? _status;

    public VmCopyViewModel(VM vm, Action close, Action<string>? status = null)
    {
        _vm = vm;
        _close = close;
        _status = status;
        CopyName = $"{vm.name_label} (copy)";
        CopyDescription = vm.name_description ?? string.Empty;

        foreach (var sr in vm.Connection.Cache.SRs
                     .Where(sr => sr != null
                                  && !sr.IsToolsSR()
                                  && sr.SupportsVdiCreate()
                                  && sr.PBDs.Count > 0
                                  && !sr.IsBroken())
                     .OrderBy(sr => Helpers.GetName(sr), StringComparer.OrdinalIgnoreCase))
        {
            StorageRepositories.Add(sr);
        }

        SelectedStorage = StorageRepositories.FirstOrDefault();
        CanFastClone = vm.AnyDiskFastClonable()
                       || vm.allowed_operations.Contains(vm_operations.clone);
        UseFastClone = CanFastClone;
    }

    public ObservableCollection<SR> StorageRepositories { get; } = new();

    public bool CanFastClone { get; }

    public bool ShowSrPicker => !UseFastClone;

    [ObservableProperty]
    private string _copyName = string.Empty;

    [ObservableProperty]
    private string _copyDescription = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSrPicker))]
    private bool _useFastClone;

    [ObservableProperty]
    private SR? _selectedStorage;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private void Copy()
    {
        if (string.IsNullOrWhiteSpace(CopyName))
        {
            StatusMessage = "Enter a name for the copy.";
            return;
        }

        var name = CopyName.Trim();
        var description = CopyDescription?.Trim() ?? string.Empty;

        AsyncAction action;
        if (UseFastClone && CanFastClone)
        {
            action = new VMCloneAction(_vm, name, description);
        }
        else
        {
            if (SelectedStorage == null)
            {
                StatusMessage = "Select a destination SR.";
                return;
            }

            var host = _vm.GetStorageHost(false) ?? Helpers.GetCoordinator(_vm.Connection);
            if (host == null)
            {
                StatusMessage = "No host available for copy.";
                return;
            }

            action = new VMCopyAction(_vm, host, SelectedStorage, name, description);
        }

        ShellActionRunner.Run(action, msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });
        StatusMessage = "Copy queued — see Logs.";
        _close();
    }
}
