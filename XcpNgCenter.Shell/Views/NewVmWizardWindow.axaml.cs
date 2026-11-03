using System.Collections.ObjectModel;
using System.Xml;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin;
using XenAdmin.Actions;
using XenAdmin.Actions.VMActions;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XenCenterLib;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public sealed class TemplateOption
{
    public TemplateOption(VM template)
    {
        Template = template;
        Name = Helpers.GetName(template);
        var type = template.TemplateType();
        TypeLabel = ShellTemplateIcons.TypeLabel(type);
        Icon = ShellTemplateIcons.ForType(type);
        var description = template.DescriptionType() == VM.VmDescriptionType.None
            ? string.Empty
            : template.Description();
        Description = string.IsNullOrWhiteSpace(description) ? "No description" : description;
        SortOrder = (int)type + (template.IsHidden() ? (int)VM.VmTemplateType.Count : 0);
    }

    public VM Template { get; }
    public string Name { get; }
    public string TypeLabel { get; }
    public string Description { get; }
    public Bitmap Icon { get; }
    public int SortOrder { get; }
}

public sealed class NewVmStorageOption
{
    public NewVmStorageOption(SR sr)
    {
        Sr = sr;
        Label = ShellStoragePicker.FormatSrLabel(sr);
    }

    public SR Sr { get; }
    public string Label { get; }
    public override string ToString() => Label;
}

public sealed record NewVmMediaOption(string Label, VDI? Vdi);
public sealed record NewVmBootOption(VmBootMode Mode, string Label);

public partial class NewVmWizardWindow : Window
{
    public NewVmWizardWindow()
    {
        InitializeComponent();
    }

    public NewVmWizardWindow(IXenConnection connection, Host? preferredHost = null) : this()
    {
        DataContext = new NewVmWizardViewModel(connection, Close, preferredHost);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}

public partial class NewVmWizardViewModel : ViewModelBase
{
    private readonly IXenConnection _connection;
    private readonly Action _close;
    private readonly Action<AsyncAction> _runAction;
    private readonly List<TemplateOption> _allTemplates = new();
    private readonly List<SR> _allStorageRepositories = new();

    public NewVmWizardViewModel(IXenConnection connection, Action close, Host? preferredHost = null,
        Action<AsyncAction>? runAction = null)
    {
        _connection = connection;
        _close = close;
        _runAction = runAction ?? (action => ShellActionRunner.Run(action));

        foreach (var template in connection.Cache.VMs
                     .Where(vm => vm.is_a_template && !vm.is_a_snapshot && vm.Show(XenAdminConfigManager.Provider.ShowHiddenVMs)))
        {
            if (connection.Cache.Hosts.Any(Host.RestrictVtpm)
                && template.platform.TryGetValue("vtpm", out var vtpm)
                && string.Equals(vtpm, "true", StringComparison.OrdinalIgnoreCase))
                continue;

            _allTemplates.Add(new TemplateOption(template));
        }

        _allTemplates.Sort((a, b) =>
        {
            var byType = a.SortOrder.CompareTo(b.SortOrder);
            if (byType != 0)
                return byType;
            var byName = -StringUtility.NaturalCompare(a.Name, b.Name);
            return byName != 0
                ? byName
                : string.Compare(a.Template.opaque_ref, b.Template.opaque_ref, StringComparison.Ordinal);
        });

        ApplyTemplateFilter();

        foreach (var host in connection.Cache.Hosts
                     .Where(h => h.enabled && h.IsLive())
                     .OrderBy(h => Helpers.GetName(h), StringComparer.OrdinalIgnoreCase))
            Hosts.Add(host);

        foreach (var sr in connection.Cache.SRs
                     .Where(sr => sr.SupportsVdiCreate()
                                  && !sr.IsToolsSR()
                                  && sr.PBDs.Count > 0
                                  && !sr.IsBroken(checkAttached: false)
                                  && !sr.IsDetached())
                     .OrderBy(sr => Helpers.GetName(sr), StringComparer.OrdinalIgnoreCase))
        {
            _allStorageRepositories.Add(sr);
        }

        foreach (var network in connection.Cache.Networks
                     .Where(n => n.Show(XenAdminConfigManager.Provider.ShowHiddenVMs) && !n.IsGuestInstallerNetwork())
                     .OrderBy(n => Helpers.GetName(n), StringComparer.OrdinalIgnoreCase))
        {
            Networks.Add(network);
        }

        SelectedNetwork = Networks.FirstOrDefault();
        var coordinator = Helpers.GetCoordinator(connection);
        SelectedHost = Hosts.FirstOrDefault(h => h.opaque_ref == preferredHost?.opaque_ref)
                       ?? Hosts.FirstOrDefault(h => h.opaque_ref == coordinator?.opaque_ref)
                       ?? Hosts.FirstOrDefault();
        StepIndex = 0;
        UpdateStepVisibility();
    }

