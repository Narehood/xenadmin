using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions;
using XenAdmin.Core;
using XenAdmin.Model;
using XenAPI;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public enum VmPropertiesSection
{
    General,
    CpuMemory,
    Boot,
    StartupHa,
    HomeServer,
    Gpu,
    Usb
}

public sealed class VmPropertiesSectionItem
{
    public VmPropertiesSectionItem(VmPropertiesSection section, string title)
    {
        Section = section;
        Title = title;
    }

    public VmPropertiesSection Section { get; }
    public string Title { get; }
}

public sealed class BootDeviceItem
{
    public BootDeviceItem(char code, string label, bool enabled)
    {
        Code = code;
        Label = label;
        Enabled = enabled;
    }

    public char Code { get; }
    public string Label { get; }
    public bool Enabled { get; set; }

    public string Display => Enabled ? $"{Label} (on)" : $"{Label} (off)";

    public override string ToString() => Display;
}

public sealed class HaPriorityOption
{
    public HaPriorityOption(VM.HaRestartPriority priority, string label)
    {
        Priority = priority;
        Label = label;
    }

    public VM.HaRestartPriority Priority { get; }
    public string Label { get; }

    public override string ToString() => Label;
}

public sealed class HomeServerOption
{
    public HomeServerOption(Host? host, string label)
    {
        Host = host;
        Label = label;
    }

    public Host? Host { get; }
    public string Label { get; }

    public override string ToString() => Label;
}

public sealed class GpuAssignmentItem
{
    public GpuAssignmentItem(VGPU? existing, GPU_group? group, VGPU_type? type, string label)
    {
        Existing = existing;
        Group = group;
        Type = type;
        Label = label;
    }

    public VGPU? Existing { get; }
    public GPU_group? Group { get; }
    public VGPU_type? Type { get; }
    public string Label { get; }

    public override string ToString() => Label;
}

public sealed class GpuChoiceItem
{
    public GpuChoiceItem(GPU_group group, VGPU_type? type, string label)
    {
        Group = group;
        Type = type;
        Label = label;
    }

    public GPU_group Group { get; }
    public VGPU_type? Type { get; }
    public string Label { get; }

    public override string ToString() => Label;
}

public sealed class UsbAttachedItem
{
    public UsbAttachedItem(VUSB vusb, string label)
    {
        Vusb = vusb;
        Label = label;
    }

    public VUSB Vusb { get; }
    public string Label { get; }

    public override string ToString() => Label;
}

public sealed class UsbAvailableItem
{
    public UsbAvailableItem(PUSB pusb, string label)
    {
        Pusb = pusb;
        Label = label;
    }

    public PUSB Pusb { get; }
    public string Label { get; }

    public override string ToString() => Label;
}

public partial class VmPropertiesViewModel : ViewModelBase
{
    private static readonly char[] BootCodes = ['C', 'D', 'N'];

    private readonly VM _original;
    private readonly VM _clone;
    private readonly Action _close;
    private readonly Action<string>? _status;
    private readonly List<VGPU> _originalGpus;
    private readonly long _origOrder;
    private readonly long _origStartDelay;
    private readonly VM.HaRestartPriority _origHaPriority;
    private readonly string _origAffinityRef;
    private readonly string[] _origTags;
    private readonly bool _origAutoPowerOn;
    private readonly string _origBootOrder;
    private readonly string _origPvArgs;
    private readonly bool _origBootFromCd;
    private bool _bootFromCd;

