using XenAdmin.Actions;
using XenAdmin.Core;
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

    /// <summary>Opens the HA editor for the alert's verified pool, independent of tree selection.</summary>
    public static Action<Pool>? OpenHaConfiguration { get; set; }

    /// <summary>Optional status reporter for deferred features.</summary>
    public static Action<string>? ReportStatus { get; set; }

    public static void ConfigureHa(IXenConnection? connection, string poolReference, string poolUuid)
    {
        if (connection is not { IsConnected: true })
        {
            ReportStatus?.Invoke("Reconnect the alert's pool before configuring HA.");
            return;
        }

        var pool = Helpers.GetPoolOfOne(connection);
        if (pool == null || string.IsNullOrWhiteSpace(poolReference) || string.IsNullOrWhiteSpace(poolUuid)
            || pool.opaque_ref != poolReference || pool.uuid != poolUuid)
        {
            ReportStatus?.Invoke("The alert's pool is no longer available. Refresh the alerts and review the current pool.");
            return;
        }

        if (OpenHaConfiguration is { } open)
            open(pool);
        else
            ReportStatus?.Invoke("The HA editor is not available in this window.");
    }

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
