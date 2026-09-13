using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAdmin.Network;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class NetworkEditorWindow : Window
{
    public NetworkEditorWindow() => InitializeComponent();
    public NetworkEditorWindow(IXenConnection connection, XenAPI.Network? network, Action<string> status) : this()
        => DataContext = new NetworkEditorViewModel(connection, network, Close, status);
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is NetworkEditorViewModel { IsSaving: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
