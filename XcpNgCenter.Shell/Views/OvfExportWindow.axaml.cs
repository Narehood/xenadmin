using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class OvfExportWindow : Window
{
    public OvfExportWindow()
    {
        InitializeComponent();
    }

    public OvfExportWindow(IXenConnection connection, VM? preferredVm, Action<string>? status = null) : this()
    {
        DataContext = new OvfExportViewModel(connection, preferredVm, PickFolderAsync, Close, status);
    }

    private async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Export destination folder",
            AllowMultiple = false
        });

        return folders.Count > 0 ? ShellFilePicker.LocalPathOrNull(folders[0]) : null;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