    public VmPropertiesViewModel(VM vm, Action close, Action<string>? status = null)
    {
        _original = vm;
        _clone = (VM)vm.Clone();
        _close = close;
        _status = status;

        NameLabel = vm.name_label ?? string.Empty;
        Description = vm.name_description ?? string.Empty;
        VcpusText = Math.Max(1, vm.VCPUs_at_startup).ToString();
        MemoryMibText = Math.Max(1, vm.memory_dynamic_max / (1024 * 1024)).ToString();
        TagsText = string.Join(", ", Tags.GetTags(vm) ?? Array.Empty<string>());
        AutoPowerOn = vm.GetAutoPowerOn();
        PvArgs = vm.PV_args ?? string.Empty;
        IsHvm = vm.IsHVM();
        OrderText = vm.order.ToString();
        StartDelayText = vm.start_delay.ToString();

        _origTags = Tags.GetTags(vm) ?? Array.Empty<string>();
        _origAutoPowerOn = AutoPowerOn;
        _origBootOrder = vm.GetBootOrder();
        _origPvArgs = PvArgs;
        _origBootFromCd = DetectBootFromCd(vm);
        _bootFromCd = _origBootFromCd;
        _origOrder = vm.order;
        _origStartDelay = vm.start_delay;
        _origHaPriority = vm.HARestartPriority();
        _origAffinityRef = vm.affinity?.opaque_ref ?? Helper.NullOpaqueRef;
        _originalGpus = vm.Connection.ResolveAll(vm.VGPUs).Where(g => g != null).Cast<VGPU>().ToList();

        Sections.Add(new VmPropertiesSectionItem(VmPropertiesSection.General, "General"));
        Sections.Add(new VmPropertiesSectionItem(VmPropertiesSection.CpuMemory, "CPU & Memory"));
        Sections.Add(new VmPropertiesSectionItem(VmPropertiesSection.Boot, "Boot Options"));
        Sections.Add(new VmPropertiesSectionItem(VmPropertiesSection.StartupHa, "Startup Options"));
        Sections.Add(new VmPropertiesSectionItem(VmPropertiesSection.HomeServer, "Home Server"));

        ShowGpuSection = vm.CanHaveGpu() && Helpers.GpusAvailable(vm.Connection);
        if (ShowGpuSection)
            Sections.Add(new VmPropertiesSectionItem(VmPropertiesSection.Gpu, "GPU"));

        var pool = Helpers.GetPoolOfOne(vm.Connection);
        ShowUsbSection = vm.IsHVM()
                         && !vm.is_a_template
                         && !Helpers.FeatureForbidden(vm, Host.RestrictUsbPassthrough)
                         && pool != null
                         && pool.Connection.Cache.Hosts.Any(h => h.PUSBs.Count > 0);
        if (ShowUsbSection)
            Sections.Add(new VmPropertiesSectionItem(VmPropertiesSection.Usb, "USB"));

        SelectedSectionItem = Sections[0];

        PopulateBootDevices();
        PopulateHaPriorities();
        PopulateHomeServers();
        PopulateGpu();
        PopulateUsb();

        PoolHaEnabled = pool?.ha_enabled == true;
        HaStatusText = PoolHaEnabled
            ? "HA is enabled on this pool — choose a restart priority."
            : "HA is not enabled — restart priority is unavailable; order and start delay still apply.";
    }

    public ObservableCollection<VmPropertiesSectionItem> Sections { get; } = new();

    public ObservableCollection<BootDeviceItem> BootDevices { get; } = new();

    public ObservableCollection<HaPriorityOption> HaPriorities { get; } = new();

    public ObservableCollection<HomeServerOption> HomeServers { get; } = new();

    public ObservableCollection<GpuAssignmentItem> AssignedGpus { get; } = new();

    public ObservableCollection<GpuChoiceItem> GpuChoices { get; } = new();

    public ObservableCollection<UsbAttachedItem> AttachedUsbs { get; } = new();

    public ObservableCollection<UsbAvailableItem> AvailableUsbs { get; } = new();

    public bool IsHvm { get; }

    public bool ShowGpuSection { get; }

    public bool ShowUsbSection { get; }

    public bool PoolHaEnabled { get; }

    public string HaStatusText { get; }

    public bool ShowHvmBoot => IsHvm;

    public bool ShowPvBoot => !IsHvm;

    public bool CanEditGpu => _original.power_state == vm_power_state.Halted;

    public bool CanEditUsb =>
        _original.power_state == vm_power_state.Halted
        && !(PoolHaEnabled && VM.HaPriorityIsRestart(_original.Connection, SelectedHaPriority?.Priority ?? _origHaPriority));

    public string GpuHint => CanEditGpu
        ? "Assign a GPU group / vGPU type. Changes apply when you click OK."
        : "Shut down the VM to change GPU assignment.";

