using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class VmPropertiesWindow : Window
{
    public VmPropertiesWindow()
    {
        InitializeComponent();
    }

    public VmPropertiesWindow(VM vm, Action<string>? status = null) : this()
    {
        DataContext = new VmPropertiesViewModel(vm, Close, status);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
