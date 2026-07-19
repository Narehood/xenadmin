/* Copyright (c) XCP-ng
 *
 * Redistribution and use in source and binary forms,
 * with or without modification, are permitted provided
 * that the following conditions are met:
 *
 * *   Redistributions of source code must retain the above
 *     copyright notice, this list of conditions and the
 *     following disclaimer.
 * *   Redistributions in binary form must reproduce the above
 *     copyright notice, this list of conditions and the
 *     following disclaimer in the documentation and/or other
 *     materials provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF
 * SUCH DAMAGE.
 */

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace XenAdmin.Actions.Updates
{
    /// <summary>
    /// Shared HttpClient-based file download used by update actions (replaces WebClient).
    /// </summary>
    internal sealed class HttpFileDownloader : IDisposable
    {
        private const int BufferSize = 81920;

        private readonly object _gate = new object();
        private CancellationTokenSource _cts;
        private bool _disposed;

        public void Cancel()
        {
            lock (_gate)
            {
                try
                {
                    _cts?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // ignored
                }
            }
        }

        /// <summary>
        /// Downloads <paramref name="uri"/> to <paramref name="outputPath"/>.
        /// Invokes <paramref name="onProgress"/> with (bytesReceived, totalBytesOrNull).
        /// Throws <see cref="OperationCanceledException"/> when cancelled via <see cref="Cancel"/>.
        /// For unsuccessful HTTP statuses, throws <see cref="HttpRequestException"/> with
        /// <c>Data["StatusCode"]</c> set to the <see cref="HttpStatusCode"/>.
        /// </summary>
        public void Download(
            Uri uri,
            string outputPath,
            IWebProxy proxy,
            string authorizationHeader,
            bool noCache,
            Action<long, long?> onProgress)
        {
            if (uri == null)
                throw new ArgumentNullException(nameof(uri));
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentNullException(nameof(outputPath));

            var cts = new CancellationTokenSource();
            lock (_gate)
            {
                _cts?.Dispose();
                _cts = cts;
            }

            try
            {
                if (uri.IsFile)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var length = new FileInfo(uri.LocalPath).Length;
                    File.Copy(uri.LocalPath, outputPath, true);
                    onProgress?.Invoke(length, length);
                    return;
                }

                DownloadHttp(uri, outputPath, proxy, authorizationHeader, noCache, onProgress, cts.Token)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_cts, cts))
                        _cts = null;
                }

                cts.Dispose();
            }
        }

        private static async Task DownloadHttp(
            Uri uri,
            string outputPath,
            IWebProxy proxy,
            string authorizationHeader,
            bool noCache,
            Action<long, long?> onProgress,
            CancellationToken cancellationToken)
        {
            using (var handler = CreateHandler(proxy))
            using (var client = new HttpClient(handler))
            {
                ApplyNoCache(client, noCache);

                if (!string.IsNullOrEmpty(authorizationHeader))
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorizationHeader);

                using (var response = await client
                           .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                           .ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var ex = new HttpRequestException(
                            $"Response status code does not indicate success: {(int)response.StatusCode} ({response.StatusCode}).");
                        ex.Data["StatusCode"] = response.StatusCode;
                        throw ex;
                    }

                    var total = response.Content.Headers.ContentLength;

                    using (var remote = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var local = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
                    {
                        var buffer = new byte[BufferSize];
                        long received = 0;
                        int read;
                        while ((read = await remote.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await local.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                            received += read;
                            onProgress?.Invoke(received, total);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Downloads response bytes (for small payloads such as update XML).
        /// </summary>
        public static byte[] DownloadBytes(Uri uri, IWebProxy proxy, string userAgent, bool noCache)
        {
            return DownloadBytesAsync(uri, proxy, userAgent, noCache)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }

        private static async Task<byte[]> DownloadBytesAsync(Uri uri, IWebProxy proxy, string userAgent, bool noCache)
        {
            using (var handler = CreateHandler(proxy))
            using (var client = new HttpClient(handler))
            {
                ApplyNoCache(client, noCache);

                if (!string.IsNullOrEmpty(userAgent))
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);

                using (var response = await client.GetAsync(uri).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
        }

        private static void ApplyNoCache(HttpClient client, bool noCache)
        {
            if (!noCache)
                return;

            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            client.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");
        }

        private static HttpClientHandler CreateHandler(IWebProxy proxy)
        {
            var handler = new HttpClientHandler();
            if (proxy != null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            else
            {
                handler.UseProxy = false;
            }

            return handler;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            lock (_gate)
            {
                _cts?.Dispose();
                _cts = null;
            }
        }
    }
}
