using XenAdmin.Network;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services;

/// <summary>Combines inventory notifications arriving before the next UI turn.</summary>
public sealed class InventoryRefreshScheduler(
    Action<Action> post,
    Action<ServerNode, IXenConnection> refresh) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<IXenConnection, ServerNode> pending = new(ReferenceEqualityComparer.Instance);
    private bool scheduled;
    private bool disposed;

    public void Request(ServerNode server, IXenConnection connection)
    {
        lock (gate)
        {
            if (disposed) return;
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
        lock (gate)
        {
            if (disposed) return;
            batch = pending.ToArray();
            pending.Clear();
            // Notifications arriving during refresh belong to a later UI turn.
            scheduled = false;
        }
        foreach (var (connection, server) in batch)
        {
            lock (gate) { if (disposed) return; }
            refresh(server, connection);
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
