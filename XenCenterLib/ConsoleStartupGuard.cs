using System;
using System.IO;
using System.Threading;

namespace XenCenterLib
{
    /// <summary>A deadline for startup only; connected, idle displays have no read deadline.</summary>
    public sealed class ConsoleStartupGuard : IDisposable
    {
        private readonly object gate = new object();
        private readonly CancellationTokenSource cancellation;
        private readonly CancellationToken token;
        private readonly Timer timer;
        private readonly CancellationTokenRegistration registration;
        private readonly Action<string, string> report;
        private Action abort;
        private bool completed;
        private bool disposed;
        private bool timedOut;
        private string stage = "opening the console tunnel";

        public ConsoleStartupGuard(CancellationToken parent, TimeSpan timeout, Action<string, string> report = null)
        {
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            this.report = report;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent);
            token = cancellation.Token;
            registration = token.Register(Abort);
            timer = new Timer(_ => Expire(), null, timeout, Timeout.InfiniteTimeSpan);
            report?.Invoke("tunnel", "started");
        }

        public CancellationToken Token => token;
        public string FailureMessage
        {
            get { lock (gate) return timedOut ? $"Console connection timed out while {stage}." : "Console connection cancelled."; }
        }

        public void AttachTransport(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            lock (gate)
            {
                if (!disposed)
                {
                    abort = stream.Dispose;
                    stage = "waiting for the console handshake";
                    report?.Invoke("handshake", "started");
                    if (!token.IsCancellationRequested) return;
                }
            }
            stream.Dispose();
            token.ThrowIfCancellationRequested();
            throw new ObjectDisposedException(nameof(ConsoleStartupGuard));
        }

        public bool Complete()
        {
            lock (gate)
            {
                if (completed || disposed || token.IsCancellationRequested || timedOut) return false;
                completed = true;
                timer.Change(Timeout.Infinite, Timeout.Infinite);
                report?.Invoke("handshake", "connected");
                return true;
            }
        }

        private void Expire()
        {
            lock (gate)
            {
                if (completed || disposed) return;
                timedOut = true;
                report?.Invoke(stage, "timeout");
            }
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void End()
        {
            lock (gate)
            {
                if (completed || disposed) return;
                completed = true;
                timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        private void Abort()
        {
            Action close;
            lock (gate) close = abort;
            try { close?.Invoke(); }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = completed = true;
                timer.Dispose();
            }
            registration.Dispose();
            cancellation.Dispose();
        }
    }
}
