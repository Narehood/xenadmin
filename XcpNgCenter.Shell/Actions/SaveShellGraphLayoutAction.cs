using XenAdmin.Actions;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services.Performance;

namespace XcpNgCenter.Shell.Actions;

/// <summary>
/// Persists shell/WinForms-compatible graph layout keys into <c>pool.gui_config</c>.
/// </summary>
public sealed class SaveShellGraphLayoutAction : AsyncAction
{
    private readonly IReadOnlyList<GraphLayoutDefinition> _graphs;
    private readonly GraphLayoutSnapshot _snapshot;

    public SaveShellGraphLayoutAction(
        IXenObject xenObject,
        IReadOnlyList<(string Title, IReadOnlyList<string> DataSourceLeaves)> graphs)
        : this(xenObject, graphs.Select(graph => new GraphLayoutDefinition(graph.Title, graph.DataSourceLeaves)).ToArray())
    {
    }

    public SaveShellGraphLayoutAction(IXenObject xenObject, IReadOnlyList<GraphLayoutDefinition> graphs,
        GraphLayoutSnapshot? snapshot = null)
        : base(xenObject.Connection, "Save performance layout", "Updating pool GUI configuration…")
    {
        _graphs = Array.AsReadOnly(graphs.Select(graph => new GraphLayoutDefinition(graph.Title, graph.DataSourceLeaves)).ToArray());
        ShellGraphLayout.Validate(_graphs);
        _snapshot = snapshot ?? ShellGraphLayout.Snapshot(xenObject);
        if (_snapshot.Kind != ShellGraphLayoutKeys.ObjectKind(xenObject)
            || _snapshot.TargetReference != xenObject.opaque_ref || _snapshot.TargetUuid != ShellGraphLayoutKeys.ObjectUuid(xenObject))
            throw new InvalidOperationException("The reviewed graph layout belongs to a different target. Reopen the editor.");
        Pool = Helpers.GetPoolOfOne(xenObject.Connection);
        ApiMethodsToRoleCheck.Add("pool.set_gui_config");
    }

    protected override void Run()
    {
        var (target, pool) = ShellGraphLayout.RequireCurrentIdentity(Connection, _snapshot);
        ShellGraphLayout.RequireUnchanged(Helpers.GetGuiConfig(pool), _snapshot);
        // Cache identity checks catch local changes before any RPC; server reads
        // also reject a replacement that the event stream has not delivered yet.
        var targetUuid = target is Host
            ? Host.get_uuid(Session, _snapshot.TargetReference)
            : VM.get_uuid(Session, _snapshot.TargetReference);
        if (targetUuid != _snapshot.TargetUuid || Pool.get_uuid(Session, _snapshot.PoolReference) != _snapshot.PoolUuid)
            throw new InvalidOperationException("The graph target or pool changed on the server. Reopen the editor.");

        var current = Pool.get_gui_config(Session, _snapshot.PoolReference);
        ShellGraphLayout.RequireUnchanged(current, _snapshot);
        ShellGraphLayout.RequireCurrentIdentity(Connection, _snapshot);
        var updated = ShellGraphLayout.Write(current, _snapshot.Kind, _snapshot.TargetUuid, _graphs);
        // XenAPI exposes get + set rather than compare-and-swap. A different
        // client can still write between these calls; do not claim atomicity.
        // Never mutate the local cache or retry an ambiguous failed response.
        Pool.set_gui_config(Session, _snapshot.PoolReference, updated);
        Description = "Performance layout saved.";
        PercentComplete = 100;
    }
}
