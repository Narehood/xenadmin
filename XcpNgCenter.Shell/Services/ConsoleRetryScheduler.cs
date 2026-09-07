namespace XcpNgCenter.Shell.Services;

/// <summary>One cancellable delayed console retry, including queued UI callbacks.</summary>
internal sealed class ConsoleRetryScheduler : IDisposable
{
    private readonly Action<Action> _dispatch;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _pending;
    private bool _disposed;

    public ConsoleRetryScheduler(Action<Action> dispatch,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _dispatch = dispatch;
        _delay = delay ?? Task.Delay;
    }

    public bool IsPending => _pending != null;

    public void Schedule(Action retry)
    {
        if (_disposed || IsPending)
            return;
        var pending = _pending = new CancellationTokenSource();
        _ = WaitAsync(pending, retry);
    }

    private async Task WaitAsync(CancellationTokenSource pending, Action retry)
    {
        try
        {
            await _delay(TimeSpan.FromMilliseconds(1500), pending.Token).ConfigureAwait(false);
            _dispatch(() =>
            {
                if (_disposed || !ReferenceEquals(_pending, pending))
                    return;
                _pending = null;
                pending.Dispose();
                retry();
            });
        }
        catch (OperationCanceledException)
        {
            // Selection changes, successful connections, and shutdown cancel retries.
        }
    }

    public void Cancel()
    {
        var pending = _pending;
        _pending = null;
        if (pending == null)
            return;
        pending.Cancel();
        pending.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }
}
