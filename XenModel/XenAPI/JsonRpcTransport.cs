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
using System.IO;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XenCenterLib;
#if NET8_0_OR_GREATER
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
#endif

namespace XenAPI
{
    public partial class JsonRpcClient
    {
        private readonly object transportGate = new object();
        private bool disposed;
#if NET8_0_OR_GREATER
        private HttpTransport transport;
#endif

        private async System.Threading.Tasks.Task<T> SendAsync<T>(string body, Func<TextReader, T> read, CancellationToken cancellationToken)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (Timeout != System.Threading.Timeout.Infinite) deadline.CancelAfter(Timeout);
                try
                {
#if NET8_0_OR_GREATER
                    HttpTransport lease;
                    lock (transportGate)
                    {
                        if (disposed) throw new ObjectDisposedException(nameof(JsonRpcClient));
                        var settings = new TransportSettings(this);
                        if (transport == null || !transport.Settings.Matches(settings))
                        {
                            transport?.Retire();
                            transport = new HttpTransport(settings);
                        }
                        lease = transport;
                        lease.Acquire();
                    }
                    try
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Post, JsonRpcUrl))
                        {
                            request.Version = ProtocolVersion ?? HttpVersion.Version11;
                            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                            request.Headers.Accept.ParseAdd("application/json");
                            if (!string.IsNullOrEmpty(UserAgent)) request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                            request.Headers.ExpectContinue = Expect100Continue;
                            request.Headers.ConnectionClose = !KeepAlive;
                            request.Content = new StringContent(body, new UTF8Encoding(false), "application/json");
                            using (var response = await lease.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                            {
                                if (response.StatusCode != HttpStatusCode.OK)
                                    throw new WebException("JSON-RPC HTTP response: " + (int)response.StatusCode, null,
                                        WebExceptionStatus.ProtocolError, new RpcHttpErrorResponse(response.StatusCode));
                                using (var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
                                using (deadline.Token.Register(stream.Dispose))
                                using (var reader = new StreamReader(stream))
                                    return read(reader);
                            }
                        }
                    }
                    finally { lease.Release(); }
#else
                    lock (transportGate) if (disposed) throw new ObjectDisposedException(nameof(JsonRpcClient));
                    var request = (HttpWebRequest)WebRequest.Create(JsonRpcUrl);
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.Accept = "application/json";
                    request.Timeout = Timeout;
                    request.Proxy = WebProxy;
                    request.KeepAlive = KeepAlive;
                    request.UserAgent = UserAgent;
                    request.ConnectionGroupName = ConnectionGroupName;
                    request.ProtocolVersion = ProtocolVersion ?? request.ProtocolVersion;
                    request.ServicePoint.Expect100Continue = Expect100Continue;
                    request.AllowAutoRedirect = AllowAutoRedirect;
                    request.PreAuthenticate = PreAuthenticate;
                    request.AllowWriteStreamBuffering = true;
                    request.CookieContainer = Cookies ?? new CookieContainer();
                    request.ServerCertificateValidationCallback = ServerCertificateValidationCallback ?? ServicePointManager.ServerCertificateValidationCallback;
                    using (deadline.Token.Register(request.Abort))
                    {
                        using (var stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                        {
                            var bytes = Encoding.UTF8.GetBytes(body);
                            await stream.WriteAsync(bytes, 0, bytes.Length, deadline.Token).ConfigureAwait(false);
                        }
                        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                        {
                            if (response.StatusCode != HttpStatusCode.OK)
                                throw new WebException("JSON-RPC HTTP response: " + (int)response.StatusCode, null,
                                    WebExceptionStatus.ProtocolError, new RpcHttpErrorResponse(response.StatusCode));
                            using (var stream = response.GetResponseStream())
                            using (var reader = new StreamReader(stream))
                                return read(reader);
                        }
                    }
#endif
                }
                catch (Exception error) when (deadline.IsCancellationRequested)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException("JSON-RPC request cancelled.", error, cancellationToken);
                    throw new WebException("JSON-RPC request timed out.", error, WebExceptionStatus.Timeout, null);
                }
#if NET8_0_OR_GREATER
                catch (HttpRequestException error)
                {
                    var status = error.HttpRequestError switch
                    {
                        HttpRequestError.NameResolutionError => WebExceptionStatus.NameResolutionFailure,
                        HttpRequestError.ConnectionError => WebExceptionStatus.ConnectFailure,
                        HttpRequestError.SecureConnectionError => WebExceptionStatus.TrustFailure,
                        HttpRequestError.HttpProtocolError => WebExceptionStatus.ServerProtocolViolation,
                        _ => WebExceptionStatus.ReceiveFailure
                    };
                    throw new WebException("JSON-RPC transport failed.", error, status, null);
                }
#endif
            }
        }

        public void Dispose()
        {
            lock (transportGate)
            {
                disposed = true;
#if NET8_0_OR_GREATER
                transport?.Retire();
                transport = null;
#endif
            }
            GC.SuppressFinalize(this);
        }

        ~JsonRpcClient() { Dispose(); }

        public static HttpStatusCode? GetHttpStatus(WebException error)
            => error.Response is HttpWebResponse legacy ? legacy.StatusCode
                : (error.Response as RpcHttpErrorResponse)?.StatusCode;

        private sealed class RpcHttpErrorResponse : WebResponse
        {
            public RpcHttpErrorResponse(HttpStatusCode statusCode) { StatusCode = statusCode; }
            public HttpStatusCode StatusCode { get; }
        }

