using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class VmEditWindow : Window
{
    public VmEditWindow()
    {
        InitializeComponent();
    }

    public VmEditWindow(VM vm) : this()
    {
        DataContext = new VmEditViewModel(vm, Close);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
