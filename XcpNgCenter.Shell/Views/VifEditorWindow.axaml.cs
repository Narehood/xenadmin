using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class VifEditorWindow : Window
{
    public VifEditorWindow() => InitializeComponent();
    public VifEditorWindow(VM vm, VIF? vif, Action<string> status) : this()
        => DataContext = new VifEditorViewModel(vm, vif, Close, status);
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is VifEditorViewModel { IsSaving: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
