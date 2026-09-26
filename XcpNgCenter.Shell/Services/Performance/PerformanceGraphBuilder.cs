using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services.Performance;

/// <summary>
/// Builds Host/VM graph groups from RRD archives, preferring saved <c>gui_config</c> layouts.
/// </summary>
public static class PerformanceGraphBuilder
{
    private static readonly string[] Palette =
    [
        "#F07318", "#3DBE7A", "#5B9BD5", "#E35D5D", "#C9A227", "#9B7EDE", "#4ECDC4", "#FF8FAB"
    ];

    public static IReadOnlyList<PerformanceRangeOption> RangeOptions { get; } =
    [
        new(RrdArchiveInterval.FiveSecond, "Last ~10 minutes", "5s samples"),
        new(RrdArchiveInterval.OneMinute, "Last ~2 hours", "1m samples"),
        new(RrdArchiveInterval.OneHour, "Last ~1 week", "1h samples"),
        new(RrdArchiveInterval.OneDay, "Last ~1 year", "1d samples")
    ];

    public static IReadOnlyList<PerformanceGraphRow> Build(
        IXenObject? xo,
        ShellRrdMaintainer? maintainer,
        RrdArchiveInterval preferredInterval)
    {
        if (xo == null)
            return Array.Empty<PerformanceGraphRow>();
        return BuildFromLayout(xo, maintainer, preferredInterval, ShellGraphLayout.Read(xo, maintainer));
    }

    /// <summary>Renders the specified selection using only the requested archive; absent data never rewrites the layout.</summary>
    public static IReadOnlyList<PerformanceGraphRow> BuildFromLayout(IXenObject xo, ShellRrdMaintainer? maintainer,
        RrdArchiveInterval interval, IReadOnlyList<GraphLayoutDefinition> layout)
    {
        var archive = maintainer != null && maintainer.Archives.TryGetValue(interval, out var selected)
            ? selected : new RrdArchive(0);
        var prefix = $"{ShellGraphLayoutKeys.ObjectKind(xo)}:{ShellGraphLayoutKeys.ObjectUuid(xo)}:";
        var catalog = ShellGraphLayout.Catalog(xo, maintainer, layout).ToDictionary(option => option.Leaf, StringComparer.Ordinal);
        return layout.Select(definition =>
        {
            var series = MatchSeries(archive, definition.DataSourceLeaves.Select(leaf => prefix + leaf));
            var unavailable = definition.DataSourceLeaves.Where(leaf => !catalog[leaf].IsAvailable).Distinct(StringComparer.Ordinal).ToArray();
            var kind = ShellGraphLayoutKeys.ObjectKind(xo);
            var missingSamples = definition.DataSourceLeaves.Where(leaf => catalog[leaf].IsAvailable
                && !series.Any(view => view.Points.Count > 0 && (view.Id == prefix + leaf
                    || ShellGraphLayout.IsMemoryLeaf(leaf, kind) && ShellGraphLayout.IsMemoryLeaf(view.Id[prefix.Length..], kind))))
                .Distinct(StringComparer.Ordinal).ToArray();
            var notices = new List<string>();
            if (unavailable.Length > 0) notices.Add($"Unavailable data sources: {string.Join(", ", unavailable)}. Saved selections are retained.");
            if (missingSamples.Length > 0) notices.Add($"No samples in the selected range: {string.Join(", ", missingSamples)}.");
            if (definition.DataSourceLeaves.Count == 0) notices.Add("This saved graph has no data sources. Edit the layout to select a source.");
            return MakeGraph(definition.Title, series, interval, definition, string.Join(" ", notices));
        }).ToArray();
    }

    public static IReadOnlyList<(string Title, IReadOnlyList<string> Leaves)> DescribeLayout(
        IReadOnlyList<PerformanceGraphRow> rows)
    {
        return rows.Select(r => (
            r.Title,
            (IReadOnlyList<string>)(r.Layout?.DataSourceLeaves ?? []).Concat(r.Series
                .Select(s =>
                {
                    var parts = s.LayoutId.Split(':');
                    return parts.Length >= 3 ? parts[^1] : s.LayoutId;
                }))
                .Distinct(StringComparer.Ordinal)
                .ToList()
        )).ToList();
    }

