using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin;
using XenAdmin.Actions.VMActions;
using XenAdmin.Core;
using XenAdmin.Mappings;
using XenAPI;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

/// <summary>
/// Mirrors WinForms <c>WizardMode</c> for the migrate_send dialog.
/// Move = halted storage relocate; Migrate = live / suspended storage motion.
/// </summary>
public enum ShellMigrateWizardMode
{
    Migrate,
    Move
}

public sealed class CrossPoolHostOption
{
    public CrossPoolHostOption(Host host, string label)
    {
        Host = host;
        Label = label;
    }

    public Host Host { get; }
    public string Label { get; }
    public override string ToString() => Label;
}

public partial class CrossPoolDiskMapRow : ObservableObject
{
    public CrossPoolDiskMapRow(VDI vdi, string label, IReadOnlyList<MigrateSrOption> storageOptions, MigrateSrOption? selected)
    {
        Vdi = vdi;
        Label = label;
        StorageOptions = storageOptions;
        SelectedStorage = selected;
    }

    public VDI Vdi { get; }
    public string Label { get; }
    public IReadOnlyList<MigrateSrOption> StorageOptions { get; }

    [ObservableProperty]
    private MigrateSrOption? _selectedStorage;
}

public partial class CrossPoolVifMapRow : ObservableObject
{
    public CrossPoolVifMapRow(VIF vif, string label, IReadOnlyList<XenAPI.Network> networkOptions, XenAPI.Network? selected)
    {
        Vif = vif;
        Label = label;
        NetworkOptions = networkOptions;
        SelectedNetwork = selected;
    }

    public VIF Vif { get; }
    public string Label { get; }
    public IReadOnlyList<XenAPI.Network> NetworkOptions { get; }

    [ObservableProperty]
    private XenAPI.Network? _selectedNetwork;
}

public partial class VmCrossPoolMigrateViewModel : ViewModelBase
{
    private readonly VM _vm;
    private readonly Action _close;
    private readonly Action<string>? _status;
    private readonly IReadOnlyList<VDI> _movableDisks;
    private readonly ShellMigrateWizardMode _mode;

