using XenAdmin;
using XenAdmin.Actions;
using XenAdmin.Core;
using XenAPI;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Avalonia stand-ins for WinForms <c>AddHostToPoolCommand.NtolDialog</c> /
/// <c>EnableNtolDialog</c> used by host reboot/shutdown/evacuate/enable actions.
/// </summary>
public static class ShellHaNtolPrompt
{
    /// <summary>
    /// Returns <c>true</c> when the user cancels (same contract as WinForms NtolDialog).
    /// Safe to call from action worker threads.
    /// </summary>
    public static bool NtolDialog(HostAbstractAction action, Pool pool, long currentNtol, long targetNtol)
    {
        var poolName = Helpers.GetName(pool).Ellipsise(500);
        var hostName = Helpers.GetName(action.Host).Ellipsise(500);

        string msg;
        if (targetNtol == 0)
        {
            string format;
            if (action is EvacuateHostAction)
                format = Messages.HA_HOST_DISABLE_NTOL_ZERO;
            else if (action is RebootHostAction)
                format = Messages.HA_HOST_REBOOT_NTOL_ZERO;
            else
                format = Messages.HA_HOST_SHUTDOWN_NTOL_ZERO;

            msg = string.Format(format, poolName, hostName);
        }
        else
        {
            string format;
            if (action is EvacuateHostAction)
                format = Messages.HA_HOST_DISABLE_NTOL_DROP;
            else if (action is RebootHostAction)
                format = Messages.HA_HOST_REBOOT_NTOL_DROP;
            else
                format = Messages.HA_HOST_SHUTDOWN_NTOL_DROP;

            msg = string.Format(format, poolName, currentNtol, hostName, targetNtol);
        }

        // WinForms returns true to cancel; Confirm returns true to accept.
        var accepted = ShellConfirmPrompt.Confirm(new ShellConfirmRequest
        {
            Title = Messages.HIGH_AVAILABILITY,
            Message = msg,
            AcceptLabel = Messages.YES_BUTTON_CAPTION,
            CancelLabel = Messages.NO_BUTTON_CAPTION
        });
        return !accepted;
    }

    /// <summary>
    /// Returns <c>true</c> when the user wants to raise ntol after enabling a host.
    /// Safe to call from action worker threads.
    /// </summary>
    public static bool EnableNtolDialog(Pool pool, Host host, long currentNtol, long max)
    {
        var poolName = Helpers.GetName(pool).Ellipsise(500);
        var hostName = Helpers.GetName(host).Ellipsise(500);
        var msg = string.Format(Messages.HA_HOST_ENABLE_NTOL_RAISE_QUERY, poolName, hostName, currentNtol, max);

        return ShellConfirmPrompt.Confirm(new ShellConfirmRequest
        {
            Title = Messages.HIGH_AVAILABILITY,
            Message = msg,
            AcceptLabel = Messages.YES_BUTTON_CAPTION,
            CancelLabel = Messages.NO_BUTTON_CAPTION
        });
    }
}
