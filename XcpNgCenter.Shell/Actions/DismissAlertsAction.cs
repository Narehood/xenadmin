using XenAdmin;
using XenAdmin.Actions;
using XenAdmin.Alerts;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Alerts;

namespace XcpNgCenter.Shell.Actions;

/// <summary>
/// Batch-dismiss server/client alerts without WinForms dependencies.
/// </summary>
public sealed class DismissAlertsAction : AsyncAction
{
    private readonly List<Alert> _alerts;

    public DismissAlertsAction(List<Alert> alerts, IXenConnection? connection = null)
        : base(connection, GetActionTitle(connection, alerts.Count),
            Messages.ACTION_REMOVE_ALERTS_DESCRIPTION)
    {
        _alerts = alerts;

        if (connection != null)
        {
            Pool = Helpers.GetPoolOfOne(connection);
            Host = Helpers.GetCoordinator(connection);
        }
    }

    private static string GetActionTitle(IXenConnection? connection, int alertCount)
    {
        if (connection == null)
        {
            return alertCount == 1
                ? Messages.ACTION_REMOVE_ALERTS_ON_CLIENT_TITLE_ONE
                : string.Format(Messages.ACTION_REMOVE_ALERTS_ON_CLIENT_TITLE, alertCount);
        }

        return alertCount == 1
            ? string.Format(Messages.ACTION_REMOVE_ALERTS_ON_CONNECTION_TITLE_ONE, Helpers.GetName(connection))
            : string.Format(Messages.ACTION_REMOVE_ALERTS_ON_CONNECTION_TITLE, alertCount, Helpers.GetName(connection));
    }

    protected override void Run()
    {
        LogDescriptionChanges = false;

        try
        {
            if (Connection != null && Helpers.XapiEqualOrGreater_22_19_0(Connection))
            {
                var msgRefs = new List<XenRef<Message>>();
                var msgAlerts = new List<ShellMessageAlert>();
                var otherAlerts = new List<Alert>();

                foreach (var a in _alerts)
                {
                    if (a is ShellMessageAlert ma)
                    {
                        msgAlerts.Add(ma);
                        msgRefs.Add(new XenRef<Message>(ma.Message.opaque_ref));
                    }
                    else
                    {
                        otherAlerts.Add(a);
                    }
                }

                var midPoint = _alerts.Count > 0 ? 100 * msgAlerts.Count / _alerts.Count : 0;

                if (msgAlerts.Count > 0)
                {
                    RelatedTask = Message.async_destroy_many(Session, msgRefs);
                    PollToCompletion(0, midPoint);
                    Alert.RemoveAlert(a => msgAlerts.Contains(a));
                }

                for (var i = 0; i < otherAlerts.Count; i++)
                {
                    otherAlerts[i].Dismiss();
                    PercentComplete = midPoint + i * (100 - midPoint) / Math.Max(1, otherAlerts.Count);
                }
            }
            else
            {
                for (var i = 0; i < _alerts.Count; i++)
                {
                    Description = string.Format(Messages.ACTION_REMOVE_ALERTS_PROGRESS_DESCRIPTION, i, _alerts.Count);
                    _alerts[i].Dismiss();
                    PercentComplete = i * 100 / Math.Max(1, _alerts.Count);
                }
            }
        }
        finally
        {
            LogDescriptionChanges = true;
        }

        Description = _alerts.Count == 1
            ? Messages.ACTION_REMOVE_ALERTS_DONE_ONE
            : string.Format(Messages.ACTION_REMOVE_ALERTS_DONE, _alerts.Count);
    }
}
