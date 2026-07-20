using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Trust-on-first-use validator for self-signed XCP-ng hosts.
/// First sight and certificate changes prompt via Avalonia dialogs (no silent re-pin).
/// </summary>
public sealed class TofuCertificateValidator
{
    private readonly TofuCertificateStore _store;
    private readonly object _gate = new();

    public TofuCertificateValidator(TofuCertificateStore store)
    {
        _store = store;
    }

    public string? LastMessage { get; private set; }

    public bool Validate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        if (certificate == null)
        {
            LastMessage = "TLS certificate missing.";
            return false;
        }

        var hostname = ResolveHostname(sender);
        if (string.IsNullOrEmpty(hostname))
        {
            LastMessage = "TLS hostname could not be resolved.";
            return false;
        }

        var hash = certificate.GetCertHashString() ?? string.Empty;
        string? pinned;
        lock (_gate)
            _store.TryGet(hostname, out pinned);

        if (!string.IsNullOrEmpty(pinned))
        {
            if (string.Equals(pinned, hash, StringComparison.OrdinalIgnoreCase))
                return true;

            var changedRequest = TofuTrustPrompt.BuildRequest(
                CertificateTrustKind.Changed,
                hostname,
                certificate,
                previousFingerprint: pinned);

            var acceptChanged = TofuTrustPrompt.Prompt(changedRequest);
            if (!acceptChanged)
            {
                LastMessage = $"Certificate change for {hostname} was rejected.";
                return false;
            }

            lock (_gate)
                _store.Set(hostname, hash);
            LastMessage = $"Updated pinned certificate for {hostname}.";
            return true;
        }

        var firstSeenRequest = TofuTrustPrompt.BuildRequest(
            CertificateTrustKind.FirstSeen,
            hostname,
            certificate);

        var acceptFirst = TofuTrustPrompt.Prompt(firstSeenRequest);
        if (!acceptFirst)
        {
            LastMessage = $"Certificate for {hostname} was not trusted.";
            return false;
        }

        lock (_gate)
            _store.Set(hostname, hash);
        LastMessage = $"Pinned certificate for {hostname}.";
        return true;
    }

    private static string? ResolveHostname(object sender)
    {
        if (sender is HttpWebRequest request)
            return request.Address?.Host;
        if (sender is string host)
            return host;
        return null;
    }
}
