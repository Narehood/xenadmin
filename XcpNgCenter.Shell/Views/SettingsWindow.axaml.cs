using Avalonia.Controls;

namespace XcpNgCenter.Shell.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(ViewModels.MainViewModel main, Services.ShellAppSettings settings) : this()
    {
        DataContext = new ViewModels.SettingsViewModel(main, settings, Close);
    }
}
