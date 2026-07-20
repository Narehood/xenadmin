using System.Security.Cryptography.X509Certificates;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using XcpNgCenter.Shell.Views;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Shows blocking Avalonia TOFU dialogs from the TLS validation callback thread.
/// </summary>
public static class TofuTrustPrompt
{
    public static bool Prompt(CertificateTrustRequest request)
    {
        // Never block the UI thread waiting on ShowDialog — that deadlocks.
        if (Dispatcher.UIThread.CheckAccess())
            return false;

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow: { } owner
                })
            {
                return false;
            }

            var window = new CertificateTrustWindow(request);
            return await window.ShowDialog<bool>(owner);
        }).GetAwaiter().GetResult();
    }

    public static CertificateTrustRequest BuildRequest(
        CertificateTrustKind kind,
        string hostname,
        X509Certificate certificate,
        string? previousFingerprint = null)
    {
        using var cert2 = new X509Certificate2(certificate);
        var hash = certificate.GetCertHashString() ?? string.Empty;

        return new CertificateTrustRequest
        {
            Kind = kind,
            Hostname = hostname,
            Fingerprint = FormatFingerprint(hash),
            PreviousFingerprint = string.IsNullOrEmpty(previousFingerprint)
                ? null
                : FormatFingerprint(previousFingerprint),
            Subject = string.IsNullOrWhiteSpace(cert2.Subject) ? "—" : cert2.Subject,
            Issuer = string.IsNullOrWhiteSpace(cert2.Issuer) ? "—" : cert2.Issuer,
            ValidFrom = cert2.NotBefore.ToLocalTime().ToString("g"),
            ValidTo = cert2.NotAfter.ToLocalTime().ToString("g")
        };
    }

    public static string FormatFingerprint(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return string.Empty;

        var cleaned = hash.Replace(":", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .ToUpperInvariant();

        if (cleaned.Length < 2)
            return cleaned;

        return string.Join(':', Enumerable.Range(0, cleaned.Length / 2)
            .Select(i => cleaned.Substring(i * 2, 2)));
    }
}
