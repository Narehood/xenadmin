using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class VmMigrateWindow : Window
{
    public VmMigrateWindow()
    {
        InitializeComponent();
    }

    public VmMigrateWindow(VM vm, Action<string>? status = null) : this()
    {
        DataContext = new VmMigrateViewModel(vm, Close, status);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
