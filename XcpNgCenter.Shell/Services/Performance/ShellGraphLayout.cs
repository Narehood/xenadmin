using System.Collections.ObjectModel;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;

namespace XcpNgCenter.Shell.Services.Performance;

/// <summary>A graph's persistent selection, independent of currently available samples.</summary>
public sealed record GraphLayoutDefinition
{
    public GraphLayoutDefinition(string title, IReadOnlyList<string> dataSourceLeaves)
    {
        Title = title;
        DataSourceLeaves = Array.AsReadOnly(dataSourceLeaves.ToArray());
    }

    public string Title { get; }
    public IReadOnlyList<string> DataSourceLeaves { get; }
}

public sealed record GraphDataSourceOption(string Leaf, string Label, string Units, bool IsAvailable);

/// <summary>The target identity and configuration that the operator reviewed before saving.</summary>
public sealed class GraphLayoutSnapshot
{
    internal GraphLayoutSnapshot(IXenObject target, Pool pool, Dictionary<string, string> entries)
    {
        Kind = ShellGraphLayoutKeys.ObjectKind(target);
        TargetReference = target.opaque_ref;
        TargetUuid = ShellGraphLayoutKeys.ObjectUuid(target);
        PoolReference = pool.opaque_ref;
        PoolUuid = pool.uuid;
        Entries = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(entries, StringComparer.Ordinal));
    }

    public string Kind { get; }
    public string TargetReference { get; }
    public string TargetUuid { get; }
    public string PoolReference { get; }
    public string PoolUuid { get; }
    public IReadOnlyDictionary<string, string> Entries { get; }
}

