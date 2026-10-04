using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace XcpNgCenter.Shell.Services;

/// <summary>Opt-in bounded capture of this application's identifier-free performance provider.</summary>
internal sealed class PerformanceCaptureSession : EventListener, IAsyncDisposable
{
    private readonly Channel<CaptureEvent> queue;
    private readonly Task writer;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly Timer timer;
    private readonly object captureGate = new();
    private int ready;
    private int stopped;
    private long observed;
    private long dropped;
    private double durationMilliseconds;
    private Exception? writeError;
    private const long MaxFileBytes = 128 * 1024 * 1024;

    internal PerformanceCaptureSession(string path, TimeSpan duration, int capacity = 4096,
        Func<string, Stream>? openOutput = null)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Use an absolute performance capture path.", nameof(path));
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(duration));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        var output = openOutput != null ? openOutput(path) : new FileStream(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
        queue = Channel.CreateBounded<CaptureEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        writer = Task.Run(() => Write(output));
        Volatile.Write(ref ready, 1);
        foreach (var source in EventSource.GetSources()) OnEventSourceCreated(source);
        timer = new Timer(_ => Stop(), null, duration, Timeout.InfiniteTimeSpan);
    }

    internal static PerformanceCaptureSession? FromArguments(ref string[] args, Func<string, Stream>? openOutput = null)
    {
        const string pathOption = "--performance-capture";
        const string secondsOption = "--performance-capture-seconds";
        string? path = null;
        var seconds = 180;
        var hasSeconds = false;
        var remaining = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (option is not (pathOption or secondsOption)) { remaining.Add(option); continue; }
            if (++i == args.Length) throw new ArgumentException($"Missing value for {option}.");
            if (option == pathOption)
            {
                if (path != null) throw new ArgumentException("Specify one performance capture path.");
                path = args[i];
            }
            else
            {
                if (hasSeconds || !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
                    || seconds is < 1 or > 3600) throw new ArgumentException("Capture seconds must be between 1 and 3600.");
                hasSeconds = true;
            }
        }
        if (path == null && hasSeconds) throw new ArgumentException("Capture seconds requires a performance capture path.");
        args = remaining.ToArray();
        return path == null ? null : new PerformanceCaptureSession(path, TimeSpan.FromSeconds(seconds), openOutput: openOutput);
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (Volatile.Read(ref ready) != 0 && Volatile.Read(ref stopped) == 0
            && source.Name == "XcpNgCenter-Shell-Performance")
        {
            EnableEvents(source, EventLevel.Informational);
            if (Volatile.Read(ref stopped) != 0) DisableEvents(source);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs args)
    {
        if (Volatile.Read(ref ready) == 0 || Volatile.Read(ref stopped) != 0) return;
        // Provider metadata errors are never usable samples. Ignore new event
        // schemas until the reader and privacy regression explicitly cover them.
        if (args.EventId is < 1 or > 4) return;
        var sample = new CaptureEvent("event", Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            args.EventId, args.Payload?.ToArray() ?? []);
        // Complete the queue only after admitted callbacks finish their loss
        // accounting, so the footer cannot overtake a rejected write.
        lock (captureGate)
        {
            if (stopped != 0) return;
            observed++;
            if (!queue.Writer.TryWrite(sample)) Interlocked.Increment(ref dropped);
        }
    }

    private async Task Write(Stream output)
    {
        try
        {
            await using (output)
            await using (var text = new StreamWriter(output, new UTF8Encoding(false), 65536, leaveOpen: true))
            {
                await text.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    kind = "capture", schema = 1, provider = "XcpNgCenter-Shell-Performance",
                    startedUtc = DateTime.UtcNow, applicationVersion = typeof(App).Assembly.GetName().Version?.ToString()
                })).ConfigureAwait(false);
                long events = 0;
                long bytes = 0;
                await foreach (var sample in queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    var json = JsonSerializer.Serialize(sample);
                    var sampleBytes = Encoding.UTF8.GetByteCount(json) + 1;
                    if (bytes + sampleBytes > MaxFileBytes)
                    {
                        Interlocked.Increment(ref dropped);
                        continue;
                    }
                    await text.WriteLineAsync(json).ConfigureAwait(false);
                    bytes += sampleBytes;
                    events++;
                }
                await text.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    kind = "summary", eventsObserved = Interlocked.Read(ref observed),
                    eventsWritten = events, eventsDropped = Interlocked.Read(ref dropped),
                    durationMilliseconds
                })).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            writeError = error;
            Stop();
        }
    }

    private void Stop()
    {
        lock (captureGate)
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            durationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            queue.Writer.TryComplete();
        }
        // EventSource has its own callback lock; never acquire it while holding
        // the capture gate used by OnEventWritten.
        DisableEvents(ShellPerformanceDiagnostics.Log);
    }

    public override void Dispose()
    {
        Stop();
        timer.Dispose();
        writer.GetAwaiter().GetResult();
        base.Dispose();
        if (writeError != null) throw new IOException("Performance capture could not be completed.", writeError);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        timer.Dispose();
        await writer.ConfigureAwait(false);
        base.Dispose();
        if (writeError != null) throw new IOException("Performance capture could not be completed.", writeError);
    }

    private sealed record CaptureEvent(string kind, double elapsedMilliseconds, int eventId, object?[] payload);
}
