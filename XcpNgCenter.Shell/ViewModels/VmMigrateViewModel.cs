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

public sealed class MigrateSrOption
{
    public MigrateSrOption(SR sr, string label)
    {
        Sr = sr;
        Label = label;
    }

    public SR Sr { get; }
    public string Label { get; }
    public override string ToString() => Label;
}

public partial class VmMigrateViewModel : ViewModelBase
{
    private readonly VM _vm;
    private readonly Action _close;
    private readonly Action<string>? _status;
    private readonly IReadOnlyList<VDI> _movableDisks;

    public VmMigrateViewModel(VM vm, Action close, Action<string>? status = null)
    {
        _vm = vm;
        _close = close;
        _status = status;
        _movableDisks = ShellStoragePicker.GetMovableDisks(vm);

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
        RefreshStorageOptions();

        ShowStoragePicker = NeedsStorageMigrate || CanStorageMigrate;
        RequireStorage = NeedsStorageMigrate || !CanPoolMigrate;

        if (NeedsStorageMigrate)
        {
            Hint = resident == null
                ? "This VM uses local storage. Choose a destination host and an SR visible to that host (shared or that host’s local disks)."
                : $"Currently on {resident.Name()} with local disks. Choose another host and a destination SR (shared, or local on the target).";
        }
        else
        {
            Hint = resident == null
                ? "Select a destination host. Optionally pick an SR to move disks during migrate."
                : $"Currently on {resident.Name()}. Select another host; optionally choose a destination SR for storage migrate.";
        }
    }

    public ObservableCollection<Host> Hosts { get; } = new();
    public ObservableCollection<MigrateSrOption> StorageRepositories { get; } = new();

    public string Hint { get; }
    public bool NeedsStorageMigrate { get; }
    public bool CanPoolMigrate { get; }
    public bool CanStorageMigrate { get; }
    public bool ShowStoragePicker { get; }
    public bool RequireStorage { get; }

    [ObservableProperty]
    private Host? _selectedHost;

    [ObservableProperty]
    private MigrateSrOption? _selectedStorage;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    partial void OnSelectedHostChanged(Host? value) => RefreshStorageOptions();

    private void RefreshStorageOptions()
    {
        StorageRepositories.Clear();
        SelectedStorage = null;

        var target = SelectedHost;
        if (target == null || !CanStorageMigrate)
            return;

        var srs = _vm.Connection.Cache.SRs
            .Where(sr => ShellStoragePicker.IsUsableDestination(sr, _movableDisks, requireStorageMigration: true))
            .Where(sr => ShellStoragePicker.SrVisibleToHost(sr, target))
            .OrderByDescending(sr => sr.shared)
            .ThenBy(sr => Helpers.GetName(sr), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var sr in srs)
            StorageRepositories.Add(new MigrateSrOption(sr, ShellStoragePicker.FormatSrLabel(sr)));

        if (RequireStorage || NeedsStorageMigrate || !CanPoolMigrate)
        {
            SelectedStorage = StorageRepositories.FirstOrDefault(o => o.Sr.shared)
                              ?? StorageRepositories.FirstOrDefault();
        }
    }

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
                StatusMessage = NeedsStorageMigrate
                    ? "Local disks require a destination SR on the target host (or shared storage)."
                    : "Select a destination SR for the disks.";
                return;
            }

            if (!ShellStoragePicker.SrVisibleToHost(SelectedStorage.Sr, SelectedHost))
            {
                StatusMessage = "That SR is not visible from the selected host.";
                return;
            }

            if (ShellStoragePicker.IsCurrentLocation(SelectedStorage.Sr, _movableDisks))
            {
                StatusMessage = "Choose a different SR — disks are already on that storage.";
                return;
            }

            StartStorageMigrate(SelectedHost, SelectedStorage.Sr);
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

        foreach (var vdi in _movableDisks)
            mapping.Storage[vdi.opaque_ref] = sr;

        if (mapping.Storage.Count == 0)
        {
            StatusMessage = "No movable disks found on this VM.";
            return;
        }

        // Intra-pool migrate_send forbids a VIF map — leave VIFs empty when same connection.
        var intraPool = ReferenceEquals(_vm.Connection, host.Connection);
        if (!intraPool)
        {
            foreach (var vif in _vm.Connection.ResolveAll(_vm.VIFs))
            {
                if (string.IsNullOrEmpty(vif.MAC))
                    continue;
                var network = _vm.Connection.Resolve(vif.network);
                if (network != null)
                    mapping.VIFs[vif.MAC] = network;
            }
        }

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
