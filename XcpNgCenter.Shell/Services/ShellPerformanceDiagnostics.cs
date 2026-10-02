using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace XcpNgCenter.Shell.Services;

/// <summary>Opt-in performance events; payloads contain counts/timings, never object identities.</summary>
[EventSource(Name = "XcpNgCenter-Shell-Performance")]
internal sealed class ShellPerformanceDiagnostics : EventSource
{
    public static readonly ShellPerformanceDiagnostics Log = new();

    [NonEvent]
    public static Measurement Measure(string operation) => Log.IsEnabled() ? new(operation) : default;

    [Event(1, Level = EventLevel.Informational)]
    public void Operation(string operation, double milliseconds, long allocatedBytes)
    {
        if (IsEnabled()) WriteEvent(1, operation, milliseconds, allocatedBytes);
    }

    [Event(2, Level = EventLevel.Informational)]
    public void InventoryBatch(int notifications, int connections)
    {
        if (IsEnabled()) WriteEvent(2, notifications, connections);
    }

    [Event(3, Level = EventLevel.Informational)]
    public void ConsoleFrame(int width, int height)
    {
        if (IsEnabled()) WriteEvent(3, width, height);
    }

    [Event(4, Level = EventLevel.Informational)]
    public void ConsoleStartup(string stage, string outcome)
    {
        if (IsEnabled()) WriteEvent(4, stage, outcome);
    }

    public readonly struct Measurement : IDisposable
    {
        private readonly string? operation;
        private readonly long started;
        private readonly long allocated;
        private readonly int thread;

        internal Measurement(string operation)
        {
            this.operation = operation;
            started = Stopwatch.GetTimestamp();
            allocated = GC.GetAllocatedBytesForCurrentThread();
            thread = Environment.CurrentManagedThreadId;
        }

        public void Dispose()
        {
            if (operation == null) return;
            // These scopes measure synchronous UI/worker phases. A cross-thread
            // scope reports unknown allocation rather than a misleading delta.
            Log.Operation(operation, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                thread == Environment.CurrentManagedThreadId ? GC.GetAllocatedBytesForCurrentThread() - allocated : -1);
        }
    }
}
