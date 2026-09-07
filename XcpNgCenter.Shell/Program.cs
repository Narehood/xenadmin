using System;
using Avalonia;
using Avalonia.Logging;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (ShellUpdateInstaller.TryRunBootstrapMode(args, out var bootstrapExitCode)
            || ShellUpdateInstaller.TryRunProtectedCleanupMode(args, out bootstrapExitCode))
        {
            Environment.ExitCode = bootstrapExitCode;
            return;
        }

        // An unelevated broker relaunches the app after a protected-directory update.
        if (ShellUpdateInstaller.TryRunRestartBrokerMode(args, out var restartExitCode))
        {
            Environment.ExitCode = restartExitCode;
            return;
        }

        // A staged new build runs headlessly while replacing the previous installation.
        if (ShellUpdateInstaller.TryRunApplyMode(args, out var updateExitCode))
        {
            Environment.ExitCode = updateExitCode;
            return;
        }

        args = ShellUpdateInstaller.PrepareApplicationStartup(args);

        // XenModel's snapshot action references System.Drawing.Common for an optional
        // console thumbnail. The shell supplies no thumbnail, so all snapshot modes stay GDI-free.
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace(level: LogEventLevel.Warning);
}
