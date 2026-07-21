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
    [NotifyPropertyChangedFor(nameof(CanCopy))]
    [NotifyPropertyChangedFor(nameof(CopyText))]
    private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopy))]
    [NotifyPropertyChangedFor(nameof(CopyText))]
    private string _description = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopy))]
    [NotifyPropertyChangedFor(nameof(CopyText))]
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
    [NotifyPropertyChangedFor(nameof(CanCopy))]
    [NotifyPropertyChangedFor(nameof(CopyText))]
    private string _startedText = string.Empty;

    public bool CanCopy =>
        !string.IsNullOrWhiteSpace(Title)
        || !string.IsNullOrWhiteSpace(Description)
        || !string.IsNullOrWhiteSpace(Status);

    /// <summary>Clipboard payload matching the visible log fields.</summary>
    public string CopyText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title))
                parts.Add(Title);
            if (!string.IsNullOrWhiteSpace(Status))
                parts.Add($"Status: {Status}");
            if (!string.IsNullOrWhiteSpace(Description))
                parts.Add(Description);
            if (!string.IsNullOrWhiteSpace(StartedText))
                parts.Add($"Started: {StartedText}");
            return string.Join(Environment.NewLine, parts);
        }
    }

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
