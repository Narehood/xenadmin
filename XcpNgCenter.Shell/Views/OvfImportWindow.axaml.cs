using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class OvfImportWindow : Window
{
    public OvfImportWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as OvfImportViewModel)?.Dispose();
    }

    public OvfImportWindow(IXenConnection connection, Host? preferredHost, Action<string>? status = null) : this()
    {
        DataContext = new OvfImportViewModel(connection, preferredHost, PickOpenPathAsync, Close, status);
    }

    private async Task<string?> PickOpenPathAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import OVF / OVA",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("OVF / OVA") { Patterns = ["*.ovf", "*.ova", "*.ova.gz"] },
                ShellFilePicker.AllFiles
            ]
        });

        return files.Count > 0 ? ShellFilePicker.LocalPathOrNull(files[0]) : null;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
