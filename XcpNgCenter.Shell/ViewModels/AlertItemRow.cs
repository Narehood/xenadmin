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
        ConnectionLabel = FormatConnection(alert);
        SourceLabel = string.IsNullOrWhiteSpace(ConnectionLabel)
            ? AppliesTo
            : string.IsNullOrWhiteSpace(AppliesTo) || string.Equals(AppliesTo, ConnectionLabel, StringComparison.OrdinalIgnoreCase)
                ? ConnectionLabel
                : $"{ConnectionLabel} · {AppliesTo}";
        PriorityLabel = FormatPriority(alert.Priority);
        TimestampText = alert.Timestamp.ToLocalTime().ToString("g");
        CanDismiss = alert.AllowedToDismiss();
        FixLinkText = string.IsNullOrWhiteSpace(alert.FixLinkText) ? "" : alert.FixLinkText;
        HasFixLink = alert.FixLinkAction != null && !string.IsNullOrWhiteSpace(FixLinkText);
    }

    public Alert Alert { get; }

    public string Title { get; }
    public string Description { get; }
    public string AppliesTo { get; }
    /// <summary>Hostname / nickname of the connected server or pool coordinator.</summary>
    public string ConnectionLabel { get; }
    /// <summary>Connection + applies-to label for the global alerts pane.</summary>
    public string SourceLabel { get; }
    public string PriorityLabel { get; }
    public string TimestampText { get; }
    public string FixLinkText { get; }
    public bool HasFixLink { get; }

    private static string FormatConnection(Alert alert)
    {
        var conn = alert.Connection;
        if (conn == null)
            return "";

        if (!string.IsNullOrWhiteSpace(conn.Name))
            return conn.Name;
        if (!string.IsNullOrWhiteSpace(conn.Hostname))
            return conn.Hostname;
        return "";
    }

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
