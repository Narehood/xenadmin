using System.Text.RegularExpressions;
using XenAdmin;
using XenAdmin.Alerts;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;

namespace XcpNgCenter.Shell.Alerts;

/// <summary>
/// WinForms-free <see cref="MessageAlert"/> equivalent for the Avalonia shell.
/// Fix-link commands stay deferred (HA/SR wizards are not in the shell yet).
/// </summary>
public class ShellMessageAlert : Alert
{
    private static readonly log4net.ILog Log =
        log4net.LogManager.GetLogger(typeof(ShellMessageAlert));

    private const int DefaultPriority = 0;

    private static readonly Regex ExtAuthRegex = new(@"error=(.*)");
    private static readonly Regex MultipathRegex = new(@"^.*host=(.*); host-name=.*; current=(\d+); max=(\d+)$");
    private static readonly Regex WlbOptAlertRegex = new(@"severity:(.*) mode:(.*)");

    public Message Message { get; }

    public IXenObject? XenObject { get; }

    public ShellMessageAlert(Message m)
    {
        Message = m;
        uuid = m.uuid;
        _timestamp = m.timestamp;
        try
        {
            _priority = (int)m.priority;
        }
        catch (OverflowException)
        {
            _priority = DefaultPriority;
        }

        Connection = m.Connection;
        XenObject = m.GetXenObject();

        var h = XenObject as Host ?? Helpers.GetCoordinator(m.Connection);
        if (h != null)
            HostUuid = h.uuid;
    }

    public override AlertPriority Priority =>
        Enum.IsDefined(typeof(AlertPriority), _priority)
            ? (AlertPriority)_priority
            : AlertPriority.Unknown;

    public override string AppliesTo
    {
        get
        {
            var name = Helpers.GetName(Helpers.GetPoolOfOne(Connection));
            return !string.IsNullOrEmpty(name) ? name : Message.obj_uuid;
        }
    }

