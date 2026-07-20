using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using XenAdmin;
using XenAdmin.Actions;
using XenAdmin.Actions.VMActions;
using XenAdmin.Core;
using XenAPI;
using XcpNgCenter.Shell.Views;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Avalonia stand-ins for WinForms <c>VMOperationCommand.WarningDialogHAInvalidConfig</c>
/// and <c>StartDiagnosisForm</c>.
/// </summary>
public static class ShellVmHaPrompt
{
    public static void WarningDialogHAInvalidConfig(VM vm, bool isStart)
    {
        var name = Helpers.GetName(vm);
        if (name.Length > 80)
            name = name[..77] + "…";

        var message = string.Format(
            isStart ? Messages.HA_INVALID_CONFIG_START : Messages.HA_INVALID_CONFIG_RESUME,
            name);

        var accepted = ShellConfirmPrompt.Confirm(new ShellConfirmRequest
        {
            Title = Messages.HIGH_AVAILABILITY,
            Message = message,
            AcceptLabel = "Continue",
            CancelLabel = "Cancel",
            ShowCancel = true
        });

        if (!accepted)
            throw new CancelledException();
    }

    public static void StartDiagnosisForm(VMStartAbstractAction startAction, Failure failure)
    {
        if (failure.ErrorDescription.Count == 0)
            return;

        if (failure.ErrorDescription[0] == Failure.NO_HOSTS_AVAILABLE)
        {
            ShowHostBootReasons(startAction.VM, startAction.IsStart);
            return;
        }

        if (failure.ErrorDescription[0] != Failure.HA_OPERATION_WOULD_BREAK_FAILOVER_PLAN)
            return;

        var pool = Helpers.GetPool(startAction.VM.Connection);
        if (pool == null)
            return;

        var ntol = pool.ha_host_failures_to_tolerate;
        var newNtol = Math.Min(pool.ha_plan_exists_for - 1, ntol - 1);
        var poolName = Helpers.GetName(pool);
        if (poolName.Length > 80)
            poolName = poolName[..77] + "…";
        var vmName = Helpers.GetName(startAction.VM);
        if (vmName.Length > 80)
            vmName = vmName[..77] + "…";

        if (newNtol <= 0)
        {
            var msg = string.Format(
                startAction.IsStart ? Messages.HA_VM_START_NTOL_ZERO : Messages.HA_VM_RESUME_NTOL_ZERO,
                poolName,
                vmName);
            ShellConfirmPrompt.Alert(Messages.HIGH_AVAILABILITY, msg);
            return;
        }

        var dropMsg = string.Format(
            startAction.IsStart ? Messages.HA_VM_START_NTOL_DROP : Messages.HA_VM_RESUME_NTOL_DROP,
            poolName,
            ntol,
            vmName,
            newNtol);

        var lower = ShellConfirmPrompt.Confirm(new ShellConfirmRequest
        {
            Title = Messages.HIGH_AVAILABILITY,
            Message = dropMsg,
            AcceptLabel = "Yes",
            CancelLabel = "No",
            ShowCancel = true
        });

        if (!lower)
            return;

        var action = new DelegatedAsyncAction(
            startAction.VM.Connection,
            Messages.HA_LOWERING_NTOL,
            null,
            null,
            session =>
            {
                Pool.set_ha_host_failures_to_tolerate(session, pool.opaque_ref, newNtol);
                startAction.Clone().RunAsync();
            });
        action.RunAsync();
    }

    private static void ShowHostBootReasons(VM vm, bool isStart)
    {
        var connection = vm.Connection;
        Session? session;
        try
        {
            session = connection.DuplicateSession();
            if (session == null)
                return;
        }
        catch
        {
            return;
        }

        var rows = new List<HostBootReasonRow>();
        foreach (Host host in connection.Cache.Hosts.OrderBy(h => Helpers.GetName(h), StringComparer.OrdinalIgnoreCase))
        {
            string reason;
            var canBoot = false;
            try
            {
                VM.assert_can_boot_here(session, vm.opaque_ref, host.opaque_ref);
                reason = isStart ? "Host can start this VM." : "Host can resume this VM.";
                canBoot = true;
            }
            catch (Failure failure)
            {
                reason = failure.Message;
            }
            catch (Exception e)
            {
                reason = e.Message;
            }

            rows.Add(new HostBootReasonRow(Helpers.GetName(host), reason, canBoot));
        }

        var summary = string.Format(Messages.ERROR_DIALOG_START_VM_TEXT, Helpers.GetName(vm));
        ShowStartFailureTable(Messages.ERROR_DIALOG_START_VM_TITLE, summary, rows);
    }

    private static void ShowStartFailureTable(string title, string summary, IReadOnlyList<HostBootReasonRow> rows)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return;

        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow: { } owner
                })
            {
                return;
            }

            var window = new VmStartFailureWindow(title, summary, rows);
            await window.ShowDialog(owner);
        }).GetAwaiter().GetResult();
    }
}
