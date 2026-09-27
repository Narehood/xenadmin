using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAdmin.Network;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class BondEditorWindow : Window
{
    public BondEditorWindow() => InitializeComponent();
    public BondEditorWindow(IXenConnection connection, XenAPI.Network? network, Action<string> status) : this()
        => DataContext = new BondEditorViewModel(connection, network, Close, status);
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is BondEditorViewModel { IsSaving: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
