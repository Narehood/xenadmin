using Avalonia.Controls;
using Avalonia.Interactivity;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.Views;

public partial class ShellConfirmWindow : Window
{
    public ShellConfirmWindow()
    {
        InitializeComponent();
    }

    public ShellConfirmWindow(ShellConfirmRequest request) : this()
    {
        DataContext = request;
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
