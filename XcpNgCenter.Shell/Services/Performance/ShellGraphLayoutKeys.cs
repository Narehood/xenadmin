using XenAdmin.Core;
using XenAPI;
using System.Globalization;

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

    private static string GetKey(string keyType, int index, IXenObject xo)
        => GetKey(keyType, index, ObjectKind(xo), ObjectUuid(xo));

    internal static string GetKey(string keyType, int index, string kind, string uuid)
    {
        if (index < 0 || kind is not ("host" or "vm") || string.IsNullOrWhiteSpace(uuid))
            throw new ArgumentException("A graph layout needs a host or VM identity and a nonnegative index.");
        return $"XenCenter.{keyType}.{index.ToString(CultureInfo.InvariantCulture)}.{kind}.{uuid}";
    }

    /// <summary>Recognizes only the canonical keys shared with the WinForms graph editor.</summary>
    public static bool TryParse(string key, out string keyType, out int index, out string kind, out string uuid)
    {
        keyType = kind = uuid = "";
        index = -1;
        var parts = key.Split('.');
        if (parts.Length != 5 || parts[0] != "XenCenter" || parts[1] is not (GraphLayout or GraphName)
            || parts[3] is not ("host" or "vm") || string.IsNullOrWhiteSpace(parts[4])
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out index)
            || parts[2] != index.ToString(CultureInfo.InvariantCulture))
            return false;
        keyType = parts[1];
        kind = parts[3];
        uuid = parts[4];
        return true;
    }

    public static bool IsTargetKey(string key, string kind, string uuid)
        => TryParse(key, out _, out _, out var keyKind, out var keyUuid) && keyKind == kind && keyUuid == uuid;

    public static string ObjectKind(IXenObject xo) => xo switch { Host => "host", VM => "vm", _ => "" };

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
