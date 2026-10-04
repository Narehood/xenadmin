using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

[Collection("Performance diagnostics")]
public sealed class PerformanceCaptureStartupTests
{
    [Theory]
    [InlineData("--performance-capture")]
    [InlineData("--performance-capture-seconds", "0")]
    [InlineData("--performance-capture", "relative.jsonl")]
    public void InvalidArgumentsReportAnErrorWithoutStartingDesktop(params string[] args)
    {
        using var errors = new StringWriter();
        var started = false;
        var exit = Program.RunDesktop(args, _ => { started = true; return 0; }, errors);
        Assert.Equal(1, exit);
        Assert.False(started);
        Assert.Contains("Could not start performance capture:", errors.ToString());
    }

    [Fact]
    public void ExistingCaptureFileIsPreservedAndStartupFailsCleanly()
    {
        var path = Path.Combine(Path.GetTempPath(), "capture-existing-" + Guid.NewGuid() + ".jsonl");
        try
        {
            File.WriteAllText(path, "existing capture");
            using var errors = new StringWriter();
            var started = false;
            var exit = Program.RunDesktop(["--performance-capture", path], _ => { started = true; return 0; }, errors);
            Assert.Equal(1, exit);
            Assert.False(started);
            Assert.Equal("existing capture", File.ReadAllText(path));
            Assert.Contains("Could not start performance capture:", errors.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WriterFailureIsReportedAtShutdownWithoutUnhandledException()
    {
        using var errors = new StringWriter();
        var stream = new FailedOutput();
        var started = false;
        var exit = Program.RunDesktop(["--performance-capture", Path.GetFullPath("synthetic-capture.jsonl")], _ =>
        {
            started = true;
            ShellPerformanceDiagnostics.Log.ConsoleFrame(100, 100);
            return 0;
        }, errors, _ => stream);
        Assert.True(started);
        Assert.Equal(1, exit);
        Assert.Contains("Performance capture could not be completed:", errors.ToString());
        Assert.False(stream.CanWrite);
        Assert.False(ShellPerformanceDiagnostics.Log.IsEnabled());
    }

    [Fact]
    public void CaptureShutdownFailureDoesNotMaskDesktopFailure()
    {
        using var errors = new StringWriter();
        var expected = new InvalidOperationException("Synthetic desktop failure");
        var actual = Assert.Throws<InvalidOperationException>(() => Program.RunDesktop(
            ["--performance-capture", Path.GetFullPath("synthetic-capture.jsonl")], _ => throw expected,
            errors, _ => new FailedOutput()));
        Assert.Same(expected, actual);
        Assert.Contains("Performance capture could not be completed:", errors.ToString());
    }

    [Fact]
    public void NormalStartupForwardsOtherArgumentsAndPreservesDesktopExitCode()
    {
        using var errors = new StringWriter();
        var exit = Program.RunDesktop(["--ordinary-option", "value"], args =>
        {
            Assert.Equal(new[] { "--ordinary-option", "value" }, args);
            return 7;
        }, errors);
        Assert.Equal(7, exit);
        Assert.Equal("", errors.ToString());
    }

    private sealed class FailedOutput : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("Synthetic disk full"));
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException(new IOException("Synthetic disk full"));
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Synthetic disk full");
    }
}
