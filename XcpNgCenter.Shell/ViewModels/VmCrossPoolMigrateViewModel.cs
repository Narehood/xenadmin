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
        Hint = "Uses VM.migrate_send (cross-pool or storage migration). Connect the destination pool first. Storage and VIFs map to a single SR/network for this simplified shell dialog.";
        RefreshDestinationOptions();
    }

    public ObservableCollection<CrossPoolHostOption> Hosts { get; } = new();
    public ObservableCollection<SR> StorageRepositories { get; } = new();
    public ObservableCollection<XenAPI.Network> GuestNetworks { get; } = new();
    public ObservableCollection<XenAPI.Network> TransferNetworks { get; } = new();

    public string Hint { get; }

    [ObservableProperty]
    private CrossPoolHostOption? _selectedHost;

    [ObservableProperty]
    private SR? _selectedStorage;

    [ObservableProperty]
    private XenAPI.Network? _selectedGuestNetwork;

    [ObservableProperty]
    private XenAPI.Network? _selectedTransferNetwork;

    [ObservableProperty]
    private bool _copyInsteadOfMigrate;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    partial void OnSelectedHostChanged(CrossPoolHostOption? value) => RefreshDestinationOptions();

    private void RefreshDestinationOptions()
    {
        StorageRepositories.Clear();
        GuestNetworks.Clear();
        TransferNetworks.Clear();
        SelectedStorage = null;
        SelectedGuestNetwork = null;
        SelectedTransferNetwork = null;

        var host = SelectedHost?.Host;
        var conn = host?.Connection;
        if (conn is not { IsConnected: true })
            return;

        foreach (var sr in conn.Cache.SRs
                     .Where(sr => sr != null
                                  && !sr.IsToolsSR()
                                  && sr.SupportsVdiCreate()
                                  && sr.PBDs.Count > 0
                                  && !sr.IsBroken())
                     .OrderBy(sr => Helpers.GetName(sr), StringComparer.OrdinalIgnoreCase))
        {
            StorageRepositories.Add(sr);
        }

        SelectedStorage = StorageRepositories.FirstOrDefault();

        foreach (var network in conn.Cache.Networks
                     .Where(n => n != null && n.Show(true) && !n.IsGuestInstallerNetwork())
                     .OrderBy(n => n.Name(), StringComparer.OrdinalIgnoreCase))
        {
            GuestNetworks.Add(network);
        }

        SelectedGuestNetwork = GuestNetworks.FirstOrDefault();

        // Transfer networks: those with a PIF that has an IP (management or secondary)
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

    [RelayCommand]
    private void Start()
    {
        if (SelectedHost == null)
        {
            StatusMessage = "Select a destination host.";
            return;
        }

        if (SelectedStorage == null)
        {
            StatusMessage = "Select a destination SR for disks.";
            return;
        }

        if (SelectedTransferNetwork == null)
        {
            StatusMessage = "Select a transfer network on the destination.";
            return;
        }

        if (SelectedGuestNetwork == null && _vm.VIFs.Count > 0)
        {
            StatusMessage = "Select a destination network for VIFs.";
            return;
        }

        var mapping = new VmMapping(_vm.opaque_ref)
        {
            VmNameLabel = _vm.name_label ?? string.Empty,
            XenRef = SelectedHost.Host.opaque_ref,
            TargetName = SelectedHost.Host.Name()
        };

        foreach (var vbd in _vm.Connection.ResolveAll(_vm.VBDs))
        {
            if (vbd.type == vbd_type.CD)
                continue;
            var vdi = _vm.Connection.Resolve(vbd.VDI);
            if (vdi == null || vdi.IsToolsIso())
                continue;
            mapping.Storage[vdi.opaque_ref] = SelectedStorage;
        }

        if (mapping.Storage.Count == 0)
        {
            StatusMessage = "No movable disks found on this VM.";
            return;
        }

        foreach (var vif in _vm.Connection.ResolveAll(_vm.VIFs))
        {
            if (string.IsNullOrEmpty(vif.MAC) || SelectedGuestNetwork == null)
                continue;
            mapping.VIFs[vif.MAC] = SelectedGuestNetwork;
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
