using System.Net;
using Avalonia.Threading;
using XenAdmin;
using XenAPI;
using XenCenterLib;

namespace XcpNgCenter.Shell.Services;

public static class ShellBootstrap
{
    public static TofuCertificateStore CertificateStore { get; private set; } = null!;

    public static TofuCertificateValidator CertificateValidator { get; private set; } = null!;

    public static ShellActionHistory ActionHistory { get; private set; } = null!;

    public static void Initialize()
    {
        InvokeHelper.Initialize(new AvaloniaSynchronizeInvoke(Dispatcher.UIThread));
        XenAdminConfigManager.Provider = new ShellConfigProvider();

        CertificateStore = new TofuCertificateStore();
        CertificateValidator = new TofuCertificateValidator(CertificateStore);
        ServicePointManager.ServerCertificateValidationCallback = CertificateValidator.Validate;
        ServicePointManager.SecurityProtocol = TlsPolicy.AllowedSecurityProtocols;
        Session.UserAgent = "XCP-ng Center Shell/preview (.NET 8 Avalonia)";

        ActionHistory = new ShellActionHistory();
        ActionHistory.Initialize();
    }
}
