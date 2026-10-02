/* Copyright (c) Cloud Software Group, Inc. 
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
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using AsyncTask = System.Threading.Tasks.Task;
using XenAPI;
using XenAdmin.Core;

namespace XenAdmin.Network
{
    /// <summary>
    /// A per-connection thread that periodically calls host.get_servertime.  This is used to tell us the skew between the clocks on the client
    /// and the server, but more importantly, to act as a connection heartbeat.
    /// xapi will return from event.next every so often even if there are no new events, to act as the connection heartbeat in the other direction.
    /// </summary>
    public class Heartbeat
    {
        private const int HeartbeatInterval = 15000;

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IXenConnection connection;
        private Session session = null;

        private readonly object lifecycleGate = new object();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private bool started;
        private bool stopped;
        private AsyncTask completion = AsyncTask.CompletedTask;

        /// <summary>Finishes after polling and owned transport cleanup have stopped.</summary>
        public AsyncTask Completion { get { lock (lifecycleGate) return completion; } }

        /// <summary>
        /// If true, the heartbeat has already failed once, and we are giving the server a final second chance.
        /// </summary>
        private bool retrying = false;

        private readonly int connectionTimeout;

        public Heartbeat(IXenConnection connection, int connectionTimeout)
        {
            this.connection = connection;
            this.connectionTimeout = connectionTimeout;
        }

        /// <summary>
        /// Should only be called once.
        /// </summary>
        public void Start()
        {
            lock (lifecycleGate)
            {
                if (started || stopped) return;
                started = true;
                completion = AsyncTask.Run(() => HeartbeatLoop(cancellation.Token));
            }
        }

        public void Stop()
        {
            lock (lifecycleGate)
            {
                if (stopped) return;
                stopped = true;
                cancellation.Cancel();
                if (!started) cancellation.Dispose();
            }
        }

        public AsyncTask StopAsync()
        {
            Stop();
            return Completion;
        }

        private async AsyncTask HeartbeatLoop(CancellationToken token)
        {
            log.DebugFormat("Heartbeat thread for connection to {0} started", connection.Hostname);

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await DoHeartbeat(token).ConfigureAwait(false);
                    await AsyncTask.Delay(HeartbeatInterval, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { log.Error("Heartbeat worker failed", error); }
            finally
            {
                DropSession();
                lock (lifecycleGate)
                {
                    stopped = true;
                    cancellation.Dispose();
                }
                log.DebugFormat("Heartbeat thread for connection to {0} terminated", connection.Hostname);
            }
        }

        private readonly string heartbeatConnectionGroupName = Guid.NewGuid().ToString(); 

        private async AsyncTask DoHeartbeat(CancellationToken token)
        {
            if (!connection.IsConnected)
                return;

            try
            {
                if (session == null)
                {
                    // Try to get a new session, but only give the server one chance (otherwise we get the default 3x timeout)
                    session = connection.DuplicateSession(connectionTimeout < 5000 ? 5000 : connectionTimeout);
                    session.ConnectionGroupName = heartbeatConnectionGroupName; // this will force the Heartbeat session onto its own set of TCP streams (see CA-108676)
                }

                await GetServerTime(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                // Now that we've successfully received a heartbeat, reset our 'second chance' for the server to timeout
                if (retrying)
                    log.DebugFormat("Heartbeat for {0} has come back", session.Url);
                retrying = false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            catch (TargetInvocationException exn)
            {
                if (exn.InnerException is SocketException ||
                    exn.InnerException is WebException)
                {
                    log.Debug(exn.Message);
                }
                else
                {
                    log.Error(exn);
                }
                HandleConnectionLoss(exn.InnerException ?? exn);
            }
            catch (WebException exn)
            {
                log.Error(exn);
                if (JsonRpcClient.GetHttpStatus(exn) == HttpStatusCode.ProxyAuthenticationRequired) // work-around for CA-214653
                {
                    if (session == null)
                        log.Debug("Heartbeat has failed due to null session; closing the main connection");
                    else if (session.Credentials == null)
                        log.DebugFormat("Heartbeat for {0} has failed due to missing credentials; closing the main connection", session.Url);
                    else
                        log.DebugFormat("Heartbeat for {0} has failed due to incorrect credentials; closing the main connection", session.Url);

                    connection.Interrupt();
                    DropSession();
                }
                else
                {
                    HandleConnectionLoss(exn);
                }
            }
            catch (Exception exn)
            {
                log.Error(exn);
                HandleConnectionLoss(exn);
            }
        }

        private async AsyncTask GetServerTime(CancellationToken token)
        {
            Host coordinator = Helpers.GetCoordinator(connection);
            if (coordinator == null)
                return;
            DateTime t = await session.JsonRpcClient.HostGetServerTimeAsync(
                session.opaque_ref, coordinator.opaque_ref, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            connection.ServerTimeOffset = DateTime.UtcNow - t;
        }

        private void HandleConnectionLoss(Exception error = null)
        {
            // Hard connection failures (coordinator reboot / refused / reset) skip the
            // second chance so the UI does not keep painting hosts as online.
            // Transient timeouts still get one retry to avoid flapping on brief blips.
            var hardFail = IsHardConnectionFailure(error);

            // We retry once, as the server is entitled to drop persistent connections from time to time.
            // After that, we assume that the server's gone away.
            // Note that this doubles the effective timeout before we decide the server has died.
            if ((retrying || hardFail) && !connection.ExpectDisruption)
            {
                log.DebugFormat("Heartbeat for {0} has failed{1}; closing the main connection",
                                session == null ? "null" : session.Url,
                                hardFail && !retrying ? " hard" : " for the second time");
                connection.Interrupt();
                DropSession();
            }
            else
            {
                log.DebugFormat("Heartbeat for {0} has failed; retrying",
                                session == null ? "null" : session.Url);
                retrying = true;
            }
        }

        private static bool IsHardConnectionFailure(Exception error)
        {
            for (var ex = error; ex != null; ex = ex.InnerException)
            {
                if (ex is SocketException)
                    return true;

                if (ex is WebException web)
                {
                    switch (web.Status)
                    {
                        case WebExceptionStatus.ConnectFailure:
                        case WebExceptionStatus.ConnectionClosed:
                        case WebExceptionStatus.NameResolutionFailure:
                        case WebExceptionStatus.ProxyNameResolutionFailure:
                        case WebExceptionStatus.KeepAliveFailure:
                        case WebExceptionStatus.ReceiveFailure:
                        case WebExceptionStatus.SendFailure:
                        case WebExceptionStatus.PipelineFailure:
                            return true;
                    }
                }
            }

            return false;
        }

        /// Drop the session so that we'll get a new one the next time.
        /// This is used when handling exceptions.
        private void DropSession()
        {
            Session s = session;
            session = null;
            // DuplicateSession shares the pool token. Release only this worker's
            // transport; logging it out would invalidate the main connection.
            s?.JsonRpcClient?.Dispose();
        }
    }
}
