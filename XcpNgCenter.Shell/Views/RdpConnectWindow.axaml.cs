using Avalonia.Controls;
using Avalonia.Interactivity;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class RdpConnectWindow : Window
{
    public RdpConnectWindow() => InitializeComponent();
    public RdpConnectWindow(string guestName, IReadOnlyList<string> addresses) : this()
        => DataContext = new RdpConnectViewModel(guestName, addresses, endpoint => Close(endpoint));
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
