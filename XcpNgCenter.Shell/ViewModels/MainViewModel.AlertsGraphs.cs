using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Alerts;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Actions;
using XcpNgCenter.Shell.Alerts;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.Services.Performance;
using XcpNgCenter.Shell.Views;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.ViewModels;

public partial class MainViewModel
{
    private readonly ShellAlertHub _alertHub = new();
    private readonly Dictionary<Alert, AlertItemRow> _alertRows = new();
    private ShellRrdMaintainer? _rrdMaintainer;
    private string? _rrdObjectKey;

    public ObservableCollection<AlertItemRow> AlertItems { get; } = new();

    public ObservableCollection<PerformanceGraphRow> PerformanceGraphs { get; } = new();

    public IReadOnlyList<PerformanceRangeOption> PerformanceRangeOptions => PerformanceGraphBuilder.RangeOptions;

    public bool HasAlertItems => AlertItems.Count > 0;

    public bool HasPerformanceGraphs => PerformanceGraphs.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlertsBadgeText))]
    [NotifyPropertyChangedFor(nameof(HasAlertsBadge))]
    private int _alertCount;

    [ObservableProperty]
    private string _performanceStatusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPerformanceWaiting))]
    private bool _canShowPerformance;

    [ObservableProperty]
    private PerformanceRangeOption? _selectedPerformanceRange;

    public bool HasAlertsBadge => AlertCount > 0;

    public string AlertsBadgeText => AlertCount > 99 ? "99+" : AlertCount.ToString();

    public bool HasSelectedAlerts => AlertItems.Any(a => a.IsSelected);

    public bool ShowPerformanceWaiting => CanShowPerformance && !HasPerformanceGraphs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditPerformanceLayout))]
    [NotifyCanExecuteChangedFor(nameof(EditPerformanceLayoutCommand))]
    private bool _isPerformanceLayoutOpen;

    public bool CanEditPerformanceLayout => CanShowPerformance && _rrdMaintainer != null && !IsPerformanceLayoutOpen;

    private void InitializeAlertsAndGraphsUi()
    {
        SelectedPerformanceRange = PerformanceRangeOptions[0];
        AlertItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAlertItems));
            OnPropertyChanged(nameof(HasSelectedAlerts));
            DismissSelectedAlertsCommand.NotifyCanExecuteChanged();
            DismissAllAlertsCommand.NotifyCanExecuteChanged();
        };

        Alert.RegisterAlertCollectionChanged(OnAlertCollectionChanged);
        ShellAlertFixActions.ReportStatus = msg => StatusMessage = msg;
        ShellAlertFixActions.OpenLogs = () =>
        {
            StatusMessage = "See the Logs tab for recent actions and host messages.";
            CloseGlobalAlerts();
        };
        RebuildAlertItems();
    }

    [RelayCommand]
    private void OpenGlobalAlerts()
    {
        ShowGlobalAlerts = true;
        StatusMessage = HasAlertItems
            ? $"Showing {AlertCount} alert{(AlertCount == 1 ? "" : "s")} across all connected servers."
            : "No alerts from connected servers.";
    }

    [RelayCommand]
    private void CloseGlobalAlerts()
    {
        if (!ShowGlobalAlerts)
            return;
        ShowGlobalAlerts = false;
    }

    private void DisposeAlertsAndGraphsUi()
    {
        Alert.DeregisterAlertCollectionChanged(OnAlertCollectionChanged);
        ShellAlertFixActions.ReportStatus = null;
        ShellAlertFixActions.OpenLogs = null;
        StopPerformancePolling();
        _alertHub.Dispose();
        _alertRows.Clear();
        AlertItems.Clear();
    }

    private void AttachAlertsForConnection(IXenConnection connection)
        => _alertHub.Attach(connection);

    private void DetachAlertsForConnection(IXenConnection? connection)
    {
        if (connection != null)
            _alertHub.Detach(connection);
    }

    private void OnAlertCollectionChanged(object? sender, CollectionChangeEventArgs e)
    {
        void Apply()
        {
            switch (e.Action)
            {
                case CollectionChangeAction.Add when e.Element is Alert added:
                    InsertAlertRow(added);
                    break;
                case CollectionChangeAction.Remove when e.Element is Alert removed:
                    RemoveAlertRow(removed);
                    break;
                case CollectionChangeAction.Refresh:
                    RebuildAlertItems();
                    break;
                default:
                    RebuildAlertItems();
                    break;
            }

            RefreshAlertCount();
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private void RebuildAlertItems()
    {
        AlertItems.Clear();
        _alertRows.Clear();
        foreach (var alert in Alert.NonDismissingAlerts.OrderByDescending(a => a.Timestamp))
            InsertAlertRow(alert);
        RefreshAlertCount();
    }

    private void InsertAlertRow(Alert alert)
    {
        if (_alertRows.ContainsKey(alert))
            return;

        var row = new AlertItemRow(alert);
        row.PropertyChanged += OnAlertRowPropertyChanged;
        _alertRows[alert] = row;

        var insertAt = 0;
        while (insertAt < AlertItems.Count && AlertItems[insertAt].Alert.Timestamp > alert.Timestamp)
            insertAt++;
        AlertItems.Insert(insertAt, row);
    }

    private void RemoveAlertRow(Alert alert)
    {
        if (!_alertRows.Remove(alert, out var row))
            return;
        row.PropertyChanged -= OnAlertRowPropertyChanged;
        AlertItems.Remove(row);
    }

    private void OnAlertRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AlertItemRow.IsSelected))
        {
            OnPropertyChanged(nameof(HasSelectedAlerts));
            DismissSelectedAlertsCommand.NotifyCanExecuteChanged();
        }
    }

    private void RefreshAlertCount()
    {
        AlertCount = Alert.NonDismissingAlertCount;
        DismissAllAlertsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ToggleAlertSelection(AlertItemRow? row)
    {
        if (row == null)
            return;
        row.IsSelected = !row.IsSelected;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedAlerts))]
    private async Task DismissSelectedAlertsAsync()
    {
        var selected = AlertItems.Where(a => a.IsSelected && a.CanDismiss).Select(a => a.Alert).ToList();
        if (!await ConfirmAlertDismissalAsync(selected.Count).ConfigureAwait(true))
            return;
        DismissAlerts(selected);
    }

    [RelayCommand(CanExecute = nameof(HasAlertItems))]
    private async Task DismissAllAlertsAsync()
    {
        var all = AlertItems.Where(a => a.CanDismiss).Select(a => a.Alert).ToList();
        if (!await ConfirmAlertDismissalAsync(all.Count).ConfigureAwait(true))
            return;
        DismissAlerts(all);
    }

    private async Task<bool> ConfirmAlertDismissalAsync(int count)
    {
        if (count == 0 || !_appSettings.ConfirmAlertDismissals)
            return true;

        return await ShellConfirmPrompt.ConfirmAsync(new ShellConfirmRequest
        {
            Title = count == 1 ? "Dismiss alert" : "Dismiss alerts",
            Message = count == 1
                ? "Dismiss the selected alert? It may not be possible to restore it."
                : $"Dismiss {count} alerts? They may not be possible to restore.",
            AcceptLabel = count == 1 ? "Dismiss alert" : "Dismiss alerts",
            CancelLabel = "Cancel"
        }).ConfigureAwait(true);
    }

    private void DismissAlerts(List<Alert> alerts)
    {
        if (alerts.Count == 0)
        {
            StatusMessage = "No dismissable alerts.";
            return;
        }

        foreach (var a in alerts)
        {
            a.Dismissing = true;
            if (_alertRows.TryGetValue(a, out var row))
                row.RefreshDismissable();
        }

        foreach (var group in alerts.GroupBy(a => a.Connection))
        {
            var list = group.ToList();
            ShellActionRunner.Run(new DismissAlertsAction(list, group.Key), msg => StatusMessage = msg);
        }

        RefreshAlertCount();
        DismissSelectedAlertsCommand.NotifyCanExecuteChanged();
        DismissAllAlertsCommand.NotifyCanExecuteChanged();
    }

    private void RefreshPerformanceProperties(InfraTreeNode? node)
    {
        var xo = ResolvePerformanceTarget(node);
        CanShowPerformance = xo is Host or VM { power_state: vm_power_state.Running };

        if (!CanShowPerformance || xo == null)
        {
            StopPerformancePolling();
            PerformanceGraphs.Clear();
            PerformanceStatusMessage = xo is VM
                ? "Performance graphs are available when the VM is running."
                : "Select a host or running VM to view performance graphs.";
            OnPropertyChanged(nameof(HasPerformanceGraphs));
            OnPropertyChanged(nameof(ShowPerformanceWaiting));
            RefreshPerformanceLayoutCommand();
            return;
        }

        var key = $"{xo.GetType().Name}:{xo.opaque_ref}:{ShellGraphLayoutKeys.ObjectUuid(xo)}";
        if (_rrdObjectKey != key || _rrdMaintainer == null
            || !ReferenceEquals(_rrdMaintainer.XenObject.Connection, xo.Connection))
        {
            StopPerformancePolling();
            _rrdObjectKey = key;
            _rrdMaintainer = new ShellRrdMaintainer(xo);
            _rrdMaintainer.ArchivesUpdated += OnRrdArchivesUpdated;
            PerformanceStatusMessage = "Loading performance data…";
            PerformanceGraphs.Clear();
            OnPropertyChanged(nameof(HasPerformanceGraphs));
            OnPropertyChanged(nameof(ShowPerformanceWaiting));
            _rrdMaintainer.Start();
            RefreshPerformanceLayoutCommand();
            return;
        }

        RebuildPerformanceGraphs();
    }

    private IXenObject? ResolvePerformanceTarget(InfraTreeNode? node)
    {
        var conn = SelectedConnection;
        if (conn is not { IsConnected: true } || node == null)
            return null;

        if (node.Kind == InfraNodeKind.Host && node.OpaqueRef != null)
            return conn.Resolve(new XenRef<Host>(node.OpaqueRef));

        if (node.Kind == InfraNodeKind.Vm && node.OpaqueRef != null)
            return conn.Resolve(new XenRef<VM>(node.OpaqueRef));

        return null;
    }

    private void OnRrdArchivesUpdated()
    {
        if (_rrdMaintainer?.LoadingInitialData == true)
        {
            PerformanceStatusMessage = "Loading performance data…";
            return;
        }

        RebuildPerformanceGraphs();
    }

    private void RebuildPerformanceGraphs(IReadOnlyList<GraphLayoutDefinition>? savedLayout = null)
    {
        var xo = ResolvePerformanceTarget(SelectedInfraNode ?? _pinnedInfraNode);
        var interval = SelectedPerformanceRange?.Interval ?? RrdArchiveInterval.FiveSecond;
        PerformanceGraphs.Clear();
        var rows = savedLayout == null || xo == null
            ? PerformanceGraphBuilder.Build(xo, _rrdMaintainer, interval)
            : PerformanceGraphBuilder.BuildFromLayout(xo, _rrdMaintainer, interval, savedLayout);
        foreach (var row in rows)
            PerformanceGraphs.Add(row);

        OnPropertyChanged(nameof(HasPerformanceGraphs));
        OnPropertyChanged(nameof(ShowPerformanceWaiting));
        RefreshPerformanceLayoutCommand();

        var rangeLabel = SelectedPerformanceRange?.Label ?? "Last ~10 minutes";
        PerformanceStatusMessage = !PerformanceGraphs.Any(graph => graph.HasData)
            ? $"No RRD samples for {rangeLabel}. Choose another range or wait for new samples."
            : $"Updated {DateTime.Now:T} — {rangeLabel}.";
    }

    private void RefreshPerformanceLayoutCommand()
    {
        OnPropertyChanged(nameof(CanEditPerformanceLayout));
        EditPerformanceLayoutCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedPerformanceRangeChanged(PerformanceRangeOption? value)
    {
        if (_rrdMaintainer != null)
            RebuildPerformanceGraphs();
    }

    [RelayCommand(CanExecute = nameof(CanEditPerformanceLayout))]
    private async Task EditPerformanceLayoutAsync()
    {
        var xo = ResolvePerformanceTarget(SelectedInfraNode ?? _pinnedInfraNode);
        var owner = GetMainWindow();
        if (xo == null || _rrdMaintainer == null || owner == null || IsPerformanceLayoutOpen)
            return;

        IsPerformanceLayoutOpen = true;
        try
        {
            // Capture the reviewed target and persisted keys before opening the
            // draft. Inventory updates must not redirect or overwrite this edit.
            var snapshot = ShellGraphLayout.Snapshot(xo);
            var targetReference = xo.opaque_ref;
            var targetUuid = ShellGraphLayoutKeys.ObjectUuid(xo);
            var layout = ShellGraphLayout.Read(xo, _rrdMaintainer);
            var sources = ShellGraphLayout.Catalog(xo, _rrdMaintainer, layout);
            var targetName = xo is Host host ? host.name_label : ((VM)xo).name_label;
            var dialog = new GraphLayoutEditorWindow { Title = $"Edit performance graphs — {targetName}" };
            dialog.DataContext = new GraphLayoutEditorViewModel(layout, sources, async draft =>
            {
                var action = new SaveShellGraphLayoutAction(xo, draft, snapshot);
                if (!await ShellActionRunner.RunAndWaitAsync(action, message => StatusMessage = message))
                    throw new InvalidOperationException(action.Exception?.Message ?? "Saving the performance layout was cancelled.");

                var current = ResolvePerformanceTarget(SelectedInfraNode ?? _pinnedInfraNode);
                if (current != null && ReferenceEquals(current.Connection, xo.Connection)
                    && current.GetType() == xo.GetType() && current.opaque_ref == targetReference
                    && ShellGraphLayoutKeys.ObjectUuid(current) == targetUuid)
                {
                    // Display the acknowledged layout immediately. Later cache
                    // and RRD updates continue to use the server's saved layout.
                    RebuildPerformanceGraphs(draft);
                    PerformanceStatusMessage = "Performance layout saved.";
                }
            }, dialog.Close);
            await dialog.ShowDialog(owner);
        }
        catch (Exception error)
        {
            StatusMessage = $"Unable to edit performance graphs: {error.Message}";
            PerformanceStatusMessage = StatusMessage;
        }
        finally
        {
            IsPerformanceLayoutOpen = false;
        }
    }

    [RelayCommand]
    private void RunAlertFix(AlertItemRow? row)
    {
        if (row?.Alert.FixLinkAction == null)
            return;
        try
        {
            row.Alert.FixLinkAction();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void StopPerformancePolling()
    {
        if (_rrdMaintainer != null)
        {
            _rrdMaintainer.ArchivesUpdated -= OnRrdArchivesUpdated;
            _rrdMaintainer.Dispose();
            _rrdMaintainer = null;
        }

        _rrdObjectKey = null;
    }
}
