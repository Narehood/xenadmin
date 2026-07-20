using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using XcpNgCenter.Shell.Controls;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnConsoleFocusCaptureChanged(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not RfbConsoleView view)
            return;

        vm.SetConsoleInputFocused(view.IsFocused);
    }

    private void OnInfraTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;

        if (DataContext is not MainViewModel vm)
            return;

        var source = e.Source as Control;
        while (source != null && source is not TreeViewItem)
            source = source.GetVisualParent() as Control;

        if (source is TreeViewItem { DataContext: InfraTreeNode node })
            vm.SelectedInfraNode = node;
    }
}
