using XenAPI;
using XcpNgCenter.Shell.Services.Performance;
using Xunit;
using Newtonsoft.Json.Linq;

namespace XcpNgCenter.Shell.Tests;

public sealed class PerformancePollingTests
{
    [Fact]
    public async System.Threading.Tasks.Task DisposeInterruptsBlockedRrdBodyAndDropsLateUiWork()
    {
        using var stream = new BlockingRrdStream();
        using var maintainer = new ShellRrdMaintainer(new Host(), action => action(), (_, _) => stream);
        var work = System.Threading.Tasks.Task.Run(() => maintainer.Get(
            _ => new Uri("http://synthetic.invalid/rrd"), (_, _) => { }, maintainer.XenObject));
        await stream.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await maintainer.DisposeAsync();
        Assert.False(await work.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(stream.Closed);
    }

    [Fact]
    public async System.Threading.Tasks.Task DisposeCancelsInitialMetadataRpcAndReleasesOnlyDuplicateClient()
    {
        using var server = new JsonRpcTransportTests.RpcServer { StallHeaders = true };
        using var connection = HeartbeatLifecycleTests.Connection(server);
        var host = connection.Cache.Hosts[0];
        using var maintainer = new ShellRrdMaintainer(host, action => action());
        maintainer.Start();
        await server.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await maintainer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(maintainer.LoadingInitialData);
        Assert.True(connection.IsConnected);
        Assert.Equal("host.get_data_sources", Assert.Single(server.Requests)["method"]!.Value<string>());
        server.StallHeaders = false;
        Assert.Equal("ok", await connection.Session.JsonRpcClient.CallAsync<string>("synthetic.read", new Newtonsoft.Json.Linq.JArray(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async System.Threading.Tasks.Task DisposalCancelsIdleDelayAndCannotRestartPoller()
    {
        using var maintainer = new ShellRrdMaintainer(new SR(), action => action());
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        maintainer.ArchivesUpdated += () => updated.TrySetResult();
        maintainer.Start();
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await maintainer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        maintainer.Start();
        Assert.True(maintainer.Completion.IsCompleted);
    }
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
            TimeSpan.FromTicks(points[0].Ticks - points[^1].Ticks).TotalSeconds);
        Assert.All(points.Zip(points.Skip(1)), pair =>
            Assert.Equal(seconds, TimeSpan.FromTicks(pair.First.Ticks - pair.Second.Ticks).TotalSeconds));
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
        Assert.Equal(now.AddMinutes(2).Ticks, points[0].Ticks);
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
            new RrdPoint(latest.AddSeconds(-(long)i * seconds).Ticks, 50)));
        return series;
    }

    private sealed class BlockingRrdStream : Stream
    {
        private readonly ManualResetEventSlim closed = new();
        public readonly TaskCompletionSource Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed => closed.IsSet;
        public override int Read(byte[] buffer, int offset, int count) { Reading.TrySetResult(); closed.Wait(); return 0; }
        protected override void Dispose(bool disposing) { closed.Set(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

}
