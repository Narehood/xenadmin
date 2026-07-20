using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class IsoAttachWindow : Window
{
    public IsoAttachWindow()
    {
        InitializeComponent();
    }

    public IsoAttachWindow(VM vm) : this()
    {
        DataContext = new IsoAttachViewModel(vm, Close);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
