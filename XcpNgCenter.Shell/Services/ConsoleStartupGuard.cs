namespace XcpNgCenter.Shell.Services;

/// <summary>A deadline for startup only; a connected, idle display has no read deadline.</summary>
internal sealed class ConsoleStartupGuard : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation;
    private readonly CancellationToken token;
    private readonly Timer timer;
    private readonly CancellationTokenRegistration registration;
    private Action? abort;
    private bool completed;
    private bool timedOut;
    private string stage = "opening the console tunnel";

    public ConsoleStartupGuard(CancellationToken parent, TimeSpan timeout)
    {
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent);
        token = cancellation.Token;
        registration = token.Register(Abort);
        timer = new Timer(_ => Expire(), null, timeout, Timeout.InfiniteTimeSpan);
        ShellPerformanceDiagnostics.Log.ConsoleStartup("tunnel", "started");
    }

    public CancellationToken Token => token;
    public string FailureMessage
    {
        get { lock (gate) return timedOut ? $"Console connection timed out while {stage}." : "Console connection cancelled."; }
    }

    public void AttachTransport(Stream stream)
    {
        lock (gate)
        {
            abort = stream.Dispose;
            stage = "waiting for the console handshake";
            ShellPerformanceDiagnostics.Log.ConsoleStartup("handshake", "started");
            if (!cancellation.IsCancellationRequested) return;
        }
        Abort();
        Token.ThrowIfCancellationRequested();
    }

    public bool Complete()
    {
        lock (gate)
        {
            if (completed || token.IsCancellationRequested || timedOut) return false;
            completed = true;
            ShellPerformanceDiagnostics.Log.ConsoleStartup("handshake", "connected");
            timer.Change(Timeout.Infinite, Timeout.Infinite);
            return true;
        }
    }

    private void Expire()
    {
        lock (gate)
        {
            if (completed) return;
            timedOut = true;
            ShellPerformanceDiagnostics.Log.ConsoleStartup(stage, "timeout");
        }
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void End()
    {
        lock (gate)
        {
            if (completed) return;
            completed = true;
            timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void Abort()
    {
        Action? close;
        lock (gate) close = abort;
        try { close?.Invoke(); }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    public void Dispose()
    {
        lock (gate) completed = true;
        timer.Dispose();
        registration.Dispose();
        cancellation.Dispose();
    }
}