/// <summary>Reads and edits WinForms-compatible layouts without discarding unavailable sources.</summary>
public static class ShellGraphLayout
{
    public static IReadOnlyList<GraphLayoutDefinition> Read(IXenObject target, ShellRrdMaintainer? maintainer)
    {
        var gui = ShellGraphLayoutKeys.GetGuiConfig(target);
        var kind = ShellGraphLayoutKeys.ObjectKind(target);
        var uuid = ShellGraphLayoutKeys.ObjectUuid(target);
        var saved = gui.Keys.Select(key =>
                ShellGraphLayoutKeys.TryParse(key, out var type, out var index, out var keyKind, out var keyUuid)
                && type == "GraphLayout" && keyKind == kind && keyUuid == uuid ? (Key: key, Index: index) : (Key: "", Index: -1))
            .Where(entry => entry.Index >= 0).OrderBy(entry => entry.Index).ToArray();
        if (saved.Length > 0)
            return saved.Select(entry => new GraphLayoutDefinition(
                gui.TryGetValue(ShellGraphLayoutKeys.GetGraphNameKey(entry.Index, target), out var title) && !string.IsNullOrWhiteSpace(title)
                    ? title : $"Graph {entry.Index + 1L}",
                gui[entry.Key].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                .ToArray();

        var sources = RecordedSources(target, maintainer);
        var defaults = new List<GraphLayoutDefinition>();
        void Add(string title, IEnumerable<string> leaves)
        {
            var available = leaves.Where(leaf => IsAvailable(leaf, sources, kind)).Distinct(StringComparer.Ordinal).ToArray();
            if (available.Length > 0) defaults.Add(new(title, available));
        }
        if (target is Host host)
        {
            Add("CPU", host.Connection.ResolveAll(host.host_CPUs).Select(cpu => $"cpu{cpu.number}"));
            Add("Memory", ["memory_free_kib"]);
            Add("Network", host.Connection.ResolveAll(host.PIFs).SelectMany(pif => new[] { $"pif_{pif.device}_tx", $"pif_{pif.device}_rx" }));
        }
        else if (target is VM vm)
        {
            Add("CPU", sources.Keys.Where(leaf => leaf.StartsWith("cpu", StringComparison.Ordinal)
                && int.TryParse(leaf.AsSpan(3), out var cpu) && cpu >= 0 && cpu < Math.Max(1, vm.VCPUs_at_startup))
                .OrderBy(leaf => int.Parse(leaf.AsSpan(3))));
            Add("Memory", ["memory_internal_free"]);
            Add("Network", vm.Connection.ResolveAll(vm.VIFs).SelectMany(vif => new[] { $"vif_{vif.device}_tx", $"vif_{vif.device}_rx" }));
            Add("Disk", vm.Connection.ResolveAll(vm.VBDs).SelectMany(vbd => new[] { $"vbd_{vbd.device}_read", $"vbd_{vbd.device}_write" }));
        }
        return defaults;
    }

    /// <summary>Includes recorded sources from every range, plus missing selections retained by the layout.</summary>
    public static IReadOnlyList<GraphDataSourceOption> Catalog(IXenObject target, ShellRrdMaintainer? maintainer,
        IReadOnlyList<GraphLayoutDefinition> layout)
    {
        var kind = ShellGraphLayoutKeys.ObjectKind(target);
        var sources = RecordedSources(target, maintainer);
        var selected = layout.SelectMany(graph => graph.DataSourceLeaves).ToHashSet(StringComparer.Ordinal);
        // Memory renders as the existing Used/Free/Total group. Offer one new
        // choice while retaining aliases already present in compatible layouts.
        var leaves = sources.Keys.Where(leaf => !IsMemoryLeaf(leaf, kind)).Concat(selected).ToHashSet(StringComparer.Ordinal);
        var memoryLeaf = kind == "host" ? "memory_free_kib" : "memory_internal_free";
        if (sources.Keys.Any(leaf => IsMemoryLeaf(leaf, kind))) leaves.Add(memoryLeaf);
        return leaves.Select(leaf =>
        {
            sources.TryGetValue(leaf, out var source);
            var memory = IsMemoryLeaf(leaf, kind);
            return new GraphDataSourceOption(leaf,
                memory ? "Memory (used/free/total)" : source?.FriendlyName ?? leaf,
                memory ? "bytes" : source?.Units ?? "",
                IsAvailable(leaf, sources, kind));
        }).OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase).ThenBy(option => option.Leaf, StringComparer.Ordinal).ToArray();
    }

    public static void Validate(IReadOnlyList<GraphLayoutDefinition> layout)
    {
        if (layout.Count == 0) throw new InvalidOperationException("Keep at least one graph in the layout.");
        foreach (var graph in layout)
        {
            if (graph.DataSourceLeaves.Count == 0) throw new InvalidOperationException("Select at least one data source for every graph.");
            if (graph.DataSourceLeaves.Any(leaf => string.IsNullOrWhiteSpace(leaf) || leaf != leaf.Trim() || leaf.IndexOfAny([',', ':']) >= 0))
                throw new InvalidOperationException("A data source name is invalid. Remove it and select an available source.");
            if (graph.DataSourceLeaves.Distinct(StringComparer.Ordinal).Count() != graph.DataSourceLeaves.Count)
                throw new InvalidOperationException("A graph cannot contain the same data source more than once.");
        }
    }

    public static GraphLayoutSnapshot Snapshot(IXenObject target)
    {
        var pool = Helpers.GetPoolOfOne(target.Connection)
            ?? throw new InvalidOperationException("No pool is available to save this layout. Reconnect and reopen the editor.");
        if (string.IsNullOrWhiteSpace(target.opaque_ref) || string.IsNullOrWhiteSpace(ShellGraphLayoutKeys.ObjectUuid(target))
            || string.IsNullOrWhiteSpace(pool.opaque_ref) || string.IsNullOrWhiteSpace(pool.uuid))
            throw new InvalidOperationException("The graph target or pool identity is incomplete. Reconnect and reopen the editor.");
        return new(target, pool, TargetEntries(ShellGraphLayoutKeys.GetGuiConfig(target), ShellGraphLayoutKeys.ObjectKind(target), ShellGraphLayoutKeys.ObjectUuid(target)));
    }

    internal static (IXenObject Target, Pool Pool) RequireCurrentIdentity(IXenConnection connection, GraphLayoutSnapshot snapshot)
    {
        if (!connection.IsConnected) throw new InvalidOperationException("The server disconnected. Reconnect and reopen the graph editor.");
        IXenObject? target = snapshot.Kind switch
        {
            "host" => connection.Resolve(new XenRef<Host>(snapshot.TargetReference)),
            "vm" => connection.Resolve(new XenRef<VM>(snapshot.TargetReference)),
            _ => null
        };
        var pool = Helpers.GetPoolOfOne(connection);
        if (target == null || ShellGraphLayoutKeys.ObjectUuid(target) != snapshot.TargetUuid
            || pool == null || pool.opaque_ref != snapshot.PoolReference || pool.uuid != snapshot.PoolUuid)
            throw new InvalidOperationException("The graph target or pool changed. Reopen the editor and review the current layout.");
        return (target, pool);
    }

    internal static Dictionary<string, string> TargetEntries(IReadOnlyDictionary<string, string> gui, string kind, string uuid)
        => gui.Where(pair => ShellGraphLayoutKeys.IsTargetKey(pair.Key, kind, uuid)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    internal static void RequireUnchanged(IReadOnlyDictionary<string, string> gui, GraphLayoutSnapshot snapshot)
    {
        var entries = TargetEntries(gui, snapshot.Kind, snapshot.TargetUuid);
        if (entries.Count != snapshot.Entries.Count || entries.Any(pair => !snapshot.Entries.TryGetValue(pair.Key, out var value) || pair.Value != value))
            throw new InvalidOperationException("This graph layout changed after the editor opened. Reopen it to review the latest layout before saving.");
    }

    internal static Dictionary<string, string> Write(IReadOnlyDictionary<string, string> gui, string kind, string uuid,
        IReadOnlyList<GraphLayoutDefinition> layout)
    {
        Validate(layout);
        var result = gui.Where(pair => !ShellGraphLayoutKeys.IsTargetKey(pair.Key, kind, uuid))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        for (var index = 0; index < layout.Count; index++)
        {
            result[ShellGraphLayoutKeys.GetKey("GraphLayout", index, kind, uuid)] = string.Join(',', layout[index].DataSourceLeaves);
            result[ShellGraphLayoutKeys.GetKey("GraphName", index, kind, uuid)] = string.IsNullOrWhiteSpace(layout[index].Title)
                ? $"Graph {index + 1}" : layout[index].Title;
        }
        return result;
    }

    private static Dictionary<string, RrdSeries> RecordedSources(IXenObject target, ShellRrdMaintainer? maintainer)
    {
        var result = new Dictionary<string, RrdSeries>(StringComparer.Ordinal);
        if (maintainer == null) return result;
        var prefix = $"{ShellGraphLayoutKeys.ObjectKind(target)}:{ShellGraphLayoutKeys.ObjectUuid(target)}:";
        foreach (var series in maintainer.Archives.Values.SelectMany(archive => archive.SeriesById.Values))
            if (!series.Hide && series.Id.StartsWith(prefix, StringComparison.Ordinal)
                && series.Id.AsSpan(prefix.Length).IndexOfAny(',', ':') < 0)
                result.TryAdd(series.Id[prefix.Length..], series);
        return result;
    }

    private static bool IsAvailable(string leaf, IReadOnlyDictionary<string, RrdSeries> sources, string kind)
        => IsMemoryLeaf(leaf, kind) ? sources.Keys.Any(candidate => IsMemoryLeaf(candidate, kind)) : sources.ContainsKey(leaf);

    internal static bool IsMemoryLeaf(string leaf, string kind) => kind == "host"
        ? leaf is "memory_total_kib" or "memory_free_kib" or "memory_used_kib"
        : kind == "vm" && leaf is "memory" or "memory_internal_free" or "memory_internal_used";

}
