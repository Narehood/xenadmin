using Avalonia.Controls;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class GraphLayoutEditorWindow : Window
{
    public GraphLayoutEditorWindow() => InitializeComponent();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is GraphLayoutEditorViewModel { IsSaving: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
