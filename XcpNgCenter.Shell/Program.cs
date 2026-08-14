using System;
using Avalonia;
using Avalonia.Logging;

namespace XcpNgCenter.Shell;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // XenModel's snapshot action references System.Drawing.Common for an optional
        // console thumbnail. The shell supplies no thumbnail, so all snapshot modes stay GDI-free.
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace(level: LogEventLevel.Warning);
}
