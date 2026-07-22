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
    private readonly IXenObject _xenObject;
    private readonly IReadOnlyList<(string Title, IReadOnlyList<string> DataSourceLeaves)> _graphs;

    public SaveShellGraphLayoutAction(
        IXenObject xenObject,
        IReadOnlyList<(string Title, IReadOnlyList<string> DataSourceLeaves)> graphs)
        : base(xenObject.Connection, "Save performance layout", "Updating pool GUI configuration…")
    {
        _xenObject = xenObject;
        _graphs = graphs;
        Pool = Helpers.GetPoolOfOne(xenObject.Connection);
    }

    protected override void Run()
    {
        if (Pool == null)
            throw new Exception("No pool available to save graph layout.");

        var uuid = ShellGraphLayoutKeys.ObjectUuid(_xenObject);
        var kind = ShellGraphLayoutKeys.ObjectKind(_xenObject);
        if (string.IsNullOrEmpty(uuid))
            throw new Exception("Unsupported object for graph layout.");

        var guiConfig = new Dictionary<string, string>(Helpers.GetGuiConfig(Pool));
        var toRemove = guiConfig.Keys
            .Where(k => k.Contains($".{kind}.{uuid}", StringComparison.Ordinal)
                        && (k.Contains(".GraphLayout.", StringComparison.Ordinal)
                            || k.Contains(".GraphName.", StringComparison.Ordinal)))
            .ToList();
        foreach (var key in toRemove)
            guiConfig.Remove(key);

        for (var i = 0; i < _graphs.Count; i++)
        {
            var (title, leaves) = _graphs[i];
            guiConfig[ShellGraphLayoutKeys.GetLayoutKey(i, _xenObject)] = string.Join(",", leaves);
            if (!string.IsNullOrWhiteSpace(title))
                guiConfig[ShellGraphLayoutKeys.GetGraphNameKey(i, _xenObject)] = title;
        }

        Pool.set_gui_config(Session, Pool.opaque_ref, guiConfig);
        Description = "Performance layout saved.";
        PercentComplete = 100;
    }
}