    public ObservableCollection<TemplateOption> Templates { get; } = new();
    public ObservableCollection<Host> Hosts { get; } = new();
    public ObservableCollection<NewVmStorageOption> StorageRepositories { get; } = new();
    public ObservableCollection<XenAPI.Network> Networks { get; } = new();
    public ObservableCollection<NewVmMediaOption> InstallationMedia { get; } = new();
    public ObservableCollection<NewVmBootOption> BootModes { get; } = new();

    public string CpuTopologyText => long.TryParse(VcpusMaxText, out var cpus)
        && long.TryParse(CoresPerSocketText, out var cores) && cores > 0
        && VM.ValidVCPUConfiguration(cpus, cores).Length == 0
            ? VM.GetTopology(cpus / cores, cores)
            : "Maximum vCPUs must be a multiple of cores per socket.";

    private bool TemplateProvidesVtpm => SelectedTemplate?.Template is { } template
        && (template.VTPMs.Count > 0 || template.platform.TryGetValue("vtpm", out var flag)
            && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase));

    public bool VtpmSupported => SelectedTemplate?.Template.IsHVM() == true
        && SelectedBootMode?.Mode is VmBootMode.Uefi or VmBootMode.SecureUefi
        && Helpers.XapiEqualOrGreater_22_26_0(_connection)
        && !Helpers.FeatureForbidden(_connection, Host.RestrictVtpm);
    public bool CanEditVtpm => VtpmSupported && !TemplateProvidesVtpm;
    public string VtpmHelpText => TemplateProvidesVtpm
        ? "This template provides a vTPM; it will be retained."
        : "A virtual TPM 2.0 requires UEFI and vTPM support on the pool.";

    public string PlacementHostText => SelectedHost == null
        ? "No placement host selected"
        : Helpers.GetName(SelectedHost);

    public string StoragePlacementText
    {
        get
        {
            var sr = SelectedStorage?.Sr;
            if (sr == null)
                return "No compatible storage repository is available for this host.";

            if (sr.shared)
                return $"Shared storage visible from {PlacementHostText}.";

            var storageHost = sr.GetStorageHost();
            return storageHost == null
                ? "Host-local storage."
                : $"Host-local storage on {storageHost.Name()}.";
        }
    }

    [ObservableProperty] private int _stepIndex;
    [ObservableProperty] private bool _showTemplateStep;
    [ObservableProperty] private bool _showNameStep;
    [ObservableProperty] private bool _showInstallationStep;
    [ObservableProperty] private bool _showComputeStep;
    [ObservableProperty] private bool _showStorageStep;
    [ObservableProperty] private bool _showNetworkStep;
    [ObservableProperty] private bool _showConfirmStep;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _isLastStep;
    [ObservableProperty] private string _stepTitle = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private string _templateFilter = string.Empty;
    [ObservableProperty] private TemplateOption? _selectedTemplate;
    [ObservableProperty] private string _vmName = string.Empty;
    [ObservableProperty] private string _vmDescription = string.Empty;
    [ObservableProperty] private Host? _selectedHost;
    [ObservableProperty] private string _vcpusText = "1";
    [ObservableProperty] private string _vcpusMaxText = "1";
    [ObservableProperty] private string _coresPerSocketText = "1";
    [ObservableProperty] private NewVmMediaOption? _selectedMedia;
    [ObservableProperty] private NewVmBootOption? _selectedBootMode;
    [ObservableProperty] private bool _enableVtpm;
    [ObservableProperty] private string _memoryMibText = "1024";
    [ObservableProperty] private NewVmStorageOption? _selectedStorage;
    [ObservableProperty] private string _diskGibText = "24";
    [ObservableProperty] private XenAPI.Network? _selectedNetwork;
    [ObservableProperty] private bool _startAfter = true;
    [ObservableProperty] private string _summaryText = string.Empty;

    partial void OnTemplateFilterChanged(string value) => ApplyTemplateFilter();

    partial void OnSelectedHostChanged(Host? value)
    {
        OnPropertyChanged(nameof(PlacementHostText));
        RefreshStorageRepositories();
        RefreshInstallationMedia();
        RefreshBootModes();
        OnPropertyChanged(nameof(StoragePlacementText));
    }

