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

public sealed class ImportSrOption
{
    public ImportSrOption(SR sr, string label)
    {
        Sr = sr;
        Label = label;
    }

    public SR Sr { get; }
    public string Label { get; }
    public override string ToString() => Label;
}

public sealed class ImportHostOption
{
    public ImportHostOption(Host? host, string label)
    {
        Host = host;
        Label = label;
    }

    public Host? Host { get; }
    public string Label { get; }
    public override string ToString() => Label;
}

public partial class VmImportViewModel : ViewModelBase
{
    private readonly IXenConnection _connection;
    private readonly Action _close;
    private readonly Action<string>? _status;
    private readonly Func<Task<string?>> _pickOpenPath;

    public VmImportViewModel(
        IXenConnection connection,
        Host? preferredHost,
        Func<Task<string?>> pickOpenPath,
        Action close,
        Action<string>? status = null)
    {
        _connection = connection;
        _close = close;
        _status = status;
        _pickOpenPath = pickOpenPath;

        foreach (var sr in connection.Cache.SRs
                     .Where(sr => sr.SupportsVdiCreate() && !sr.IsToolsSR() && sr.PBDs.Count > 0 && !sr.IsBroken())
                     .OrderByDescending(sr => sr.shared)
                     .ThenBy(sr => Helpers.GetName(sr), StringComparer.OrdinalIgnoreCase))
        {
            StorageRepositories.Add(new ImportSrOption(sr, ShellStoragePicker.FormatSrLabel(sr)));
        }

        Hosts.Add(new ImportHostOption(null, "(Pool default)"));
        foreach (var host in connection.Cache.Hosts
                     .Where(h => h.enabled && h.IsLive())
                     .OrderBy(h => Helpers.GetName(h), StringComparer.OrdinalIgnoreCase))
        {
            Hosts.Add(new ImportHostOption(host, host.Name()));
        }

        foreach (var network in connection.Cache.Networks
                     .Where(n => n != null && n.Show(true) && !n.IsGuestInstallerNetwork())
                     .OrderBy(n => n.Name(), StringComparer.OrdinalIgnoreCase))
        {
            Networks.Add(network);
        }

        SelectedStorage = StorageRepositories.FirstOrDefault(o =>
        {
            var pool = Helpers.GetPoolOfOne(connection);
            return pool != null && connection.Resolve(pool.default_SR) == o.Sr;
        }) ?? StorageRepositories.FirstOrDefault();

        SelectedHost = preferredHost != null
            ? Hosts.FirstOrDefault(h => h.Host?.opaque_ref == preferredHost.opaque_ref) ?? Hosts.FirstOrDefault()
            : Hosts.FirstOrDefault();

        SelectedNetwork = Networks.FirstOrDefault();
        Hint = "Imports an XVA backup into the selected SR (WinForms Import XVA). Network remap is optional.";
    }

    public ObservableCollection<ImportSrOption> StorageRepositories { get; } = new();
    public ObservableCollection<ImportHostOption> Hosts { get; } = new();
    public ObservableCollection<XenAPI.Network> Networks { get; } = new();
    public string Hint { get; }

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private ImportSrOption? _selectedStorage;

    [ObservableProperty]
    private ImportHostOption? _selectedHost;

    [ObservableProperty]
    private XenAPI.Network? _selectedNetwork;

    [ObservableProperty]
    private bool _remapNetwork = true;

    [ObservableProperty]
    private bool _startAfterImport;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _pickOpenPath();
        if (!string.IsNullOrWhiteSpace(path))
            FilePath = path;
    }

    [RelayCommand]
    private void Import()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
        {
            StatusMessage = "Choose an existing .xva file to import.";
            return;
        }

        if (SelectedStorage == null)
        {
            StatusMessage = "Select a destination SR.";
            return;
        }

        var affinity = SelectedHost?.Host;
        var action = new ImportVmAction(
            _connection,
            affinity,
            FilePath.Trim(),
            SelectedStorage.Sr,
            ShellVmHaPrompt.WarningDialogHAInvalidConfig,
            ShellVmHaPrompt.StartDiagnosisForm);

        List<VIF>? vifs = null;
        if (RemapNetwork && SelectedNetwork != null)
        {
            vifs =
            [
                new VIF
                {
                    device = "0",
                    network = new XenRef<XenAPI.Network>(SelectedNetwork.opaque_ref)
                }
            ];
        }

        // EndWizard must be signaled so ImportVmAction does not wait forever after upload.
        action.EndWizard(StartAfterImport, vifs);

        ShellActionRunner.Run(action, msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });
        StatusMessage = "Import queued — see Logs.";
        _close();
    }
}
