using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class ShellUpdateMachineTrustTests
{
    [Theory]
    [InlineData(SslPolicyErrors.None)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    public void PrivateRootCannotAuthenticatePrivilegedMetadataEvenWhenPresentedChainTrustsIt(SslPolicyErrors errors)
    {
        if (!OperatingSystem.IsWindows()) return;
        var (root, leaf) = CreatePrivateCertificateChain();
        using (root)
        using (leaf)
        using (var presentedChain = new X509Chain())
        {
            // This trust exists only in the supplied chain object. No certificate
            // store is opened or changed, and no network connection is made.
            presentedChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            presentedChain.ChainPolicy.CustomTrustStore.Add(root);
            presentedChain.ChainPolicy.ExtraStore.Add(root);
            presentedChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            presentedChain.ChainPolicy.DisableCertificateDownloads = true;
            Assert.True(presentedChain.Build(leaf));

            Assert.False(ShellGitHubUpdateChecker.ValidateMachineTrustCertificate(leaf, presentedChain, errors));
            // Validation borrows the supplied certificates and must leave them usable.
            Assert.True(presentedChain.Build(leaf));
        }
    }

    [Fact]
    public void SelfSignedCertificateIsRejectedDespiteDefaultValidationSuccess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = CreateServerRequest(key);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var presentedChain = new X509Chain();
        presentedChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        presentedChain.ChainPolicy.DisableCertificateDownloads = true;

        Assert.False(ShellGitHubUpdateChecker.ValidateMachineTrustCertificate(certificate, presentedChain, SslPolicyErrors.None));
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors)]
    public void TransportNameAndAvailabilityErrorsRemainFatal(SslPolicyErrors errors)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = CreateServerRequest(key).CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.False(ShellGitHubUpdateChecker.ValidateMachineTrustCertificate(certificate, null, errors));
    }

    [Theory]
    [InlineData(SslPolicyErrors.None)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable)]
    public void MissingCertificateIsRejected(SslPolicyErrors errors)
    {
        Assert.False(ShellGitHubUpdateChecker.ValidateMachineTrustCertificate(null, null, errors));
    }

    private static (X509Certificate2 Root, X509Certificate2 Leaf) CreatePrivateCertificateChain()
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Temporary updater test CA " + Guid.NewGuid().ToString("N"),
            rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var now = DateTimeOffset.UtcNow;
        var root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        try
        {
            using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var leaf = CreateServerRequest(leafKey).Create(root, now.AddHours(-1), now.AddHours(1),
                RandomNumberGenerator.GetBytes(16));
            return (root, leaf);
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    private static CertificateRequest CreateServerRequest(ECDsa key)
    {
        var request = new CertificateRequest("CN=api.github.com", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("api.github.com");
        request.CertificateExtensions.Add(names.Build());
        return request;
    }
}
