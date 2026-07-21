using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services.Performance;

/// <summary>
/// Builds default Host/VM graph groups from RRD archives (CPU / memory / network / disk).
/// </summary>
public static class PerformanceGraphBuilder
{
    private static readonly string[] Palette =
    [
        "#F07318", "#3DBE7A", "#5B9BD5", "#E35D5D", "#C9A227", "#9B7EDE", "#4ECDC4", "#FF8FAB"
    ];

    public static IReadOnlyList<PerformanceGraphRow> Build(IXenObject? xo, ShellRrdMaintainer? maintainer)
    {
        if (xo == null || maintainer == null)
            return Array.Empty<PerformanceGraphRow>();

        var archive = PickArchive(maintainer);
        if (archive == null || archive.SeriesById.Count == 0)
            return Array.Empty<PerformanceGraphRow>();

        return xo switch
        {
            Host host => BuildHost(host, archive),
            VM vm => BuildVm(vm, archive),
            _ => Array.Empty<PerformanceGraphRow>()
        };
    }

    private static RrdArchive? PickArchive(ShellRrdMaintainer maintainer)
    {
        // Prefer densest recent archive with data.
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

    private static IReadOnlyList<PerformanceGraphRow> BuildHost(Host host, RrdArchive archive)
    {
        var rows = new List<PerformanceGraphRow>();

        var cpuIds = host.Connection.ResolveAll(host.host_CPUs)
            .Select(cpu => $"host:{host.uuid}:cpu{cpu.number}")
            .ToList();
        rows.Add(MakeGraph("CPU", MatchSeries(archive, cpuIds)));

        var memIds = new[]
        {
            $"host:{host.uuid}:memory_free_kib"
        };
        rows.Add(MakeGraph("Memory", MatchSeries(archive, memIds)));

        var netIds = new List<string>();
        foreach (var pif in host.Connection.ResolveAll(host.PIFs))
        {
            netIds.Add($"host:{host.uuid}:pif_{pif.device}_tx");
            netIds.Add($"host:{host.uuid}:pif_{pif.device}_rx");
        }
        rows.Add(MakeGraph("Network", MatchSeries(archive, netIds)));

        return rows.Where(r => r.Series.Count > 0).ToList();
    }

    private static IReadOnlyList<PerformanceGraphRow> BuildVm(VM vm, RrdArchive archive)
    {
        var rows = new List<PerformanceGraphRow>();

        var cpuIds = Enumerable.Range(0, (int)Math.Max(1, vm.VCPUs_at_startup))
            .Select(i => $"vm:{vm.uuid}:cpu{i}")
            .ToList();
        rows.Add(MakeGraph("CPU", MatchSeries(archive, cpuIds)));

        rows.Add(MakeGraph("Memory", MatchSeries(archive, [$"vm:{vm.uuid}:memory_internal_free"])));

        var netIds = new List<string>();
        foreach (var vif in vm.Connection.ResolveAll(vm.VIFs))
        {
            netIds.Add($"vm:{vm.uuid}:vif_{vif.device}_tx");
            netIds.Add($"vm:{vm.uuid}:vif_{vif.device}_rx");
        }
        rows.Add(MakeGraph("Network", MatchSeries(archive, netIds)));

        var diskIds = new List<string>();
        foreach (var vbd in vm.Connection.ResolveAll(vm.VBDs))
        {
            diskIds.Add($"vm:{vm.uuid}:vbd_{vbd.device}_read");
            diskIds.Add($"vm:{vm.uuid}:vbd_{vbd.device}_write");
        }
        rows.Add(MakeGraph("Disk", MatchSeries(archive, diskIds)));

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

    private static PerformanceGraphRow MakeGraph(string title, List<PerformanceSeriesView> series)
        => new(title, series);
}