    public override string Description
    {
        get
        {
            var typ = Message.Type;
            switch (typ)
            {
                case Message.MessageType.HA_POOL_DROP_IN_PLAN_EXISTS_FOR:
                case Message.MessageType.HA_POOL_OVERCOMMITTED:
                    if (XenObject != null && int.TryParse(Message.body, out var pef))
                    {
                        var f = Message.FriendlyBody("ha_pool_drop_in_plan_exists_for-" + (pef == 0 ? "0" : pef == 1 ? "1" : "n"));
                        return string.Format(f, Helpers.GetName(XenObject), pef);
                    }
                    break;

                case Message.MessageType.HA_HEARTBEAT_APPROACHING_TIMEOUT:
                case Message.MessageType.HA_HOST_FAILED:
                case Message.MessageType.HA_HOST_WAS_FENCED:
                case Message.MessageType.HA_PROTECTED_VM_RESTART_FAILED:
                case Message.MessageType.HA_STATEFILE_APPROACHING_TIMEOUT:
                case Message.MessageType.HA_STATEFILE_LOST:
                case Message.MessageType.HA_XAPI_HEALTHCHECK_APPROACHING_TIMEOUT:
                case Message.MessageType.LICENSE_DOES_NOT_SUPPORT_POOLING:
                case Message.MessageType.PBD_PLUG_FAILED_ON_SERVER_START:
                case Message.MessageType.VM_CLONED:
                case Message.MessageType.VM_CRASHED:
                case Message.MessageType.VM_REBOOTED:
                case Message.MessageType.VM_RESUMED:
                case Message.MessageType.VM_SHUTDOWN:
                case Message.MessageType.VM_STARTED:
                case Message.MessageType.VM_SUSPENDED:
                case Message.MessageType.METADATA_LUN_BROKEN:
                case Message.MessageType.METADATA_LUN_HEALTHY:
                case Message.MessageType.LICENSE_SERVER_UNREACHABLE:
                case Message.MessageType.LICENSE_SERVER_VERSION_OBSOLETE:
                case Message.MessageType.GRACE_LICENSE:
                case Message.MessageType.LICENSE_NOT_AVAILABLE:
                case Message.MessageType.LICENSE_EXPIRED:
                case Message.MessageType.LICENSE_SERVER_CONNECTED:
                case Message.MessageType.LICENSE_SERVER_UNAVAILABLE:
                case Message.MessageType.HOST_CLOCK_WENT_BACKWARDS:
                case Message.MessageType.POOL_CPU_FEATURES_UP:
                case Message.MessageType.POOL_CPU_FEATURES_DOWN:
                case Message.MessageType.HOST_CPU_FEATURES_UP:
                case Message.MessageType.HOST_CPU_FEATURES_DOWN:
                case Message.MessageType.VDI_CBT_RESIZE_FAILED:
                case Message.MessageType.VDI_CBT_SNAPSHOT_FAILED:
                case Message.MessageType.VDI_CBT_METADATA_INCONSISTENT:
                case Message.MessageType.CLUSTER_HOST_FENCING:
                case Message.MessageType.CLUSTER_HOST_ENABLE_FAILED:
                case Message.MessageType.VM_SECURE_BOOT_FAILED:
                case Message.MessageType.TLS_VERIFICATION_EMERGENCY_DISABLED:
                    if (XenObject != null)
                        return string.Format(FriendlyFormat(), Helpers.GetName(XenObject));
                    break;

                case Message.MessageType.HOST_CLOCK_SKEW_DETECTED:
                case Message.MessageType.POOL_MASTER_TRANSITION:
                    if (XenObject != null)
                    {
                        var pool = Helpers.GetPoolOfOne(XenObject.Connection);
                        if (pool != null)
                            return string.Format(FriendlyFormat(), Helpers.GetName(XenObject), pool.Name());
                    }
                    break;

                case Message.MessageType.HA_NETWORK_BONDING_ERROR:
                    if (XenObject != null)
                        return string.Format(FriendlyFormat(), GetManagementBondName(), Helpers.GetName(XenObject));
                    break;

                case Message.MessageType.LICENSE_EXPIRES_SOON:
                    if (XenObject != null)
                        return string.Format(FriendlyFormat(), Helpers.GetName(XenObject));
                    break;

                case Message.MessageType.VBD_QOS_FAILED:
                case Message.MessageType.VCPU_QOS_FAILED:
                case Message.MessageType.VIF_QOS_FAILED:
                    if (XenObject != null)
                        return string.Format(FriendlyFormat(), "", Helpers.GetName(XenObject));
                    break;

                case Message.MessageType.EXTAUTH_INIT_IN_HOST_FAILED:
                    if (XenObject != null)
                    {
                        var m = ExtAuthRegex.Match(Message.body);
                        return m.Success
                            ? string.Format(FriendlyFormat(), Helpers.GetName(XenObject), m.Groups[1].Value)
                            : "";
                    }
                    break;

                case Message.MessageType.EXTAUTH_IN_POOL_IS_NON_HOMOGENEOUS:
                    if (XenObject != null)
                        return string.Format(FriendlyFormat(), Helpers.GetName(Helpers.GetPoolOfOne(XenObject.Connection)));
                    break;

                case Message.MessageType.MULTIPATH_PERIODIC_ALERT:
                    if (XenObject != null)
                        return ExtractMultipathCurrentState(Message.body, FriendlyFormat());
                    break;

                case Message.MessageType.WLB_CONSULTATION_FAILED:
                    if (XenObject != null)
                    {
                        var p = Helpers.GetPoolOfOne(XenObject.Connection);
                        return string.Format(FriendlyFormat(), Helpers.GetName(p), Helpers.GetName(XenObject));
                    }
                    break;

                case Message.MessageType.WLB_OPTIMIZATION_ALERT:
                    if (XenObject != null)
                    {
                        var match = WlbOptAlertRegex.Match(Message.body);
                        return match.Success
                            ? string.Format(FriendlyFormat(),
                                Helpers.GetName(Helpers.GetPoolOfOne(XenObject.Connection)),
                                match.Groups[2], match.Groups[1])
                            : "";
                    }
                    break;

                case Message.MessageType.PVS_PROXY_NO_CACHE_SR_AVAILABLE:
                    if (XenObject is PVS_proxy proxy)
                        return string.Format(FriendlyFormat(), proxy.VM(), proxy.Connection.Resolve(proxy.site));
                    break;

                case Message.MessageType.unknown when Message.name == "GFS2_CAPACITY":
                    if (XenObject != null)
                        return string.Format(Message.FriendlyBody(Message.name), XenObject.Name());
                    break;
            }

            return Message.body;
        }
    }

