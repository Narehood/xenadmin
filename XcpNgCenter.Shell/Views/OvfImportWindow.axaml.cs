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
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
