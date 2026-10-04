using System;
using Avalonia;
using System.IO;
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

        if (ShellUpdateInstaller.TryRunUpdateProgressMode(args, out var progressExitCode))
        {
            Environment.ExitCode = progressExitCode;
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
        Environment.ExitCode = RunDesktop(args, applicationArgs =>
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(applicationArgs), Console.Error);
    }

    internal static int RunDesktop(string[] args, Func<string[], int> startDesktop, TextWriter errors,
        Func<string, Stream>? openCaptureOutput = null)
    {
        PerformanceCaptureSession? capture;
        try { capture = PerformanceCaptureSession.FromArguments(ref args, openCaptureOutput); }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            errors.WriteLine("Could not start performance capture: " + error.Message);
            return 1;
        }

        var exitCode = 0;
        try { exitCode = startDesktop(args); }
        finally
        {
            try { capture?.Dispose(); }
            catch (IOException error)
            {
                errors.WriteLine("Performance capture could not be completed: " + error.Message);
                exitCode = 1;
            }
        }
        return exitCode;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace(level: LogEventLevel.Warning);
}
