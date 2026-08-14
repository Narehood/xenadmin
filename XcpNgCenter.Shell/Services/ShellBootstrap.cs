using System.Net;
using Avalonia.Threading;
using XenAdmin;
using XenAPI;
using XenCenterLib;

namespace XcpNgCenter.Shell.Services;

public static class ShellBootstrap
{
    public static ShellAppSettings AppSettings { get; } = new();

    public static TofuCertificateStore CertificateStore { get; private set; } = null!;

    public static TofuCertificateValidator CertificateValidator { get; private set; } = null!;

    public static ShellActionHistory ActionHistory { get; private set; } = null!;

    public static void Initialize()
    {
        InvokeHelper.Initialize(new AvaloniaSynchronizeInvoke(Dispatcher.UIThread));
        XenAdminConfigManager.Provider = new ShellConfigProvider(AppSettings);
        ApplyProxySettings();

        CertificateStore = new TofuCertificateStore();
        CertificateValidator = new TofuCertificateValidator(CertificateStore, AppSettings);
        ServicePointManager.ServerCertificateValidationCallback = CertificateValidator.Validate;
        ServicePointManager.SecurityProtocol = TlsPolicy.AllowedSecurityProtocols;
        Session.UserAgent = $"XCP-ng Center Shell/{ShellVersionInfo.Display} (.NET 8 Avalonia)";

        ActionHistory = new ShellActionHistory();
        ActionHistory.Initialize();
    }

    public static void ApplyProxySettings()
    {
        HTTP.CurrentProxyAuthenticationMethod = AppSettings.ProxyAuthentication == ShellProxyAuthentication.Basic
            ? HTTP.ProxyAuthenticationMethod.Basic
            : HTTP.ProxyAuthenticationMethod.Digest;
        Session.Proxy = XenAdminConfigManager.Provider.GetProxyFromSettings(null, true);
    }
}
