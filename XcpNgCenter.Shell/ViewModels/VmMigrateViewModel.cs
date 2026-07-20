using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions.VMActions;
using XenAdmin.Core;
using XenAPI;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class VmMigrateViewModel : ViewModelBase
{
    private readonly VM _vm;
    private readonly Action _close;
    private readonly Action<string>? _status;

    public VmMigrateViewModel(VM vm, Action close, Action<string>? status = null)
    {
        _vm = vm;
        _close = close;
        _status = status;

        var resident = vm.Connection.Resolve(vm.resident_on);
        foreach (var host in vm.Connection.Cache.Hosts
                     .OrderBy(h => h.name_label, StringComparer.OrdinalIgnoreCase))
        {
            if (resident != null && host.opaque_ref == resident.opaque_ref)
                continue;
            if (!host.enabled || !host.IsLive())
                continue;
            Hosts.Add(host);
        }

        SelectedHost = Hosts.FirstOrDefault();
        Hint = resident == null
            ? "Select a host to migrate this VM to (live pool migrate)."
            : $"Currently on {resident.Name()}. Select another host.";
    }

    public ObservableCollection<Host> Hosts { get; } = new();

    public string Hint { get; }

    [ObservableProperty]
    private Host? _selectedHost;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private void Migrate()
    {
        if (SelectedHost == null)
        {
            StatusMessage = "Select a destination host.";
            return;
        }

        var action = new VMMigrateAction(_vm, SelectedHost);
        ShellActionRunner.Run(action, msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });
        StatusMessage = "Migrate queued — see Logs.";
        _close();
    }
}
