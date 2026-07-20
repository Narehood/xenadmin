using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin;
using XenAdmin.Actions.VMActions;
using XenAdmin.Core;
using XenAdmin.Mappings;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

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
    public CrossPoolDiskMapRow(VDI vdi, string label, IReadOnlyList<SR> storageOptions, SR? selected)
    {
        Vdi = vdi;
        Label = label;
        StorageOptions = storageOptions;
        SelectedStorage = selected;
    }

    public VDI Vdi { get; }
    public string Label { get; }
    public IReadOnlyList<SR> StorageOptions { get; }

    [ObservableProperty]
    private SR? _selectedStorage;
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

    public VmCrossPoolMigrateViewModel(VM vm, Action close, Action<string>? status = null)
    {
        _vm = vm;
        _close = close;
        _status = status;

        var resident = vm.Connection.Resolve(vm.resident_on);
        foreach (var conn in ConnectionsManager.XenConnectionsCopy.Where(c => c is { IsConnected: true }))
        {
            var pool = Helpers.GetPool(conn);
            var poolLabel = pool?.Name() ?? conn.Name;
            foreach (var host in conn.Cache.Hosts
                         .Where(h => h.enabled && h.IsLive())
                         .OrderBy(h => h.name_label, StringComparer.OrdinalIgnoreCase))
            {
                if (resident != null && host.opaque_ref == resident.opaque_ref)
                    continue;
                Hosts.Add(new CrossPoolHostOption(host, $"{host.Name()} · {poolLabel}"));
            }
        }

        SelectedHost = Hosts.FirstOrDefault();
        Hint = "Uses VM.migrate_send. For same-pool storage migrate, VIFs stay on their networks. Cross-pool requires per-disk SR and per-VIF network maps.";
        RefreshDestinationOptions();
    }

    public ObservableCollection<CrossPoolHostOption> Hosts { get; } = new();
    public ObservableCollection<CrossPoolDiskMapRow> DiskMaps { get; } = new();
    public ObservableCollection<CrossPoolVifMapRow> VifMaps { get; } = new();
    public ObservableCollection<XenAPI.Network> TransferNetworks { get; } = new();

    public string Hint { get; }

    public bool HasDiskMaps => DiskMaps.Count > 0;
    public bool HasVifMaps => VifMaps.Count > 0;
    public bool IsIntraPoolTarget =>
        SelectedHost?.Host.Connection != null
        && ReferenceEquals(_vm.Connection, SelectedHost.Host.Connection);

    [ObservableProperty]
    private CrossPoolHostOption? _selectedHost;

    [ObservableProperty]
    private XenAPI.Network? _selectedTransferNetwork;

    [ObservableProperty]
    private bool _copyInsteadOfMigrate;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private SR? _applyAllStorage;

    [ObservableProperty]
    private XenAPI.Network? _applyAllNetwork;

    public ObservableCollection<SR> StorageRepositories { get; } = new();
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

        var host = SelectedHost?.Host;
        var conn = host?.Connection;
        if (conn is not { IsConnected: true })
            return;

        var srs = conn.Cache.SRs
            .Where(sr => sr != null
                         && !sr.IsToolsSR()
                         && sr.SupportsVdiCreate()
                         && sr.PBDs.Count > 0
                         && !sr.IsBroken())
            .OrderBy(sr => Helpers.GetName(sr), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var sr in srs)
            StorageRepositories.Add(sr);

        ApplyAllStorage = StorageRepositories.FirstOrDefault();

        var networks = conn.Cache.Networks
            .Where(n => n != null && n.Show(true) && !n.IsGuestInstallerNetwork())
            .OrderBy(n => n.Name(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var network in networks)
            GuestNetworks.Add(network);

        ApplyAllNetwork = GuestNetworks.FirstOrDefault();

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

        foreach (var vbd in _vm.Connection.ResolveAll(_vm.VBDs)
                     .Where(v => v.type != vbd_type.CD)
                     .OrderBy(v => v.userdevice, StringComparer.OrdinalIgnoreCase))
        {
            var vdi = _vm.Connection.Resolve(vbd.VDI);
            if (vdi == null || vdi.IsToolsIso())
                continue;

            var size = Util.DiskSizeString(vdi.virtual_size);
            var label = $"[{vbd.userdevice}] {Helpers.GetName(vdi)} ({size})";
            DiskMaps.Add(new CrossPoolDiskMapRow(vdi, label, srs, ApplyAllStorage));
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

        if (SelectedTransferNetwork == null)
        {
            StatusMessage = "Select a transfer network on the destination.";
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

        var mapping = new VmMapping(_vm.opaque_ref)
        {
            VmNameLabel = _vm.name_label ?? string.Empty,
            XenRef = SelectedHost.Host.opaque_ref,
            TargetName = SelectedHost.Host.Name()
        };

        foreach (var row in DiskMaps)
            mapping.Storage[row.Vdi.opaque_ref] = row.SelectedStorage!;

        // Intra-pool migrate_send forbids a non-empty VIF map.
        var intraPool = ReferenceEquals(_vm.Connection, SelectedHost.Host.Connection);
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

        var action = new VMCrossPoolMigrateAction(
            _vm,
            SelectedHost.Host,
            SelectedTransferNetwork,
            mapping,
            CopyInsteadOfMigrate);

        ShellActionRunner.Run(action, msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });

        StatusMessage = CopyInsteadOfMigrate
            ? "Cross-pool copy queued — see Logs."
            : "Cross-pool migrate queued — see Logs.";
        _close();
    }
}
