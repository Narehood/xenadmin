using Avalonia.Controls;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class HaEditorWindow : Window
{
    public HaEditorWindow() => InitializeComponent();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is HaEditorViewModel { IsBusy: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
