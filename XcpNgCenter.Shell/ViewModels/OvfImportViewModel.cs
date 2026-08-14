using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions.OvfActions;
using XenAdmin.Core;
using XenAdmin.Mappings;
using XenAdmin.Network;
using XenAPI;
using XenOvf;
using XenOvf.Definitions;
using XcpNgCenter.Shell.Services;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.ViewModels;

public partial class OvfImportViewModel : ViewModelBase
{
    private readonly IXenConnection _connection;
    private readonly Action _close;
    private readonly Action<string>? _status;
    private readonly Func<Task<string?>> _pickOpenPath;
    private readonly ShellAppSettings _settings = ShellBootstrap.AppSettings;
    private Package? _package;

    public OvfImportViewModel(
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
        Hint = "Imports an OVF/OVA appliance. Disks map to one SR; NICs map to one network (minimal shell wizard).";
    }

    public ObservableCollection<ImportSrOption> StorageRepositories { get; } = new();
    public ObservableCollection<ImportHostOption> Hosts { get; } = new();
    public ObservableCollection<XenAPI.Network> Networks { get; } = new();
    public ObservableCollection<string> SystemSummaries { get; } = new();
    public ObservableCollection<string> ValidationWarnings { get; } = new();
    public string Hint { get; }

    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private ImportSrOption? _selectedStorage;
    [ObservableProperty] private ImportHostOption? _selectedHost;
    [ObservableProperty] private XenAPI.Network? _selectedNetwork;
    [ObservableProperty] private bool _startAfterImport;
    [ObservableProperty] private bool _verifyManifest;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasPackage;
    [ObservableProperty] private bool _hasValidationWarnings;
    [ObservableProperty] private bool _acknowledgeValidationWarnings;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _pickOpenPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        FilePath = path;
        LoadPackage(path);
    }

    private void LoadPackage(string path)
    {
        SystemSummaries.Clear();
        ValidationWarnings.Clear();
        _package = null;
        HasPackage = false;
        HasValidationWarnings = false;
        AcknowledgeValidationWarnings = false;
        try
        {
            _package = Package.Create(path);
            if (!OVF.Validate(_package, out var warnings))
            {
                StatusMessage = warnings?.LastOrDefault()
                                ?? "The appliance did not pass OVF validation.";
                _package = null;
                return;
            }

            var envelope = _package.OvfEnvelope
                           ?? throw new InvalidOperationException("Appliance has no OVF envelope.");
            foreach (var sysId in OVF.FindSystemIds(envelope))
            {
                var name = FindVmName(envelope, sysId);
                SystemSummaries.Add($"{name} ({sysId})");
            }

            HasPackage = SystemSummaries.Count > 0;
            if (warnings is { Count: > 0 } && !_settings.IgnoreOvfValidationWarnings)
            {
                foreach (var warning in warnings.Where(w => !string.IsNullOrWhiteSpace(w)))
                    ValidationWarnings.Add(warning);
                HasValidationWarnings = ValidationWarnings.Count > 0;
            }

            StatusMessage = !HasPackage
                ? "No virtual systems found in the appliance."
                : HasValidationWarnings
                    ? $"Loaded {SystemSummaries.Count} system(s). Review the validation warnings before importing."
                    : $"Loaded {SystemSummaries.Count} system(s) from {_package.Name}.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Import()
    {
        if (_package?.OvfEnvelope == null)
        {
            StatusMessage = "Choose a valid .ovf / .ova appliance first.";
            return;
        }

        if (SelectedStorage == null)
        {
            StatusMessage = "Select a destination SR.";
            return;
        }

        if (SelectedNetwork == null)
        {
            StatusMessage = "Select a network for imported VIFs.";
            return;
        }

        if (HasValidationWarnings
            && !_settings.IgnoreOvfValidationWarnings
            && !AcknowledgeValidationWarnings)
        {
            StatusMessage = "Review and accept the OVF validation warnings before importing.";
            return;
        }

        var envelope = _package.OvfEnvelope;
        var mappings = new Dictionary<string, VmMapping>();
        var targetRef = (object?)SelectedHost?.Host?.opaque_ref
                        ?? Helpers.GetPoolOfOne(_connection)?.opaque_ref
                        ?? _connection.Hostname;
        var targetName = SelectedHost?.Host?.Name()
                         ?? Helpers.GetName(Helpers.GetPoolOfOne(_connection))
                         ?? _connection.Name;

        foreach (var sysId in OVF.FindSystemIds(envelope))
        {
            var mapping = new VmMapping(sysId)
            {
                VmNameLabel = FindVmName(envelope, sysId),
                XenRef = targetRef,
                TargetName = targetName
            };

            foreach (var rasd in OVF.FindDiskRasds(envelope, sysId) ?? Array.Empty<RASD_Type>())
            {
                var id = rasd.InstanceID?.Value;
                if (!string.IsNullOrEmpty(id))
                    mapping.Storage[id] = SelectedStorage.Sr;
            }

            foreach (var rasd in OVF.FindRasdByType(envelope, sysId, 10) ?? Array.Empty<RASD_Type>())
            {
                var id = rasd.InstanceID?.Value;
                if (!string.IsNullOrEmpty(id))
                    mapping.Networks[id] = SelectedNetwork;
            }

            mappings[sysId] = mapping;
        }

        if (mappings.Count == 0)
        {
            StatusMessage = "No systems to import.";
            return;
        }

        var action = new ImportApplianceAction(
            _connection,
            _package,
            mappings,
            VerifyManifest,
            false,
            null!,
            false,
            null!,
            StartAfterImport);

        ShellActionRunner.Run(action, msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });
        StatusMessage = "OVF/OVA import queued — see Logs.";
        _close();
    }

    private static string FindVmName(EnvelopeType ovfEnv, string sysId)
    {
        try
        {
            var name = OVF.FindSystemName(ovfEnv, sysId);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch
        {
            // Fall through.
        }

        return sysId;
    }
}
