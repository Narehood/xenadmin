using XenAdmin;
using XenAdmin.Core;
using XenAPI;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Shared SR filtering for Move / Migrate / Cross-pool dialogs.
/// Mirrors WinForms <c>SrPickerItem</c> rules used by soak-critical paths.
/// </summary>
public static class ShellStoragePicker
{
    public static IReadOnlyList<VDI> GetMovableDisks(VM vm)
    {
        var disks = new List<VDI>();
        foreach (var vbd in vm.Connection.ResolveAll(vm.VBDs))
        {
            if (vbd.type == vbd_type.CD || !vbd.GetIsOwner())
                continue;
            var vdi = vm.Connection.Resolve(vbd.VDI);
            if (vdi == null || vdi.IsToolsIso())
                continue;
            disks.Add(vdi);
        }

        return disks;
    }

    public static bool IsCurrentLocation(SR sr, IReadOnlyList<VDI> disks) =>
        disks.Count > 0 && disks.All(vdi => vdi.SR.opaque_ref == sr.opaque_ref);

    public static bool IsUsableDestination(SR sr, IReadOnlyList<VDI> disks, bool requireStorageMigration)
    {
        if (sr == null || sr.IsToolsSR() || sr.PBDs.Count == 0 || sr.IsBroken() || sr.IsDetached())
            return false;
        if (!sr.SupportsVdiCreate())
            return false;
        if (IsCurrentLocation(sr, disks))
            return false;
        if (requireStorageMigration && !sr.SupportsStorageMigration())
            return false;
        return true;
    }

    /// <summary>
    /// Shared SRs visible from the host, or host-local SRs owned by that host.
    /// </summary>
    public static bool SrVisibleToHost(SR sr, Host host)
    {
        if (sr.shared)
            return sr.CanBeSeenFrom(host) || host.Connection.ResolveAll(sr.PBDs).Any(p => p.currently_attached);

        var storageHost = sr.GetStorageHost();
        return storageHost != null && storageHost.opaque_ref == host.opaque_ref;
    }

    public static string FormatSrLabel(SR sr)
    {
        var name = Helpers.GetName(sr);
        var free = Util.DiskSizeString(sr.FreeSpace(), 1);
        var total = Util.DiskSizeString(sr.physical_size, 1);
        var space = $"{free} free of {total}";

        if (sr.shared)
            return $"{name} · shared · {space}";

        var host = sr.GetStorageHost();
        return host != null
            ? $"{name} · {host.Name()} · {space}"
            : $"{name} · local · {space}";
    }

    public static Host? ResolveTargetHostForSr(VM vm, SR sr) =>
        sr.GetStorageHost()
        ?? vm.GetStorageHost(false)
        ?? Helpers.GetCoordinator(vm.Connection)
        ?? vm.Connection.Cache.Hosts.FirstOrDefault();
}
