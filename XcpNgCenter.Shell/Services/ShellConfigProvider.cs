using System.Net;
using XenAdmin;
using XenAdmin.Actions;
using XenAdmin.Network;
using XenAPI;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Minimal <see cref="IXenAdminConfigProvider"/> so XenModel connect/cache paths can run
/// without WinForms settings, history, or elevation dialogs.
/// </summary>
public sealed class ShellConfigProvider : IXenAdminConfigProvider
{
    private static readonly HashSet<string> HiddenObjects = new(StringComparer.Ordinal);
    private static readonly object HiddenObjectsLock = new();

    public Func<List<Role>, IXenConnection, string, AsyncAction.SudoElevationResult?> ElevatedSessionDelegate =>
        (_, _, _) => null;

    public int ConnectionTimeout => 20_000;

    public Session CreateActionSession(Session session, IXenConnection connection)
        => new Session(session, connection) { Timeout = ConnectionTimeout };

    public bool Exiting { get; set; }
    public bool ForcedExiting { get; set; }
    public string XenCenterUUID { get; } = Guid.NewGuid().ToString();
    public bool DontSudo => true;
    public bool ShowHiddenVMs => false;

    public string FileServiceUsername => string.Empty;
    public string FileServiceClientId => string.Empty;

    public IWebProxy? GetProxyFromSettings(IXenConnection connection)
        => GetProxyFromSettings(connection, true);

    public IWebProxy? GetProxyFromSettings(IXenConnection connection, bool isForXenServer)
        => null;

    public int GetProxyTimeout(bool timeout) => timeout ? 30_000 : 0;

    public void ShowObject(string opaqueRef)
    {
        lock (HiddenObjectsLock)
            HiddenObjects.Remove(opaqueRef);
    }

    public void HideObject(string opaqueRef)
    {
        lock (HiddenObjectsLock)
            HiddenObjects.Add(opaqueRef);
    }

    public bool ObjectIsHidden(string opaqueRef)
    {
        lock (HiddenObjectsLock)
            return HiddenObjects.Contains(opaqueRef);
    }

    public string GetLogFile() => string.Empty;

    public void UpdateServerHistory(string hostnameWithPort)
    {
        // Server list persistence is owned by SavedServerStore / MainViewModel.
    }

    public void SaveSettingsIfRequired()
    {
    }

    public string GetXenCenterMetadata() => "{}";
    public string GetCustomClientUpdatesXmlLocation() => string.Empty;
    public string GetCustomCfuLocation() => string.Empty;
    public string GetClientUpdatesQueryParam() => string.Empty;
    public string GetCustomFileServicePrefix() => string.Empty;
    public string GetCustomTokenUrl() => string.Empty;
}
