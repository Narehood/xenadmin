using XenAdmin.Network;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services;

/// <summary>Combines inventory notifications arriving before the next UI turn.</summary>
public sealed class InventoryRefreshScheduler(
    Action<Action> post,
    Action<ServerNode, IXenConnection> refresh) : IDisposable
{
    private static readonly log4net.ILog log = log4net.LogManager.GetLogger(typeof(InventoryRefreshScheduler));
    private readonly object gate = new();
    private readonly Dictionary<IXenConnection, ServerNode> pending = new(ReferenceEqualityComparer.Instance);
    private bool scheduled;
    private bool disposed;
    private int notifications;

    public void Request(ServerNode server, IXenConnection connection)
    {
        lock (gate)
        {
            if (disposed) return;
            notifications++;
            pending[connection] = server;
            if (scheduled) return;
            scheduled = true;
        }

        try { post(Drain); }
        catch
        {
            lock (gate) scheduled = false;
            throw;
        }
    }

    private void Drain()
    {
        KeyValuePair<IXenConnection, ServerNode>[] batch;
        int notificationCount;
        lock (gate)
        {
            if (disposed) return;
            batch = pending.ToArray();
            pending.Clear();
            notificationCount = notifications;
            notifications = 0;
            // Notifications arriving during refresh belong to a later UI turn.
            scheduled = false;
        }
        ShellPerformanceDiagnostics.Log.InventoryBatch(notificationCount, batch.Length);
        using var measurement = ShellPerformanceDiagnostics.Measure("inventory.batch");
        foreach (var (connection, server) in batch)
        {
            lock (gate) { if (disposed) return; }
            try { refresh(server, connection); }
            catch (Exception error)
            {
                lock (gate)
                {
                    if (disposed) return;
                    // A notification received during refresh is more recent than
                    // this failed pair. Keep it; otherwise retry this pair when
                    // the next notification schedules a turn, without a busy loop.
                    pending.TryAdd(connection, server);
                }
                log.Warn("Inventory refresh failed; retained for the next inventory notification.", error);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pending.Clear();
        }
    }
}
