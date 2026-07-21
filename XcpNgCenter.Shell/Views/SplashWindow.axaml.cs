using Avalonia.Controls;
using Avalonia.Threading;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {ShellVersionInfo.Display}";
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
