using XenAPI;
using XcpNgCenter.Shell.Services.Performance;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class PerformancePollingTests
{
    [Theory]
    [InlineData(RrdArchiveInterval.FiveSecond, 5)]
    [InlineData(RrdArchiveInterval.OneMinute, 60)]
    [InlineData(RrdArchiveInterval.OneHour, 3600)]
    [InlineData(RrdArchiveInterval.OneDay, 86400)]
    public void IncrementalPollPreservesArchiveResolutionAndHistory(RrdArchiveInterval interval, int seconds)
    {
        using var maintainer = new ShellRrdMaintainer(new Host(), action => action());
        var archive = maintainer.Archives[interval];
        var now = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        maintainer.PollArchive(interval, now, (_, requestedSeconds) =>
        {
            Assert.Equal(seconds, requestedSeconds);
            return [Samples(now, seconds, archive.MaxPoints)];
        });
        var next = now.AddSeconds(seconds);
        maintainer.PollArchive(interval, next, (start, requestedSeconds) =>
        {
            Assert.Equal(seconds, requestedSeconds);
            Assert.Equal(new DateTimeOffset(now).ToUnixTimeSeconds(), start);
            // An RRD response can include the cursor sample as well as new samples.
            return [Samples(next, requestedSeconds, 2)];
        });

        var points = archive.SeriesById["host:test:cpu0"].Points;
        Assert.Equal(archive.MaxPoints, points.Count);
        Assert.Equal((archive.MaxPoints - 1L) * seconds,
            TimeSpan.FromTicks(UtcTicks(points[0]) - UtcTicks(points[^1])).TotalSeconds);
        Assert.All(points.Zip(points.Skip(1)), pair =>
            Assert.Equal(seconds, TimeSpan.FromTicks(UtcTicks(pair.First) - UtcTicks(pair.Second)).TotalSeconds));
    }

    [Fact]
    public void FailedFetchDoesNotAdvanceCursorOrMergePriorResponse()
    {
        using var maintainer = new ShellRrdMaintainer(new Host(), action => action());
        var now = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        maintainer.PollArchive(RrdArchiveInterval.OneMinute, now, (_, _) => [Samples(now, 60, 120)]);
        maintainer.PollArchive(RrdArchiveInterval.OneMinute, now.AddMinutes(1), (_, _) => null);
        maintainer.PollArchive(RrdArchiveInterval.OneMinute, now.AddMinutes(2), (start, seconds) =>
        {
            Assert.Equal(new DateTimeOffset(now).ToUnixTimeSeconds(), start);
            return [Samples(now.AddMinutes(2), seconds, 3)];
        });
        var points = maintainer.Archives[RrdArchiveInterval.OneMinute].SeriesById["host:test:cpu0"].Points;
        Assert.Equal(120, points.Count);
        Assert.Equal(now.AddMinutes(2).ToLocalTime().Ticks, points[0].Ticks);
    }

    [Fact]
    public void DisposedPollerDoesNotPublishQueuedArchiveChanges()
    {
        var queued = new Queue<Action>();
        var maintainer = new ShellRrdMaintainer(new Host(), queued.Enqueue);
        maintainer.PollArchive(RrdArchiveInterval.OneMinute, DateTime.UtcNow,
            (_, _) => [Samples(DateTime.UtcNow, 60, 2)]);
        maintainer.Dispose();
        while (queued.TryDequeue(out var action))
            action();
        Assert.Empty(maintainer.Archives[RrdArchiveInterval.OneMinute].SeriesById);
    }

    private static RrdSeries Samples(DateTime latest, int seconds, int count)
    {
        var series = new RrdSeries("host:test:cpu0", "cpu0", "CPU", "(fraction)");
        series.Points.AddRange(Enumerable.Range(0, count).Select(i =>
            new RrdPoint(latest.AddSeconds(-(long)i * seconds).ToLocalTime().Ticks, 50)));
        return series;
    }

    private static long UtcTicks(RrdPoint point) => new DateTime(point.Ticks, DateTimeKind.Local).ToUniversalTime().Ticks;
}