    public VmCrossPoolMigrateViewModel(
        VM vm,
        Action close,
        Action<string>? status = null,
        ShellMigrateWizardMode mode = ShellMigrateWizardMode.Migrate)
    {
        _vm = vm;
        _close = close;
        _status = status;
        _mode = mode;
        _movableDisks = ShellStoragePicker.GetMovableDisks(vm);

        WindowTitle = mode == ShellMigrateWizardMode.Move ? "Move VM" : "Cross-pool migrate";
        Heading = mode == ShellMigrateWizardMode.Move ? "Move VM" : "Cross-pool migrate";
        ShowCopyOption = mode == ShellMigrateWizardMode.Migrate;

        foreach (var host in ShellStoragePicker.EnumerateEligibleMigrateSendHosts(vm)
                     .OrderBy(h =>
                     {
                         var pool = Helpers.GetPool(h.Connection);
                         return pool?.Name() ?? h.Connection.Name;
                     }, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(h => h.name_label, StringComparer.OrdinalIgnoreCase))
        {
            var pool = Helpers.GetPool(host.Connection);
            var poolLabel = pool?.Name() ?? host.Connection.Name;
            Hosts.Add(new CrossPoolHostOption(host, $"{host.Name()} · {poolLabel}"));
        }

        SelectedHost = Hosts.FirstOrDefault();
        Hint = mode == ShellMigrateWizardMode.Move
            ? "Halted move. Same-pool targets use VDI copy + destroy (like WinForms Move). Cross-pool uses migrate_send with per-disk SR maps."
            : "Uses VM.migrate_send. For same-pool storage migrate, VIFs stay on their networks. Cross-pool requires per-disk SR and per-VIF network maps.";
        RefreshDestinationOptions();
    }

    public ObservableCollection<CrossPoolHostOption> Hosts { get; } = new();
    public ObservableCollection<CrossPoolDiskMapRow> DiskMaps { get; } = new();
    public ObservableCollection<CrossPoolVifMapRow> VifMaps { get; } = new();
    public ObservableCollection<XenAPI.Network> TransferNetworks { get; } = new();

    public string WindowTitle { get; }
    public string Heading { get; }
    public string Hint { get; }
    public bool ShowCopyOption { get; }

    public bool HasDiskMaps => DiskMaps.Count > 0;
    public bool HasVifMaps => VifMaps.Count > 0;
    public bool IsIntraPoolTarget =>
        SelectedHost?.Host.Connection != null
        && ReferenceEquals(_vm.Connection, SelectedHost.Host.Connection);

    /// <summary>
    /// Intra-pool halted Move uses <see cref="VMMoveAction"/> and does not need a transfer network.
    /// </summary>
    public bool ShowTransferNetwork =>
        SelectedHost != null
        && !(_mode == ShellMigrateWizardMode.Move && IsIntraPoolTarget && _vm.CanBeMoved());

    [ObservableProperty]
    private CrossPoolHostOption? _selectedHost;

    [ObservableProperty]
    private XenAPI.Network? _selectedTransferNetwork;

    [ObservableProperty]
    private bool _copyInsteadOfMigrate;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private MigrateSrOption? _applyAllStorage;

    [ObservableProperty]
    private XenAPI.Network? _applyAllNetwork;

    public ObservableCollection<MigrateSrOption> StorageRepositories { get; } = new();
    public ObservableCollection<XenAPI.Network> GuestNetworks { get; } = new();

    partial void OnSelectedHostChanged(CrossPoolHostOption? value) => RefreshDestinationOptions();

    private void RefreshDestinationOptions()
    {
        StorageRepositories.Clear();
        GuestNetworks.Clear();
        TransferNetworks.Clear();
        DiskMaps.Clear();
        VifMaps.Clear();
        SelectedTransferNetwork = null;
        ApplyAllStorage = null;
        ApplyAllNetwork = null;
        OnPropertyChanged(nameof(HasDiskMaps));
        OnPropertyChanged(nameof(HasVifMaps));
        OnPropertyChanged(nameof(IsIntraPoolTarget));
        OnPropertyChanged(nameof(ShowTransferNetwork));

        var host = SelectedHost?.Host;
        var conn = host?.Connection;
        if (conn is not { IsConnected: true } || host == null)
            return;

        // Intra-pool Move (VMMoveAction) only needs SupportsVdiCreate, not SupportsStorageMigration.
        var requireStorageMigration = !(_mode == ShellMigrateWizardMode.Move
                                        && ReferenceEquals(_vm.Connection, conn)
                                        && _vm.CanBeMoved());

        var srOptions = conn.Cache.SRs
            .Where(sr => sr != null
                         && !sr.IsToolsSR()
                         && sr.SupportsVdiCreate()
                         && (!requireStorageMigration || sr.SupportsStorageMigration())
                         && sr.PBDs.Count > 0
                         && !sr.IsBroken()
                         && !sr.IsDetached()
                         && ShellStoragePicker.SrVisibleToHost(sr, host)
                         && ShellStoragePicker.CanFitDisks(sr, _movableDisks))
            .OrderByDescending(sr => sr.shared)
            .ThenBy(sr => Helpers.GetName(sr), StringComparer.OrdinalIgnoreCase)
            .Select(sr => new MigrateSrOption(sr, ShellStoragePicker.FormatSrLabel(sr)))
            .ToList();

        foreach (var option in srOptions)
            StorageRepositories.Add(option);

        // Prefer shared destination that is not already the sole location of every disk.
        ApplyAllStorage = StorageRepositories.FirstOrDefault(o => o.Sr.shared
                                                                  && !ShellStoragePicker.IsCurrentLocation(o.Sr, _movableDisks))
                          ?? StorageRepositories.FirstOrDefault(o =>
                              !ShellStoragePicker.IsCurrentLocation(o.Sr, _movableDisks))
                          ?? StorageRepositories.FirstOrDefault();

        var networks = conn.Cache.Networks
            .Where(n => n != null && n.Show(true) && !n.IsGuestInstallerNetwork())
            .OrderBy(n => n.Name(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var network in networks)
            GuestNetworks.Add(network);

        ApplyAllNetwork = GuestNetworks.FirstOrDefault();

        if (ShowTransferNetwork)
        {
            foreach (var network in conn.Cache.Networks.OrderBy(n => n.Name(), StringComparer.OrdinalIgnoreCase))
            {
                var pifs = conn.ResolveAll(network.PIFs);
                if (pifs.Any(p => p.IsManagementInterface(false) || !string.IsNullOrEmpty(p.IP)))
                    TransferNetworks.Add(network);
            }

            SelectedTransferNetwork = TransferNetworks.FirstOrDefault(n =>
            {
                var pifs = conn.ResolveAll(n.PIFs);
                return pifs.Any(p => p.management);
            }) ?? TransferNetworks.FirstOrDefault();
        }

        foreach (var vbd in _vm.Connection.ResolveAll(_vm.VBDs)
                     .Where(v => v.type != vbd_type.CD && v.GetIsOwner())
                     .OrderBy(v => v.userdevice, StringComparer.OrdinalIgnoreCase))
        {
            var vdi = _vm.Connection.Resolve(vbd.VDI);
            if (vdi == null || vdi.IsToolsIso())
                continue;

            // Per-disk options: SRs that can fit this VDI (not necessarily all disks).
            var perDiskOptions = srOptions
                .Where(o => ShellStoragePicker.CanFitDisk(o.Sr, vdi))
                .ToList();
            var preferred = perDiskOptions.FirstOrDefault(o => o.Sr.shared
                                                               && o.Sr.opaque_ref != vdi.SR.opaque_ref)
                            ?? perDiskOptions.FirstOrDefault(o => o.Sr.opaque_ref != vdi.SR.opaque_ref)
                            ?? perDiskOptions.FirstOrDefault();

            var size = Util.DiskSizeString(vdi.virtual_size);
            var label = $"[{vbd.userdevice}] {Helpers.GetName(vdi)} ({size})";
            DiskMaps.Add(new CrossPoolDiskMapRow(vdi, label, perDiskOptions, preferred));
        }

        // Intra-pool migrate_send rejects VIF maps — only collect them for true cross-pool moves.
        if (!IsIntraPoolTarget)
        {
            foreach (var vif in _vm.Connection.ResolveAll(_vm.VIFs)
                         .OrderBy(v => v.device, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(vif.MAC))
                    continue;
                var net = _vm.Connection.Resolve(vif.network);
                var src = net != null ? Helpers.GetName(net) : "—";
                var label = $"device {vif.device} · {vif.MAC} (from {src})";
                VifMaps.Add(new CrossPoolVifMapRow(vif, label, networks, ApplyAllNetwork));
            }
        }

        OnPropertyChanged(nameof(HasDiskMaps));
        OnPropertyChanged(nameof(HasVifMaps));
        OnPropertyChanged(nameof(IsIntraPoolTarget));
        OnPropertyChanged(nameof(ShowTransferNetwork));
    }

    [RelayCommand]
    private void ApplyStorageToAll()
    {
        if (ApplyAllStorage == null)
            return;
        foreach (var row in DiskMaps)
            row.SelectedStorage = ApplyAllStorage;
    }

    [RelayCommand]
    private void ApplyNetworkToAll()
    {
        if (ApplyAllNetwork == null)
            return;
        foreach (var row in VifMaps)
            row.SelectedNetwork = ApplyAllNetwork;
    }

    [RelayCommand]
    private void Start()
    {
        if (SelectedHost == null)
        {
            StatusMessage = "Select a destination host.";
            return;
        }

        if (DiskMaps.Count == 0)
        {
            StatusMessage = "No movable disks found on this VM.";
            return;
        }

        if (DiskMaps.Any(d => d.SelectedStorage == null))
        {
            StatusMessage = "Map every disk to a destination SR.";
            return;
        }

        // Reject maps where nothing actually moves (all disks already on chosen SRs).
        var anyMove = DiskMaps.Any(d => d.Vdi.SR.opaque_ref != d.SelectedStorage!.Sr.opaque_ref);
        if (!anyMove)
        {
            StatusMessage = "Choose different destination SRs — disks are already on the selected storage.";
            return;
        }

        foreach (var row in DiskMaps)
        {
            if (!ShellStoragePicker.CanFitDisk(row.SelectedStorage!.Sr, row.Vdi))
            {
                StatusMessage = $"Not enough free space on {Helpers.GetName(row.SelectedStorage.Sr)} for {Helpers.GetName(row.Vdi)}.";
                return;
            }
        }

        var intraPool = ReferenceEquals(_vm.Connection, SelectedHost.Host.Connection);

        // WinForms CrossPoolMigrateWizard: WizardMode.Move + intra-pool → VMMoveAction (copy+destroy).
        if (_mode == ShellMigrateWizardMode.Move && intraPool && _vm.CanBeMoved())
        {
            var storage = new Dictionary<string, SR>();
            foreach (var row in DiskMaps)
                storage[row.Vdi.opaque_ref] = row.SelectedStorage!.Sr;

            var moveAction = new VMMoveAction(_vm, storage, SelectedHost.Host);
            ShellActionRunner.Run(moveAction, msg =>
            {
                StatusMessage = msg;
                _status?.Invoke(msg);
            });
            StatusMessage = "Move queued — see Logs.";
            _close();
            return;
        }

        if (SelectedTransferNetwork == null)
        {
            StatusMessage = "Select a transfer network on the destination.";
            return;
        }

        var mapping = new VmMapping(_vm.opaque_ref)
        {
            VmNameLabel = _vm.name_label ?? string.Empty,
            XenRef = SelectedHost.Host.opaque_ref,
            TargetName = SelectedHost.Host.Name()
        };

        foreach (var row in DiskMaps)
            mapping.Storage[row.Vdi.opaque_ref] = row.SelectedStorage!.Sr;

        // Intra-pool migrate_send forbids a non-empty VIF map.
        if (!intraPool)
        {
            if (VifMaps.Any(v => v.SelectedNetwork == null))
            {
                StatusMessage = "Map every VIF to a destination network.";
                return;
            }

            foreach (var row in VifMaps)
                mapping.VIFs[row.Vif.MAC] = row.SelectedNetwork!;
        }

        var copy = ShowCopyOption && CopyInsteadOfMigrate;
        var action = new VMCrossPoolMigrateAction(
            _vm,
            SelectedHost.Host,
            SelectedTransferNetwork,
            mapping,
            copy);

        ShellActionRunner.Run(action, msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });

        StatusMessage = copy
            ? "Cross-pool copy queued — see Logs."
            : _mode == ShellMigrateWizardMode.Move
                ? "Move queued — see Logs."
                : "Cross-pool migrate queued — see Logs.";
        _close();
    }
}
