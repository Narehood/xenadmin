using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services.Performance;
using XcpNgCenter.Shell.ViewModels;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class MemoryPerformanceTests
{
    private const double GiB = 1024d * 1024 * 1024;
    private static readonly long Now = new DateTime(2026, 9, 11, 12, 0, 0).Ticks;

    [Theory]
    [InlineData(RrdArchiveInterval.FiveSecond, false)]
    [InlineData(RrdArchiveInterval.FiveSecond, true)]
    [InlineData(RrdArchiveInterval.OneMinute, false)]
    [InlineData(RrdArchiveInterval.OneHour, true)]
    [InlineData(RrdArchiveInterval.OneDay, false)]
    public void HostMemoryShowsUsedFreeAndPhysicalCapacityRegardlessOfColumnOrder(RrdArchiveInterval interval, bool freeFirst)
    {
        var host = Host();
        using var maintainer = new ShellRrdMaintainer(host, action => action());
        var total = maintainer.CreateSeries(host, "memory_total_kib", false);
        var free = maintainer.CreateSeries(host, "memory_free_kib", false);
        var columns = freeFirst ? new[] { free, total } : new[] { total, free };
        foreach (var tick in new[] { Now, Now - TimeSpan.TicksPerSecond * 5 })
            foreach (var column in columns)
                column.AddRawValue(column == total ? "33554432" : "14680064", tick);
        maintainer.Archives[interval].Merge(columns);

        var graph = Memory(host, maintainer, interval);

        Assert.Equal(32 * GiB, graph.YAxisMax);
        Assert.Collection(graph.Series,
            series => AssertSeries(series, "Used", 18 * GiB),
            series => AssertSeries(series, "Free", 14 * GiB),
            series => AssertSeries(series, "Total", 32 * GiB));
        Assert.Equal("Used: 18 GiB · Free: 14 GiB · Total: 32 GiB", graph.LatestSummary);
        Assert.Equal(32 * GiB, total.Points[0].Value);
        Assert.Equal(14 * GiB, free.Points[0].Value);
    }

    [Fact]
    public void ExistingSavedMemoryLayoutGetsCapacityAndSavesOnlyRealDataSources()
    {
        var host = Host();
        host.Connection.Cache.UpdateFrom(host.Connection, [new ObjectChange(typeof(Pool), "pool", new Pool
        {
            gui_config = new Dictionary<string, string>
            {
                [ShellGraphLayoutKeys.GetLayoutKey(0, host)] = "memory_free_kib",
                [ShellGraphLayoutKeys.GetGraphNameKey(0, host)] = "Host RAM"
            }
        })]);
        using var maintainer = new ShellRrdMaintainer(host, action => action());
        Merge(maintainer, "memory_total_kib", "33554432");
        Merge(maintainer, "memory_free_kib", "14680064");

        var rows = PerformanceGraphBuilder.Build(host, maintainer, RrdArchiveInterval.FiveSecond);
        var graph = Assert.Single(rows);
        Assert.Equal("Host RAM", graph.Title);
        Assert.Equal(32 * GiB, graph.YAxisMax);
        Assert.Equal(3, graph.Series.Count);
        var layout = Assert.Single(PerformanceGraphBuilder.DescribeLayout(rows));
        Assert.Equal(new[] { "memory_free_kib", "memory_total_kib" }, layout.Leaves);

        host.Connection.Cache.Pools[0].gui_config[ShellGraphLayoutKeys.GetLayoutKey(0, host)] = string.Join(',', layout.Leaves);
        Assert.Equal(3, Assert.Single(PerformanceGraphBuilder.Build(host, maintainer, RrdArchiveInterval.FiveSecond)).Series.Count);
    }

    [Fact]
    public void GuestMemoryCombinesAllocatedBytesWithFreeKibibytes()
    {
        var vm = new VM { uuid = "guest", Connection = new XenConnection() };
        using var maintainer = new ShellRrdMaintainer(vm, action => action());
        Merge(maintainer, "memory", "4294967296");
        Merge(maintainer, "memory_internal_free", "1048576");

        var graph = Memory(vm, maintainer);

        Assert.Equal(4 * GiB, graph.YAxisMax);
        Assert.Equal("Used: 3 GiB · Free: 1 GiB · Total: 4 GiB", graph.LatestSummary);
    }

    [Fact]
    public void GuestWithoutAgentReportsAllocationWithoutInventingUsage()
    {
        var vm = new VM { uuid = "guest", Connection = new XenConnection() };
        using var maintainer = new ShellRrdMaintainer(vm, action => action());
        Merge(maintainer, "memory", "4294967296");

        var graph = Memory(vm, maintainer);

        Assert.Equal("Total", Assert.Single(graph.Series).Name);
        Assert.Equal(4 * GiB, graph.YAxisMax);
    }

    [Theory]
    [InlineData("NaN", "100")]
    [InlineData("100", "NaN")]
    [InlineData("100", "Infinity")]
    [InlineData("100", "-1")]
    [InlineData("100", "101")]
    public void InvalidMemoryPairDoesNotProduceUsedMemory(string total, string free)
    {
        var host = Host();
        using var maintainer = new ShellRrdMaintainer(host, action => action());
        Merge(maintainer, "memory_total_kib", total);
        Merge(maintainer, "memory_free_kib", free);

        var graph = Memory(host, maintainer);

        Assert.Equal(-1, graph.Series[0].Points[0].Value);
        Assert.StartsWith("Used: —", graph.LatestSummary);
    }

    [Fact]
    public void MemoryPairMustBelongToTheSameObjectAndTimestamp()
    {
        var host = Host();
        using var maintainer = new ShellRrdMaintainer(host, action => action());
        var free = maintainer.CreateSeries(host, "memory_free_kib", false);
        var foreign = maintainer.CreateSeries(new Host { uuid = "other" }, "memory_total_kib", true);
        var total = maintainer.CreateSeries(host, "memory_total_kib", false);
        free.AddRawValue("14680064", Now);
        foreign.AddRawValue("67108864", Now);
        total.AddRawValue("33554432", Now - TimeSpan.TicksPerSecond * 5);
        maintainer.Archives[RrdArchiveInterval.FiveSecond].Merge([free, foreign, total]);

        var graph = Memory(host, maintainer);

        Assert.Equal(-1, graph.Series[0].Points[0].Value);
        Assert.Equal(14 * GiB, graph.Series[1].Points[0].Value);
        Assert.Equal(32 * GiB, graph.YAxisMax);
        Assert.DoesNotContain(maintainer.Archives[RrdArchiveInterval.FiveSecond].SeriesById.Keys, id => id.Contains("other"));
    }

    [Fact]
    public void MissingTotalReportsFreeMemoryHonestly()
    {
        var host = Host();
        using var maintainer = new ShellRrdMaintainer(host, action => action());
        Merge(maintainer, "memory_free_kib", "14680064");

        var graph = Memory(host, maintainer);

        Assert.Equal("Free", Assert.Single(graph.Series).Name);
        Assert.Null(graph.YAxisMax);
        Assert.Equal("Free: 14 GiB", graph.LatestSummary);
    }

    [Fact]
    public void ChangingGuestAllocationKeepsHistoricalCapacityInRange()
    {
        var vm = new VM { uuid = "guest", Connection = new XenConnection() };
        using var maintainer = new ShellRrdMaintainer(vm, action => action());
        Merge(maintainer, "memory", "8589934592", Now - TimeSpan.TicksPerSecond * 5);
        Merge(maintainer, "memory", "4294967296");
        Merge(maintainer, "memory_internal_free", "1048576");

        var graph = Memory(vm, maintainer);

        Assert.Equal(8 * GiB, graph.YAxisMax);
        Assert.Equal("Used: 3 GiB · Free: 1 GiB · Total: 4 GiB", graph.LatestSummary);
    }

    [Fact]
    public void CpuGraphStillUsesPercentScale()
    {
        var vm = new VM { uuid = "guest", Connection = new XenConnection(), VCPUs_at_startup = 1 };
        using var maintainer = new ShellRrdMaintainer(vm, action => action());
        var cpu = new RrdSeries("vm:guest:cpu0", "cpu0", "CPU 0", "(fraction)");
        cpu.AddRawValue("0.5", Now);
        maintainer.Archives[RrdArchiveInterval.FiveSecond].Merge([cpu]);

        var graph = Assert.Single(PerformanceGraphBuilder.Build(vm, maintainer, RrdArchiveInterval.FiveSecond));

        Assert.Equal(100, graph.YAxisMax);
        Assert.Equal(50, Assert.Single(graph.Series).Points[0].Value);
    }

    private static Host Host() => new() { uuid = "host", Connection = new XenConnection() };

    private static void Merge(ShellRrdMaintainer maintainer, string source, string raw, long? tick = null)
    {
        var series = maintainer.CreateSeries(maintainer.XenObject, source, false);
        series.AddRawValue(raw, tick ?? Now);
        maintainer.Archives[RrdArchiveInterval.FiveSecond].Merge([series]);
    }

    private static PerformanceGraphRow Memory(IXenObject target, ShellRrdMaintainer maintainer,
        RrdArchiveInterval interval = RrdArchiveInterval.FiveSecond) =>
        Assert.Single(PerformanceGraphBuilder.Build(target, maintainer, interval), row => row.Title == "Memory");

    private static void AssertSeries(PerformanceSeriesView series, string name, double bytes)
    {
        Assert.Equal(name, series.Name);
        Assert.Equal("bytes", series.Units);
        Assert.All(series.Points, point => Assert.Equal(bytes, point.Value));
    }
}