    private static List<PerformanceSeriesView> MatchSeries(RrdArchive archive, IEnumerable<string> ids)
    {
        var list = new List<PerformanceSeriesView>();
        var memoryObjects = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        foreach (var id in ids)
        {
            var identity = id.Split(':');
            // Layout entries are source leaves, not full or prefixed RRD IDs.
            // Retain malformed saved entries in the editor without interpreting
            // them as another object's memory group.
            if (identity.Length != 3) continue;
            var (kind, uuid, source) = (identity[0], identity[1], identity[2]);
            if (ShellGraphLayout.IsMemoryLeaf(source, kind))
            {
                var prefix = $"{kind}:{uuid}:";
                if (memoryObjects.Add(prefix))
                    list.AddRange(MemorySeries(archive, prefix, kind == "host"));
                continue;
            }

            if (!archive.SeriesById.TryGetValue(id, out var series) || series.Points.Count == 0)
                continue;

            list.Add(new PerformanceSeriesView(
                series.Id,
                series.FriendlyName,
                Palette[i % Palette.Length],
                series.Points.ToList(),
                series.Units));
            i++;
        }

        return list;
    }

    private static IEnumerable<PerformanceSeriesView> MemorySeries(RrdArchive archive, string prefix, bool host)
    {
        archive.SeriesById.TryGetValue(prefix + (host ? "memory_total_kib" : "memory"), out var total);
        archive.SeriesById.TryGetValue(prefix + (host ? "memory_free_kib" : "memory_internal_free"), out var free);

        if (free is { Points.Count: > 0 })
        {
            if (total is { Points.Count: > 0 })
            {
                var totals = total.Points.ToDictionary(p => p.Ticks, p => p.Value);
                var used = free.Points.Select(p => new RrdPoint(p.Ticks,
                    totals.TryGetValue(p.Ticks, out var capacity)
                    && double.IsFinite(capacity) && double.IsFinite(p.Value)
                    && capacity >= 0 && p.Value >= 0 && p.Value <= capacity
                        ? capacity - p.Value : -1)).ToList();
                // Persist the real free source for compatibility with existing
                // XenCenter layouts; the synthetic used series is display-only.
                yield return new PerformanceSeriesView(prefix + (host ? "memory_used_kib" : "memory_internal_used"),
                    "Used", Palette[0], used, "bytes", free.Id);
            }
            yield return new PerformanceSeriesView(free.Id, "Free", Palette[1], free.Points.ToList(), "bytes");
        }
        if (total is { Points.Count: > 0 })
            yield return new PerformanceSeriesView(total.Id, "Total", "#9AA6B2", total.Points.ToList(), "bytes");
    }

    private static PerformanceGraphRow MakeGraph(string title, List<PerformanceSeriesView> series, RrdArchiveInterval interval,
        GraphLayoutDefinition? layout = null, string availabilityNotice = "")
    {
        double? yAxisMax = null;
        if (series.Count > 0 && series.All(s => s.IsPercentUnit))
            yAxisMax = 100;
        else if (series.Count > 0 && series.All(s => s.Units == "bytes"))
        {
            var capacity = series.Where(s => s.Id.EndsWith(":memory_total_kib", StringComparison.Ordinal)
                                             || s.Id.EndsWith(":memory", StringComparison.Ordinal))
                .SelectMany(s => s.Points).Where(p => double.IsFinite(p.Value) && p.Value > 0)
                .Select(p => p.Value).DefaultIfEmpty(0).Max();
            if (capacity > 0) yAxisMax = capacity;
        }

        return new PerformanceGraphRow(title, series, interval, yAxisMax, layout, availabilityNotice);
    }

}

public sealed record PerformanceRangeOption(RrdArchiveInterval Interval, string Label, string Hint)
{
    public override string ToString() => Label;
}
