using System;
using Avalonia;
using Avalonia.Logging;

namespace XcpNgCenter.Shell;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // XenModel pulls System.Drawing.Common (Windows-only GDI+ on .NET 8).
        // Disk-only snapshots never touch Image APIs; memory/quiesced stay deferred.
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace(level: LogEventLevel.Warning);
}
