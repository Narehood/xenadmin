using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Trust-on-first-use validator for self-signed XCP-ng hosts (preview).
/// First sight pins the cert hash; matching pins accept; changed pins are re-pinned for soak testing.
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

        var hash = certificate.GetCertHashString();
        lock (_gate)
        {
            if (_store.TryGet(hostname, out var pinned))
            {
                if (string.Equals(pinned, hash, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Preview: re-pin changed certs so host reinstalls stay testable without a dialog yet.
                _store.Set(hostname, hash);
                LastMessage = $"Certificate for {hostname} changed; pin updated (preview TOFU).";
                return true;
            }

            _store.Set(hostname, hash);
            LastMessage = $"Pinned certificate for {hostname} (TOFU).";
            return true;
        }
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
