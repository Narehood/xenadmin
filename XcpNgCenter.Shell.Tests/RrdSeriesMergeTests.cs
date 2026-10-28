using XcpNgCenter.Shell.Services.Performance;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class RrdSeriesMergeTests
{
    [Fact]
    public void MergeKeepsExistingValuesAndFirstIncomingValueForEachTimestamp()
    {
        var series = new RrdSeries("vm:guest:cpu0", "cpu0", "CPU 0", "");
        series.Points.AddRange([new(20, 2), new(10, 1)]);
        RrdPoint[] incoming = [new(30, 3), new(10, 9), new(40, 4), new(30, 8), new(5, 5)];
        var original = incoming.ToArray();

        series.MergePoints(incoming, 4);

        Assert.Equal([new(40, 4), new(30, 3), new(20, 2), new RrdPoint(10, 1)], series.Points);
        Assert.Equal(original, incoming);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void EmptyUpdateStillAppliesArchiveLimit(int maxPoints)
    {
        var series = new RrdSeries("vm:guest:cpu0", "cpu0", "CPU 0", "");
        series.Points.AddRange([new(10, 1), new(30, 3), new(20, 2)]);

        series.MergePoints([], maxPoints);

        Assert.Equal(new[] { new RrdPoint(30, 3), new RrdPoint(20, 2) }.Take(maxPoints), series.Points);
    }

    [Fact]
    public void SeriesCanMergeItsOwnPointsWithoutDuplicatingThem()
    {
        var series = new RrdSeries("vm:guest:cpu0", "cpu0", "CPU 0", "");
        series.Points.AddRange([new(10, 1), new(20, 2)]);

        series.MergePoints(series.Points, 10);

        Assert.Equal([new(20, 2), new RrdPoint(10, 1)], series.Points);
    }
}
