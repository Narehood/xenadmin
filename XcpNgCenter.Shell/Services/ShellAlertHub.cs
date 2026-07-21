using System.ComponentModel;
using Avalonia.Threading;
using XenAdmin.Alerts;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Alerts;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Wires XAPI <see cref="Message"/> cache changes into the shared XenModel <see cref="Alert"/> collection.
/// </summary>
public sealed class ShellAlertHub : IDisposable
{
    private readonly Dictionary<IXenConnection, CollectionChangeEventHandler> _handlers = new();
    private bool _disposed;

    public void Attach(IXenConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (_disposed || _handlers.ContainsKey(connection))
            return;

        CollectionChangeEventHandler handler = (_, e) => OnMessageCollectionChanged(e);
        _handlers[connection] = handler;
        connection.Cache.RegisterCollectionChanged<Message>(handler);

        // Seed alerts already present when the cache finished populating.
        foreach (var message in connection.Cache.Messages)
            ConsiderAdd(message);
    }

    public void Detach(IXenConnection connection)
    {
        if (!_handlers.Remove(connection, out var handler))
            return;

        try
        {
            connection.Cache.DeregisterCollectionChanged<Message>(handler);
        }
        catch
        {
            // Connection may already be torn down.
        }

        Alert.RemoveAlert(a => ReferenceEquals(a.Connection, connection));
    }

    private void OnMessageCollectionChanged(CollectionChangeEventArgs e)
    {
        void Apply()
        {
            if (e.Element is not Message m)
                return;

            if (e.Action == CollectionChangeAction.Add)
                ConsiderAdd(m);
            else if (e.Action == CollectionChangeAction.Remove && !m.ShowOnGraphs())
                ShellMessageAlert.RemoveAlert(m);
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private static void ConsiderAdd(Message m)
    {
        if (m.ShowOnGraphs() || m.IsSquelched())
            return;

        if (Alert.FindAlert(a => a is ShellMessageAlert sa
                                 && sa.Message.opaque_ref == m.opaque_ref
                                 && sa.Connection == m.Connection) != null)
            return;

        Alert.AddAlert(ShellMessageAlert.ParseMessage(m));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var connection in _handlers.Keys.ToList())
            Detach(connection);
    }
}
