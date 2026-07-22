using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class VmImportWindow : Window
{
    public VmImportWindow()
    {
        InitializeComponent();
    }

    public VmImportWindow(IXenConnection connection, Host? preferredHost, Action<string>? status = null) : this()
    {
        DataContext = new VmImportViewModel(connection, preferredHost, PickOpenPathAsync, Close, status);
    }

    private async Task<string?> PickOpenPathAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import XVA",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("XVA backup") { Patterns = ["*.xva", "*.ova"] },
                ShellFilePicker.AllFiles
            ]
        });

        return files.Count > 0 ? ShellFilePicker.LocalPathOrNull(files[0]) : null;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
