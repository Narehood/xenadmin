using Avalonia.Controls;

namespace XcpNgCenter.Shell.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        // Prefer the calendar build stamped at compile time (year.month.day.revision).
        VersionText.Text = $"Version {Services.ShellVersionInfo.Display}";
    }
}