#if NET8_0_OR_GREATER
        private sealed class TransportSettings
        {
            public readonly IWebProxy Proxy;
            public readonly CookieContainer Cookies;
            public readonly RemoteCertificateValidationCallback Certificate;
            public readonly bool Redirects;
            public readonly bool PreAuthenticate;
            public readonly string Group;

            public TransportSettings(JsonRpcClient owner)
            {
                Proxy = owner.WebProxy;
                Cookies = owner.Cookies;
#pragma warning disable SYSLIB0014 // Read the application's explicit policy; HttpClient does not inherit it.
                Certificate = owner.ServerCertificateValidationCallback ?? ServicePointManager.ServerCertificateValidationCallback;
#pragma warning restore SYSLIB0014
                Redirects = owner.AllowAutoRedirect;
                PreAuthenticate = owner.PreAuthenticate;
                Group = owner.ConnectionGroupName;
            }

            public bool Matches(TransportSettings other) => ReferenceEquals(Proxy, other.Proxy)
                && ReferenceEquals(Cookies, other.Cookies) && Equals(Certificate, other.Certificate)
                && Redirects == other.Redirects && PreAuthenticate == other.PreAuthenticate && Group == other.Group;
        }

        private sealed class HttpTransport
        {
            private readonly object gate = new object();
            private int users;
            private bool retired;
            public readonly TransportSettings Settings;
            public readonly HttpClient Client;

            public HttpTransport(TransportSettings settings)
            {
                Settings = settings;
                var handler = new SocketsHttpHandler
                {
                    UseProxy = settings.Proxy != null,
                    Proxy = settings.Proxy,
                    UseCookies = settings.Cookies != null,
                    CookieContainer = settings.Cookies ?? new CookieContainer(),
                    AllowAutoRedirect = settings.Redirects,
                    PreAuthenticate = settings.PreAuthenticate,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        EnabledSslProtocols = TlsPolicy.AllowedSslProtocols,
                        CertificateRevocationCheckMode = X509RevocationMode.Online,
                        RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                        {
                            var hostname = (sender as SslStream)?.TargetHostName;
                            if (string.IsNullOrEmpty(hostname)) return false;
                            return settings.Certificate != null
                                ? settings.Certificate(hostname, certificate, chain, errors)
                                : certificate != null && errors == SslPolicyErrors.None;
                        }
                    }
                };
                Client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            }

            public void Acquire() { lock (gate) users++; }
            public void Release() { lock (gate) { if (--users == 0 && retired) Client.Dispose(); } }
            public void Retire() { lock (gate) { retired = true; if (users == 0) Client.Dispose(); } }
        }
#endif
    }
}
