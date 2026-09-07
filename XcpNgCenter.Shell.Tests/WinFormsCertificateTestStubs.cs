// Minimal dependencies for compile-linked XenAdmin/Network/SSL.cs. The callback
// itself remains production source; no policy logic is duplicated here.
namespace System.Windows.Forms
{
    public enum DialogResult { OK, Cancel }
}

namespace XenAdmin
{
    internal static class Program
    {
        public static object? MainWindow { get; set; }
        public static void Invoke(object owner, Action action) => action();
    }

    internal static class Settings
    {
        public static Dictionary<string, string> KnownServers { get; } = new();
        public static void ReplaceCertificate(string host, string hash) => KnownServers[host] = hash;
        public static void AddCertificate(string hash, string host) => KnownServers[host] = hash;
    }
}

namespace XenAdmin.Core
{
    internal enum SSLCertificateTypes { None, Changed, All }
    internal static class Registry
    {
        public static SSLCertificateTypes SSLCertificateTypes => SSLCertificateTypes.None;
    }
}

namespace XenAdmin.Properties
{
    internal sealed class Settings
    {
        public static Settings Default { get; } = new();
        public bool WarnChangedCertificate { get; set; } = true;
        public bool WarnUnrecognizedCertificate { get; set; } = true;
    }
}

namespace XenAdmin.Dialogs.Network
{
    internal sealed class CertificateChangedDialog : IDisposable
    {
        public CertificateChangedDialog(System.Security.Cryptography.X509Certificates.X509Certificate certificate, string host) { }
        public System.Windows.Forms.DialogResult ShowDialog(object owner) => System.Windows.Forms.DialogResult.Cancel;
        public void Dispose() { }
    }
    internal sealed class UnknownCertificateDialog : IDisposable
    {
        public UnknownCertificateDialog(System.Security.Cryptography.X509Certificates.X509Certificate certificate, string host) { }
        public System.Windows.Forms.DialogResult ShowDialog(object owner) => System.Windows.Forms.DialogResult.Cancel;
        public void Dispose() { }
    }
}
