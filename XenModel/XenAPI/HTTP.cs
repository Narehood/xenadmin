/*
 * Copyright (c) Cloud Software Group, Inc.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 *
 *   1) Redistributions of source code must retain the above copyright
 *      notice, this list of conditions and the following disclaimer.
 *
 *   2) Redistributions in binary form must reproduce the above
 *      copyright notice, this list of conditions and the following
 *      disclaimer in the documentation and/or other materials
 *      provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
 * "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
 * LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
 * FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
 * COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
 * INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
 * HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
 * STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
 * OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Serialization;
using System.Threading;
using Tasks = System.Threading.Tasks;
using XenCenterLib;

namespace XenAPI
{
    public partial class HTTP
    {
        #region Exceptions

        [Serializable]
        public class TooManyRedirectsException : Exception
        {
            private readonly int redirect;
            private readonly Uri uri;

            public TooManyRedirectsException(int redirect, Uri uri)
            {
                this.redirect = redirect;
                this.uri = uri;
            }

            public TooManyRedirectsException() : base() { }

            public TooManyRedirectsException(string message) : base(message) { }

            public TooManyRedirectsException(string message, Exception exception) : base(message, exception) { }

            protected TooManyRedirectsException(SerializationInfo info, StreamingContext context)
                : base(info, context)
            {
                redirect = info.GetInt32("redirect");
                uri = (Uri)info.GetValue("uri", typeof(Uri));
            }

            public override void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                if (info == null)
                    throw new ArgumentNullException(nameof(info));

                info.AddValue("redirect", redirect);
                info.AddValue("uri", uri, typeof(Uri));

                base.GetObjectData(info, context);
            }
        }

        [Serializable]
        public class BadServerResponseException : Exception
        {
            public BadServerResponseException() : base() { }

            public BadServerResponseException(string message) : base(message) { }

            public BadServerResponseException(string message, Exception exception) : base(message, exception) { }

            protected BadServerResponseException(SerializationInfo info, StreamingContext context) : base(info, context) { }
        }

        [Serializable]
        public class CancelledException : Exception
        {
            public CancelledException() : base() { }

            public CancelledException(string message) : base(message) { }

            public CancelledException(string message, Exception exception) : base(message, exception) { }

            protected CancelledException(SerializationInfo info, StreamingContext context) : base(info, context) { }
        }

        [Serializable]
        public class ProxyServerAuthenticationException : Exception
        {
            public ProxyServerAuthenticationException() : base() { }

            public ProxyServerAuthenticationException(string message) : base(message) { }

            public ProxyServerAuthenticationException(string message, Exception exception) : base(message, exception) { }

            protected ProxyServerAuthenticationException(SerializationInfo info, StreamingContext context) : base(info, context) { }
        }

        #endregion

        public delegate bool FuncBool();
        public delegate void UpdateProgressDelegate(int percent);
        public delegate void DataCopiedDelegate(long bytes);

        // Size of byte buffer used for GETs and PUTs
        // (not the socket rx buffer)
        public const int BUFFER_SIZE = 32 * 1024;
        public const int MAX_REDIRECTS = 10;

        public const int DEFAULT_HTTPS_PORT = 443;
        private const int NONCE_LENGTH = 16;

        public enum ProxyAuthenticationMethod
        {
            Basic = 0,
            Digest = 1
        }

        /// <summary>
        /// The authentication scheme to use for authenticating to a proxy server.
        /// Defaults to Digest.
        /// </summary>
        public static ProxyAuthenticationMethod CurrentProxyAuthenticationMethod = ProxyAuthenticationMethod.Digest;

        #region Helper functions

        private static void WriteLine(String txt, Stream stream)
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(String.Format("{0}\r\n", txt));
            stream.Write(bytes, 0, bytes.Length);
        }

        // Stream.ReadByte() is used because using StreamReader in its place causes the reading to become stuck,
        // as it seems the Stream object has trouble recognizing the end of the stream. This seems to be a common
        // problem, of which a common solution is to read each byte until an EndOfStreamException is thrown, as is
        // done here.
        private static string ReadLine(Stream stream)
        {
            System.Text.StringBuilder result = new StringBuilder();
            while (true)
            {
                int b = stream.ReadByte();
                if (b == -1)
                    throw new EndOfStreamException();
                char c = Convert.ToChar(b);
                result.Append(c);
                if (c == '\n')
                    return result.ToString();
            }
        }

        private static async Tasks.Task WriteLineAsync(string text, Stream stream, ConnectionAttempt attempt)
        {
            if (attempt?.IsAsynchronous != true)
            {
                WriteLine(text, stream);
                return;
            }

            byte[] bytes = Encoding.ASCII.GetBytes(text + "\r\n");
            await stream.WriteAsync(bytes, 0, bytes.Length, attempt.Token).ConfigureAwait(false);
        }

        private static async Tasks.Task<string> ReadLineAsync(Stream stream, ConnectionAttempt attempt)
        {
            if (attempt?.IsAsynchronous != true)
                return ReadLine(stream);

            var result = new StringBuilder();
            var buffer = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(buffer, 0, 1, attempt.Token).ConfigureAwait(false) == 0)
                    throw new EndOfStreamException();
                char value = (char)buffer[0];
                result.Append(value);
                if (value == '\n')
                    return result.ToString();
            }
        }

        /// <summary>Reads response headers and follows only nonempty, policy-approved 302 locations; proxy callers without a request/policy reject redirects.</summary>
        /// <param name="stream">The transport; replaced only after an approved redirect.</param>
        /// <param name="proxy">Optional configured proxy.</param>
        /// <param name="nodelay">Whether to disable socket coalescing for latency-sensitive traffic.</param>
        /// <param name="timeout_ms">Transport timeout in milliseconds; zero disables the timeout.</param>
        /// <param name="headers">Optional response-header collection used by proxy authentication.</param>
        /// <param name="attempt">Optional startup cancellation/transport owner.</param>
        /// <param name="requestUri">The URI producing this response; required to resolve redirects.</param>
        /// <param name="redirectAllowed">Captured predicate authorizing a candidate redirect recipient.</param>
        /// <param name="redirected">Callback updating the current URI after an approved connection.</param>
        /// <returns>The current stream and whether an approved redirect requires request retransmission.</returns>
        private static async Tasks.Task<(Stream Stream, bool Redirected)> ReadHttpHeadersAsync(Stream stream, IWebProxy proxy, bool nodelay, int timeout_ms, List<string> headers = null, ConnectionAttempt attempt = null,
            Uri requestUri = null, Func<Uri, bool> redirectAllowed = null, Action<Uri> redirected = null)
        {
            // read headers/fields
            string line = await ReadLineAsync(stream, attempt).ConfigureAwait(false), initialLine = line, transferEncodingField = null;
            if (string.IsNullOrEmpty(initialLine)) // sanity check
                return (stream, false);
            if (headers == null)
                headers = new List<string>();
            while (!string.IsNullOrWhiteSpace(line)) // IsNullOrWhiteSpace also checks for empty string
            {
                line = line.TrimEnd('\r', '\n');
                headers.Add(line);
                if (line == "Transfer-Encoding: Chunked")
                    transferEncodingField = line;
                line = await ReadLineAsync(stream, attempt).ConfigureAwait(false);
            }

            // read chunks
            string entityBody = "";
            if (!string.IsNullOrEmpty(transferEncodingField))
            {
                int lastChunkSize = -1;
                do
                {
                    // read chunk size
                    string chunkSizeStr = await ReadLineAsync(stream, attempt).ConfigureAwait(false);
                    chunkSizeStr = chunkSizeStr.TrimEnd('\r', '\n');
                    int chunkSize = 0;
                    int.TryParse(chunkSizeStr, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out chunkSize);

                    // read <chunkSize> number of bytes from the stream
                    int totalNumberOfBytesRead = 0;
                    int numberOfBytesRead;
                    byte[] bytes = new byte[chunkSize];
                    do
                    {
                        numberOfBytesRead = attempt?.IsAsynchronous == true
                            ? await stream.ReadAsync(bytes, totalNumberOfBytesRead, chunkSize - totalNumberOfBytesRead, attempt.Token).ConfigureAwait(false)
                            : stream.Read(bytes, totalNumberOfBytesRead, chunkSize - totalNumberOfBytesRead);
                        totalNumberOfBytesRead += numberOfBytesRead;
                    } while (numberOfBytesRead > 0 && totalNumberOfBytesRead < chunkSize);

                    string str = System.Text.Encoding.ASCII.GetString(bytes);
                    string[] split = str.Split(new string[] {"\r\n"}, StringSplitOptions.RemoveEmptyEntries);
                    headers.AddRange(split);

                    entityBody += str;

                    line = await ReadLineAsync(stream, attempt).ConfigureAwait(false); // empty line in the end of chunk

                    lastChunkSize = chunkSize;
                } while (lastChunkSize != 0);

                entityBody = entityBody.TrimEnd('\r', '\n');
                headers.Add(entityBody); // keep entityBody if it's needed for Digest authentication (when qop="auth-int")
            }
            else
            {
                // todo: handle other transfer types, in case "Transfer-Encoding: Chunked" isn't used
            }

            // handle server response
            int code = getResultCode(initialLine);
            switch (code)
            {
                case 407: // authentication error; caller must handle this case
                case 200:
                    break;

                case 302:
                    var header = headers.FirstOrDefault(h => h.StartsWith("Location:", StringComparison.InvariantCultureIgnoreCase));
                    string url = header == null ? "" : header.Substring(9).Trim();
                    if (requestUri == null || string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(requestUri, url, out var redirect)
                        || redirectAllowed == null || !redirectAllowed(redirect))
                        throw new BadServerResponseException("The server returned an unsafe HTTP redirect.");
                    stream.Close();
                    stream = await ConnectStreamAsync(redirect, proxy, nodelay, timeout_ms, attempt).ConfigureAwait(false);
                    redirected?.Invoke(redirect);
                    return (stream, true); // headers need to be sent again

                default:
                    stream.Close();
                    throw new BadServerResponseException(string.Format("Received error code {0} from the server", initialLine));
            }

            return (stream, false);
        }

        private static int getResultCode(string line)
        {
            string[] bits = line.Split(new char[] { ' ' });
            return (bits.Length < 2 ? 0 : Int32.Parse(bits[1]));
        }

        public static bool UseSSL(Uri uri)
        {
            return uri.Scheme == "https" || uri.Port == DEFAULT_HTTPS_PORT;
        }

        /// <summary>
        /// Computes the MD5 digest required by legacy proxy Digest authentication.
        /// </summary>
        /// <param name="str">The string to hash.</param>
        /// <returns>The compatibility digest as a hexadecimal string.</returns>
        private static string _MD5Hash(string str)
        {
            return ComputeHash(str, "MD5");
        }

        /// <summary>
        /// Computes the SHA-256 digest used by proxy Digest authentication.
        /// </summary>
        /// <param name="str">The string to hash.</param>
        /// <returns>The digest as a hexadecimal string.</returns>
        private static string Sha256Hash(string str)
        {
            return ComputeHash(str, "SHA256");
        }

        private static string ComputeHash(string input, string method)
        {
            if (input == null)
                return null;

            var enc = new UTF8Encoding();
            byte[] bytes = enc.GetBytes(input);

            using (var hasher = CreateHashAlgorithm(method))
            {
                if (hasher != null)
                {
                    byte[] hash = hasher.ComputeHash(bytes);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }

            return null;
        }

        private static HashAlgorithm CreateHashAlgorithm(string method)
        {
            switch (method?.ToUpperInvariant())
            {
                case "MD5":
                    return MD5.Create();
                case "SHA1":
                    return SHA1.Create();
                case "SHA256":
                    return SHA256.Create();
                case "SHA384":
                    return SHA384.Create();
                case "SHA512":
                    return SHA512.Create();
                default:
                    return null;
            }
        }

        private static string GenerateNonce()
        {
            var nonceBytes = new byte[NONCE_LENGTH];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(nonceBytes);
            return Convert.ToBase64String(nonceBytes);
        }

        public static long CopyStream(Stream inStream, Stream outStream,
            DataCopiedDelegate progressDelegate, FuncBool cancellingDelegate)
        {
            long bytesWritten = 0;
            byte[] buffer = new byte[BUFFER_SIZE];
            DateTime lastUpdate = DateTime.Now;

            while (cancellingDelegate == null || !cancellingDelegate())
            {
                int bytesRead = inStream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                    break;
                outStream.Write(buffer, 0, bytesRead);
                bytesWritten += bytesRead;

                if (progressDelegate != null &&
                    DateTime.Now - lastUpdate > TimeSpan.FromMilliseconds(500))
                {
                    progressDelegate(bytesWritten);
                    lastUpdate = DateTime.Now;
                }
            }

            if (cancellingDelegate != null && cancellingDelegate())
                throw new CancelledException();

            if (progressDelegate != null)
                progressDelegate(bytesWritten);

            return bytesWritten;
        }

        /// <summary>
        /// Build a URI from a hostname, a path, and some query arguments
        /// </summary>
        /// <param name="args">An even-length array, alternating argument names and values</param>
        /// <param name="hostname"></param>
        /// <param name="path"></param>
        public static Uri BuildUri(string hostname, string path, params object[] args)
        {
            // The last argument may be an object[] in its own right, in which case we need
            // to flatten the array.
            List<object> flatargs = new List<object>();
            foreach (object arg in args)
            {
                if (arg is IEnumerable<object>)
                    flatargs.AddRange((IEnumerable<object>)arg);
                else
                    flatargs.Add(arg);
            }

            var query = new StringBuilder();
            for (int i = 0; i < flatargs.Count - 1; i += 2)
            {
                if (flatargs[i + 1] == null)//skip null arguments
                    continue;

                if (query.Length != 0)
                    query.Append('&');

                query.Append(flatargs[i]).Append("=");

                if (flatargs[i + 1] is bool)
                    query.Append((bool)flatargs[i + 1] ? "true" : "false");
                else
                    query.Append(Uri.EscapeDataString(flatargs[i + 1].ToString()));
            }

            UriBuilder uri = new UriBuilder
            {
                Scheme = "https",
                Port = DEFAULT_HTTPS_PORT,
                Host = hostname,
                Path = path,
                Query = query.ToString()
            };

            return uri.Uri;
        }

        private static string GetPartOrNull(string str, int partIndex)
        {
            string[] parts = str.Split(new char[] { ' ' }, partIndex + 2, StringSplitOptions.RemoveEmptyEntries);
            return partIndex < parts.Length - 1 ? parts[partIndex] : null;
        }

        #endregion

        private static async Tasks.Task<NetworkStream> ConnectSocketAsync(Uri uri, bool nodelay, int timeoutMs, ConnectionAttempt attempt = null)
        {
            AddressFamily addressFamily = uri.HostNameType == UriHostNameType.IPv6
                                              ? AddressFamily.InterNetworkV6
                                              : AddressFamily.InterNetwork;
            Socket socket =
                new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = nodelay;
            socket.ReceiveTimeout = timeoutMs;
            socket.SendTimeout = timeoutMs;
            try
            {
                attempt?.Track(socket);
                if (attempt?.IsAsynchronous == true)
                {
#if NET8_0_OR_GREATER
                    await socket.ConnectAsync(uri.Host, uri.Port, attempt.Token).ConfigureAwait(false);
#else
                    await socket.ConnectAsync(uri.Host, uri.Port).ConfigureAwait(false);
#endif
                }
                else
                {
#if NET8_0_OR_GREATER
                    if (attempt != null)
                        socket.ConnectAsync(uri.Host, uri.Port, attempt.Token).AsTask().GetAwaiter().GetResult();
                    else
#endif
                        socket.Connect(uri.Host, uri.Port);
                }
                return new NetworkStream(socket, true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>
        /// This function will connect a stream to a uri (host and port),
        /// negotiating proxies and SSL
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="proxy"></param>
        /// <param name="nodelay"></param>
        /// <param name="timeoutMs">Timeout, in ms. 0 for no timeout.</param>
        public static Stream ConnectStream(Uri uri, IWebProxy proxy, bool nodelay, int timeoutMs)
            => ConnectStream(uri, proxy, nodelay, timeoutMs, null);

        public static Stream ConnectStream(Uri uri, IWebProxy proxy, bool nodelay, int timeoutMs, CancellationToken cancellationToken)
        {
            using (var attempt = new ConnectionAttempt(cancellationToken))
            {
                try
                {
                    var stream = ConnectStream(uri, proxy, nodelay, timeoutMs, attempt);
                    cancellationToken.ThrowIfCancellationRequested();
                    return stream;
                }
                catch { cancellationToken.ThrowIfCancellationRequested(); throw; }
            }
        }

        private static Stream ConnectStream(Uri uri, IWebProxy proxy, bool nodelay, int timeoutMs, ConnectionAttempt attempt)
            => ConnectStreamAsync(uri, proxy, nodelay, timeoutMs, attempt).GetAwaiter().GetResult();

        private static async Tasks.Task<Stream> ConnectStreamAsync(Uri uri, IWebProxy proxy, bool nodelay, int timeoutMs, ConnectionAttempt attempt)
        {
            IMockWebProxy mockProxy = proxy as IMockWebProxy;
            if (mockProxy != null)
            {
                var mocked = mockProxy.GetStream(uri);
                attempt?.Track(mocked);
                return mocked;
            }

            Stream stream;
            bool useProxy = proxy != null && !proxy.IsBypassed(uri);

            if (useProxy)
            {
                Uri proxyURI = proxy.GetProxy(uri);
                stream = await ConnectSocketAsync(proxyURI, nodelay, timeoutMs, attempt).ConfigureAwait(false);
            }
            else
            {
                stream = await ConnectSocketAsync(uri, nodelay, timeoutMs, attempt).ConfigureAwait(false);
            }

            try
            {
                if (useProxy)
                {
                    string line = string.Format("CONNECT {0}:{1} HTTP/1.0", uri.Host, uri.Port);
                    await WriteLineAsync(line, stream, attempt).ConfigureAwait(false);
                    await WriteLineAsync("", stream, attempt).ConfigureAwait(false);

                    List<string> initialResponse = new List<string>();
                    await ReadHttpHeadersAsync(stream, proxy, nodelay, timeoutMs, initialResponse, attempt).ConfigureAwait(false);

                    if (initialResponse.Any(field => field.StartsWith("Proxy-Authenticate:", StringComparison.InvariantCultureIgnoreCase))
                        && initialResponse.Any(field => field.StartsWith("Proxy-Connection: Close", StringComparison.InvariantCultureIgnoreCase)))
                    {
                        stream.Close();
                        stream = await ConnectSocketAsync(proxy.GetProxy(uri), nodelay, timeoutMs, attempt).ConfigureAwait(false);
                    }
                    await AuthenticateProxyAsync(stream, uri, proxy, nodelay, timeoutMs, initialResponse, line, attempt).ConfigureAwait(false);
                }

                if (UseSSL(uri))
                {
                    // XCP-ng hosts commonly present self-signed certs. When the app has installed
                    // its TOFU callback (SSL.ValidateServerCertificate), that path accepts and pins
                    // them; we only fall back to strict chain validation when no app callback exists.
                    SslStream sslStream = new SslStream(stream, false,
                        (sender, certificate, chain, sslPolicyErrors) =>
                        {
#pragma warning disable SYSLIB0014 // Both clients register their TOFU policy here; this is an explicit callback lookup.
                            var appCallback = ServicePointManager.ServerCertificateValidationCallback;
#pragma warning restore SYSLIB0014
                            if (appCallback != null)
                                return appCallback(uri.Host, certificate, chain, sslPolicyErrors);

                            return sslPolicyErrors == SslPolicyErrors.None;
                        }, null);
                    stream = sslStream;
                    attempt?.Track(stream);
                    if (attempt?.IsAsynchronous == true)
                        await sslStream.AuthenticateAsClientAsync(uri.Host, null, TlsPolicy.AllowedSslProtocols, true).ConfigureAwait(false);
                    else
                        sslStream.AuthenticateAsClient(uri.Host, null, TlsPolicy.AllowedSslProtocols, true);
                }

                return stream;
            }
            catch
            {
                stream.Close();
                throw;
            }
        }

        private static async Tasks.Task AuthenticateProxyAsync(Stream stream, Uri uri, IWebProxy proxy, bool nodelay, int timeoutMs, List<string> initialResponse, string header, ConnectionAttempt attempt = null)
        {
            // perform authentication only if proxy requires it
            List<string> fields = initialResponse.FindAll(str => str.StartsWith("Proxy-Authenticate:", StringComparison.InvariantCultureIgnoreCase));
            if (fields.Count <= 0)
                return;

            if (proxy.Credentials == null)
                throw new BadServerResponseException(string.Format("Received error code {0} from the server", initialResponse[0]));

            NetworkCredential credentials = proxy.Credentials.GetCredential(uri, null);

            string basicField = fields.Find(str => str.StartsWith("Proxy-Authenticate: Basic", StringComparison.InvariantCultureIgnoreCase));
            var digestFields = fields.FindAll(str => str.StartsWith("Proxy-Authenticate: Digest", StringComparison.InvariantCultureIgnoreCase));

            if (CurrentProxyAuthenticationMethod == ProxyAuthenticationMethod.Basic)
            {
                if (string.IsNullOrEmpty(basicField))
                    throw new ProxyServerAuthenticationException("Basic authentication scheme is not supported/enabled by the proxy server.");

                string authenticationFieldReply = string.Format("Proxy-Authorization: Basic {0}",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.UserName + ":" + credentials.Password)));
                await WriteLineAsync(header, stream, attempt).ConfigureAwait(false);
                await WriteLineAsync(authenticationFieldReply, stream, attempt).ConfigureAwait(false);
                await WriteLineAsync("", stream, attempt).ConfigureAwait(false);
            }
            else if (CurrentProxyAuthenticationMethod == ProxyAuthenticationMethod.Digest)
            {
                var digestField = digestFields.FirstOrDefault(f => f.ToLowerInvariant().Contains("sha-256")) ?? digestFields.FirstOrDefault();

                if (string.IsNullOrEmpty(digestField))
                    throw new ProxyServerAuthenticationException("Digest authentication scheme is not supported/enabled by the proxy server.");

                string authenticationFieldReply = string.Format(
                    "Proxy-Authorization: Digest username=\"{0}\", uri=\"{1}:{2}\"",
                    credentials.UserName, uri.Host, uri.Port);

                int len = "Proxy-Authorization: Digest".Length;
                string directiveString = digestField.Substring(len, digestField.Length - len);
                string[] directives = directiveString.Split(new[] {", ", "\""}, StringSplitOptions.RemoveEmptyEntries);

                string algorithm = null; // optional
                string opaque = null;    // optional
                string qop = null;       // optional
                string realm = null;
                string nonce = null;

                for (int i = 0; i < directives.Length; ++i)
                {
                    // RFC 7616 sends algorithm as a token; retain quoted legacy challenges too.
                    const string algorithmPrefix = "algorithm=";
                    if (directives[i].StartsWith(algorithmPrefix, StringComparison.OrdinalIgnoreCase)
                        && directives[i].Length > algorithmPrefix.Length)
                    {
                        algorithm = directives[i].Substring(algorithmPrefix.Length);
                        authenticationFieldReply += string.Format(", algorithm={0}", algorithm);
                        continue;
                    }
                    switch (directives[i].ToLowerInvariant())
                    {
                        case "stale=":
                            if (directives[++i].ToLowerInvariant() == "true")
                                throw new ProxyServerAuthenticationException("Stale nonce in Digest authentication attempt.");
                            break;
                        case "realm=":
                            authenticationFieldReply += string.Format(", realm=\"{0}\"", directives[++i]);
                            realm = directives[i];
                            break;
                        case "nonce=":
                            authenticationFieldReply += string.Format(", nonce=\"{0}\"", directives[++i]);
                            nonce = directives[i];
                            break;
                        case "opaque=":
                            authenticationFieldReply += string.Format(", opaque=\"{0}\"", directives[++i]);
                            opaque = directives[i];
                            break;
                        case "algorithm=":
                            authenticationFieldReply += string.Format(", algorithm={0}", directives[++i]); //unquoted; see RFC7616-3.4
                            algorithm = directives[i];
                            break;
                        case "qop=":
                            var qops = directives[++i].Split(',');
                            if (qops.Length > 0)
                            {
                                qop = qops.FirstOrDefault(q => q.ToLowerInvariant() == "auth") ??
                                      qops.FirstOrDefault(q => q.ToLowerInvariant() == "auth-int");
                                if (qop == null)
                                      throw new ProxyServerAuthenticationException(
                                          "Digest authentication's quality-of-protection directive is not supported.");
                                authenticationFieldReply += string.Format(", qop={0}", qop); //unquoted; see RFC7616-3.4
                            }
                            break;
                    }
                }

                string clientNonce = GenerateNonce();
                if (qop != null)
                    authenticationFieldReply += string.Format(", cnonce=\"{0}\"", clientNonce);

                string nonceCount = "00000001"; // todo: track nonces and their corresponding nonce counts
                if (qop != null)
                    authenticationFieldReply += string.Format(", nc={0}", nonceCount); //unquoted; see RFC7616-3.4

                Func<string, string> algFunc;
                var scratch1 = string.Join(":", credentials.UserName, realm, credentials.Password);
                string HA1;
                algorithm = algorithm ?? "md5";

                switch (algorithm.ToLowerInvariant())
                {
                    case "sha-256-sess":
                        algFunc = Sha256Hash;
                        HA1 = algFunc(string.Join(":", algFunc(scratch1), nonce, clientNonce));
                        break;
                    case "md5-sess":
                        algFunc = _MD5Hash;
                        HA1 = algFunc(string.Join(":", algFunc(scratch1), nonce, clientNonce));
                        break;
                    case "sha-256":
                        algFunc = Sha256Hash;
                        HA1 = algFunc(scratch1);
                        break;
                    case "md5":
                    default:
                        algFunc = _MD5Hash;
                        HA1 = algFunc(scratch1);
                        break;
                }

                var scratch2 = string.Join(":", GetPartOrNull(header, 0) ?? "CONNECT", uri.Host, uri.Port);
                string HA2 = qop == null || qop.ToLowerInvariant() == "auth"
                    ? algFunc(scratch2)
                    : algFunc(string.Join(":", scratch2, algFunc(initialResponse[initialResponse.Count - 1])));

                string[] array3 = qop == null
                    ? new[] {HA1, nonce, HA2}
                    : new[] {HA1, nonce, nonceCount, clientNonce, qop, HA2};
                var response = algFunc(string.Join(":", array3));

                authenticationFieldReply += string.Format(", response=\"{0}\"", response);

                await WriteLineAsync(header, stream, attempt).ConfigureAwait(false);
                await WriteLineAsync(authenticationFieldReply, stream, attempt).ConfigureAwait(false);
                await WriteLineAsync("", stream, attempt).ConfigureAwait(false);
            }
            else
            {
                string authType = GetPartOrNull(fields[0], 1);
                throw new ProxyServerAuthenticationException(
                    string.Format("Proxy server's {0} authentication method is not supported.", authType ?? "chosen"));
            }

            // handle authentication attempt response
            List<string> authenticatedResponse = new List<string>();
            await ReadHttpHeadersAsync(stream, proxy, nodelay, timeoutMs, authenticatedResponse, attempt).ConfigureAwait(false);
            if (authenticatedResponse.Count == 0)
                throw new BadServerResponseException("No response from the proxy server after authentication attempt.");

            switch (getResultCode(authenticatedResponse[0]))
            {
                case 200:
                    break;
                case 407:
                    throw new ProxyServerAuthenticationException("Proxy server denied access due to wrong credentials.");
                default:
                    throw new BadServerResponseException(string.Format(
                        "Received error code {0} from the server", authenticatedResponse[0]));
            }
        }


        private static Stream DoHttp(Uri uri, IWebProxy proxy, bool nodelay, int timeout_ms, params string[] headers)
            => DoHttp(uri, proxy, nodelay, timeout_ms, null, headers);

        /// <summary>Sends a request and follows bounded redirects within the captured recipient policy, rebuilding path and Host for every accepted destination.</summary>
        /// <param name="uri">The initial destination URI.</param>
        /// <param name="proxy">Optional configured proxy.</param>
        /// <param name="nodelay">Whether to disable socket coalescing for latency-sensitive traffic.</param>
        /// <param name="timeout_ms">Transport timeout in milliseconds; zero disables the timeout.</param>
        /// <param name="attempt">Optional startup cancellation/transport owner.</param>
        /// <param name="headers">Request headers, or a response-header collection when reading a response.</param>
        /// <returns>The response or tunnel stream; the caller owns its disposal.</returns>
        private static Stream DoHttp(Uri uri, IWebProxy proxy, bool nodelay, int timeout_ms, ConnectionAttempt attempt, params string[] headers)
            => DoHttpAsync(uri, proxy, nodelay, timeout_ms, attempt, headers).GetAwaiter().GetResult();

        private static async Tasks.Task<Stream> DoHttpAsync(Uri uri, IWebProxy proxy, bool nodelay, int timeout_ms, ConnectionAttempt attempt, params string[] headers)
        {
            var allowed = CreateRedirectPolicy(uri, headers);
            var currentUri = uri;
            string method = headers[0].Split(' ')[0];
            Stream stream = await ConnectStreamAsync(uri, proxy, nodelay, timeout_ms, attempt).ConfigureAwait(false);

            try
            {
                int redirects = 0;

                while (true)
                {
                    if (redirects > MAX_REDIRECTS)
                        throw new TooManyRedirectsException(redirects, uri);

                    redirects++;

                    await WriteLineAsync(string.Format("{0} {1} HTTP/1.0", method, currentUri.PathAndQuery), stream, attempt).ConfigureAwait(false);
                    foreach (string header in headers.Skip(1))
                        await WriteLineAsync(header.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)
                            ? "Host: " + currentUri.Authority : header, stream, attempt).ConfigureAwait(false);
                    await WriteLineAsync("", stream, attempt).ConfigureAwait(false);

                    if (attempt?.IsAsynchronous == true)
                        await stream.FlushAsync(attempt.Token).ConfigureAwait(false);
                    else
                        stream.Flush();
                    var response = await ReadHttpHeadersAsync(stream, proxy, nodelay, timeout_ms, attempt: attempt,
                        requestUri: currentUri, redirectAllowed: allowed, redirected: target => currentUri = target).ConfigureAwait(false);
                    stream = response.Stream;
                    if (!response.Redirected)
                        return stream;
                }
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        /// <summary>Captures same-authority and owning-token pool recipients, requiring HTTPS and the original port for pool-member redirects.</summary>
        /// <param name="origin">The initial request URI.</param>
        /// <param name="headers">Request headers, or a response-header collection when reading a response.</param>
        /// <returns>A recipient predicate based on the original authority and captured authenticated inventory.</returns>
        private static Func<Uri, bool> CreateRedirectPolicy(Uri origin, string[] headers)
        {
            // A pool can redirect a transfer to another authenticated member.
            // Snapshot the inventory of the connection owning this exact token;
            // a Location header must never establish a new credential recipient.
            string token = QueryStringUtility.ParseQueryString(origin.Query)["session_id"];
            const string cookiePrefix = "Cookie: session_id=";
            var cookie = headers.FirstOrDefault(h => h.StartsWith(cookiePrefix, StringComparison.OrdinalIgnoreCase));
            if (cookie != null) token = cookie.Substring(cookiePrefix.Length);
            var originHost = CanonicalRedirectHost(origin.IdnHost);
            var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(token))
                foreach (var connection in XenAdmin.ConnectionsManager.XenConnectionsCopy)
                {
                    if (!connection.IsConnected || connection.Session?.opaque_ref != token
                        || connection.Port != origin.Port)
                        continue;
                    var hosts = connection.Cache.Hosts.Select(h => CanonicalRedirectHost(h.address)).Where(h => h != null).ToArray();
                    var connectionHost = CanonicalRedirectHost(connection.Hostname);
                    if (!string.Equals(originHost, connectionHost, StringComparison.OrdinalIgnoreCase)
                        && !hosts.Contains(originHost, StringComparer.OrdinalIgnoreCase))
                        continue;
                    members.UnionWith(hosts);
                    if (connectionHost != null) members.Add(connectionHost);
                }
            return target =>
                originHost != null && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps)
                && string.IsNullOrEmpty(target.UserInfo) && string.IsNullOrEmpty(target.Fragment)
                && (!UseSSL(origin) || target.Scheme == Uri.UriSchemeHttps)
                && (string.Equals(origin.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(originHost, CanonicalRedirectHost(target.IdnHost), StringComparison.OrdinalIgnoreCase) && origin.Port == target.Port
                    || target.Scheme == Uri.UriSchemeHttps && target.Port == origin.Port && members.Contains(CanonicalRedirectHost(target.IdnHost)));
        }

        /// <summary>Normalizes IP literals and IDN DNS names for origin, connection and inventory comparisons; invalid host strings return null.</summary>
        /// <param name="host">Bare DNS name or IP literal, optionally bracketed for IPv6.</param>
        /// <returns>Canonical IP/ASCII DNS spelling, or null for an invalid host.</returns>
        private static string CanonicalRedirectHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return null;
            if (IPAddress.TryParse(host, out var address)) return address.ToString();
            try
            {
                string ascii = new System.Globalization.IdnMapping().GetAscii(host.TrimEnd('.'));
                return Uri.CheckHostName(ascii) == UriHostNameType.Dns ? ascii.ToLowerInvariant() : null;
            }
            catch (ArgumentException) { return null; }
        }

        /// <summary>Opens an authenticated CONNECT stream with constrained redirects; the cancellation overload aborts all startup phases.</summary>
        /// <param name="uri">The initial destination URI.</param>
        /// <param name="proxy">Optional configured proxy.</param>
        /// <param name="session">Authenticated XenAPI session token sent in the CONNECT cookie.</param>
        /// <param name="timeoutMs">Startup transport timeout in milliseconds.</param>
        /// <returns>The authenticated tunnel stream; the caller owns its disposal.</returns>
        public static Stream HttpConnectStream(Uri uri, IWebProxy proxy, String session, int timeoutMs)
        {
            return DoHttp(uri, proxy, true, timeoutMs,
                string.Format("CONNECT {0} HTTP/1.0", uri.PathAndQuery),
                string.Format("Host: {0}", uri.Host),
                string.Format("Cookie: session_id={0}", session));
        }

        /// <summary>Cancels every startup phase, including socket, proxy and TLS negotiation.</summary>
        public static Stream HttpConnectStream(Uri uri, IWebProxy proxy, string session, int timeoutMs, CancellationToken cancellationToken)
        {
            using (var attempt = new ConnectionAttempt(cancellationToken))
            {
                try
                {
                    var stream = DoHttp(uri, proxy, true, timeoutMs, attempt,
                        string.Format("CONNECT {0} HTTP/1.0", uri.PathAndQuery),
                        string.Format("Host: {0}", uri.Host),
                        string.Format("Cookie: session_id={0}", session));
                    cancellationToken.ThrowIfCancellationRequested();
                    return stream;
                }
                catch
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }
            }
        }

        private sealed class ConnectionAttempt : IDisposable
        {
            private readonly object gate = new object();
            private readonly CancellationToken token;
            private readonly CancellationTokenRegistration registration;
            private IDisposable current;

            public ConnectionAttempt(CancellationToken token, bool asynchronous = false)
            {
                this.token = token;
                IsAsynchronous = asynchronous;
                token.ThrowIfCancellationRequested();
                registration = token.Register(Abort);
            }

            public CancellationToken Token => token;

            public bool IsAsynchronous { get; }

            public void Track(IDisposable transport)
            {
                lock (gate)
                {
                    if (token.IsCancellationRequested)
                    {
                        transport.Dispose();
                        token.ThrowIfCancellationRequested();
                    }
                    current = transport;
                }
            }

            private void Abort()
            {
                IDisposable transport;
                lock (gate) transport = current;
                try { transport?.Dispose(); } catch (ObjectDisposedException) { }
            }

            public void Dispose() => registration.Dispose();
        }

        /// <summary>
        /// Adds HTTP PUT headers returning the stream ready for use
        /// </summary>
        public static Stream HttpPutStream(Uri uri, IWebProxy proxy, long contentLength, int timeoutMs)
        {
            return DoHttp(uri, proxy, false, timeoutMs,
                string.Format("PUT {0} HTTP/1.0", uri.PathAndQuery),
                string.Format("Host: {0}", uri.Host),
                string.Format("Content-Length: {0}", contentLength));
        }

        /// <summary>
        /// Adds HTTP GET headers returning the stream ready for use
        /// </summary>
        public static Stream HttpGetStream(Uri uri, IWebProxy proxy, int timeoutMs)
        {
            return DoHttp(uri, proxy, false, timeoutMs,
                string.Format("GET {0} HTTP/1.0", uri.PathAndQuery),
                string.Format("Host: {0}", uri.Host));
        }

        public static Stream HttpGetStream(Uri uri, IWebProxy proxy, int timeoutMs, CancellationToken cancellationToken)
        {
            using (var attempt = new ConnectionAttempt(cancellationToken))
            {
                try
                {
                    var stream = DoHttp(uri, proxy, false, timeoutMs, attempt,
                        string.Format("GET {0} HTTP/1.0", uri.PathAndQuery), string.Format("Host: {0}", uri.Host));
                    cancellationToken.ThrowIfCancellationRequested();
                    return stream;
                }
                catch { cancellationToken.ThrowIfCancellationRequested(); throw; }
            }
        }

        /// <summary>Opens a GET stream using asynchronous TCP, proxy, TLS and header I/O with a bounded setup deadline.</summary>
        /// <param name="uri">The initial request URI.</param>
        /// <param name="proxy">The configured proxy, or null for a direct connection.</param>
        /// <param name="timeoutMs">Setup deadline and returned transport timeouts, in milliseconds; zero disables timeouts.</param>
        /// <param name="cancellationToken">Cancels setup; the caller controls cancellation of the returned body stream.</param>
        /// <returns>The response body stream, owned by the caller.</returns>
        public static async Tasks.Task<Stream> HttpGetStreamAsync(Uri uri, IWebProxy proxy, int timeoutMs, CancellationToken cancellationToken)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (timeoutMs > 0)
                    deadline.CancelAfter(timeoutMs);
                using (var attempt = new ConnectionAttempt(deadline.Token, asynchronous: true))
                {
                    try
                    {
                        var stream = await DoHttpAsync(uri, proxy, false, timeoutMs, attempt,
                            string.Format("GET {0} HTTP/1.0", uri.PathAndQuery), string.Format("Host: {0}", uri.Host)).ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        return stream;
                    }
                    catch (Exception error)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (deadline.IsCancellationRequested)
                            throw new IOException("HTTP request setup timed out.", error);
                        throw;
                    }
                }
            }
        }


        /// <summary>
        /// A general HTTP PUT method, with delegates for progress and cancelling. May throw various exceptions.
        /// </summary>
        /// <param name="progressDelegate">Delegate called periodically (500ms) with percent complete</param>
        /// <param name="cancellingDelegate">Delegate called periodically to see if need to cancel</param>
        /// <param name="uri">URI to PUT to</param>
        /// <param name="proxy">A proxy to handle the HTTP connection</param>
        /// <param name="path">Path to file to put</param>
        /// <param name="timeoutMs">Timeout for the connection in ms. 0 for no timeout.</param>
        public static void Put(UpdateProgressDelegate progressDelegate, FuncBool cancellingDelegate,
            Uri uri, IWebProxy proxy, string path, int timeoutMs)
        {
            using (Stream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read),
                requestStream = HttpPutStream(uri, proxy, fileStream.Length, timeoutMs))
            {
                long len = fileStream.Length;
                DataCopiedDelegate dataCopiedDelegate = delegate(long bytes)
                    {
                        if (progressDelegate != null && len > 0)
                            progressDelegate((int)((bytes * 100) / len));
                    };

                CopyStream(fileStream, requestStream, dataCopiedDelegate, cancellingDelegate);
            }
        }

        /// <summary>
        /// A general HTTP GET method, with delegates for progress and cancelling. May throw various exceptions.
        /// </summary>
        /// <param name="dataCopiedDelegate">Delegate called periodically (500 ms) with the number of bytes transferred</param>
        /// <param name="cancellingDelegate">Delegate called periodically to see if need to cancel</param>
        /// <param name="uri">URI to GET from</param>
        /// <param name="proxy">A proxy to handle the HTTP connection</param>
        /// <param name="path">Path to file to receive the data</param>
        /// <param name="timeoutMs">Timeout for the connection in ms. 0 for no timeout.</param>
        public static void Get(DataCopiedDelegate dataCopiedDelegate, FuncBool cancellingDelegate,
            Uri uri, IWebProxy proxy, string path, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException(nameof(path));

            var tmpFile = Path.GetTempFileName();

            if (Path.GetPathRoot(path) != Path.GetPathRoot(tmpFile))
            {
                //CA-365905: if the target path is under a root different from
                //the temp file, use instead a temp file under the target root,
                //otherwise there may not be enough space for the download

                var dir = Path.GetDirectoryName(path);
                if (dir == null) //path is root directory
                    throw new ArgumentException(nameof(path));

                tmpFile = Path.Combine(dir, Path.GetRandomFileName());
                File.Delete(tmpFile);
            }

            try
            {
                using (Stream fileStream = new FileStream(tmpFile, FileMode.Create, FileAccess.Write, FileShare.None),
                       downloadStream = HttpGetStream(uri, proxy, timeoutMs))
                {
                    CopyStream(downloadStream, fileStream, dataCopiedDelegate, cancellingDelegate);
                    fileStream.Flush();
                }

                File.Delete(path);
                MoveFileWithRetry(tmpFile, path);
            }
            finally
            {
                File.Delete(tmpFile);
            }
        }

        private const int FILE_MOVE_MAX_RETRIES = 5;
        private const int FILE_MOVE_SLEEP_BETWEEN_RETRIES = 100;

        /// <summary>
        /// Move a file, retrying a few times with a short sleep between retries.
        /// If it still fails after these retries, then throw the error.
        /// </summary>
        public static void MoveFileWithRetry(string sourceFileName, string destFileName)
        {
            int retriesRemaining = FILE_MOVE_MAX_RETRIES;
            do
            {
                try
                {
                    File.Move(sourceFileName, destFileName);
                    break;
                }
                catch (IOException)
                {
                    if (retriesRemaining <= 0)
                        throw;
                    System.Threading.Thread.Sleep(FILE_MOVE_SLEEP_BETWEEN_RETRIES);
                }
            } while (retriesRemaining-- > 0);
        }
    }
}
