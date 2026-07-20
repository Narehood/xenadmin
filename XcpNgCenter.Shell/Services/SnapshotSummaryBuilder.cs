using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services;

public static class SnapshotSummaryBuilder
{
    public static IReadOnlyList<SnapshotItemRow> Build(VM? vm)
    {
        if (vm?.Connection is not { IsConnected: true } conn)
            return Array.Empty<SnapshotItemRow>();

        return conn.ResolveAll(vm.snapshots)
            .Where(s => s != null && s.is_a_snapshot)
            .OrderByDescending(s => s.snapshot_time)
            .Select(s => new SnapshotItemRow(
                Helpers.GetName(s),
                string.IsNullOrWhiteSpace(s.name_description) ? "—" : s.name_description,
                s.snapshot_time.ToLocalTime().ToString("g"),
                s.opaque_ref))
            .ToList();
    }
}
