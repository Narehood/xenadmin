using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class CertificatePinTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shell-pin-tests-" + Guid.NewGuid().ToString("N"));
    private readonly X509Certificate2 _first = CreateCertificate();
    private readonly X509Certificate2 _replacement = CreateCertificate();

    [Theory]
    [InlineData(SslPolicyErrors.None)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    public void ShellChangedPinAlwaysRequiresConfiguredDecision(SslPolicyErrors errors)
    {
        var store = new TofuCertificateStore(Path.Combine(_root, "pins.json"));
        store.Set("synthetic.local", _first.GetCertHashString());
        CertificateTrustRequest? prompt = null;
        var settings = new ShellAppSettings(Path.Combine(_root, "settings.json"));
        var validator = new TofuCertificateValidator(store, settings, request => { prompt = request; return false; });
        Assert.False(validator.Validate("SYNTHETIC.local", _replacement, null, errors));
        Assert.Equal(CertificateTrustKind.Changed, prompt!.Kind);
        Assert.True(store.TryGet("synthetic.local", out var pinned));
        Assert.Equal(_first.GetCertHashString(), pinned);
        Assert.True(validator.Validate("synthetic.local", _first, null, errors));

        validator = new TofuCertificateValidator(store, settings, _ => true);
        Assert.True(validator.Validate("synthetic.local", _replacement, null, errors));
        Assert.True(store.TryGet("synthetic.local", out pinned));
        Assert.Equal(_replacement.GetCertHashString(), pinned);
    }

    [Fact]
    public void ShellUnpinnedCaTrustedCertificateUsesOsTrustWithoutCreatingPin()
    {
        var store = new TofuCertificateStore(Path.Combine(_root, "pins.json"));
        var validator = new TofuCertificateValidator(store,
            new ShellAppSettings(Path.Combine(_root, "settings.json")), _ => throw new Exception("Unexpected prompt"));
        Assert.True(validator.Validate("synthetic.local", _first, null, SslPolicyErrors.None));
        Assert.Equal(0, store.Count);
        Assert.False(validator.Validate("synthetic.local", null, null, SslPolicyErrors.None));
    }

    [Theory]
    [InlineData(SslPolicyErrors.None)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    public void WinFormsActualCallbackChecksExistingPinBeforeCaTrust(SslPolicyErrors errors)
    {
        // The production SSL.cs is compile-linked; stubs replace only UI/settings
        // dependencies so this executes its real branch ordering without RDP interop.
        XenAdmin.Settings.KnownServers.Clear();
        XenAdmin.Settings.KnownServers["synthetic.local"] = _first.GetCertHashString();
        XenAdmin.Properties.Settings.Default.WarnChangedCertificate = true;
        XenAdmin.Program.MainWindow = null;
        Assert.False(XenAdmin.Network.SSL.ValidateServerCertificate("SYNTHETIC.local", _replacement, null!, errors));
        Assert.True(XenAdmin.Network.SSL.ValidateServerCertificate("synthetic.local", _first, null!, errors));
        Assert.Equal(_first.GetCertHashString(), XenAdmin.Settings.KnownServers["synthetic.local"]);
        XenAdmin.Settings.KnownServers.Clear();
        Assert.True(XenAdmin.Network.SSL.ValidateServerCertificate("synthetic.local", _first, null!, SslPolicyErrors.None));
        Assert.Empty(XenAdmin.Settings.KnownServers);
        Assert.False(XenAdmin.Network.SSL.ValidateServerCertificate("synthetic.local", null!, null!, SslPolicyErrors.None));
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=synthetic.local", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    public void Dispose()
    {
        _first.Dispose();
        _replacement.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
