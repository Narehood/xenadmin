using System.ComponentModel;
using Avalonia.Threading;
using XenAdmin;
using XenAdmin.Actions;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Mirrors WinForms HistoryPage wiring: subscribe to <see cref="ActionBase.NewAction"/>
/// and keep <see cref="ConnectionsManager.History"/> populated for the Logs tab.
/// </summary>
public sealed class ShellActionHistory : IDisposable
{
    public const int MaxHistoryItems = 500;

    private bool _disposed;

    public void Initialize()
    {
        ActionBase.NewAction += OnNewAction;
        ConnectionsManager.History.CollectionChanged += OnHistoryCollectionChanged;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ActionBase.NewAction -= OnNewAction;
        ConnectionsManager.History.CollectionChanged -= OnHistoryCollectionChanged;
    }

    private static void OnNewAction(ActionBase action)
    {
        if (action == null)
            return;

        void Add()
        {
            while (ConnectionsManager.History.Count >= MaxHistoryItems)
                ConnectionsManager.History.RemoveAt(0);
            ConnectionsManager.History.Add(action);
        }

        if (Dispatcher.UIThread.CheckAccess())
            Add();
        else
            Dispatcher.UIThread.Post(Add);
    }

    private static void OnHistoryCollectionChanged(object? sender, CollectionChangeEventArgs e)
    {
        // Subscribers (MainViewModel) watch History directly; this keeps the list alive.
    }
}
