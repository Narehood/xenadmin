using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class VmMoveWindow : Window
{
    public VmMoveWindow()
    {
        InitializeComponent();
    }

    public VmMoveWindow(VM vm, Action<string>? status = null) : this()
    {
        DataContext = new VmMoveViewModel(vm, Close, status);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
