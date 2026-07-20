using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using XenAdmin.Actions;

namespace XcpNgCenter.Shell.ViewModels;

public partial class ActionLogRow : ObservableObject, IDisposable
{
    private bool _disposed;

    public ActionLogRow(ActionBase action)
    {
        Action = action;
        RefreshFromAction();
        action.Changed += OnActionChanged;
        action.Completed += OnActionCompleted;
    }

    public ActionBase Action { get; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private int _percentComplete;

    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private bool _succeeded;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private string _startedText = string.Empty;

    private void OnActionChanged(ActionBase _)
    {
        if (Action is AsyncAction async)
            async.RecomputeCanCancel();
        PostRefresh();
    }

    private void OnActionCompleted(ActionBase _) => PostRefresh();

    private void PostRefresh()
    {
        // AsyncAction raises Changed/Completed from ThreadPool workers.
        if (Dispatcher.UIThread.CheckAccess())
            RefreshFromAction();
        else
            Dispatcher.UIThread.Post(RefreshFromAction);
    }

    public void RefreshFromAction()
    {
        Title = Action.Title ?? string.Empty;
        Description = Action.Description ?? string.Empty;
        PercentComplete = Action.PercentComplete;
        IsCompleted = Action.IsCompleted;
        Succeeded = Action.Succeeded;
        IsError = Action.IsError;
        CanCancel = !Action.IsCompleted && Action.CanCancel;
        StartedText = Action.Started.ToLocalTime().ToString("g");

        if (Action.IsCompleted)
        {
            Status = Action.Succeeded
                ? "Succeeded"
                : Action.IsCancelled
                    ? "Cancelled"
                    : Action.Exception?.Message ?? "Failed";
        }
        else if (Action.ShowProgress && Action.PercentComplete > 0)
        {
            Status = $"{Action.PercentComplete}%";
        }
        else
        {
            Status = "Running";
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Action.Changed -= OnActionChanged;
        Action.Completed -= OnActionCompleted;
    }
}
