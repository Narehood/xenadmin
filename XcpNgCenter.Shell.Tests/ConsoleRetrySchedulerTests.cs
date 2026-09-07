using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class ConsoleRetrySchedulerTests
{
    [Fact]
    public async Task RapidFailuresAlwaysRetainNextDelayedRetry()
    {
        var fixture = new ControlledScheduler();
        using var scheduler = fixture.Scheduler;
        var attempts = 0;
        for (var i = 1; i <= 3; i++)
        {
            scheduler.Schedule(() => attempts++);
            scheduler.Schedule(() => throw new Exception("Duplicate retry"));
            Assert.True(scheduler.IsPending);
            Assert.Equal(i - 1, attempts);
            await fixture.FireAsync();
            Assert.Equal(i, attempts);
            Assert.False(scheduler.IsPending);
            // A rapid failure schedules the next attempt; no cache event is needed.
        }
        Assert.All(fixture.Delays, delay => Assert.Equal(TimeSpan.FromMilliseconds(1500), delay));
    }

    [Fact]
    public async Task CancelInvalidatesAlreadyQueuedUiCallback()
    {
        var fixture = new ControlledScheduler();
        using var scheduler = fixture.Scheduler;
        var attempts = 0;
        scheduler.Schedule(() => attempts++);
        var callback = await fixture.ReadCallbackAsync();
        scheduler.Cancel();
        callback();
        Assert.Equal(0, attempts);
        Assert.False(scheduler.IsPending);

        scheduler.Schedule(() => attempts++);
        await fixture.FireAsync();
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void DisposeCancelsWaitingAndDisallowsFurtherRetries()
    {
        var fixture = new ControlledScheduler();
        fixture.Scheduler.Schedule(() => throw new Exception("Disposed retry"));
        fixture.Scheduler.Dispose();
        Assert.True(fixture.LastToken.IsCancellationRequested);
        fixture.Scheduler.Schedule(() => throw new Exception("New retry after disposal"));
        Assert.False(fixture.Scheduler.IsPending);
    }

    private sealed class ControlledScheduler
    {
        private TaskCompletionSource _delay = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<Action> _callback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<TimeSpan> Delays { get; } = new();
        public CancellationToken LastToken { get; private set; }
        public ConsoleRetryScheduler Scheduler { get; }

        public ControlledScheduler()
        {
            Scheduler = new ConsoleRetryScheduler(action => _callback.SetResult(action), (delay, token) =>
            {
                Delays.Add(delay);
                LastToken = token;
                var currentDelay = _delay;
                token.Register(() => currentDelay.TrySetCanceled(token));
                return currentDelay.Task;
            });
        }

        public async Task<Action> ReadCallbackAsync()
        {
            _delay.SetResult();
            var callback = await _callback.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _delay = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _callback = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return callback;
        }

        public async Task FireAsync() => (await ReadCallbackAsync())();
    }
}
