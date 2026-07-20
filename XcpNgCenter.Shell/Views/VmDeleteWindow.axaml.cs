using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class VmDeleteWindow : Window
{
    public VmDeleteWindow()
    {
        InitializeComponent();
    }

    public VmDeleteWindow(VM vm, Action<string>? status = null) : this()
    {
        DataContext = new VmDeleteViewModel(vm, Close, status);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
