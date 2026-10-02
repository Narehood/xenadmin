using System.Text.Json;
using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

[Collection("Performance diagnostics")]
public sealed class PerformanceCaptureTests
{
    [Fact]
    public async Task OverloadedCaptureRecordsLostSamplesRatherThanBlockingEventProducer()
    {
        var path = Path.Combine(Path.GetTempPath(), "xcp-performance-" + Guid.NewGuid() + ".jsonl");
        try
        {
            await using (var capture = new PerformanceCaptureSession(path, TimeSpan.FromSeconds(30), capacity: 1))
                for (var i = 0; i < 20000; i++) ShellPerformanceDiagnostics.Log.ConsoleFrame(100, 100);
            using var footer = JsonDocument.Parse(File.ReadAllLines(path)[^1]);
            var written = footer.RootElement.GetProperty("eventsWritten").GetInt64();
            var dropped = footer.RootElement.GetProperty("eventsDropped").GetInt64();
            Assert.True(dropped > 0);
            Assert.Equal(20000, written + dropped);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task CaptureWritesOnlyPerformanceProviderAndFlushesCompleteSchema()
    {
        var path = Path.Combine(Path.GetTempPath(), "xcp-performance-" + Guid.NewGuid() + ".jsonl");
        try
        {
            await using (var capture = new PerformanceCaptureSession(path, TimeSpan.FromSeconds(30)))
            {
                ShellPerformanceDiagnostics.Log.Operation("inventory.refresh", 12, 1024);
                ShellPerformanceDiagnostics.Log.InventoryBatch(200, 1);
                ShellPerformanceDiagnostics.Log.ConsoleFrame(1024, 768);
                ShellPerformanceDiagnostics.Log.ConsoleStartup("handshake", "connected");
                ShellPerformanceDiagnostics.Log.Operation("graphs.rrd-fetch", 4, -1);
                using var unrelated = new PrivateEvents();
                unrelated.Secret("synthetic-host-token-clipboard");
            }
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("synthetic-host-token-clipboard", text);
            var records = File.ReadAllLines(path).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.Equal("capture", records[0].RootElement.GetProperty("kind").GetString());
                Assert.Equal(1, records[0].RootElement.GetProperty("schema").GetInt32());
                Assert.Equal(5, records.Count(record => record.RootElement.GetProperty("kind").GetString() == "event"));
                Assert.Equal("summary", records[^1].RootElement.GetProperty("kind").GetString());
                Assert.Equal(5, records[^1].RootElement.GetProperty("eventsWritten").GetInt64());
                Assert.Equal(0, records[^1].RootElement.GetProperty("eventsDropped").GetInt64());
            }
            finally { foreach (var record in records) record.Dispose(); }
            Assert.False(ShellPerformanceDiagnostics.Log.IsEnabled());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CaptureDeadlineStopsCollectionWithoutClosingTheApplication()
    {
        var path = Path.Combine(Path.GetTempPath(), "xcp-performance-" + Guid.NewGuid() + ".jsonl");
        try
        {
            await using var capture = new PerformanceCaptureSession(path, TimeSpan.FromMilliseconds(100));
            ShellPerformanceDiagnostics.Log.ConsoleFrame(100, 100);
            await Task.Delay(250);
            Assert.False(ShellPerformanceDiagnostics.Log.IsEnabled());
            ShellPerformanceDiagnostics.Log.ConsoleFrame(999, 999);
            await capture.DisposeAsync();
            foreach (var line in File.ReadAllLines(path))
            {
                using var record = JsonDocument.Parse(line);
                if (record.RootElement.GetProperty("kind").GetString() == "event")
                    Assert.Equal(100, record.RootElement.GetProperty("payload")[0].GetInt32());
            }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("--performance-capture-seconds", "0")]
    [InlineData("--performance-capture-seconds", "3601")]
    [InlineData("--performance-capture-seconds", "ten")]
    public void InvalidDurationIsRejected(string option, string value)
    {
        var args = new[] { option, value };
        Assert.Throws<ArgumentException>(() => PerformanceCaptureSession.FromArguments(ref args));
    }

    [Fact]
    public void CaptureArgumentsPreserveOtherFlagsAndNeverOverwriteFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "xcp-performance-" + Guid.NewGuid() + ".jsonl");
        try
        {
            var args = new[] { "--existing-option", "value", "--performance-capture", path, "--performance-capture-seconds", "30" };
            using (PerformanceCaptureSession.FromArguments(ref args))
                Assert.Equal(new[] { "--existing-option", "value" }, args);
            var previous = File.ReadAllText(path);
            var again = new[] { "--performance-capture", path };
            Assert.Throws<IOException>(() => PerformanceCaptureSession.FromArguments(ref again));
            Assert.Equal(previous, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [System.Diagnostics.Tracing.EventSource(Name = "Synthetic-Private-Data")]
    private sealed class PrivateEvents : System.Diagnostics.Tracing.EventSource
    {
        [System.Diagnostics.Tracing.Event(1)]
        public void Secret(string secret) => WriteEvent(1, secret);
    }
}
