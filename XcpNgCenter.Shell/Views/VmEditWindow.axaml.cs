using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

/// <summary>
/// Legacy entry point — redirects to the full <see cref="VmPropertiesWindow"/>.
/// </summary>
public partial class VmEditWindow : Window
{
    public VmEditWindow()
    {
        InitializeComponent();
    }

    public VmEditWindow(VM vm) : this()
    {
        // Keep constructor for any leftover callers; prefer VmPropertiesWindow.
        DataContext = new VmEditViewModel(vm, Close);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
