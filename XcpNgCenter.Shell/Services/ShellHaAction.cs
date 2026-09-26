using XenAdmin.Actions;
using XenAdmin.Network;
using XenAPI;

namespace XcpNgCenter.Shell.Services;

/// <summary>Rechecks the reviewed scope and server prerequisites before running the shared HA actions.</summary>
public sealed class ShellHaAction : AsyncAction
{
    private readonly HaSnapshot _snapshot;
    private readonly HaRequest _request;
    private readonly HaReview _review;
    public const string RecoveryNotice = "HA settings may have partially changed, or the last server response may have been lost. Reconnect and inspect the pool HA state, VM policies, failure tolerance, and heartbeat storage before another attempt. No automatic rollback or retry was performed.";

    public ShellHaAction(IXenConnection connection, HaSnapshot snapshot, HaRequest request, HaReview review)
        : base(connection, request.Operation switch { HaOperation.Enable => "Enable pool HA", HaOperation.Disable => "Disable pool HA", _ => "Configure pool HA" })
    {
        _snapshot = snapshot;
        _request = new(request.Operation, request.FailuresToTolerate, request.HeartbeatSrReference, request.VmSettings);
        _review = review;
        ApiMethodsToRoleCheck.AddRange(HaManagement.Methods(request.Operation));
    }

    protected override void Run()
    {
        var expected = _request.Operation == HaOperation.Disable ? _snapshot.DisableFingerprint : _snapshot.Fingerprint;
        if (!_review.CanApply || _review.RequestFingerprint != HaManagement.RequestFingerprint(_request)
            || _review.SnapshotFingerprint.Length == 0 || _review.SnapshotFingerprint != expected)
            throw new InvalidOperationException("Review the current HA draft successfully before applying it.");
        var reviewed = HaManagement.Review(Connection, Session, _snapshot, _request);
        if (!reviewed.CanApply) throw new InvalidOperationException(reviewed.Error);
        var current = HaManagement.RequireCurrent(Connection, _snapshot, _request.Operation);
        HaManagement.RequireCachedDraft(current, _request);
        var pool = current.Pool;
        // Only settings that differ are passed to the shared worker. The
        // capacity review above still included every real VM's intended policy.
        var changes = _request.VmSettings.Where(setting => _request.Operation != HaOperation.Disable).Select(setting =>
            (Vm: current.Vms.Single(vm => vm.opaque_ref == setting.Reference), Setting: setting))
            .Where(pair => pair.Vm.ha_restart_priority != PolicyText(pair.Setting.Priority!.Value))
            .ToDictionary(pair => pair.Vm, pair => new VMStartupOptions(pair.Setting.Order, pair.Setting.StartDelay, pair.Setting.Priority!.Value));
        try
        {
            switch (_request.Operation)
            {
                case HaOperation.Enable:
                    new EnableHAAction(pool, changes, [current.Srs.Single(sr => sr.opaque_ref == _request.HeartbeatSrReference)], _request.FailuresToTolerate).RunSync(Session);
                    break;
                case HaOperation.Configure:
                    new SetHaPrioritiesAction(Connection, changes, _request.FailuresToTolerate, true).RunSync(Session);
                    break;
                case HaOperation.Disable:
                    new DisableHAAction(pool).RunSync(Session);
                    break;
            }
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"{error.Message} {RecoveryNotice}", error);
        }
        Description = _request.Operation == HaOperation.Disable ? "Pool HA disabled. VM restart policy values were preserved." : "Pool HA settings applied. Verify protection and heartbeat health on the pool.";
    }

    private static string PolicyText(VM.HaRestartPriority priority) => priority switch
    { VM.HaRestartPriority.Restart => "restart", VM.HaRestartPriority.BestEffort => "best-effort", _ => "" };
}
