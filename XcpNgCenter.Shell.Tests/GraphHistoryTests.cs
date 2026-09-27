using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Controls;
using XcpNgCenter.Shell.Services.Performance;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class GraphHistoryTests
{
    private const string SeriesId = "vm:history-guest:cpu0";
    private static readonly DateTime LatestUtc = new(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(RrdArchiveInterval.OneHour, 3600, 168)]
    [InlineData(RrdArchiveInterval.OneDay, 86400, 366)]
    public void SelectedLongRangeUsesItsOwnSamplesAndTimestamps(
        RrdArchiveInterval interval, int seconds, int count)
    {
        var vm = TargetWithSavedCpuGraph();
        using var maintainer = new ShellRrdMaintainer(vm, action => action());
        maintainer.Archives[RrdArchiveInterval.FiveSecond].Merge([Samples(LatestUtc, 5, 124, 91)]);
        maintainer.Archives[RrdArchiveInterval.OneMinute].Merge([Samples(LatestUtc, 60, 120, 82)]);
        maintainer.Archives[interval].Merge([Samples(LatestUtc, seconds, count, 27)]);

        var graph = Assert.Single(PerformanceGraphBuilder.Build(vm, maintainer, interval));
        var series = Assert.Single(graph.Series);

        Assert.Equal(interval, graph.Interval);
        Assert.True(graph.HasData);
        Assert.Equal(count, series.Points.Count);
        Assert.Equal(Samples(LatestUtc, seconds, count, 27).Points, series.Points);
        Assert.Equal(LatestUtc.AddSeconds(-(count - 1L) * seconds).Ticks,
            series.Points[^1].Ticks);
    }

    [Theory]
    [InlineData(RrdArchiveInterval.OneHour)]
    [InlineData(RrdArchiveInterval.OneDay)]
    public void EmptyLongRangeDoesNotDisplayShortHistoryUnderALongRangeLabel(RrdArchiveInterval interval)
    {
        var vm = TargetWithSavedCpuGraph();
        using var maintainer = new ShellRrdMaintainer(vm, action => action());
        maintainer.Archives[RrdArchiveInterval.FiveSecond].Merge([Samples(LatestUtc, 5, 124, 91)]);
        maintainer.Archives[RrdArchiveInterval.OneMinute].Merge([Samples(LatestUtc, 60, 120, 82)]);

        var graphs = PerformanceGraphBuilder.Build(vm, maintainer, interval);

        Assert.All(graphs, graph =>
        {
            Assert.Equal(interval, graph.Interval);
            Assert.False(graph.HasData);
            Assert.All(graph.Series, series => Assert.Empty(series.Points));
        });
        Assert.Equal(124, maintainer.Archives[RrdArchiveInterval.FiveSecond].SeriesById[SeriesId].Points.Count);
    }

    [Theory]
    [InlineData(RrdArchiveInterval.OneHour, 3600, 168)]
    [InlineData(RrdArchiveInterval.OneDay, 86400, 366)]
    public void IncrementalLongRangePollingPreservesHistoryAndPreviouslyRenderedSnapshot(
        RrdArchiveInterval interval, int seconds, int count)
    {
        var vm = TargetWithSavedCpuGraph();
        var pending = new Queue<Action>();
        using var maintainer = new ShellRrdMaintainer(vm, pending.Enqueue);
        maintainer.PollArchive(interval, LatestUtc, (_, requestedSeconds) =>
        {
            Assert.Equal(seconds, requestedSeconds);
            return [Samples(LatestUtc, seconds, count, 27)];
        });
        Drain(pending);
        var priorGraph = Assert.Single(PerformanceGraphBuilder.Build(vm, maintainer, interval));
        var priorPoints = Assert.Single(priorGraph.Series).Points.ToArray();

        // Fast polling must not consume or replace the separately retained long archive.
        for (var i = 1; i <= 12; i++)
            maintainer.PollArchive(RrdArchiveInterval.FiveSecond, LatestUtc.AddSeconds(i * 5),
                (_, _) => [Samples(LatestUtc.AddSeconds(i * 5), 5, 2, 91)]);
        Drain(pending);
        Assert.Equal(priorPoints,
            Assert.Single(Assert.Single(PerformanceGraphBuilder.Build(vm, maintainer, interval)).Series).Points);

        var next = LatestUtc.AddSeconds(seconds);
        maintainer.PollArchive(interval, next, (start, requestedSeconds) =>
        {
            Assert.Equal(new DateTimeOffset(LatestUtc).ToUnixTimeSeconds(), start);
            Assert.Equal(seconds, requestedSeconds);
            return [Samples(next, seconds, 2, 36)];
        });
        // A response waiting for the UI dispatcher must not mutate a rendered graph.
        Assert.Equal(priorPoints, Assert.Single(priorGraph.Series).Points);
        Drain(pending);

        var current = Assert.Single(Assert.Single(PerformanceGraphBuilder.Build(vm, maintainer, interval)).Series);
        Assert.Equal(count, current.Points.Count);
        Assert.Equal(new RrdPoint(next.Ticks, 36), current.Points[0]);
        Assert.Equal(priorPoints.Take(count - 1), current.Points.Skip(1));
        Assert.Equal(priorPoints, Assert.Single(priorGraph.Series).Points);
    }

    [Theory]
    [InlineData(RrdArchiveInterval.OneHour, 3600, 168, 3, 10, false)]
    [InlineData(RrdArchiveInterval.OneHour, 3600, 168, 3, 10, true)]
    [InlineData(RrdArchiveInterval.OneHour, 3600, 168, 11, 3, false)]
    [InlineData(RrdArchiveInterval.OneHour, 3600, 168, 11, 3, true)]
    [InlineData(RrdArchiveInterval.OneDay, 86400, 366, 9, 25, false)]
    [InlineData(RrdArchiveInterval.OneDay, 86400, 366, 9, 25, true)]
    public void FullArchiveKeepsUtcSampleIdentityAndChartDisplaysEachLocalTimestampAcrossDaylightSavingTransitions(
        RrdArchiveInterval interval, int seconds, int count, int month, int day, bool daylightSaving)
    {
        var zone = daylightSaving ? EasternTimeZone() : TimeZoneInfo.Utc;
        var vm = TargetWithSavedCpuGraph();
        using var maintainer = new ShellRrdMaintainer(vm, action => action());
        var lastUpdate = new DateTime(2026, month, day, 12, 0, 17, DateTimeKind.Utc);
        var latest = Align(lastUpdate, seconds);

        ReadFullDump(maintainer, FullDump(lastUpdate, (seconds, count, 27)));

        var graph = Assert.Single(PerformanceGraphBuilder.Build(vm, maintainer, interval));
        var points = Assert.Single(graph.Series).Points;
        var expectedTicks = Enumerable.Range(0, count)
            .Select(i => latest.AddSeconds(-(long)i * seconds).Ticks).ToArray();
        Assert.Equal(count, points.Count);
        Assert.Equal(expectedTicks, points.Select(point => point.Ticks));
        Assert.Equal(expectedTicks.Select(ticks => DisplayTicks(new DateTime(ticks, DateTimeKind.Utc), zone)),
            points.Select(point => PerformanceChart.LocalDisplayTime(point.Ticks, zone).Ticks));
        Assert.All(points, point => Assert.Equal(27, point.Value));
        Assert.Equal(interval, graph.Interval);

        maintainer.PollArchive(interval, latest.AddSeconds(seconds), (start, requestedSeconds) =>
        {
            Assert.Equal(new DateTimeOffset(latest).ToUnixTimeSeconds(), start);
            Assert.Equal(seconds, requestedSeconds);
            return null;
        });
    }

    [Fact]
    public void FullDumpKeepsIndependentWeekAndYearArchivesAndIncludesLeapDay()
    {
        var vm = TargetWithSavedCpuGraph();
        using var maintainer = new ShellRrdMaintainer(vm, action => action());
        var lastUpdate = new DateTime(2024, 7, 1, 12, 34, 57, DateTimeKind.Utc);
        ReadFullDump(maintainer, FullDump(lastUpdate,
            (5, 120, 91), (60, 120, 82), (3600, 168, 27), (86400, 366, 18)));

        var week = Assert.Single(Assert.Single(PerformanceGraphBuilder.Build(
            vm, maintainer, RrdArchiveInterval.OneHour)).Series);
        var year = Assert.Single(Assert.Single(PerformanceGraphBuilder.Build(
            vm, maintainer, RrdArchiveInterval.OneDay)).Series);

        Assert.Equal(168, week.Points.Count);
        Assert.Equal(366, year.Points.Count);
        Assert.All(week.Points, point => Assert.Equal(27, point.Value));
        Assert.All(year.Points, point => Assert.Equal(18, point.Value));
        Assert.Equal(Align(lastUpdate, 3600).Ticks, week.Points[0].Ticks);
        Assert.Equal(Align(lastUpdate, 86400).Ticks, year.Points[0].Ticks);
        Assert.Contains(year.Points, point => point.Ticks == new DateTime(2024, 2, 29).Ticks);
        Assert.Equal(120, maintainer.Archives[RrdArchiveInterval.FiveSecond].SeriesById[SeriesId].Points.Count);
    }

    [Fact]
    public void FallBackHourRetainsBothSamplesAndPollingCursorDoesNotSkipTheRepeatedHour()
    {
        var zone = EasternTimeZone();
        var vm = TargetWithSavedCpuGraph();
        using var maintainer = new ShellRrdMaintainer(vm, action => action());
        var firstOneOClock = new DateTime(2026, 11, 1, 5, 0, 0, DateTimeKind.Utc);
        var repeatedOneOClock = firstOneOClock.AddHours(1);
        Assert.Equal(PerformanceChart.LocalDisplayTime(firstOneOClock.Ticks, zone),
            PerformanceChart.LocalDisplayTime(repeatedOneOClock.Ticks, zone));
        ReadFullDump(maintainer, FullDump(firstOneOClock, (3600, 168, 27)));

        maintainer.PollArchive(RrdArchiveInterval.OneHour, repeatedOneOClock, (start, seconds) =>
        {
            Assert.Equal(new DateTimeOffset(firstOneOClock).ToUnixTimeSeconds(), start);
            Assert.Equal(3600, seconds);
            return ReadUpdate(maintainer, repeatedOneOClock, 38);
        });

        var points = Assert.Single(Assert.Single(PerformanceGraphBuilder.Build(
            vm, maintainer, RrdArchiveInterval.OneHour)).Series).Points;
        Assert.Equal(168, points.Count);
        Assert.Equal(new RrdPoint(repeatedOneOClock.Ticks, 38), points[0]);
        Assert.Equal(new RrdPoint(firstOneOClock.Ticks, 27), points[1]);
        maintainer.PollArchive(RrdArchiveInterval.OneHour, repeatedOneOClock.AddHours(1), (start, seconds) =>
        {
            Assert.Equal(new DateTimeOffset(repeatedOneOClock).ToUnixTimeSeconds(), start);
            Assert.Equal(3600, seconds);
            return null;
        });
    }

    [Fact]
    public void SpringForwardUpdatesStayOneHourApartWhileDisplaySkipsTheNonexistentHour()
    {
        var vm = TargetWithSavedCpuGraph();
        using var maintainer = new ShellRrdMaintainer(vm, action => action());
        var before = new DateTime(2026, 3, 8, 6, 0, 0, DateTimeKind.Utc);
        var after = before.AddHours(1);
        maintainer.PollArchive(RrdArchiveInterval.OneHour, before, (_, _) => ReadUpdate(maintainer, before, 27));
        maintainer.PollArchive(RrdArchiveInterval.OneHour, after, (start, _) =>
        {
            Assert.Equal(new DateTimeOffset(before).ToUnixTimeSeconds(), start);
            return ReadUpdate(maintainer, after, 38);
        });

        var points = maintainer.Archives[RrdArchiveInterval.OneHour].SeriesById[SeriesId].Points;
        Assert.Equal(2, points.Count);
        Assert.Equal(TimeSpan.TicksPerHour, points[0].Ticks - points[1].Ticks);
        Assert.Equal(1, PerformanceChart.LocalDisplayTime(points[1].Ticks, EasternTimeZone()).Hour);
        Assert.Equal(3, PerformanceChart.LocalDisplayTime(points[0].Ticks, EasternTimeZone()).Hour);
    }

    [Fact]
    public void ChartUsesCurrentLocalTimeZoneForDisplayByDefault()
    {
        Assert.Equal(LatestUtc.ToLocalTime().Ticks, PerformanceChart.LocalDisplayTime(LatestUtc.Ticks).Ticks);
    }

    private static VM TargetWithSavedCpuGraph()
    {
        var vm = new VM { uuid = "history-guest", Connection = new XenConnection(), VCPUs_at_startup = 1 };
        vm.Connection.Cache.UpdateFrom(vm.Connection, [new ObjectChange(typeof(Pool), "history-pool", new Pool
        {
            gui_config = new Dictionary<string, string>
            {
                [ShellGraphLayoutKeys.GetLayoutKey(0, vm)] = "cpu0",
                [ShellGraphLayoutKeys.GetGraphNameKey(0, vm)] = "CPU history"
            }
        }), new ObjectChange(typeof(VM), "history-vm", vm)]);
        return vm.Connection.Cache.VMs.Single();
    }

    private static RrdSeries Samples(DateTime latest, int seconds, int count, double value)
    {
        var series = new RrdSeries(SeriesId, "cpu0", "CPU 0", "percent");
        series.Points.AddRange(Enumerable.Range(0, count).Select(i =>
            new RrdPoint(latest.AddSeconds(-(long)i * seconds).Ticks, value)));
        return series;
    }

    private static string FullDump(DateTime lastUpdate, params (int Seconds, int Count, int Value)[] archives)
    {
        var xml = new StringBuilder("<rrd><step>5</step><lastupdate>")
            .Append(new DateTimeOffset(lastUpdate).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
            .Append("</lastupdate><ds><name>cpu0</name></ds>");
        foreach (var archive in archives)
        {
            xml.Append("<rra><cf>AVERAGE</cf><pdp_per_row>")
                .Append(archive.Seconds / 5).Append("</pdp_per_row><database>");
            for (var i = 0; i < archive.Count; i++)
                xml.Append("<row><v>").Append(archive.Value).Append("</v></row>");
            xml.Append("</database></rra>");
        }
        return xml.Append("</rrd>").ToString();
    }

    private static void ReadFullDump(ShellRrdMaintainer maintainer, string xml)
        => InspectXml(maintainer, xml, "RrdFullInspect");

    private static List<RrdSeries> ReadUpdate(ShellRrdMaintainer maintainer, DateTime time, int value)
        => InspectXml(maintainer,
            $"<xport><meta><legend><entry>AVERAGE:{SeriesId}</entry></legend></meta><data><row>"
            + $"<t>{new DateTimeOffset(time).ToUnixTimeSeconds()}</t><v>{value}</v></row></data></xport>",
            "RrdUpdateInspect");

    private static List<RrdSeries> InspectXml(ShellRrdMaintainer maintainer, string xml, string method)
    {
        // Exercise the production streaming inspector without connecting to a server.
        var type = typeof(ShellRrdMaintainer);
        var series = new List<RrdSeries>();
        type.GetField("_setsAdded", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(maintainer, series);
        var inspect = type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input);
        while (reader.Read())
            inspect.Invoke(maintainer, [reader, maintainer.XenObject]);
        return series;
    }

    private static DateTime Align(DateTime time, int seconds)
    {
        var unix = new DateTimeOffset(time).ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(unix - unix % seconds).UtcDateTime;
    }

    private static long DisplayTicks(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc, zone).Ticks;

    private static TimeZoneInfo EasternTimeZone() => TimeZoneInfo.CreateCustomTimeZone(
        "GraphHistoryEastern", TimeSpan.FromHours(-5), "Graph history Eastern", "EST", "EDT",
        [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday))]);

    private static void Drain(Queue<Action> pending)
    {
        while (pending.TryDequeue(out var action))
            action();
    }
}
