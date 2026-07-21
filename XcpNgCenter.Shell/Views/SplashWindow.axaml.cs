using Avalonia.Controls;
using Avalonia.Threading;

namespace XcpNgCenter.Shell.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {ShellVersion.Display}";
    }

    /// <summary>
    /// Keeps the splash visible briefly (WinForms used ~2s after main was ready).
    /// </summary>
    public async Task WaitVisibleAsync(TimeSpan? minimumVisible = null)
    {
        var delay = minimumVisible ?? TimeSpan.FromMilliseconds(1100);
        await Task.Delay(delay);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
    }
}

internal static class ShellVersion
{
    public static string Display
    {
        get
        {
            var asm = typeof(ShellVersion).Assembly;
            var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }

            var v = asm.GetName().Version;
            return v == null || (v.Major == 0 && v.Minor == 0 && v.Build == 0)
                ? "Preview"
                : v.ToString(3);
        }
    }
}
