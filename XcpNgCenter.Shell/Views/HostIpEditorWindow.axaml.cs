using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class HostIpEditorWindow : Window
{
    public HostIpEditorWindow() => InitializeComponent();
    public HostIpEditorWindow(IXenConnection connection, Host host, Action<string> status) : this()
        => DataContext = new HostIpEditorViewModel(connection, host, Close, status);
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is HostIpEditorViewModel { IsSaving: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
