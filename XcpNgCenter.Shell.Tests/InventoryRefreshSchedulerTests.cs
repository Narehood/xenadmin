using XenAdmin.Network;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class InventoryRefreshSchedulerTests
{
    [Fact]
    public void BurstRefreshesEachConnectionOnceOnTheNextUiTurn()
    {
        var queue = new Queue<Action>();
        var refreshed = new List<IXenConnection>();
        using var scheduler = new InventoryRefreshScheduler(queue.Enqueue, (_, conn) => refreshed.Add(conn));
        var first = new XenConnection();
        var second = new XenConnection();
        for (var i = 0; i < 200; i++)
        {
            scheduler.Request(new ServerNode(), first);
            scheduler.Request(new ServerNode(), second);
        }
        Assert.Single(queue);
        Assert.Empty(refreshed);
        queue.Dequeue()();
        Assert.Equal(new IXenConnection[] { first, second }, refreshed);
    }

    [Fact]
    public void NotificationDuringRefreshSchedulesAnotherTurnWithoutLosingTheChange()
    {
        var queue = new Queue<Action>();
        var count = 0;
        var connection = new XenConnection();
        var server = new ServerNode();
        InventoryRefreshScheduler? scheduler = null;
        using (scheduler = new InventoryRefreshScheduler(queue.Enqueue, (_, _) =>
        {
            count++;
            if (count == 1) scheduler!.Request(server, connection);
        }))
        {
            scheduler.Request(server, connection);
            queue.Dequeue()();
            Assert.Single(queue);
            queue.Dequeue()();
            Assert.Equal(2, count);
            Assert.Empty(queue);
        }
    }

    [Fact]
    public void OldConnectionCannotDisplaceTheReplacementConnectionInTheSameBatch()
    {
        var queue = new Queue<Action>();
        var refreshed = new List<IXenConnection>();
        var old = new XenConnection();
        var current = new XenConnection();
        var server = new ServerNode { Connection = current };
        using var scheduler = new InventoryRefreshScheduler(queue.Enqueue, (node, conn) =>
        {
            if (ReferenceEquals(node.Connection, conn)) refreshed.Add(conn);
        });
        scheduler.Request(server, current);
        scheduler.Request(server, old);
        queue.Dequeue()();
        Assert.Same(current, Assert.Single(refreshed));
    }

    [Fact]
    public void DisposeCancelsQueuedAndFutureRefreshes()
    {
        var queue = new Queue<Action>();
        var count = 0;
        var scheduler = new InventoryRefreshScheduler(queue.Enqueue, (_, _) => count++);
        scheduler.Request(new ServerNode(), new XenConnection());
        scheduler.Dispose();
        queue.Dequeue()();
        scheduler.Request(new ServerNode(), new XenConnection());
        Assert.Equal(0, count);
        Assert.Empty(queue);
    }

    [Fact]
    public void FailedUiPostCanBeRetriedWithoutLosingPendingConnections()
    {
        var queue = new Queue<Action>();
        var refreshed = 0;
        var fail = true;
        using var scheduler = new InventoryRefreshScheduler(action =>
        {
            if (fail) throw new InvalidOperationException("Synthetic dispatcher failure");
            queue.Enqueue(action);
        }, (_, _) => refreshed++);
        var connection = new XenConnection();
        Assert.Throws<InvalidOperationException>(() => scheduler.Request(new ServerNode(), connection));
        fail = false;
        scheduler.Request(new ServerNode(), connection);
        queue.Dequeue()();
        Assert.Equal(1, refreshed);
    }

    [Fact]
    public void FailedRefreshDoesNotDropLaterConnectionsAndRemainsPendingForAnotherNotification()
    {
        var queue = new Queue<Action>();
        var refreshed = new List<IXenConnection>();
        var failing = new XenConnection();
        var healthy = new XenConnection();
        var fail = true;
        using var scheduler = new InventoryRefreshScheduler(queue.Enqueue, (_, conn) =>
        {
            if (ReferenceEquals(conn, failing) && fail) throw new InvalidOperationException("Synthetic rebuild failure");
            refreshed.Add(conn);
        });
        scheduler.Request(new ServerNode(), failing);
        scheduler.Request(new ServerNode(), healthy);
        var error = Record.Exception(queue.Dequeue());
        Assert.Same(healthy, Assert.Single(refreshed));
        Assert.Null(error);
        Assert.Empty(queue); // A persistent failure must not create an automatic UI retry loop.

        fail = false;
        scheduler.Request(new ServerNode(), healthy);
        queue.Dequeue()();
        Assert.Equal(new IXenConnection[] { healthy, failing, healthy }, refreshed);
        Assert.Empty(queue);
    }

    [Fact]
    public void PersistentFailureCannotStarveOtherConnectionsOrScheduleABusyLoop()
    {
        var queue = new Queue<Action>();
        var failing = new XenConnection();
        var healthy = new XenConnection();
        var failures = 0;
        var successes = 0;
        using var scheduler = new InventoryRefreshScheduler(queue.Enqueue, (_, conn) =>
        {
            if (ReferenceEquals(conn, failing))
            {
                failures++;
                throw new InvalidOperationException("Synthetic persistent failure");
            }
            successes++;
        });
        scheduler.Request(new ServerNode(), failing);
        for (var turn = 1; turn <= 3; turn++)
        {
            scheduler.Request(new ServerNode(), healthy);
            Assert.Single(queue);
            Assert.Null(Record.Exception(queue.Dequeue()));
            Assert.Equal(turn, failures);
            Assert.Equal(turn, successes);
            Assert.Empty(queue);
        }
    }

    [Fact]
    public void FailedRefreshCannotOverwriteANewerServerNotification()
    {
        var queue = new Queue<Action>();
        var connection = new XenConnection();
        var old = new ServerNode();
        var current = new ServerNode();
        var refreshed = new List<ServerNode>();
        InventoryRefreshScheduler? scheduler = null;
        using (scheduler = new InventoryRefreshScheduler(queue.Enqueue, (server, _) =>
        {
            if (ReferenceEquals(server, old))
            {
                scheduler!.Request(current, connection);
                throw new InvalidOperationException("Synthetic failure after a new notification");
            }
            refreshed.Add(server);
        }))
        {
            scheduler.Request(old, connection);
            Assert.Null(Record.Exception(queue.Dequeue()));
            Assert.Single(queue);
            queue.Dequeue()();
            Assert.Same(current, Assert.Single(refreshed));
            Assert.Empty(queue);
        }
    }

    [Fact]
    public void ConcurrentNotificationsScheduleOnlyOneUiCallback()
    {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var count = 0;
        using var scheduler = new InventoryRefreshScheduler(queue.Enqueue, (_, _) => count++);
        var connection = new XenConnection();
        Parallel.For(0, 1000, _ => scheduler.Request(new ServerNode(), connection));
        Assert.Single(queue);
        Assert.True(queue.TryDequeue(out var action));
        action();
        Assert.Equal(1, count);
    }
}
