using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class ShellUpdateRestartBrokerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidBrokerContextCannotLaunchExecutableFromSuppliedInstallDirectory(bool includeInvalidUpdateRoot)
    {
        // No executable is created or run. Intercept the actual restart boundary so
        // a context-validation failure cannot be mistaken for safe process creation.
        var untrusted = Path.Combine(Path.GetTempPath(), "xcpng-rejected-broker-" + Guid.NewGuid().ToString("N"));
        var args = new List<string>
        {
            "--wait-for-shell-update-result", "--wait-pid", int.MaxValue.ToString(),
            "--install-directory", Path.Combine(untrusted, "install")
        };
        if (includeInvalidUpdateRoot)
            args.AddRange(["--update-root", Path.Combine(untrusted, "invalid-staging")]);
        var waits = new List<int>();
        var launches = new List<string>();

        var handled = ShellUpdateInstaller.TryRunRestartBrokerMode(args.ToArray(), out var exitCode,
            waits.Add, (executable, _, _, _, _) => { launches.Add(executable); return true; });

        Assert.True(handled);
        Assert.Equal(1, exitCode);
        Assert.Empty(waits);
        Assert.Empty(launches);
    }

    [Fact]
    public void NormalStartupDoesNotRunRestartBroker()
    {
        var handled = ShellUpdateInstaller.TryRunRestartBrokerMode([], out var exitCode,
            _ => throw new Exception("Unexpected wait"),
            (_, _, _, _, _) => throw new Exception("Unexpected launch"));
        Assert.False(handled);
        Assert.Equal(0, exitCode);
    }
}
