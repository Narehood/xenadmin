namespace XcpNgCenter.Shell.Services;

public enum CertificateTrustKind
{
    FirstSeen,
    Changed
}

/// <summary>Data shown in the Avalonia TOFU trust dialog.</summary>
public sealed class CertificateTrustRequest
{
    public required CertificateTrustKind Kind { get; init; }
    public required string Hostname { get; init; }
    public required string Fingerprint { get; init; }
    public string? PreviousFingerprint { get; init; }
    public string Subject { get; init; } = string.Empty;
    public string Issuer { get; init; } = string.Empty;
    public string ValidFrom { get; init; } = string.Empty;
    public string ValidTo { get; init; } = string.Empty;

    public bool HasPreviousPin => !string.IsNullOrWhiteSpace(PreviousFingerprint);

    public string ValidityRange => $"{ValidFrom} — {ValidTo}";

    public string Title => Kind == CertificateTrustKind.Changed
        ? "Certificate changed"
        : "Trust this certificate?";

    public string Summary => Kind == CertificateTrustKind.Changed
        ? $"The TLS certificate for {Hostname} no longer matches the pinned fingerprint. Accepting replaces the pin."
        : $"XCP-ng hosts often use self-signed certificates. Pin the certificate for {Hostname} on first use?";

    public string AcceptLabel => Kind == CertificateTrustKind.Changed
        ? "Trust new certificate"
        : "Trust and pin";
}
