using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using XcpNgCenter.Shell.Services;
using XenAPI;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.Tests;

#pragma warning disable SYSLIB0014 // Exercise the existing SDK-to-application callback contract.

// HTTP.ConnectStream uses this process-wide callback in both UI clients.
[CollectionDefinition("HTTP certificate policy", DisableParallelization = true)]
public sealed class HttpCertificatePolicyCollection;

[Collection("HTTP certificate policy")]
public sealed class HttpTransportCertificateTests : IDisposable
{
    private readonly RemoteCertificateValidationCallback? _previousCallback =
        ServicePointManager.ServerCertificateValidationCallback;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "http-tls-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConnectStream_WithoutApplicationPolicyRejectsUntrustedCertificate()
    {
        using var certificate = CreateCertificate();
        ServicePointManager.ServerCertificateValidationCallback = null;

        var result = await ExchangeAsync(certificate);

        Assert.IsType<AuthenticationException>(result.ClientError);
        Assert.False(result.ReceivedClientData);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectStream_HonorsApplicationDecisionAndPassesDestinationHostname(bool accept)
    {
        using var certificate = CreateCertificate();
        object? senderSeen = null;
        string? fingerprintSeen = null;
        SslPolicyErrors? errorsSeen = null;
        ServicePointManager.ServerCertificateValidationCallback = (sender, presented, _, errors) =>
        {
            senderSeen = sender;
            fingerprintSeen = presented?.GetCertHashString();
            errorsSeen = errors;
            return accept;
        };

        var result = await ExchangeAsync(certificate);

        Assert.Equal("127.0.0.1", senderSeen);
        Assert.Equal(certificate.GetCertHashString(), fingerprintSeen);
        Assert.NotNull(errorsSeen);
        Assert.NotEqual(SslPolicyErrors.None, errorsSeen.Value & SslPolicyErrors.RemoteCertificateChainErrors);
        Assert.Equal(accept, result.ReceivedClientData);
        if (accept)
            Assert.Null(result.ClientError);
        else
            Assert.IsType<AuthenticationException>(result.ClientError);
    }

    [Fact]
    public async Task ConnectStream_RejectsChangedPinBeforeSendingApplicationData()
    {
        using var original = CreateCertificate();
        using var replacement = CreateCertificate();
        var store = new TofuCertificateStore(Path.Combine(_root, "pins.json"));
        store.Set("127.0.0.1", original.GetCertHashString());
        var settings = new ShellAppSettings(Path.Combine(_root, "settings.json"))
        {
            WarnChangedCertificates = true
        };
        CertificateTrustRequest? promptSeen = null;
        var validator = new TofuCertificateValidator(store, settings, request =>
        {
            promptSeen = request;
            return false;
        });
        ServicePointManager.ServerCertificateValidationCallback = validator.Validate;

        var trusted = await ExchangeAsync(original);
        var changed = await ExchangeAsync(replacement);

        Assert.Null(trusted.ClientError);
        Assert.True(trusted.ReceivedClientData);
        Assert.IsType<AuthenticationException>(changed.ClientError);
        Assert.False(changed.ReceivedClientData);
        Assert.NotNull(promptSeen);
        Assert.Equal(CertificateTrustKind.Changed, promptSeen.Kind);
        Assert.True(store.TryGet("127.0.0.1", out var fingerprint));
        Assert.Equal(original.GetCertHashString(), fingerprint);
    }

    private static async Task<(Exception? ClientError, bool ReceivedClientData)> ExchangeAsync(X509Certificate2 certificate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var tls = new SslStream(peer.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12
                }, timeout.Token);
                var received = new byte[1];
                if (await tls.ReadAsync(received, timeout.Token) != 1)
                    return false;
                Assert.Equal(0x2a, received[0]);
                await tls.WriteAsync(new byte[] { 0x2b }, timeout.Token);
                return true;
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException)
            {
                // TLS peers may observe certificate rejection during the handshake or first read.
                return false;
            }
        }, timeout.Token);

        var error = await Record.ExceptionAsync(() => Task.Run(() =>
        {
            using var stream = HTTP.ConnectStream(new Uri($"https://127.0.0.1:{port}/"), null, false, 5000);
            var tls = Assert.IsType<SslStream>(stream);
            Assert.True(tls.IsAuthenticated && tls.IsEncrypted);
            stream.WriteByte(0x2a);
            Assert.Equal(0x2b, stream.ReadByte());
        }, timeout.Token));

        return (error, await server);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=loopback-sdk-test", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Schannel needs a usable key container for server credentials; a freshly generated
        // ephemeral RSA key can fail before the client receives any certificate on Windows.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    public void Dispose()
    {
        ServicePointManager.ServerCertificateValidationCallback = _previousCallback;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

#pragma warning restore SYSLIB0014
