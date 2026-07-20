using Avalonia.Controls;
using Avalonia.Interactivity;
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
}
