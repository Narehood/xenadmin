using XenAdmin.Actions;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.Alerts;

/// <summary>
/// Shell-safe alert fix-link handlers (no WinForms Commands / MainWindow).
/// </summary>
public static class ShellAlertFixActions
{
    /// <summary>Optional UI callback to focus the Logs tab.</summary>
    public static Action? OpenLogs { get; set; }

    /// <summary>Optional status reporter for deferred features.</summary>
    public static Action<string>? ReportStatus { get; set; }

    public static void NotifyHaConfigureUnavailable()
        => ReportStatus?.Invoke("HA configuration is not yet available in the Avalonia shell — use WinForms XCP-ng Center.");

    public static void RepairBrokenStorage(IXenConnection? connection)
    {
        if (connection is not { IsConnected: true })
        {
            ReportStatus?.Invoke("Not connected — cannot repair storage.");
            return;
        }

        var broken = connection.Cache.SRs.Where(sr => sr.IsBroken() && !sr.IsToolsSR()).ToList();
        if (broken.Count == 0)
        {
            ReportStatus?.Invoke("No broken storage repositories to repair.");
            return;
        }

        foreach (var sr in broken)
            ShellActionRunner.Run(new SrRepairAction(connection, sr, isSharedAction: broken.Count > 1), ReportStatus);

        ReportStatus?.Invoke($"Repairing {broken.Count} storage repository(ies)…");
    }
}
