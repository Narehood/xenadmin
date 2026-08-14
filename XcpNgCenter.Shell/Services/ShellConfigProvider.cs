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
    private readonly ShellAppSettings _settings;

    public ShellConfigProvider(ShellAppSettings settings)
    {
        _settings = settings;
    }

    public Func<List<Role>, IXenConnection, string, AsyncAction.SudoElevationResult?> ElevatedSessionDelegate =>
        (_, _, _) => null;

    public int ConnectionTimeout => Math.Clamp(_settings.ConnectionTimeoutSeconds, 1, 3600) * 1000;

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
    {
        try
        {
            if (isForXenServer && _settings.BypassProxyForServers)
                return null;

            if (_settings.ProxyMode == ShellProxyMode.System)
                return WebRequest.GetSystemWebProxy();
            if (_settings.ProxyMode != ShellProxyMode.Custom)
                return null;

            var uri = new UriBuilder(
                Uri.UriSchemeHttp,
                _settings.ProxyAddress,
                _settings.ProxyPort).Uri;
            var proxy = new WebProxy(uri, false);
            if (_settings.ProvideProxyAuthentication)
            {
                proxy.Credentials = new NetworkCredential(
                    _settings.GetProxyUsername(),
                    _settings.GetProxyPassword());
            }

            return proxy;
        }
        catch
        {
            // Invalid persisted settings fail closed to a direct connection.
            return null;
        }
    }

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