    partial void OnSelectedStorageChanged(NewVmStorageOption? value) =>
        OnPropertyChanged(nameof(StoragePlacementText));

    partial void OnVcpusMaxTextChanged(string value) => OnPropertyChanged(nameof(CpuTopologyText));
    partial void OnCoresPerSocketTextChanged(string value) => OnPropertyChanged(nameof(CpuTopologyText));

    partial void OnSelectedBootModeChanged(NewVmBootOption? value)
    {
        OnPropertyChanged(nameof(VtpmSupported));
        OnPropertyChanged(nameof(CanEditVtpm));
        EnableVtpm = VtpmSupported && (TemplateProvidesVtpm || EnableVtpm);
    }

    /// <summary>Lists pool ISO images and physical DVD drives accessible to the placement host.</summary>
    private void RefreshInstallationMedia()
    {
        var previousRef = SelectedMedia?.Vdi?.opaque_ref;
        InstallationMedia.Clear();
        InstallationMedia.Add(new NewVmMediaOption("None (empty DVD drive)", null));
        if (SelectedTemplate?.Template is { } template && SelectedHost != null)
        {
            foreach (var iso in ShellIsoLibrary.Enumerate(template)
                         .Where(iso => _connection.Resolve(iso.Vdi.SR)?.CanBeSeenFrom(SelectedHost) == true))
            {
                var sr = _connection.Resolve(iso.Vdi.SR);
                var label = sr.Physical()
                    ? $"Physical DVD: {iso.Name} ({PlacementHostText})"
                    : iso.DisplayLabel;
                InstallationMedia.Add(new NewVmMediaOption(label, iso.Vdi));
            }
        }
        SelectedMedia = InstallationMedia.FirstOrDefault(o => o.Vdi?.opaque_ref == previousRef)
                        ?? InstallationMedia[0];
    }

    /// <summary>Preserves template firmware defaults and restricts choices to supported pool capabilities.</summary>
    private void RefreshBootModes()
    {
        var template = SelectedTemplate?.Template;
        var previous = SelectedBootMode?.Mode;
        var keepVtpm = EnableVtpm;
        BootModes.Clear();
        if (template == null)
            return;
        var uefi = template.IsDefaultBootModeUefi();
        var canChange = template.CanChangeBootMode() && !TemplateProvidesVtpm;
        if (!uefi || canChange)
            BootModes.Add(new NewVmBootOption(VmBootMode.Bios, "BIOS"));
        if (template.IsHVM() && Helpers.NaplesOrGreater(_connection)
            && !Helpers.FeatureForbidden(_connection, Host.UefiBootDisabled)
            && (uefi || canChange && template.SupportsUefiBoot()))
        {
            BootModes.Add(new NewVmBootOption(VmBootMode.Uefi, "UEFI"));
            if (!Helpers.FeatureForbidden(_connection, Host.UefiSecureBootDisabled)
                && (template.SupportsSecureUefiBoot() || uefi && template.GetSecureBootMode() == "true"))
                BootModes.Add(new NewVmBootOption(VmBootMode.SecureUefi, "UEFI with Secure Boot"));
        }
        var secureBoot = template.GetSecureBootMode();
        var defaultMode = uefi
            ? secureBoot == "true" || secureBoot == "auto" && !string.IsNullOrEmpty(Helpers.GetPoolOfOne(_connection)?.uefi_certificates)
                ? VmBootMode.SecureUefi : VmBootMode.Uefi
            : VmBootMode.Bios;
        SelectedBootMode = BootModes.FirstOrDefault(o => o.Mode == (previous ?? defaultMode))
                           ?? BootModes.FirstOrDefault(o => o.Mode == defaultMode)
                           ?? BootModes.FirstOrDefault();
        EnableVtpm = VtpmSupported && (TemplateProvidesVtpm || keepVtpm);
        OnPropertyChanged(nameof(VtpmHelpText));
    }

