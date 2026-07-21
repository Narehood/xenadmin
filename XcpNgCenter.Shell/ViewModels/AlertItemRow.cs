using CommunityToolkit.Mvvm.ComponentModel;
using XenAdmin.Alerts;

namespace XcpNgCenter.Shell.ViewModels;

public partial class AlertItemRow : ObservableObject
{
    public AlertItemRow(Alert alert)
    {
        Alert = alert;
        Title = alert.Title ?? "";
        Description = alert.Description ?? "";
        AppliesTo = alert.AppliesTo ?? "";
        PriorityLabel = FormatPriority(alert.Priority);
        TimestampText = alert.Timestamp.ToLocalTime().ToString("g");
        CanDismiss = alert.AllowedToDismiss();
    }

    public Alert Alert { get; }

    public string Title { get; }
    public string Description { get; }
    public string AppliesTo { get; }
    public string PriorityLabel { get; }
    public string TimestampText { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _canDismiss;

    public void RefreshDismissable() => CanDismiss = Alert.AllowedToDismiss() && !Alert.Dismissing;

    private static string FormatPriority(AlertPriority priority) => priority switch
    {
        AlertPriority.Priority1 => "Critical",
        AlertPriority.Priority2 => "Major",
        AlertPriority.Priority3 => "Warning",
        AlertPriority.Priority4 => "Minor",
        AlertPriority.Priority5 => "Info",
        _ => "Alert"
    };
}
