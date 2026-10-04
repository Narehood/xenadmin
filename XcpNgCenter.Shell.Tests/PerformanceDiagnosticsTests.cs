using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using XenAdmin.Network;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

[CollectionDefinition("Performance diagnostics", DisableParallelization = true)]
public sealed class PerformanceDiagnosticsCollection;

[Collection("Performance diagnostics")]
public sealed class PerformanceDiagnosticsTests
{
    [Fact]
    public void RealInventoryBurstsReportCoalescingAndNoIdentifiers()
    {
        using var listener = new Capture();
        using var connection = new XenConnection { Hostname = "synthetic-private-host" };
        var server = new ServerNode { Hostname = "synthetic-private-host", Connection = connection };
        var callbacks = new Queue<Action>();
        using var scheduler = new InventoryRefreshScheduler(callbacks.Enqueue, (_, _) => { });
        for (var i = 0; i < 200; i++) scheduler.Request(server, connection);
        Assert.Single(callbacks);
        callbacks.Dequeue()();
        var events = listener.Events.ToArray();
        Assert.DoesNotContain(events, e => e.Id == 0); // EventSource metadata/write errors.
        var batch = Assert.Single(events, e => e.Id == 2);
        Assert.Equal(new object[] { 200, 1 }, batch.Payload);
        var timing = Assert.Single(events, e => e.Id == 1);
        Assert.Equal("inventory.batch", timing.Payload[0]);
        Assert.True(Assert.IsType<double>(timing.Payload[1]) >= 0);
        Assert.True(Assert.IsType<long>(timing.Payload[2]) >= 0);
        Assert.DoesNotContain(events.SelectMany(e => e.Payload).OfType<string>(), value => value.Contains("synthetic-private-host"));
    }

    [Fact]
    public void DisabledDiagnosticsDoNotAllocateForMeasurementScopes()
    {
        Assert.False(ShellPerformanceDiagnostics.Log.IsEnabled());
        using (ShellPerformanceDiagnostics.Measure("warmup")) { }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            using (ShellPerformanceDiagnostics.Measure("inventory.refresh")) { }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    private sealed class Capture : EventListener
    {
        public readonly ConcurrentQueue<(int Id, object?[] Payload)> Events = new();
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "XcpNgCenter-Shell-Performance") EnableEvents(source, EventLevel.Informational);
        }
        protected override void OnEventWritten(EventWrittenEventArgs args)
            => Events.Enqueue((args.EventId, args.Payload?.ToArray() ?? []));
    }
}