    private void ApplyTemplateFilter()
    {
        var keep = SelectedTemplate;
        Templates.Clear();
        var filter = TemplateFilter?.Trim() ?? string.Empty;
        foreach (var option in _allTemplates)
        {
            if (filter.Length > 0
                && option.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                && option.TypeLabel.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            Templates.Add(option);
        }

        if (keep != null && Templates.Contains(keep))
            SelectedTemplate = keep;
        else
            SelectedTemplate = Templates.FirstOrDefault();
    }

    private void RefreshStorageRepositories()
    {
        var previousRef = SelectedStorage?.Sr.opaque_ref;
        var targetHost = SelectedHost;
        var pool = Helpers.GetPoolOfOne(_connection);
        var defaultSr = pool == null ? null : _connection.Resolve(pool.default_SR);

        SelectedStorage = null;
        StorageRepositories.Clear();

        foreach (var sr in _allStorageRepositories
                     .Where(sr => targetHost == null
                         ? sr.shared
                         : sr.CanBeSeenFrom(targetHost))
                     .OrderByDescending(sr => sr.shared)
                     .ThenBy(sr => Helpers.GetName(sr), StringComparer.OrdinalIgnoreCase))
        {
            StorageRepositories.Add(new NewVmStorageOption(sr));
        }

        SelectedStorage = StorageRepositories.FirstOrDefault(o => o.Sr.opaque_ref == previousRef)
                          ?? StorageRepositories.FirstOrDefault(o => o.Sr.opaque_ref == defaultSr?.opaque_ref)
                          ?? StorageRepositories.FirstOrDefault(o => o.Sr.shared)
                          ?? StorageRepositories.FirstOrDefault();
    }

    partial void OnSelectedTemplateChanged(TemplateOption? value)
    {
        var template = value?.Template;
        if (template == null)
            return;
        if (string.IsNullOrWhiteSpace(VmName))
            VmName = Helpers.DefaultVMName(Helpers.GetName(template), _connection);
        VcpusText = Math.Max(1, template.VCPUs_at_startup).ToString();
        VcpusMaxText = Math.Max(1, template.VCPUs_max).ToString();
        CoresPerSocketText = Math.Max(1, template.GetCoresPerSocket()).ToString();
        SelectedBootMode = null;
        EnableVtpm = false;
        RefreshBootModes();
        RefreshInstallationMedia();
        MemoryMibText = Math.Max(1, template.memory_dynamic_max / (1024 * 1024)).ToString();
        var provisionSize = TryGetProvisionSize(template);
        if (provisionSize > 0)
            DiskGibText = Math.Max(1, provisionSize / (1024L * 1024L * 1024L)).ToString();
    }

    [RelayCommand]
    private void Next()
    {
        if (!ValidateCurrentStep())
            return;
        if (IsLastStep)
        {
            Finish();
            return;
        }

        StepIndex++;
        UpdateStepVisibility();
    }

    [RelayCommand]
    private void Back()
    {
        if (StepIndex <= 0)
            return;
        StepIndex--;
        UpdateStepVisibility();
    }

    private bool ValidateCurrentStep()
    {
        StatusMessage = string.Empty;
        switch (StepIndex)
        {
            case 0 when SelectedTemplate == null:
                StatusMessage = "Select a template.";
                return false;
            case 1 when string.IsNullOrWhiteSpace(VmName):
                StatusMessage = "Enter a VM name.";
                return false;
            case 1 when SelectedHost == null:
                StatusMessage = "Select a placement host.";
                return false;
            case 2:
                return ValidateInstallation();
            case 3 when !long.TryParse(VcpusText, out var v) || v < 1:
                StatusMessage = "Enter a valid vCPU count.";
                return false;
            case 3 when !ValidateCpuTopology():
                return false;
            case 3 when !long.TryParse(MemoryMibText, out var m) || m < 1:
                StatusMessage = "Enter memory in MiB.";
                return false;
            case 4 when SelectedStorage == null:
                StatusMessage = "Select a storage repository visible from the placement host.";
                return false;
            case 4 when SelectedHost != null
                             && SelectedStorage != null
                             && !SelectedStorage.Sr.CanBeSeenFrom(SelectedHost):
                StatusMessage = "That storage repository is not visible from the placement host.";
                return false;
            case 4 when !long.TryParse(DiskGibText, out var g) || g < 1:
                StatusMessage = "Enter disk size in GiB.";
                return false;
            case 5 when SelectedNetwork == null:
                StatusMessage = "Select a network.";
                return false;
        }

        return true;
    }

    private bool ValidateCpuTopology()
    {
        var template = SelectedTemplate?.Template;
        if (!long.TryParse(VcpusText, out var startup) || !long.TryParse(VcpusMaxText, out var maximum)
            || startup < 1 || maximum < startup || template == null || maximum > template.MaxVCPUsAllowed()
            || startup < template.MinVCPUs())
        {
            StatusMessage = "Enter valid startup and maximum vCPU counts within the template limits (startup <= maximum).";
            return false;
        }
        if (!long.TryParse(CoresPerSocketText, out var cores) || cores < 1
            || VM.ValidVCPUConfiguration(maximum, cores).Length != 0
            || SelectedHost is { } host && host.CoresPerSocket() > 0 && cores > host.CoresPerSocket())
        {
            StatusMessage = $"Choose cores per socket that divide maximum vCPUs, fit the placement host, and use at most {VM.MAX_SOCKETS} sockets.";
            return false;
        }
        return true;
    }

    private bool ValidateInstallation()
    {
        if (SelectedBootMode == null || !BootModes.Contains(SelectedBootMode)
            || SelectedBootMode.Mode != VmBootMode.Bios && (Helpers.FeatureForbidden(_connection, Host.UefiBootDisabled)
                || !Helpers.NaplesOrGreater(_connection))
            || SelectedBootMode.Mode == VmBootMode.SecureUefi && Helpers.FeatureForbidden(_connection, Host.UefiSecureBootDisabled))
            StatusMessage = "Select a boot firmware supported by this template and pool.";
        else if (EnableVtpm && !VtpmSupported || TemplateProvidesVtpm && !VtpmSupported)
            StatusMessage = "This vTPM configuration requires UEFI and vTPM support on the pool.";
        else if (SelectedMedia?.Vdi is { } media
                 && !ShellIsoLibrary.Enumerate(SelectedTemplate!.Template).Any(iso => iso.Vdi.opaque_ref == media.opaque_ref
                     && _connection.Resolve(iso.Vdi.SR)?.CanBeSeenFrom(SelectedHost) == true))
            StatusMessage = "The selected ISO or DVD drive is no longer available on the placement host. Go back and select installation media again.";
        else
            return true;
        return false;
    }

    private void Finish()
    {
        var template = SelectedTemplate?.Template;
        if (template == null || SelectedHost == null || SelectedStorage == null || SelectedNetwork == null)
            return;
        if (!ValidateInstallation() || !ValidateCpuTopology())
            return;
        if (!long.TryParse(VcpusText, out var vcpus) || !long.TryParse(MemoryMibText, out var mib)
            || !long.TryParse(DiskGibText, out var gib))
            return;

        var memory = mib * 1024L * 1024L;
        var disks = BuildDisks(template, SelectedStorage.Sr, VmName.Trim(), gib * 1024L * 1024L * 1024L);
        var vifs = new List<VIF>
        {
            new()
            {
                device = "0",
                network = new XenRef<XenAPI.Network>(SelectedNetwork.opaque_ref)
            }
        };

        var action = new CreateVMAction(
            _connection,
            template,
            copyBiosStringsFrom: null,
            VmName.Trim(),
            VmDescription.Trim(),
            SelectedMedia?.Vdi == null ? InstallMethod.None : InstallMethod.CD,
            pvArgs: template.PV_args ?? string.Empty,
            cd: SelectedMedia?.Vdi,
            url: string.Empty,
            SelectedBootMode!.Mode,
            SelectedHost,
            vcpusMax: long.Parse(VcpusMaxText),
            vcpusAtStartup: vcpus,
            memoryDynamicMin: Math.Min(template.memory_dynamic_min, memory),
            memoryDynamicMax: memory,
            memoryStaticMax: memory,
            disks,
            fullCopySR: null,
            vifs,
            StartAfter,
            assignVtpm: EnableVtpm && !TemplateProvidesVtpm,
            (_, _) => { },
            (_, _) => { },
            vGpus: null,
            modifyVgpuSettings: false,
            coresPerSocket: long.Parse(CoresPerSocketText),
            cloudConfigDriveTemplateText: string.Empty);

        _runAction(action);
        _close();
    }

    private void UpdateStepVisibility()
    {
        ShowTemplateStep = StepIndex == 0;
        ShowNameStep = StepIndex == 1;
        ShowInstallationStep = StepIndex == 2;
        ShowComputeStep = StepIndex == 3;
        ShowStorageStep = StepIndex == 4;
        ShowNetworkStep = StepIndex == 5;
        ShowConfirmStep = StepIndex == 6;
        CanGoBack = StepIndex > 0;
        IsLastStep = StepIndex >= 6;
        StepTitle = StepIndex switch
        {
            0 => "Template",
            1 => "Name",
            2 => "Installation media & firmware",
            3 => "CPU & memory",
            4 => "Storage",
            5 => "Network",
            _ => "Confirm"
        };

        if (ShowInstallationStep)
        {
            RefreshInstallationMedia();
            RefreshBootModes();
        }
        if (ShowConfirmStep)
        {
            SummaryText =
                $"Create VM '{VmName}' from '{SelectedTemplate?.Name}'\n" +
                $"Placement host: {PlacementHostText}\n" +
                $"Installation media: {SelectedMedia?.Label}\n" +
                $"Firmware: {SelectedBootMode?.Label}; vTPM: {(EnableVtpm ? "enabled" : "disabled")}\n" +
                $"Compute: {VcpusText} startup / {VcpusMaxText} maximum vCPUs; {CpuTopologyText}\n" +
                $"Memory: {MemoryMibText} MiB\n" +
                $"Storage repository: {SelectedStorage?.Label ?? "(none)"}\n" +
                $"Disk: {DiskGibText} GiB\n" +
                $"Network: {Helpers.GetName(SelectedNetwork)}\n" +
                (StartAfter ? "Start after create" : "Leave halted");
        }
    }

    private static long TryGetProvisionSize(VM template)
    {
        try
        {
            var provision = template.ProvisionXml();
            if (provision?.ChildNodes == null || provision.ChildNodes.Count == 0)
                return 0;
            long total = 0;
            foreach (XmlNode diskNode in provision.ChildNodes)
            {
                if (diskNode.Attributes?["size"]?.Value is { } sizeText
                    && long.TryParse(sizeText, out var size))
                    total += size;
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

    private static List<DiskDescription> BuildDisks(VM template, SR sr, string vmName, long preferredSize)
    {
        var disks = new List<DiskDescription>();
        try
        {
            var provision = template.ProvisionXml();
            if (provision?.ChildNodes is { Count: > 0 })
            {
                var isPrimary = true;
                foreach (XmlNode diskNode in provision.ChildNodes)
                {
                    var attrs = diskNode.Attributes;
                    if (attrs == null)
                        continue;
                    var device = new VBD
                    {
                        userdevice = attrs["device"]?.Value ?? "0",
                        bootable = attrs["bootable"]?.Value == "true",
                        mode = vbd_mode.RW
                    };

                    long size;
                    if (isPrimary && preferredSize > 0)
                    {
                        size = preferredSize;
                    }
                    else if (attrs["size"]?.Value is { } sizeText
                             && long.TryParse(sizeText, out var parsed) && parsed > 0)
                    {
                        size = parsed;
                    }
                    else
                    {
                        size = preferredSize > 0 ? preferredSize : Util.BINARY_GIGA;
                    }

                    isPrimary = false;
                    var type = vdi_type.user;
                    if (attrs["type"]?.Value is { } typeText)
                        Enum.TryParse(typeText, out type);

                    var disk = new VDI
                    {
                        name_label = $"{vmName} {device.userdevice}",
                        name_description = "Created by XCP-ng Center Shell",
                        virtual_size = size,
                        type = type,
                        read_only = false,
                        SR = new XenRef<SR>(sr.opaque_ref)
                    };
                    disks.Add(new DiskDescription(disk, device));
                }

                return disks;
            }
        }
        catch
        {
            disks.Clear();
        }

        foreach (var vbd in template.Connection.ResolveAll(template.VBDs).Where(v => v.type == vbd_type.Disk))
        {
            var source = template.Connection.Resolve(vbd.VDI);
            if (source == null)
                continue;
            var device = new VBD
            {
                userdevice = vbd.userdevice,
                bootable = vbd.bootable,
                mode = vbd.mode
            };
            var disk = new VDI
            {
                name_label = source.name_label,
                name_description = source.name_description,
                virtual_size = preferredSize > 0 ? preferredSize : source.virtual_size,
                type = source.type,
                read_only = source.read_only,
                sm_config = source.sm_config,
                SR = new XenRef<SR>(sr.opaque_ref)
            };
            disks.Add(new DiskDescription(disk, device));
        }

        if (disks.Count == 0)
        {
            var device = new VBD { userdevice = "0", bootable = true, mode = vbd_mode.RW };
            var disk = new VDI
            {
                name_label = $"{vmName} 0",
                name_description = "Created by XCP-ng Center Shell",
                virtual_size = preferredSize > 0 ? preferredSize : Util.BINARY_GIGA,
                type = vdi_type.user,
                read_only = false,
                SR = new XenRef<SR>(sr.opaque_ref)
            };
            disks.Add(new DiskDescription(disk, device));
        }

        return disks;
    }
}
