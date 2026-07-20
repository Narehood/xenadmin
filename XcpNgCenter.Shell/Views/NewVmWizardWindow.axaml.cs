using System.Collections.ObjectModel;
using System.Xml;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin;
using XenAdmin.Actions.VMActions;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class NewVmWizardWindow : Window
{
    public NewVmWizardWindow()
    {
        InitializeComponent();
    }

    public NewVmWizardWindow(IXenConnection connection) : this()
    {
        DataContext = new NewVmWizardViewModel(connection, Close);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}

public partial class NewVmWizardViewModel : ViewModelBase
{
    private readonly IXenConnection _connection;
    private readonly Action _close;

    public NewVmWizardViewModel(IXenConnection connection, Action close)
    {
        _connection = connection;
        _close = close;

        foreach (var template in connection.Cache.VMs
                     .Where(vm => vm.is_a_template && !vm.is_a_snapshot && vm.Show(XenAdminConfigManager.Provider.ShowHiddenVMs))
                     .OrderBy(vm => Helpers.GetName(vm), StringComparer.OrdinalIgnoreCase))
        {
            Templates.Add(template);
        }

        foreach (var host in connection.Cache.Hosts.OrderBy(h => Helpers.GetName(h), StringComparer.OrdinalIgnoreCase))
            Hosts.Add(host);

        foreach (var sr in connection.Cache.SRs
                     .Where(sr => sr.SupportsVdiCreate() && !sr.IsToolsSR() && sr.PBDs.Count > 0)
                     .OrderBy(sr => Helpers.GetName(sr), StringComparer.OrdinalIgnoreCase))
        {
            StorageRepositories.Add(sr);
        }

        foreach (var network in connection.Cache.Networks
                     .Where(n => n.Show(XenAdminConfigManager.Provider.ShowHiddenVMs) && !n.IsGuestInstallerNetwork())
                     .OrderBy(n => Helpers.GetName(n), StringComparer.OrdinalIgnoreCase))
        {
            Networks.Add(network);
        }

        SelectedStorage = StorageRepositories.FirstOrDefault(sr =>
        {
            var pool = Helpers.GetPoolOfOne(connection);
            return pool != null && connection.Resolve(pool.default_SR) == sr;
        }) ?? StorageRepositories.FirstOrDefault();

        SelectedNetwork = Networks.FirstOrDefault();
        SelectedHost = Hosts.FirstOrDefault();
        StepIndex = 0;
        UpdateStepVisibility();
    }

    public ObservableCollection<VM> Templates { get; } = new();
    public ObservableCollection<Host> Hosts { get; } = new();
    public ObservableCollection<SR> StorageRepositories { get; } = new();
    public ObservableCollection<XenAPI.Network> Networks { get; } = new();

    [ObservableProperty] private int _stepIndex;
    [ObservableProperty] private bool _showTemplateStep;
    [ObservableProperty] private bool _showNameStep;
    [ObservableProperty] private bool _showComputeStep;
    [ObservableProperty] private bool _showStorageStep;
    [ObservableProperty] private bool _showNetworkStep;
    [ObservableProperty] private bool _showConfirmStep;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _isLastStep;
    [ObservableProperty] private string _stepTitle = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private VM? _selectedTemplate;
    [ObservableProperty] private string _vmName = string.Empty;
    [ObservableProperty] private string _vmDescription = string.Empty;
    [ObservableProperty] private Host? _selectedHost;
    [ObservableProperty] private string _vcpusText = "1";
    [ObservableProperty] private string _memoryMibText = "1024";
    [ObservableProperty] private SR? _selectedStorage;
    [ObservableProperty] private string _diskGibText = "24";
    [ObservableProperty] private XenAPI.Network? _selectedNetwork;
    [ObservableProperty] private bool _startAfter = true;
    [ObservableProperty] private string _summaryText = string.Empty;

    partial void OnSelectedTemplateChanged(VM? value)
    {
        if (value == null)
            return;
        if (string.IsNullOrWhiteSpace(VmName))
            VmName = Helpers.DefaultVMName(Helpers.GetName(value), _connection);
        VcpusText = Math.Max(1, value.VCPUs_at_startup).ToString();
        MemoryMibText = Math.Max(1, value.memory_dynamic_max / (1024 * 1024)).ToString();
        var provisionSize = TryGetProvisionSize(value);
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
            case 2 when !long.TryParse(VcpusText, out var v) || v < 1:
                StatusMessage = "Enter a valid vCPU count.";
                return false;
            case 2 when !long.TryParse(MemoryMibText, out var m) || m < 1:
                StatusMessage = "Enter memory in MiB.";
                return false;
            case 3 when SelectedStorage == null:
                StatusMessage = "Select a storage repository.";
                return false;
            case 3 when !long.TryParse(DiskGibText, out var g) || g < 1:
                StatusMessage = "Enter disk size in GiB.";
                return false;
            case 4 when SelectedNetwork == null:
                StatusMessage = "Select a network.";
                return false;
        }

        return true;
    }

    private void Finish()
    {
        if (SelectedTemplate == null || SelectedStorage == null || SelectedNetwork == null)
            return;
        if (!long.TryParse(VcpusText, out var vcpus) || !long.TryParse(MemoryMibText, out var mib)
            || !long.TryParse(DiskGibText, out var gib))
            return;

        var memory = mib * 1024L * 1024L;
        var disks = BuildDisks(SelectedTemplate, SelectedStorage, VmName.Trim(), gib * 1024L * 1024L * 1024L);
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
            SelectedTemplate,
            copyBiosStringsFrom: null,
            VmName.Trim(),
            VmDescription.Trim(),
            InstallMethod.None,
            pvArgs: SelectedTemplate.PV_args ?? string.Empty,
            cd: null,
            url: string.Empty,
            VmBootMode.Bios,
            SelectedHost,
            vcpusMax: Math.Max(vcpus, SelectedTemplate.VCPUs_max),
            vcpusAtStartup: vcpus,
            memoryDynamicMin: Math.Min(SelectedTemplate.memory_dynamic_min, memory),
            memoryDynamicMax: memory,
            memoryStaticMax: memory,
            disks,
            fullCopySR: null,
            vifs,
            StartAfter,
            assignVtpm: false,
            (_, _) => { },
            (_, _) => { },
            vGpus: null,
            modifyVgpuSettings: false,
            coresPerSocket: 0,
            cloudConfigDriveTemplateText: string.Empty);

        ShellActionRunner.Run(action);
        _close();
    }

    private void UpdateStepVisibility()
    {
        ShowTemplateStep = StepIndex == 0;
        ShowNameStep = StepIndex == 1;
        ShowComputeStep = StepIndex == 2;
        ShowStorageStep = StepIndex == 3;
        ShowNetworkStep = StepIndex == 4;
        ShowConfirmStep = StepIndex == 5;
        CanGoBack = StepIndex > 0;
        IsLastStep = StepIndex >= 5;
        StepTitle = StepIndex switch
        {
            0 => "Template",
            1 => "Name",
            2 => "CPU & memory",
            3 => "Storage",
            4 => "Network",
            _ => "Confirm"
        };

        if (ShowConfirmStep)
        {
            SummaryText =
                $"Create VM '{VmName}' from '{Helpers.GetName(SelectedTemplate)}'\n" +
                $"Home: {Helpers.GetName(SelectedHost) ?? "(pool default)"}\n" +
                $"{VcpusText} vCPU · {MemoryMibText} MiB\n" +
                $"Disk {DiskGibText} GiB on {Helpers.GetName(SelectedStorage)}\n" +
                $"Network {Helpers.GetName(SelectedNetwork)}\n" +
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
        var provision = template.ProvisionXml();
        if (provision?.ChildNodes is { Count: > 0 })
        {
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
                var size = preferredSize;
                if (attrs["size"]?.Value is { } sizeText
                    && long.TryParse(sizeText, out var parsed) && parsed > 0)
                    size = parsed;
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
                virtual_size = preferredSize,
                type = vdi_type.user,
                read_only = false,
                SR = new XenRef<SR>(sr.opaque_ref)
            };
            disks.Add(new DiskDescription(disk, device));
        }

        return disks;
    }
}
