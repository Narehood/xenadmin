using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class VmCrossPoolMigrateWindow : Window
{
    public VmCrossPoolMigrateWindow()
    {
        InitializeComponent();
    }

    public VmCrossPoolMigrateWindow(
        VM vm,
        Action<string>? status = null,
        ShellMigrateWizardMode mode = ShellMigrateWizardMode.Migrate) : this()
    {
        var vmModel = new VmCrossPoolMigrateViewModel(vm, Close, status, mode);
        DataContext = vmModel;
        Title = vmModel.WindowTitle;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
