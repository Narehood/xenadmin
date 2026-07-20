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

public partial class VmMigrateViewModel : ViewModelBase
{
    private readonly VM _vm;
    private readonly Action _close;
    private readonly Action<string>? _status;

    public VmMigrateViewModel(VM vm, Action close, Action<string>? status = null)
    {
        _vm = vm;
        _close = close;
        _status = status;

        NeedsStorageMigrate = vm.SRs().Any(sr => sr != null && !sr.shared);
        CanPoolMigrate = vm.allowed_operations?.Contains(vm_operations.pool_migrate) == true
                         && vm.Connection.Cache.Hosts.Count(h => h.enabled && h.IsLive()) > 1;
        CanStorageMigrate = vm.allowed_operations?.Contains(vm_operations.migrate_send) == true
                            && vm.SRs().All(sr => sr != null && !sr.HBALunPerVDI());

        var resident = vm.Connection.Resolve(vm.resident_on);
        foreach (var host in vm.Connection.Cache.Hosts
                     .OrderBy(h => h.name_label, StringComparer.OrdinalIgnoreCase))
        {
            if (resident != null && host.opaque_ref == resident.opaque_ref)
                continue;
            if (!host.enabled || !host.IsLive())
                continue;
            Hosts.Add(host);
        }

        SelectedHost = Hosts.FirstOrDefault();

        foreach (var sr in vm.Connection.Cache.SRs
                     .Where(sr => sr != null
                                  && !sr.IsToolsSR()
                                  && sr.SupportsVdiCreate()
                                  && sr.PBDs.Count > 0
                                  && !sr.IsBroken()
                                  && (sr.shared || NeedsStorageMigrate))
                     .OrderByDescending(sr => sr.shared)
                     .ThenBy(sr => Helpers.GetName(sr), StringComparer.OrdinalIgnoreCase))
        {
            StorageRepositories.Add(sr);
        }

        // Prefer a shared SR when storage migrate is required; otherwise leave unset for live pool migrate.
        if (NeedsStorageMigrate || !CanPoolMigrate)
        {
            SelectedStorage = StorageRepositories.FirstOrDefault(sr => sr.shared)
                              ?? StorageRepositories.FirstOrDefault();
        }

        ShowStoragePicker = NeedsStorageMigrate || CanStorageMigrate;
        RequireStorage = NeedsStorageMigrate || !CanPoolMigrate;

        if (NeedsStorageMigrate)
        {
            Hint = resident == null
                ? "This VM uses local storage. Choose a destination host and a shared (or target-local) SR."
                : $"Currently on {resident.Name()} with local disks. Choose a host and destination SR.";
        }
        else
        {
            Hint = resident == null
                ? "Select a destination host. Optionally pick an SR to move disks during migrate."
                : $"Currently on {resident.Name()}. Select another host; optionally choose a destination SR.";
        }
    }

    public ObservableCollection<Host> Hosts { get; } = new();
    public ObservableCollection<SR> StorageRepositories { get; } = new();

    public string Hint { get; }
    public bool NeedsStorageMigrate { get; }
    public bool CanPoolMigrate { get; }
    public bool CanStorageMigrate { get; }
    public bool ShowStoragePicker { get; }
    public bool RequireStorage { get; }

    [ObservableProperty]
    private Host? _selectedHost;

    [ObservableProperty]
    private SR? _selectedStorage;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private void Migrate()
    {
        if (SelectedHost == null)
        {
            StatusMessage = "Select a destination host.";
            return;
        }

        var useStorageMigrate = RequireStorage
                                || NeedsStorageMigrate
                                || !CanPoolMigrate
                                || SelectedStorage != null;

        if (useStorageMigrate)
        {
            if (!CanStorageMigrate)
            {
                StatusMessage = "This VM cannot use storage migrate (migrate_send).";
                return;
            }

            if (SelectedStorage == null)
            {
                StatusMessage = "Select a destination SR for the disks.";
                return;
            }

            StartStorageMigrate(SelectedHost, SelectedStorage);
            return;
        }

        var action = new VMMigrateAction(_vm, SelectedHost);
        ShellActionRunner.Run(action, msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });
        StatusMessage = "Migrate queued — see Logs.";
        _close();
    }

    private void StartStorageMigrate(Host host, SR sr)
    {
        var mapping = new VmMapping(_vm.opaque_ref)
        {
            VmNameLabel = _vm.name_label ?? string.Empty,
            XenRef = host.opaque_ref,
            TargetName = host.Name()
        };

        foreach (var vbd in _vm.Connection.ResolveAll(_vm.VBDs))
        {
            if (vbd.type == vbd_type.CD)
                continue;
            var vdi = _vm.Connection.Resolve(vbd.VDI);
            if (vdi == null || vdi.IsToolsIso())
                continue;
            mapping.Storage[vdi.opaque_ref] = sr;
        }

        if (mapping.Storage.Count == 0)
        {
            StatusMessage = "No movable disks found on this VM.";
            return;
        }

        foreach (var vif in _vm.Connection.ResolveAll(_vm.VIFs))
        {
            if (string.IsNullOrEmpty(vif.MAC))
                continue;
            var network = _vm.Connection.Resolve(vif.network);
            if (network != null)
                mapping.VIFs[vif.MAC] = network;
        }

        // Same-pool storage migrate: use management network on the target host's connection.
        var transfer = host.Connection.Cache.Networks.FirstOrDefault(n =>
        {
            var pifs = host.Connection.ResolveAll(n.PIFs);
            return pifs.Any(p => p.management);
        }) ?? host.Connection.Cache.Networks.FirstOrDefault();

        if (transfer == null)
        {
            StatusMessage = "No transfer network found on the destination.";
            return;
        }

        var action = new VMCrossPoolMigrateAction(_vm, host, transfer, mapping, copy: false);
        ShellActionRunner.Run(action, msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });
        StatusMessage = "Storage migrate queued — see Logs.";
        _close();
    }
}
