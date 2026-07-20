using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class VmCloneWindow : Window
{
    public VmCloneWindow()
    {
        InitializeComponent();
    }

    public VmCloneWindow(VM vm, Action<string>? status = null) : this()
    {
        DataContext = new VmCloneViewModel(vm, Close, status);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