    public string UsbHint => CanEditUsb
        ? "Attach or detach USB devices immediately (VM must be halted)."
        : "USB changes require a halted VM without HA restart protection.";

    [ObservableProperty]
    private VmPropertiesSectionItem? _selectedSectionItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGeneral))]
    [NotifyPropertyChangedFor(nameof(ShowCpuMemory))]
    [NotifyPropertyChangedFor(nameof(ShowBoot))]
    [NotifyPropertyChangedFor(nameof(ShowStartupHa))]
    [NotifyPropertyChangedFor(nameof(ShowHomeServer))]
    [NotifyPropertyChangedFor(nameof(ShowGpu))]
    [NotifyPropertyChangedFor(nameof(ShowUsb))]
    private VmPropertiesSection _selectedSection = VmPropertiesSection.General;

    [ObservableProperty]
    private string _nameLabel = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _tagsText = string.Empty;

    [ObservableProperty]
    private string _vcpusText = "1";

    [ObservableProperty]
    private string _memoryMibText = "1024";

    [ObservableProperty]
    private bool _autoPowerOn;

    [ObservableProperty]
    private string _pvArgs = string.Empty;

    [ObservableProperty]
    private string _orderText = "0";

    [ObservableProperty]
    private string _startDelayText = "0";

    [ObservableProperty]
    private HaPriorityOption? _selectedHaPriority;

    [ObservableProperty]
    private HomeServerOption? _selectedHomeServer;

    [ObservableProperty]
    private BootDeviceItem? _selectedBootDevice;

    public IReadOnlyList<string> PvBootDevices { get; } = ["Hard Disk", "DVD Drive"];

    [ObservableProperty]
    private string _selectedPvBootDevice = "Hard Disk";

    [ObservableProperty]
    private GpuAssignmentItem? _selectedAssignedGpu;

    [ObservableProperty]
    private GpuChoiceItem? _selectedGpuChoice;

    [ObservableProperty]
    private UsbAttachedItem? _selectedAttachedUsb;

    [ObservableProperty]
    private UsbAvailableItem? _selectedAvailableUsb;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public bool ShowGeneral => SelectedSection == VmPropertiesSection.General;
    public bool ShowCpuMemory => SelectedSection == VmPropertiesSection.CpuMemory;
    public bool ShowBoot => SelectedSection == VmPropertiesSection.Boot;
    public bool ShowStartupHa => SelectedSection == VmPropertiesSection.StartupHa;
    public bool ShowHomeServer => SelectedSection == VmPropertiesSection.HomeServer;
    public bool ShowGpu => SelectedSection == VmPropertiesSection.Gpu;
    public bool ShowUsb => SelectedSection == VmPropertiesSection.Usb;

    partial void OnSelectedSectionItemChanged(VmPropertiesSectionItem? value)
    {
        if (value != null)
            SelectedSection = value.Section;
    }

    private void PopulateBootDevices()
    {
        BootDevices.Clear();
        if (!IsHvm)
        {
            SelectedPvBootDevice = _origBootFromCd ? "DVD Drive" : "Hard Disk";
            return;
        }

        var order = _origBootOrder.ToUpperInvariant();
        foreach (var c in order)
        {
            if (BootCodes.Contains(c))
                BootDevices.Add(new BootDeviceItem(c, BootLabel(c), enabled: true));
        }

        foreach (var c in BootCodes)
        {
            if (!order.Contains(c))
                BootDevices.Add(new BootDeviceItem(c, BootLabel(c), enabled: false));
        }
    }

    private static string BootLabel(char code) => code switch
    {
        'C' => "Hard Disk",
        'D' => "DVD Drive",
        'N' => "Network",
        _ => code.ToString()
    };

    private void PopulateHaPriorities()
    {
        HaPriorities.Clear();
        foreach (var p in VM.GetAvailableRestartPriorities(_original.Connection))
        {
            var opt = new HaPriorityOption(p, Helpers.RestartPriorityI18n(p));
            HaPriorities.Add(opt);
            if (p == _origHaPriority)
                SelectedHaPriority = opt;
        }

        SelectedHaPriority ??= HaPriorities.FirstOrDefault();
    }

    private void PopulateHomeServers()
    {
        HomeServers.Clear();
        var none = new HomeServerOption(null, "(None)");
        HomeServers.Add(none);

        Host? current = _original.Connection.Resolve(_original.affinity);
        SelectedHomeServer = none;

        foreach (var host in _original.Connection.Cache.Hosts
                     .OrderBy(h => h.name_label, StringComparer.OrdinalIgnoreCase))
        {
            var opt = new HomeServerOption(host, host.Name());
            HomeServers.Add(opt);
            if (current != null && host.opaque_ref == current.opaque_ref)
                SelectedHomeServer = opt;
        }
    }

    private void PopulateGpu()
    {
        AssignedGpus.Clear();
        GpuChoices.Clear();
        if (!ShowGpuSection)
            return;

        foreach (var vgpu in _originalGpus)
        {
            var group = _original.Connection.Resolve(vgpu.GPU_group);
            var type = _original.Connection.Resolve(vgpu.type);
            var label = type != null
                ? $"{group?.Name() ?? "GPU"} — {type.Description()}"
                : group?.ToString() ?? "GPU";
            AssignedGpus.Add(new GpuAssignmentItem(vgpu, group, type, label));
        }

        foreach (var group in _original.Connection.Cache.GPU_groups
                     .Where(g => g.PGPUs.Count > 0)
                     .OrderBy(g => g.Name(), StringComparer.OrdinalIgnoreCase))
        {
            var types = _original.Connection.ResolveAll(group.supported_VGPU_types)
                .OrderBy(t => t.IsPassthrough() ? 0 : 1)
                .ThenBy(t => t.model_name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (types.Count == 0)
            {
                GpuChoices.Add(new GpuChoiceItem(group, null, group.ToString()));
                continue;
            }

            foreach (var type in types)
                GpuChoices.Add(new GpuChoiceItem(group, type, $"{group.Name()} — {type.Description()}"));
        }

        SelectedGpuChoice = GpuChoices.FirstOrDefault();
    }

    private void PopulateUsb()
    {
        AttachedUsbs.Clear();
        AvailableUsbs.Clear();
        if (!ShowUsbSection)
            return;

        foreach (var vusb in _original.Connection.ResolveAll(_original.VUSBs))
        {
            var group = _original.Connection.Resolve(vusb.USB_group);
            var pusb = group?.PUSBs.Count > 0 ? _original.Connection.Resolve(group.PUSBs[0]) : null;
            var label = pusb?.Description() ?? vusb.uuid ?? "USB device";
            AttachedUsbs.Add(new UsbAttachedItem(vusb, label));
        }

        foreach (var host in _original.Connection.Cache.Hosts)
        {
            foreach (var pusb in _original.Connection.ResolveAll(host.PUSBs))
            {
                if (!pusb.passthrough_enabled)
                    continue;
                var group = _original.Connection.Resolve(pusb.USB_group);
                var attached = group?.VUSBs is { Count: > 0 };
                if (attached)
                    continue;
                AvailableUsbs.Add(new UsbAvailableItem(pusb, $"{host.Name()}: {pusb.Description()}"));
            }
        }

        SelectedAttachedUsb = AttachedUsbs.FirstOrDefault();
        SelectedAvailableUsb = AvailableUsbs.FirstOrDefault();
    }

    private static bool DetectBootFromCd(VM vm)
    {
        foreach (var vbd in vm.Connection.ResolveAll(vm.VBDs))
        {
            if (vbd.IsCDROM() && vbd.bootable)
                return true;
        }

        return false;
    }

    [RelayCommand]
    private void MoveBootUp()
    {
        if (SelectedBootDevice == null)
            return;
        var idx = BootDevices.IndexOf(SelectedBootDevice);
        if (idx <= 0)
            return;
        BootDevices.Move(idx, idx - 1);
    }

    [RelayCommand]
    private void MoveBootDown()
    {
        if (SelectedBootDevice == null)
            return;
        var idx = BootDevices.IndexOf(SelectedBootDevice);
        if (idx < 0 || idx >= BootDevices.Count - 1)
            return;
        BootDevices.Move(idx, idx + 1);
    }

    [RelayCommand]
    private void ToggleBootDevice()
    {
        if (SelectedBootDevice == null)
            return;
        SelectedBootDevice.Enabled = !SelectedBootDevice.Enabled;
        // Force list refresh for checkbox-less toggle UX
        var idx = BootDevices.IndexOf(SelectedBootDevice);
        if (idx >= 0)
        {
            var item = SelectedBootDevice;
            BootDevices.RemoveAt(idx);
            BootDevices.Insert(idx, new BootDeviceItem(item.Code, item.Label, item.Enabled));
            SelectedBootDevice = BootDevices[idx];
        }
    }

    [RelayCommand]
    private void AddGpu()
    {
        if (!CanEditGpu || SelectedGpuChoice == null)
            return;

        var choice = SelectedGpuChoice;
        var label = choice.Label;
        AssignedGpus.Add(new GpuAssignmentItem(
            existing: null,
            choice.Group,
            choice.Type,
            label));
    }

    [RelayCommand]
    private void RemoveGpu()
    {
        if (!CanEditGpu || SelectedAssignedGpu == null)
            return;
        AssignedGpus.Remove(SelectedAssignedGpu);
        SelectedAssignedGpu = AssignedGpus.FirstOrDefault();
    }

    [RelayCommand]
    private void AttachUsb()
    {
        if (!CanEditUsb || SelectedAvailableUsb == null)
            return;

        var item = SelectedAvailableUsb;
        ShellActionRunner.Run(new CreateVUSBAction(item.Pusb, _original), msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });
        AvailableUsbs.Remove(item);
        // Optimistic UI: refresh shortly via action completion is best-effort
        StatusMessage = "USB attach queued — see Logs. Re-open Properties to refresh the list.";
    }

    [RelayCommand]
    private void DetachUsb()
    {
        if (!CanEditUsb || SelectedAttachedUsb == null)
            return;

        var item = SelectedAttachedUsb;
        ShellActionRunner.Run(new DeleteVUSBAction(item.Vusb, _original), msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });
        AttachedUsbs.Remove(item);
        SelectedAttachedUsb = AttachedUsbs.FirstOrDefault();
        StatusMessage = "USB detach queued — see Logs.";
    }

    [RelayCommand]
    private void Apply()
    {
        if (string.IsNullOrWhiteSpace(NameLabel))
        {
            StatusMessage = "Enter a VM name.";
            SelectedSection = VmPropertiesSection.General;
            SelectedSectionItem = Sections.First(s => s.Section == VmPropertiesSection.General);
            return;
        }

        if (!long.TryParse(VcpusText.Trim(), out var vcpus) || vcpus < 1)
        {
            StatusMessage = "Enter a valid vCPU count.";
            SelectedSection = VmPropertiesSection.CpuMemory;
            SelectedSectionItem = Sections.First(s => s.Section == VmPropertiesSection.CpuMemory);
            return;
        }

        if (!long.TryParse(MemoryMibText.Trim(), out var mib) || mib < 1)
        {
            StatusMessage = "Enter memory in MiB.";
            SelectedSection = VmPropertiesSection.CpuMemory;
            SelectedSectionItem = Sections.First(s => s.Section == VmPropertiesSection.CpuMemory);
            return;
        }

        if (!long.TryParse(OrderText.Trim(), out var order) || order < 0)
        {
            StatusMessage = "Enter a non-negative startup order.";
            SelectedSection = VmPropertiesSection.StartupHa;
            SelectedSectionItem = Sections.First(s => s.Section == VmPropertiesSection.StartupHa);
            return;
        }

        if (!long.TryParse(StartDelayText.Trim(), out var startDelay) || startDelay < 0)
        {
            StatusMessage = "Enter a non-negative start delay (seconds).";
            SelectedSection = VmPropertiesSection.StartupHa;
            SelectedSectionItem = Sections.First(s => s.Section == VmPropertiesSection.StartupHa);
            return;
        }

        var actions = new List<AsyncAction>();
        var name = NameLabel.Trim();
        var description = Description ?? string.Empty;
        var tags = ParseTags(TagsText);

        // General fields on clone → SaveChangesAction
        _clone.name_label = name;
        _clone.name_description = description;
        _clone.tags = tags.ToArray();

        if (IsHvm)
        {
            var orderStr = string.Concat(BootDevices.Where(b => b.Enabled).Select(b => b.Code));
            if (string.IsNullOrEmpty(orderStr))
                orderStr = "CD";
            _clone.SetBootOrder(orderStr);
        }
        else
        {
            _clone.PV_args = PvArgs ?? string.Empty;
            _bootFromCd = string.Equals(SelectedPvBootDevice, "DVD Drive", StringComparison.OrdinalIgnoreCase);
        }

        _clone.SetAutoPowerOn(AutoPowerOn);

        actions.Add(new SaveChangesAction(_clone, suppressHistory: true, _original));

        // Bootable VBD changes (PV DVD vs disk)
        if (!IsHvm && _bootFromCd != _origBootFromCd)
        {
            actions.Add(CreateBootableVbdAction(_bootFromCd));
        }
        else if (IsHvm)
        {
            // Mirror WinForms: also refresh bootable flags when HVM boot order changes
            var newOrder = _clone.GetBootOrder();
            if (!string.Equals(newOrder, _origBootOrder, StringComparison.OrdinalIgnoreCase)
                || AutoPowerOn != _origAutoPowerOn
                || !string.Equals(PvArgs, _origPvArgs, StringComparison.Ordinal))
            {
                // AutoPowerOn / boot order already on clone; VBD bootable for HVM stays disk-first unless DVD first in order
                var preferCd = newOrder.StartsWith("D", StringComparison.OrdinalIgnoreCase);
                if (preferCd != _origBootFromCd || !string.Equals(newOrder, _origBootOrder, StringComparison.OrdinalIgnoreCase))
                    actions.Add(CreateBootableVbdAction(preferCd));
            }
        }

        if (vcpus != _original.VCPUs_at_startup || vcpus != _original.VCPUs_max)
        {
            var max = Math.Max(vcpus, _original.VCPUs_max);
            actions.Add(new ChangeVCPUSettingsAction(_clone, max, vcpus));
        }

        var bytes = mib * 1024L * 1024L;
        if (bytes != _original.memory_dynamic_max || bytes != _original.memory_static_max)
        {
            var staticMin = Math.Min(_original.memory_static_min, bytes);
            var dynamicMin = Math.Min(_original.memory_dynamic_min, bytes);
            actions.Add(new ChangeMemorySettingsAction(
                _clone,
                $"Set memory on {name}",
                staticMin,
                dynamicMin,
                bytes,
                bytes,
                (_, _) => { },
                (_, _) => { },
                suppressHistory: true));
        }

        var haPriority = SelectedHaPriority?.Priority ?? _origHaPriority;
        var haChanged = PoolHaEnabled && haPriority != _origHaPriority;
        var startupChanged = order != _origOrder || startDelay != _origStartDelay;
        if (haChanged || startupChanged)
        {
            var settings = new Dictionary<VM, VMStartupOptions>();
            if (haChanged)
            {
                settings[_clone] = new VMStartupOptions(order, startDelay, haPriority);
                var pool = Helpers.GetPoolOfOne(_original.Connection);
                var ntol = pool?.ha_host_failures_to_tolerate ?? 0;
                actions.Add(new SetHaPrioritiesAction(_original.Connection, settings, ntol, suppressHistory: true));
            }
            else
            {
                settings[_clone] = new VMStartupOptions(order, startDelay);
                actions.Add(new SetVMStartupOptionsAction(_original.Connection, settings, suppressHistory: true));
            }
        }

        var newAffinity = SelectedHomeServer?.Host?.opaque_ref ?? Helper.NullOpaqueRef;
        if (!string.Equals(newAffinity, _origAffinityRef, StringComparison.Ordinal))
        {
            actions.Add(new DelegatedAsyncAction(
                _original.Connection,
                "Change home server",
                "Changing home server…",
                "Home server updated.",
                session => VM.set_affinity(session, _original.opaque_ref, newAffinity),
                true,
                "vm.set_affinity"));
        }

        if (ShowGpuSection && CanEditGpu && GpuAssignmentsChanged())
        {
            var vgpus = AssignedGpus.Select(BuildVgpuForAssign).ToList();
            actions.Add(new GpuAssignAction(_clone, vgpus));
        }

        // Drop no-op SaveChanges-only when nothing else and clone equals original for tracked fields
        if (actions.Count == 1 && actions[0] is SaveChangesAction && !GeneralOrBootCloneChanged(name, description, tags))
        {
            StatusMessage = "No changes to apply.";
            _close();
            return;
        }

        var multi = new MultipleAction(
            _original.Connection,
            $"Update properties for {name}",
            "Updating properties…",
            $"Updated properties for {name}.",
            actions);

        _original.Locked = true;
        multi.Completed += _ =>
        {
            try
            {
                _original.Locked = false;
            }
            catch
            {
                // Best-effort unlock.
            }
        };

        ShellActionRunner.Run(multi, msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });

        StatusMessage = "Changes queued — see Logs.";
        _close();
    }

    private bool GeneralOrBootCloneChanged(string name, string description, List<string> tags)
    {
        if (!string.Equals(name, _original.name_label, StringComparison.Ordinal))
            return true;
        if (!string.Equals(description, _original.name_description ?? string.Empty, StringComparison.Ordinal))
            return true;
        if (AutoPowerOn != _origAutoPowerOn)
            return true;
        if (IsHvm)
        {
            var orderStr = string.Concat(BootDevices.Where(b => b.Enabled).Select(b => b.Code));
            if (!string.Equals(orderStr, _origBootOrder, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        else if (!string.Equals(PvArgs ?? string.Empty, _origPvArgs, StringComparison.Ordinal)
                 || _bootFromCd != _origBootFromCd)
        {
            return true;
        }

        var oldTags = _origTags.OrderBy(t => t, StringComparer.Ordinal).ToArray();
        var newTags = tags.OrderBy(t => t, StringComparer.Ordinal).ToArray();
        return !oldTags.SequenceEqual(newTags, StringComparer.Ordinal);
    }

    private bool GpuAssignmentsChanged()
    {
        var currentRefs = AssignedGpus
            .Where(g => g.Existing != null)
            .Select(g => g.Existing!.opaque_ref)
            .OrderBy(r => r)
            .ToList();
        var origRefs = _originalGpus.Select(g => g.opaque_ref).OrderBy(r => r).ToList();
        if (AssignedGpus.Any(g => g.Existing == null))
            return true;
        if (currentRefs.Count != origRefs.Count)
            return true;
        return !currentRefs.SequenceEqual(origRefs, StringComparer.Ordinal);
    }

    private static VGPU BuildVgpuForAssign(GpuAssignmentItem item)
    {
        if (item.Existing != null)
            return item.Existing;

        var vgpu = new VGPU
        {
            GPU_group = new XenRef<GPU_group>(item.Group!.opaque_ref),
            device = "0"
        };
        if (item.Type != null)
            vgpu.type = new XenRef<VGPU_type>(item.Type.opaque_ref);
        return vgpu;
    }

    private DelegatedAsyncAction CreateBootableVbdAction(bool bootFromCd)
    {
        return new DelegatedAsyncAction(
            _original.Connection,
            "Change VBDs bootable",
            "Change VBDs bootable",
            null,
            session =>
            {
                if (bootFromCd)
                {
                    foreach (var vbd in _original.Connection.ResolveAll(_original.VBDs))
                        VBD.set_bootable(session, vbd.opaque_ref, vbd.IsCDROM());
                }
                else
                {
                    var vbds = _original.Connection.ResolveAll(_original.VBDs);
                    vbds.Sort((a, b) =>
                    {
                        if (a.userdevice == "xvda")
                            return -1;
                        if (b.userdevice == "xvda")
                            return 1;
                        return string.Compare(a.userdevice, b.userdevice, StringComparison.Ordinal);
                    });
                    var foundSystemDisk = false;
                    foreach (var vbd in vbds)
                    {
                        var bootable = !foundSystemDisk && vbd.type == vbd_type.Disk;
                        if (bootable)
                            foundSystemDisk = true;
                        VBD.set_bootable(session, vbd.opaque_ref, bootable);
                    }
                }
            },
            true,
            "VBD.set_bootable");
    }

    private static List<string> ParseTags(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return text
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
