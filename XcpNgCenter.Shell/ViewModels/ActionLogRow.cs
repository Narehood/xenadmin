using CommunityToolkit.Mvvm.ComponentModel;
using XenAdmin.Actions;

namespace XcpNgCenter.Shell.ViewModels;

public partial class ActionLogRow : ObservableObject
{
    public ActionLogRow(ActionBase action)
    {
        Action = action;
        RefreshFromAction();
        action.Changed += _ => RefreshFromAction();
        action.Completed += _ => RefreshFromAction();
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
}
