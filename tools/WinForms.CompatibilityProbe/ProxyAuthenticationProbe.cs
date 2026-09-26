using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

// Only loopback sockets and synthetic credentials are used. The proxy terminates
// requests locally; it never resolves or connects to the example destination.
internal static class ProxyAuthenticationProbe
{
    public static int Run()
    {
        var http = Assembly.Load("XenModel").GetType("XenAPI.HTTP", throwOnError: true)!;
        var setting = http.GetField("CurrentProxyAuthenticationMethod")!;
        var original = setting.GetValue(null);
        try
        {
            foreach (var selected in new[] { "Basic", "Digest" })
            foreach (var challenge in new[] { "Basic", "Digest", "Basic+Digest" })
            foreach (var customTunnel in new[] { false, true })
            {
                setting.SetValue(null, Enum.Parse(setting.FieldType, selected));
                Check(http, selected, challenge, customTunnel).GetAwaiter().GetResult();
            }
            Console.WriteLine($"Passed 12 proxy authentication cases on .NET {Environment.Version}; no credential values logged.");
            return 0;
        }
        finally
        {
            setting.SetValue(null, original);
        }
    }

    private static async Task Check(Type http, string selected, string challenge, bool customTunnel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxyUri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        var proxy = new WebProxy(proxyUri, false)
        {
            Credentials = new NetworkCredential("synthetic-probe-user", "synthetic-probe-password")
        };
        var schemes = new List<string>();
        var server = Serve(listener, challenge, schemes, timeout.Token);
        Exception? failure = null;
        try
        {
            await Task.Run(() =>
            {
                var destination = new Uri("http://proxy-probe.invalid/test");
                if (customTunnel)
                {
                    using var stream = (Stream)http.GetMethod("ConnectStream")!
                        .Invoke(null, [destination, proxy, true, 3000])!;
                }
                else
                {
#pragma warning disable SYSLIB0014 // Exercise the production XenAPI HTTP-handler boundary.
                    var request = (HttpWebRequest)WebRequest.Create(destination);
#pragma warning restore SYSLIB0014
                    request.Proxy = proxy;
                    request.Timeout = 3000;
                    using var response = (HttpWebResponse)request.GetResponse();
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new InvalidOperationException("Loopback proxy did not accept the request.");
                }
            }, timeout.Token);
        }
        catch (Exception error)
        {
            failure = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
        }
        finally
        {
            await timeout.CancelAsync();
            await server;
        }

        var shouldReject = customTunnel && !challenge.Contains(selected, StringComparison.Ordinal);
        var expected = customTunnel ? selected : challenge.Contains("Digest", StringComparison.Ordinal) ? "Digest" : "Basic";
        if (shouldReject)
        {
            if (failure?.GetType().Name != "ProxyServerAuthenticationException" || schemes.Count != 0)
                throw new InvalidOperationException($"Tunnel {selected} must reject challenge {challenge} without authenticating.");
        }
        else if (failure != null || schemes.Count != 1 || schemes[0] != expected)
        {
            throw new InvalidOperationException($"{(customTunnel ? "Tunnel" : "HttpWebRequest")} setting={selected}, challenge={challenge}: expected {expected}, observed {string.Join(',', schemes)}, failure={failure?.GetType().Name}.");
        }
        Console.WriteLine($"{(customTunnel ? "Tunnel" : "HttpWebRequest")} setting={selected}, challenge={challenge}: {(shouldReject ? "rejected" : schemes.Single())}");
    }

    private static async Task Serve(TcpListener listener, string challenge, List<string> schemes, CancellationToken token)
    {
        try
        {
            while (true)
            {
                using var peer = await listener.AcceptTcpClientAsync(token);
                await using var stream = peer.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var firstLine = await reader.ReadLineAsync(token);
                if (firstLine == null) continue;
                string? scheme = null;
                while (await reader.ReadLineAsync(token) is { Length: > 0 } line)
                {
                    if (line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                        scheme = line["Proxy-Authorization:".Length..].TrimStart().Split(' ', 2)[0];
                }
                string response;
                if (scheme != null)
                {
                    schemes.Add(scheme);
                    response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                }
                else
                {
                    var fields = new StringBuilder();
                    if (challenge.Contains("Basic", StringComparison.Ordinal))
                        fields.Append("Proxy-Authenticate: Basic realm=\"synthetic-proxy\"\r\n");
                    if (challenge.Contains("Digest", StringComparison.Ordinal))
                        fields.Append("Proxy-Authenticate: Digest realm=\"synthetic-proxy\", nonce=\"synthetic-nonce\", qop=\"auth\", algorithm=\"MD5\"\r\n");
                    response = $"HTTP/1.1 407 Proxy Authentication Required\r\n{fields}Content-Length: 0\r\nConnection: close\r\nProxy-Connection: close\r\n\r\n";
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
