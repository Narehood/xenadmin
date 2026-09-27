using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAdmin.Network;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class SriovNetworkWindow : Window
{
    public SriovNetworkWindow() => InitializeComponent();
    public SriovNetworkWindow(IXenConnection connection, Action<string> status) : this()
        => DataContext = new SriovNetworkViewModel(connection, Close, status);
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is SriovNetworkViewModel { IsSaving: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
