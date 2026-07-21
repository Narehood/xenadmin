using XenAdmin.Core;
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
        if (xo == null || maintainer == null)
            return Array.Empty<PerformanceGraphRow>();

        var archive = PickArchive(maintainer, preferredInterval);
        if (archive == null || archive.SeriesById.Count == 0)
            return Array.Empty<PerformanceGraphRow>();

        // Prefer the interval the user selected for axis labels even if we fell back to another archive.
        var axisInterval = preferredInterval;

        var fromConfig = TryBuildFromGuiConfig(xo, archive, axisInterval);
        if (fromConfig.Count > 0)
            return fromConfig;

        return xo switch
        {
            Host host => BuildHostDefaults(host, archive, axisInterval),
            VM vm => BuildVmDefaults(vm, archive, axisInterval),
            _ => Array.Empty<PerformanceGraphRow>()
        };
    }

    public static IReadOnlyList<(string Title, IReadOnlyList<string> Leaves)> DescribeLayout(
        IReadOnlyList<PerformanceGraphRow> rows)
    {
        return rows.Select(r => (
            r.Title,
            (IReadOnlyList<string>)r.Series
                .Select(s =>
                {
                    var parts = s.Id.Split(':');
                    return parts.Length >= 3 ? parts[^1] : s.Id;
                })
                .ToList()
        )).ToList();
    }

    private static RrdArchive? PickArchive(ShellRrdMaintainer maintainer, RrdArchiveInterval preferred)
    {
        if (maintainer.Archives.TryGetValue(preferred, out var preferredArchive)
            && preferredArchive.SeriesById.Count > 0)
            return preferredArchive;

        foreach (var key in new[]
                 {
                     RrdArchiveInterval.FiveSecond,
                     RrdArchiveInterval.OneMinute,
                     RrdArchiveInterval.OneHour,
                     RrdArchiveInterval.OneDay
                 })
        {
            if (maintainer.Archives.TryGetValue(key, out var a) && a.SeriesById.Count > 0)
                return a;
        }

        return null;
    }

    private static IReadOnlyList<PerformanceGraphRow> TryBuildFromGuiConfig(
        IXenObject xo,
        RrdArchive archive,
        RrdArchiveInterval interval)
    {
        var gui = ShellGraphLayoutKeys.GetGuiConfig(xo);
        var rows = new List<PerformanceGraphRow>();
        var prefix = xo is Host ? "host" : "vm";
        var uuid = ShellGraphLayoutKeys.ObjectUuid(xo);

        for (var i = 0; ; i++)
        {
            var layoutKey = ShellGraphLayoutKeys.GetLayoutKey(i, xo);
            if (!gui.TryGetValue(layoutKey, out var csv) || string.IsNullOrWhiteSpace(csv))
                break;

            var ids = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(leaf => $"{prefix}:{uuid}:{leaf}")
                .ToList();
            var title = gui.TryGetValue(ShellGraphLayoutKeys.GetGraphNameKey(i, xo), out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : $"Graph {i + 1}";
            var series = MatchSeries(archive, ids);
            if (series.Count > 0)
                rows.Add(MakeGraph(title, series, interval));
        }

        return rows;
    }

    private static IReadOnlyList<PerformanceGraphRow> BuildHostDefaults(
        Host host,
        RrdArchive archive,
        RrdArchiveInterval interval)
    {
        var rows = new List<PerformanceGraphRow>();

        var cpuIds = host.Connection.ResolveAll(host.host_CPUs)
            .Select(cpu => $"host:{host.uuid}:cpu{cpu.number}")
            .ToList();
        rows.Add(MakeGraph("CPU", MatchSeries(archive, cpuIds), interval));

        rows.Add(MakeGraph("Memory", MatchSeries(archive, [$"host:{host.uuid}:memory_free_kib"]), interval));

        var netIds = new List<string>();
        foreach (var pif in host.Connection.ResolveAll(host.PIFs))
        {
            netIds.Add($"host:{host.uuid}:pif_{pif.device}_tx");
            netIds.Add($"host:{host.uuid}:pif_{pif.device}_rx");
        }
        rows.Add(MakeGraph("Network", MatchSeries(archive, netIds), interval));

        return rows.Where(r => r.Series.Count > 0).ToList();
    }

    private static IReadOnlyList<PerformanceGraphRow> BuildVmDefaults(
        VM vm,
        RrdArchive archive,
        RrdArchiveInterval interval)
    {
        var rows = new List<PerformanceGraphRow>();

        var cpuIds = Enumerable.Range(0, (int)Math.Max(1, vm.VCPUs_at_startup))
            .Select(i => $"vm:{vm.uuid}:cpu{i}")
            .ToList();
        rows.Add(MakeGraph("CPU", MatchSeries(archive, cpuIds), interval));
        rows.Add(MakeGraph("Memory", MatchSeries(archive, [$"vm:{vm.uuid}:memory_internal_free"]), interval));

        var netIds = new List<string>();
        foreach (var vif in vm.Connection.ResolveAll(vm.VIFs))
        {
            netIds.Add($"vm:{vm.uuid}:vif_{vif.device}_tx");
            netIds.Add($"vm:{vm.uuid}:vif_{vif.device}_rx");
        }
        rows.Add(MakeGraph("Network", MatchSeries(archive, netIds), interval));

        var diskIds = new List<string>();
        foreach (var vbd in vm.Connection.ResolveAll(vm.VBDs))
        {
            diskIds.Add($"vm:{vm.uuid}:vbd_{vbd.device}_read");
            diskIds.Add($"vm:{vm.uuid}:vbd_{vbd.device}_write");
        }
        rows.Add(MakeGraph("Disk", MatchSeries(archive, diskIds), interval));

        return rows.Where(r => r.Series.Count > 0).ToList();
    }

    private static List<PerformanceSeriesView> MatchSeries(RrdArchive archive, IEnumerable<string> ids)
    {
        var list = new List<PerformanceSeriesView>();
        var i = 0;
        foreach (var id in ids)
        {
            if (!archive.SeriesById.TryGetValue(id, out var series) || series.Points.Count == 0)
                continue;

            list.Add(new PerformanceSeriesView(
                series.Id,
                series.FriendlyName,
                Palette[i % Palette.Length],
                series.Points.ToList()));
            i++;
        }

        return list;
    }

    private static PerformanceGraphRow MakeGraph(string title, List<PerformanceSeriesView> series, RrdArchiveInterval interval)
        => new(title, series, interval);
}

public sealed record PerformanceRangeOption(RrdArchiveInterval Interval, string Label, string Hint)
{
    public override string ToString() => Label;
}
