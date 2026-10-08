using XenAPI;
using XcpNgCenter.Shell.Services.Performance;
using Xunit;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Xml;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.Tests;

public sealed class PerformancePollingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    public async Task RrdBodyReadsAsynchronouslyAcrossFragmentedText(int fragmentSize)
    {
        var expected = new string('x', 20000);
        using var stream = new AsyncOnlyRrdStream($"<rrd><name>{expected}</name></rrd>", fragmentSize);
        using var maintainer = new ShellRrdMaintainer(new Host(), action => action(), (_, _) => stream);
        string? value = null;
        var success = await maintainer.GetAsync(
            _ => new Uri("http://synthetic.invalid/rrd"), async (reader, _) =>
            {
                if (reader.NodeType == XmlNodeType.Text)
                    value = await reader.ReadContentAsStringAsync();
            }, maintainer.XenObject);

        Assert.True(success);
        Assert.Equal(expected, value);
        Assert.True(stream.AsyncReads > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalClosesBlockedAsyncRrdReads(bool insideText)
    {
        using var stream = new BlockingRrdStream(insideText ? "<rrd><name>" + new string('x', 20000) : "");
        using var maintainer = new ShellRrdMaintainer(new Host(), action => action(), (_, _) => stream);
        var contentReadStarted = false;
        var work = maintainer.GetAsync(
            _ => new Uri("http://synthetic.invalid/rrd"), async (reader, _) =>
            {
                if (reader.NodeType != XmlNodeType.Text) return;
                contentReadStarted = true;
                await reader.ReadContentAsStringAsync();
            }, maintainer.XenObject);
        await stream.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(insideText, contentReadStarted);
        await maintainer.DisposeAsync();
        Assert.False(await work.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(stream.Closed);
    }

    [Fact]
    public async Task DisposeAsyncWaitsForStartedPollersBlockedBodyRead()
    {
        using var server = new JsonRpcTransportTests.RpcServer { Result = new JArray() };
        using var connection = HeartbeatLifecycleTests.Connection(server);
        var host = connection.Cache.Hosts[0];
        host.address = "127.0.0.1";
        using var stream = new BlockingRrdStream("");
        using var maintainer = new ShellRrdMaintainer(host, action => action(), (_, _) => stream);
        maintainer.Start();
        await stream.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(maintainer.Completion.IsCompleted);

        await maintainer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(maintainer.Completion.IsCompletedSuccessfully);
        Assert.True(stream.Closed);
        Assert.False(maintainer.LoadingInitialData);
        Assert.True(connection.IsConnected);
        Assert.All(maintainer.Archives.Values, archive => Assert.Empty(archive.SeriesById));
    }

    [Fact]
    public async Task DisposeCancelsInitialMetadataRpcAndReleasesOnlyDuplicateClient()
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
    public async Task DisposalCancelsIdleDelayAndCannotRestartPoller()
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
    public async Task IncrementalPollPreservesArchiveResolutionAndHistory(RrdArchiveInterval interval, int seconds)
    {
        using var maintainer = new ShellRrdMaintainer(new Host(), action => action());
        var archive = maintainer.Archives[interval];
        var now = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        await maintainer.PollArchiveAsync(interval, now, (_, requestedSeconds) =>
        {
            Assert.Equal(seconds, requestedSeconds);
            return Task.FromResult<List<RrdSeries>?>([Samples(now, seconds, archive.MaxPoints)]);
        });
        var next = now.AddSeconds(seconds);
        await maintainer.PollArchiveAsync(interval, next, (start, requestedSeconds) =>
        {
            Assert.Equal(seconds, requestedSeconds);
            Assert.Equal(new DateTimeOffset(now).ToUnixTimeSeconds(), start);
            // An RRD response can include the cursor sample as well as new samples.
            return Task.FromResult<List<RrdSeries>?>([Samples(next, requestedSeconds, 2)]);
        });

        var points = archive.SeriesById["host:test:cpu0"].Points;
        Assert.Equal(archive.MaxPoints, points.Count);
        Assert.Equal((archive.MaxPoints - 1L) * seconds,
            TimeSpan.FromTicks(points[0].Ticks - points[^1].Ticks).TotalSeconds);
        Assert.All(points.Zip(points.Skip(1)), pair =>
            Assert.Equal(seconds, TimeSpan.FromTicks(pair.First.Ticks - pair.Second.Ticks).TotalSeconds));
    }

    [Fact]
    public async Task FailedFetchDoesNotAdvanceCursorOrMergePriorResponse()
    {
        using var maintainer = new ShellRrdMaintainer(new Host(), action => action());
        var now = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        await maintainer.PollArchiveAsync(RrdArchiveInterval.OneMinute, now,
            (_, _) => Task.FromResult<List<RrdSeries>?>([Samples(now, 60, 120)]));
        await maintainer.PollArchiveAsync(RrdArchiveInterval.OneMinute, now.AddMinutes(1),
            (_, _) => Task.FromResult<List<RrdSeries>?>(null));
        await maintainer.PollArchiveAsync(RrdArchiveInterval.OneMinute, now.AddMinutes(2), (start, seconds) =>
        {
            Assert.Equal(new DateTimeOffset(now).ToUnixTimeSeconds(), start);
            return Task.FromResult<List<RrdSeries>?>([Samples(now.AddMinutes(2), seconds, 3)]);
        });
        var points = maintainer.Archives[RrdArchiveInterval.OneMinute].SeriesById["host:test:cpu0"].Points;
        Assert.Equal(120, points.Count);
        Assert.Equal(now.AddMinutes(2).Ticks, points[0].Ticks);
    }

    [Fact]
    public async Task DisposedPollerDoesNotPublishQueuedArchiveChanges()
    {
        var queued = new Queue<Action>();
        var maintainer = new ShellRrdMaintainer(new Host(), queued.Enqueue);
        await maintainer.PollArchiveAsync(RrdArchiveInterval.OneMinute, DateTime.UtcNow,
            (_, _) => Task.FromResult<List<RrdSeries>?>([Samples(DateTime.UtcNow, 60, 2)]));
        maintainer.Dispose();
        while (queued.TryDequeue(out var action))
            action();
        Assert.Empty(maintainer.Archives[RrdArchiveInterval.OneMinute].SeriesById);
    }

    [Fact]
    public async Task DisposalDuringAwaitedFetchDropsItsResult()
    {
        using var maintainer = new ShellRrdMaintainer(new Host(), action => action());
        var fetched = new TaskCompletionSource<List<RrdSeries>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = maintainer.PollArchiveAsync(RrdArchiveInterval.OneMinute, DateTime.UtcNow, (_, _) => fetched.Task);
        Assert.False(work.IsCompleted);
        maintainer.Dispose();
        fetched.SetResult([Samples(DateTime.UtcNow, 60, 2)]);

        await work.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Empty(maintainer.Archives[RrdArchiveInterval.OneMinute].SeriesById);
    }

    private static RrdSeries Samples(DateTime latest, int seconds, int count)
    {
        var series = new RrdSeries("host:test:cpu0", "cpu0", "CPU", "(fraction)");
        series.Points.AddRange(Enumerable.Range(0, count).Select(i =>
            new RrdPoint(latest.AddSeconds(-(long)i * seconds).Ticks, 50)));
        return series;
    }

    private sealed class BlockingRrdStream(string prefix) : Stream
    {
        private readonly MemoryStream input = new(Encoding.UTF8.GetBytes(prefix));
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed => closed.Task.IsCompleted;
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous RRD body read.");
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            if (input.Position < input.Length)
                return await input.ReadAsync(buffer, offset, count, cancellationToken);
            Reading.TrySetResult();
            await closed.Task.WaitAsync(cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { closed.TrySetResult(); if (disposing) input.Dispose(); base.Dispose(disposing); }
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

    private sealed class AsyncOnlyRrdStream(string xml, int fragmentSize) : Stream
    {
        private readonly MemoryStream input = new(Encoding.UTF8.GetBytes(xml));
        public int AsyncReads { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous RRD body read.");
        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("Synchronous RRD body read.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AsyncReads++;
            return input.ReadAsync(buffer, offset, Math.Min(count, fragmentSize), cancellationToken);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AsyncReads++;
            return input.ReadAsync(buffer[..Math.Min(buffer.Length, fragmentSize)], cancellationToken);
        }
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
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
