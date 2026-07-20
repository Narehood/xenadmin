using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions.VMActions;
using XenAdmin.Core;
using XenAPI;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class VmMoveViewModel : ViewModelBase
{
    private readonly VM _vm;
    private readonly Action _close;
    private readonly Action<string>? _status;

    public VmMoveViewModel(VM vm, Action close, Action<string>? status = null)
    {
        _vm = vm;
        _close = close;
        _status = status;

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
        Hint = "Moves owned disks to the selected SR (copy + destroy). The VM must be shut down.";
    }

    public ObservableCollection<SR> StorageRepositories { get; } = new();

    public string Hint { get; }

    [ObservableProperty]
    private SR? _selectedStorage;

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

        var host = _vm.GetStorageHost(false)
                   ?? Helpers.GetCoordinator(_vm.Connection)
                   ?? _vm.Connection.Cache.Hosts.FirstOrDefault();
        if (host == null)
        {
            StatusMessage = "No host available for the move.";
            return;
        }

        var action = new VMMoveAction(_vm, SelectedStorage, host);
        ShellActionRunner.Run(action, msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });
        StatusMessage = "Move queued — see Logs.";
        _close();
    }
}
