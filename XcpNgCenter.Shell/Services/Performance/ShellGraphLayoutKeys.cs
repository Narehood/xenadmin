using XenAdmin.Core;
using XenAPI;

namespace XcpNgCenter.Shell.Services.Performance;

/// <summary>
/// WinForms-compatible <c>pool.gui_config</c> keys for graph layouts (no System.Drawing).
/// </summary>
public static class ShellGraphLayoutKeys
{
    private const string GraphLayout = "GraphLayout";
    private const string GraphName = "GraphName";

    public static string GetLayoutKey(int index, IXenObject xo) => GetKey(GraphLayout, index, xo);

    public static string GetGraphNameKey(int index, IXenObject xo) => GetKey(GraphName, index, xo);

    private static string GetKey(string keyType, int index, IXenObject xo) => xo switch
    {
        Host host => $"XenCenter.{keyType}.{index}.host.{host.uuid}",
        VM vm => $"XenCenter.{keyType}.{index}.vm.{vm.uuid}",
        _ => ""
    };

    public static string ObjectKind(IXenObject xo) => xo is Host ? "host" : "vm";

    public static string ObjectUuid(IXenObject xo) => xo switch
    {
        Host h => h.uuid,
        VM vm => vm.uuid,
        _ => ""
    };

    public static Dictionary<string, string> GetGuiConfig(IXenObject xo)
    {
        var pool = Helpers.GetPoolOfOne(xo.Connection);
        return pool == null ? new Dictionary<string, string>() : Helpers.GetGuiConfig(pool);
    }
}
