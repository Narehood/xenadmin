using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.ViewModels;

public sealed class ExportVmOption
{
    public ExportVmOption(VM vm)
    {
        Vm = vm;
        Label = $"{Helpers.GetName(vm)} · {FormatPower(vm.power_state)}";
    }

    public VM Vm { get; }
    public string Label { get; }
    public override string ToString() => Label;

    private static string FormatPower(vm_power_state state) => state switch
    {
        vm_power_state.Halted => "Halted",
        vm_power_state.Running => "Running",
        vm_power_state.Suspended => "Suspended",
        vm_power_state.Paused => "Paused",
        _ => state.ToString()
    };
}

public partial class VmExportViewModel : ViewModelBase
{
    private readonly IXenConnection _connection;
    private readonly Action _close;
    private readonly Action<string>? _status;
    private readonly Func<Task<string?>> _pickSavePath;

    public VmExportViewModel(
        IXenConnection connection,
        VM? preferredVm,
        Func<Task<string?>> pickSavePath,
        Action close,
        Action<string>? status = null)
    {
        _connection = connection;
        _close = close;
        _status = status;
        _pickSavePath = pickSavePath;

        foreach (var vm in connection.Cache.VMs
                     .Where(CanExport)
                     .OrderBy(v => Helpers.GetName(v), StringComparer.OrdinalIgnoreCase))
        {
            Vms.Add(new ExportVmOption(vm));
        }

        SelectedVm = preferredVm != null
            ? Vms.FirstOrDefault(o => o.Vm.opaque_ref == preferredVm.opaque_ref)
            : Vms.FirstOrDefault();

        Hint = "Exports a halted VM to an XVA file (WinForms Export VM as Backup). Running VMs cannot be exported.";
        if (Vms.Count == 0)
            StatusMessage = "No exportable VMs on this connection (need halted VMs with export allowed).";
    }

    public ObservableCollection<ExportVmOption> Vms { get; } = new();
    public string Hint { get; }

    [ObservableProperty]
    private ExportVmOption? _selectedVm;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private bool _verifyAfterExport;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public static bool CanExport(VM vm) =>
        vm is { is_a_template: false, is_a_snapshot: false, Locked: false, allowed_operations: { } ops }
        && ops.Contains(vm_operations.export);

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _pickSavePath();
        if (!string.IsNullOrWhiteSpace(path))
            FilePath = path;
    }

    [RelayCommand]
    private void Export()
    {
        if (SelectedVm == null)
        {
            StatusMessage = "Select a VM to export.";
            return;
        }

        if (SelectedVm.Vm.power_state == vm_power_state.Running)
        {
            StatusMessage = "Shut down the VM before exporting.";
            return;
        }

        if (string.IsNullOrWhiteSpace(FilePath))
        {
            StatusMessage = "Choose a destination .xva file.";
            return;
        }

        var path = FilePath.Trim();
        if (!path.EndsWith(".xva", StringComparison.OrdinalIgnoreCase))
            path += ".xva";

        var host = SelectedVm.Vm.Home()
                   ?? Helpers.GetCoordinator(_connection)
                   ?? _connection.Cache.Hosts.FirstOrDefault();

        var action = new ExportVmAction(_connection, host, SelectedVm.Vm, path, VerifyAfterExport);
        ShellActionRunner.Run(action, msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });
        StatusMessage = "Export queued — see Logs.";
        _close();
    }
}
