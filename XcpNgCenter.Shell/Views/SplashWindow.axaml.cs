using Avalonia.Controls;

namespace XcpNgCenter.Shell.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        // Prefer the calendar build stamped at compile time (year.month.day.revision).
        BuildTagText.Text = Services.ShellVersionInfo.TagDisplay;
        VersionText.Text = Services.ShellVersionInfo.Codename;
    }
}
