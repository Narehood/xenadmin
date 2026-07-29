using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class HostPropertiesWindow : Window
{
    public HostPropertiesWindow()
    {
        InitializeComponent();
    }

    public HostPropertiesWindow(Host host, Action<string>? status = null) : this()
    {
        DataContext = new HostPropertiesViewModel(host, Close, status);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
