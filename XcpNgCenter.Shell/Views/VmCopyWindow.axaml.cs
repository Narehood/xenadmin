using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class VmCopyWindow : Window
{
    public VmCopyWindow()
    {
        InitializeComponent();
    }

    public VmCopyWindow(VM vm, Action<string>? status = null) : this()
    {
        DataContext = new VmCopyViewModel(vm, Close, status);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
