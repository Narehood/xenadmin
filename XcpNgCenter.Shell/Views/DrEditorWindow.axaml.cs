using Avalonia.Controls;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class DrEditorWindow : Window
{
    public DrEditorWindow() => InitializeComponent();
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is DrEditorViewModel { IsBusy: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
