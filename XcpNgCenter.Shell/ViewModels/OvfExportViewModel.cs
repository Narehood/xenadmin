using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions.OvfActions;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.ViewModels;

public partial class ExportVmPickRow : ObservableObject
{
    public ExportVmPickRow(VM vm)
    {
        Vm = vm;
        Label = $"{Helpers.GetName(vm)} · {vm.power_state}";
    }

    public VM Vm { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public partial class OvfExportViewModel : ViewModelBase
{
    private readonly IXenConnection _connection;
    private readonly Action _close;
    private readonly Action<string>? _status;
    private readonly Func<Task<string?>> _pickDirectory;

    public OvfExportViewModel(
        IXenConnection connection,
        VM? preferredVm,
        Func<Task<string?>> pickDirectory,
        Action close,
        Action<string>? status = null)
    {
        _connection = connection;
        _close = close;
        _status = status;
        _pickDirectory = pickDirectory;

        foreach (var vm in connection.Cache.VMs
                     .Where(v => v.IsRealVm() && !v.is_a_template && !v.is_a_snapshot && !v.is_control_domain)
                     .OrderBy(v => Helpers.GetName(v), StringComparer.OrdinalIgnoreCase))
        {
            var row = new ExportVmPickRow(vm)
            {
                IsSelected = preferredVm != null && preferredVm.opaque_ref == vm.opaque_ref
            };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ExportVmPickRow.IsSelected))
                    OnPropertyChanged(nameof(SelectedCount));
            };
            Vms.Add(row);
        }

        if (preferredVm != null && Vms.All(v => !v.IsSelected))
        {
            var match = Vms.FirstOrDefault(v => v.Vm.opaque_ref == preferredVm.opaque_ref);
            if (match != null)
                match.IsSelected = true;
        }

        ApplianceName = preferredVm != null
            ? Helpers.GetName(preferredVm)
            : $"appliance-{DateTime.Now:yyyyMMdd-HHmm}";
        DirectoryPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Hint = "Exports selected VMs as an OVF folder or a single OVA package.";
    }

    public ObservableCollection<ExportVmPickRow> Vms { get; } = new();
    public string Hint { get; }
    public int SelectedCount => Vms.Count(v => v.IsSelected);

    [ObservableProperty] private string _directoryPath = string.Empty;
    [ObservableProperty] private string _applianceName = string.Empty;
    [ObservableProperty] private bool _createOva = true;
    [ObservableProperty] private bool _compressFiles;
    [ObservableProperty] private bool _createManifest = true;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [RelayCommand]
    private async Task BrowseDirectoryAsync()
    {
        var path = await _pickDirectory();
        if (!string.IsNullOrWhiteSpace(path))
            DirectoryPath = path;
    }

    [RelayCommand]
    private void Export()
    {
        var selected = Vms.Where(v => v.IsSelected).Select(v => v.Vm).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Select at least one VM.";
            return;
        }

        if (string.IsNullOrWhiteSpace(DirectoryPath) || !Directory.Exists(DirectoryPath))
        {
            StatusMessage = "Choose an existing destination folder.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ApplianceName))
        {
            StatusMessage = "Enter an appliance name.";
            return;
        }

        var action = new ExportApplianceAction(
            _connection,
            DirectoryPath.Trim(),
            ApplianceName.Trim(),
            selected,
            eulas: Array.Empty<string>(),
            signAppliance: false,
            createManifest: CreateManifest,
            certificate: null!,
            encryptFiles: false,
            encryptPassword: null!,
            createOVA: CreateOva,
            compressOVFfiles: CompressFiles,
            shouldVerify: false);

        ShellActionRunner.Run(action, msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });
        StatusMessage = "OVF/OVA export queued — see Logs.";
        _close();
    }
}
