using System.Collections.ObjectModel;
using XenAdmin.Core;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Builds a parent/child snapshot tree (WinForms SnapshotsPage parity).
/// </summary>
public static class SnapshotSummaryBuilder
{
    public static IReadOnlyList<SnapshotItemRow> Build(VM? vm)
    {
        if (vm?.Connection is not { IsConnected: true } conn)
            return Array.Empty<SnapshotItemRow>();

        var snapshots = conn.ResolveAll(vm.snapshots)
            .Where(s => s != null && s.is_a_snapshot)
            .ToList();

        var snapshotRefs = new HashSet<string>(snapshots.Select(s => s.opaque_ref), StringComparer.Ordinal);

        var baseNode = new SnapshotItemRow(
            Helpers.GetName(vm),
            "Base",
            string.Empty,
            opaqueRef: string.Empty,
            isBase: true,
            icon: ShellStatusIcons.VmRunning);

        // Roots: parent is null or parent is not itself a snapshot of this VM.
        var roots = snapshots
            .Where(s =>
            {
                var parent = conn.Resolve(s.parent);
                return parent == null || !snapshotRefs.Contains(parent.opaque_ref);
            })
            .OrderBy(s => s.snapshot_time)
            .ToList();

        var currentParent = conn.Resolve(vm.parent);
        var nowAttached = false;

        if (currentParent == null || !snapshotRefs.Contains(currentParent.opaque_ref))
        {
            baseNode.Children.Add(CreateNowNode());
            nowAttached = true;
        }

        foreach (var root in roots)
        {
            var icon = CreateSnapshotNode(conn, root, vm, snapshotRefs, ref nowAttached);
            baseNode.Children.Add(icon);
        }

        if (!nowAttached)
            baseNode.Children.Add(CreateNowNode());

        return new[] { baseNode };
    }

    private static SnapshotItemRow CreateSnapshotNode(
        XenAdmin.Network.IXenConnection conn,
        VM snapshot,
        VM liveVm,
        HashSet<string> snapshotRefs,
        ref bool nowAttached)
    {
        var isMemory = snapshot.power_state == vm_power_state.Suspended;
        var row = new SnapshotItemRow(
            Helpers.GetName(snapshot),
            string.IsNullOrWhiteSpace(snapshot.name_description) ? (isMemory ? "Disk + memory" : "Disk") : snapshot.name_description,
            snapshot.snapshot_time.ToLocalTime().ToString("g"),
            snapshot.opaque_ref,
            icon: isMemory ? ShellStatusIcons.SnapshotDiskMemory : ShellStatusIcons.SnapshotDisk);

        var currentParent = conn.Resolve(liveVm.parent);
        if (!nowAttached && currentParent != null && currentParent.opaque_ref == snapshot.opaque_ref)
        {
            row.Children.Add(CreateNowNode());
            nowAttached = true;
        }

        foreach (var childRef in snapshot.children ?? Enumerable.Empty<XenRef<VM>>())
        {
            if (!snapshotRefs.Contains(childRef.opaque_ref))
                continue;
            var child = conn.Resolve(childRef);
            if (child == null || !child.is_a_snapshot)
                continue;
            row.Children.Add(CreateSnapshotNode(conn, child, liveVm, snapshotRefs, ref nowAttached));
        }

        return row;
    }

    private static SnapshotItemRow CreateNowNode() =>
        new(
            "NOW",
            "Current VM state",
            string.Empty,
            opaqueRef: string.Empty,
            isNow: true,
            icon: ShellStatusIcons.VmRunning);
}
