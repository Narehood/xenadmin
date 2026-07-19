using XenAdmin;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Read-only storage summary for the Avalonia shell (Physical Storage / VM Storage shaped).
/// </summary>
public static class StorageSummaryBuilder
{
    public readonly record struct StorageSummary(
        IReadOnlyList<GeneralPropertyRow> Totals,
        IReadOnlyList<StorageItemRow> Items);

    public static StorageSummary Build(InfraTreeNode? node)
    {
        if (node?.Server?.Connection is not { IsConnected: true } conn)
            return Empty;

        return node.Kind switch
        {
            InfraNodeKind.Pool => BuildPoolOrHost(conn, host: null),
            InfraNodeKind.Host => BuildPoolOrHost(conn, FindHost(conn, node.OpaqueRef)),
            InfraNodeKind.Vm => BuildVm(conn, FindVm(conn, node.OpaqueRef)),
            _ => Empty
        };
    }

    private static StorageSummary Empty { get; } =
        new(Array.Empty<GeneralPropertyRow>(), Array.Empty<StorageItemRow>());

    private static StorageSummary BuildPoolOrHost(IXenConnection conn, Host? host)
    {
        var srs = CollectSrs(conn, host);
        var items = srs
            .OrderBy(sr => Helpers.GetName(sr), StringComparer.OrdinalIgnoreCase)
            .Select(sr =>
            {
                var used = Util.DiskSizeString(sr.physical_utilisation);
                var total = Util.DiskSizeString(sr.physical_size);
                var free = Util.DiskSizeString(sr.FreeSpace());
                var pct = sr.physical_size > 0
                    ? (int)Math.Round(100.0 * sr.physical_utilisation / sr.physical_size)
                    : 0;

                return new StorageItemRow(
                    Helpers.GetName(sr),
                    sr.FriendlyTypeName(),
                    sr.shared ? "Shared" : "Local",
                    $"{used} / {total} ({pct}%)",
                    $"Free {free}");
            })
            .ToList();

        long totalSize = srs.Sum(sr => sr.physical_size);
        long totalUsed = srs.Sum(sr => sr.physical_utilisation);
        long totalFree = srs.Sum(sr => sr.FreeSpace());

        var totals = new List<GeneralPropertyRow>
        {
            new("SRs", srs.Count.ToString()),
            new("Total size", Util.DiskSizeString(totalSize)),
            new("Used", Util.DiskSizeString(totalUsed)),
            new("Free", Util.DiskSizeString(totalFree))
        };

        return new StorageSummary(totals, items);
    }

    private static StorageSummary BuildVm(IXenConnection conn, VM? vm)
    {
        if (vm == null)
            return Empty;

        var disks = new List<(VBD vbd, VDI vdi, SR? sr)>();
        foreach (var vbd in conn.ResolveAll(vm.VBDs))
        {
            if (vbd == null || vbd.IsCDROM() || vbd.IsFloppyDrive())
                continue;

            var vdi = conn.Resolve(vbd.VDI);
            if (vdi == null || !vdi.Show(showHiddenVMs: true))
                continue;

            disks.Add((vbd, vdi, conn.Resolve(vdi.SR)));
        }

        disks.Sort((a, b) =>
        {
            _ = int.TryParse(a.vbd.userdevice, out var ai);
            _ = int.TryParse(b.vbd.userdevice, out var bi);
            var cmp = ai.CompareTo(bi);
            return cmp != 0
                ? cmp
                : string.Compare(Helpers.GetName(a.vdi), Helpers.GetName(b.vdi), StringComparison.OrdinalIgnoreCase);
        });

        var items = disks.Select(d =>
        {
            var srName = d.sr != null ? Helpers.GetName(d.sr) : "—";
            var active = d.vbd.currently_attached ? "Active" : "Inactive";
            var ro = d.vbd.IsReadOnly() ? "RO" : "RW";
            return new StorageItemRow(
                Helpers.GetName(d.vdi),
                $"Pos {d.vbd.userdevice}",
                srName,
                d.vdi.SizeText(),
                $"{active} · {ro}");
        }).ToList();

        long totalVirtual = disks.Sum(d => d.vdi.virtual_size);
        var totals = new List<GeneralPropertyRow>
        {
            new("Disks", disks.Count.ToString()),
            new("Total size", Util.DiskSizeString(totalVirtual))
        };

        return new StorageSummary(totals, items);
    }

    private static List<SR> CollectSrs(IXenConnection conn, Host? host)
    {
        var pbds = host != null
            ? conn.ResolveAll(host.PBDs)
            : conn.Cache.PBDs?.ToList() ?? new List<PBD>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var srs = new List<SR>();

        foreach (var pbd in pbds)
        {
            if (pbd == null)
                continue;

            var sr = conn.Resolve(pbd.SR);
            if (sr == null || sr.IsToolsSR() || !sr.Show(showHiddenVMs: true))
                continue;

            if (!seen.Add(sr.opaque_ref))
                continue;

            srs.Add(sr);
        }

        return srs;
    }

    private static Host? FindHost(IXenConnection conn, string? opaqueRef)
        => string.IsNullOrEmpty(opaqueRef)
            ? null
            : conn.Cache.Hosts?.FirstOrDefault(h => h.opaque_ref == opaqueRef);

    private static VM? FindVm(IXenConnection conn, string? opaqueRef)
        => string.IsNullOrEmpty(opaqueRef)
            ? null
            : conn.Cache.VMs?.FirstOrDefault(v => v.opaque_ref == opaqueRef);
}
