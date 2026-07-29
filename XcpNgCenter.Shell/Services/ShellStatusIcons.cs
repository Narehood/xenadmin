using Avalonia.Media.Imaging;
using Avalonia.Platform;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Loads WinForms-parity 16×16 status icons shipped under Assets/Status.
/// </summary>
public static class ShellStatusIcons
{
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);

    public static Bitmap VmRunning => Load("000_StartVM_h32bit_16.png");
    public static Bitmap VmStopped => Load("000_StoppedVM_h32bit_16.png");
    public static Bitmap VmSuspended => Load("000_SuspendVM_h32bit_16.png");
    public static Bitmap VmPaused => Load("000_SuspendVM_h32bit_16_green.png");
    public static Bitmap VmStarting => Load("000_VMStarting_h32bit_16.png");
    public static Bitmap VmMigrate => Load("000_MigrateVM_h32bit_16.png");
    public static Bitmap VmMigrateStopped => Load("000_MigrateStoppedVM_h32bit_16.png");
    public static Bitmap VmMigrateSuspended => Load("000_MigrateSuspendedVM_h32bit_16.png");

    public static Bitmap HostConnected => Load("000_TreeConnected_h32bit_16.png");
    public static Bitmap HostDisconnected => Load("000_ServerDisconnected_h32bit_16.png");
    public static Bitmap HostConnecting => Load("000_ServerInProgress_h32bit_16.png");
    public static Bitmap HostEvacuate => Load("000_ServerMaintenance_h32bit_16.png");
    public static Bitmap Host => Load("000_Server_h32bit_16.png");

    public static Bitmap PoolConnected => Load("pool_up_16.png");

    public static Bitmap Storage => Load("000_Storage_h32bit_16.png");
    public static Bitmap StorageLocal => Load("000_VirtualStorage_h32bit_16.png");
    public static Bitmap StorageBroken => Load("000_StorageBroken_h32bit_16.png");
    public static Bitmap StorageDefault => Load("000_StorageDefault_h32bit_16.png");
    public static Bitmap StorageDisabled => Load("000_StorageDisabled_h32bit_16.png");

    public static Bitmap SnapshotDisk => Load("000_VMSnapShotDiskOnly_h32bit_16.png");
    public static Bitmap SnapshotDiskMemory => Load("000_VMSnapshotDiskMemory_h32bit_16.png");

    public static (Bitmap Icon, string Tooltip) ForVm(VM vm)
    {
        if (vm.current_operations != null
            && vm.current_operations.ContainsValue(vm_operations.migrate_send))
        {
            return vm.power_state switch
            {
                vm_power_state.Halted => (VmMigrateStopped, "Migrating (halted)"),
                vm_power_state.Suspended or vm_power_state.Paused => (VmMigrateSuspended, "Migrating (suspended)"),
                _ => (VmMigrate, "Migrating")
            };
        }

        if (vm.current_operations != null)
        {
            foreach (var op in vm.current_operations.Values)
            {
                if (VM.is_lifecycle_operation(op))
                    return (VmStarting, FormatOp(op));
            }
        }

        return vm.power_state switch
        {
            vm_power_state.Running => (VmRunning, "Running"),
            vm_power_state.Suspended => (VmSuspended, "Suspended"),
            vm_power_state.Paused => (VmPaused, "Paused"),
            vm_power_state.Halted => (VmStopped, "Halted"),
            _ => (VmStopped, vm.power_state.ToString())
        };
    }

    public static (Bitmap Icon, string Tooltip) ForHost(Host host)
    {
        var conn = host.Connection;
        // Connection down ⇒ disconnected, even if the last Host_metrics.live snapshot was true.
        // Otherwise pool members keep a green "online" glyph after the coordinator dies.
        if (conn == null || !conn.IsConnected)
        {
            if (conn is { InProgress: true })
                return (HostConnecting, "Connecting…");
            return (HostDisconnected, "Disconnected");
        }

        var metrics = conn.Resolve(host.metrics);
        if (metrics != null && metrics.live)
        {
            if ((host.current_operations?.ContainsValue(host_allowed_operations.evacuate) == true) || !host.enabled)
                return (HostEvacuate, host.enabled ? "Evacuating" : "Disabled / maintenance");
            return (HostConnected, "Connected");
        }

        if (conn.InProgress)
            return (HostConnecting, "Connecting…");

        return (HostDisconnected, "Disconnected");
    }

    public static (Bitmap Icon, string Tooltip) ForPool(IXenConnection conn)
    {
        // InProgress can remain true after a live session is up — only show the
        // yellow "connecting" glyph while we are actually disconnected.
        if (!conn.IsConnected)
        {
            if (conn.InProgress)
                return (HostConnecting, "Connecting…");
            return (HostDisconnected, "Disconnected");
        }

        return (PoolConnected, "Connected");
    }

    public static (Bitmap Icon, string Tooltip) ForSr(SR sr)
    {
        if (!sr.HasPBDs() || sr.IsHidden())
            return (StorageDisabled, "Disabled / hidden");
        if (sr.IsDetached() || sr.IsBroken() || !sr.MultipathAOK())
            return (StorageBroken, "Broken or detached");
        if (SR.IsDefaultSr(sr))
            return (StorageDefault, "Default SR");
        if (sr.IsLocalSR())
            return (StorageLocal, "Local storage");
        return (Storage, "Shared storage");
    }

    private static string FormatOp(vm_operations op) => op switch
    {
        vm_operations.start or vm_operations.start_on => "Starting…",
        vm_operations.clean_reboot or vm_operations.hard_reboot => "Rebooting…",
        vm_operations.clean_shutdown or vm_operations.hard_shutdown => "Shutting down…",
        vm_operations.suspend => "Suspending…",
        vm_operations.resume or vm_operations.resume_on => "Resuming…",
        vm_operations.checkpoint => "Checkpoint…",
        vm_operations.snapshot => "Snapshot…",
        vm_operations.clone or vm_operations.copy => "Copying…",
        _ => $"{op}…"
    };

    private static Bitmap Load(string fileName)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(fileName, out var cached))
                return cached;

            var uri = new Uri($"avares://XcpNgCenter.Shell/Assets/Status/{fileName}");
            using var stream = AssetLoader.Open(uri);
            var bmp = new Bitmap(stream);
            Cache[fileName] = bmp;
            return bmp;
        }
    }
}
