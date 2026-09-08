using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class ShellUpdateReadinessTests
{
    [Fact]
    public async Task ProcessCreationAloneDoesNotAuthorizeShutdown()
    {
        var marker = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<TimeoutException>(() => ShellUpdateInstaller.WaitForHelperReadyAsync(
            marker, () => false, TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public async Task ExitedHelperCannotAuthorizeShutdownEvenWithReadyMarker()
    {
        var marker = Path.GetTempFileName();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ShellUpdateInstaller.WaitForHelperReadyAsync(
                marker, () => true, TimeSpan.FromSeconds(1)));
        }
        finally { File.Delete(marker); }
    }

    [Fact]
    public async Task ShutdownWaitsUntilLiveHelperSignalsReadiness()
    {
        var marker = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var waiting = ShellUpdateInstaller.WaitForHelperReadyAsync(marker, () => false, TimeSpan.FromSeconds(5));
            Assert.False(waiting.IsCompleted);
            File.WriteAllText(marker, "ready");
            await waiting;
        }
        finally { File.Delete(marker); }
    }

    [Theory]
    [InlineData("v2026.8.30.8", true)]
    [InlineData("v2026.9.7.1", true)]
    [InlineData("vgarbage", false)]
    [InlineData("v2026.8", false)]
    [InlineData("launch-123", false)]
    public void LegacyLayoutIsRecognizedOnlyForMatchingInstallation(string directory, bool expected)
    {
        var install = Path.Combine(Path.GetTempPath(), "legacy-install");
        var staging = ShellUpdateInstaller.GetStagingBaseDirectory(install, Path.GetTempPath(), OperatingSystem.IsWindows());
        var root = Path.Combine(staging, directory);
        Assert.Equal(expected, ShellUpdateInstaller.IsLegacyUpdateLocation(install, root));
        Assert.False(ShellUpdateInstaller.IsLegacyUpdateLocation(install + "-other", root));
    }
}
