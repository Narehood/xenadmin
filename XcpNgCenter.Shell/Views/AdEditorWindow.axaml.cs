using Avalonia.Controls;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class AdEditorWindow : Window
{
    public AdEditorWindow() => InitializeComponent();
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is AdEditorViewModel { IsBusy: true }) e.Cancel = true;
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        (DataContext as AdEditorViewModel)?.Dispose();
        base.OnClosed(e);
    }
}