    private string FriendlyFormat() => Message.FriendlyBody(Message.MessageTypeString());

    private string ExtractMultipathCurrentState(string body, string format)
    {
        var lines = body.Split(["\n"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return body;

        if (lines[0] == "Events received during the last 120 seconds:")
        {
            return Helpers.IsPool(Message.Connection)
                ? string.Format(FriendlyNameManager.GetFriendlyName("Message.body-multipath_periodic_alert_healthy"),
                    Helpers.GetName(XenObject))
                : string.Format(FriendlyNameManager.GetFriendlyName("Message.body-multipath_periodic_alert_healthy_standalone"),
                    Helpers.GetName(XenObject));
        }

        var currentState = new List<string>();
        for (var lineIndex = 1; lineIndex < lines.Length && lines[lineIndex].StartsWith('['); lineIndex++)
            currentState.Add(lines[lineIndex]);

        if (currentState.Count == 1)
        {
            var m = MultipathRegex.Match(currentState[0]);
            if (m.Success)
            {
                return string.Format(format,
                    Message.Connection.Cache.Find_By_Uuid<Host>(m.Groups[1].Value),
                    m.Groups[2].Value,
                    m.Groups[3].Value);
            }

            return "";
        }

        var output = string.Join(", ",
            FindHostUuids(currentState)
                .Select(s => $"'{Message.Connection.Cache.Find_By_Uuid<Host>(s)}'"));
        return string.Format(
            FriendlyNameManager.GetFriendlyName("Message.body-multipath_periodic_alert_summary"),
            Helpers.GetName(XenObject),
            output);
    }

    public static IEnumerable<string> FindHostUuids(IEnumerable<string> lines)
    {
        return lines
            .Select(s => MultipathRegex.Match(s))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .Distinct();
    }

    private string GetManagementBondName()
    {
        var bond = NetworkingHelper.GetCoordinatorManagementBond(Connection);
        return bond == null ? Messages.UNKNOWN : bond.Name();
    }

    public override Action FixLinkAction => null!;

    public override string FixLinkText => null!;

    public override string Title
    {
        get
        {
            if (Message.name == "GFS2_CAPACITY")
                return Message.FriendlyName(Message.name);

            var title = Message.FriendlyName(Message.MessageTypeString());
            if (string.IsNullOrEmpty(title))
                title = Message.name;

            if (Message.cls != cls.Pool)
            {
                var host = XenObject as Host;
                if (host == null || Helpers.IsPool(host.Connection))
                {
                    var name = Helpers.GetName(XenObject);
                    if (!string.IsNullOrEmpty(name))
                        title = string.Format(Messages.STRING_COLON_SPACE_STRING, name, title);
                }
            }

            return title;
        }
    }

    public override string Name => Message.MessageTypeString();

    public override void Dismiss()
    {
        try
        {
            Message.destroy(Connection.Session, Message.opaque_ref);
        }
        catch (Failure exn)
        {
            if (exn.ErrorDescription[0] != Failure.HANDLE_INVALID)
                throw;
            Log.Error(exn);
        }

        RemoveAlert(this);
    }

    public override bool AllowedToDismiss()
    {
        if (Dismissing)
            return false;

        if (Connection == null)
            return true;

        if (Connection.Session == null)
            return false;

        if (Connection.Session.IsLocalSuperuser)
            return true;

        var allowedRoles = Role.ValidRoleList("Message.destroy", Connection);
        return allowedRoles.Any(r => Connection.Session.Roles.Contains(r));
    }

    public static void RemoveAlert(Message m)
    {
        var alert = FindAlert(a => a is ShellMessageAlert msgAlert
                                   && msgAlert.Message.opaque_ref == m.opaque_ref
                                   && msgAlert.Connection == m.Connection);
        if (alert != null)
            RemoveAlert(alert);
    }

    public static Alert ParseMessage(Message msg)
    {
        return msg.Type == Message.MessageType.ALARM
            ? new ShellAlarmMessageAlert(msg)
            : new ShellMessageAlert(msg);
    }
}
