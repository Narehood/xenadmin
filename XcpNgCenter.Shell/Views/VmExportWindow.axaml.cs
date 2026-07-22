using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class VmExportWindow : Window
{
    public VmExportWindow()
    {
        InitializeComponent();
    }

    public VmExportWindow(IXenConnection connection, VM? preferredVm, Action<string>? status = null) : this()
    {
        DataContext = new VmExportViewModel(connection, preferredVm, PickSavePathAsync, Close, status);
    }

    private async Task<string?> PickSavePathAsync()
    {
        var suggested = (DataContext as VmExportViewModel)?.SelectedVm?.Vm.name_label ?? "vm";
        var files = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export VM as XVA",
            SuggestedFileName = $"{SanitizeFileName(suggested)}.xva",
            DefaultExtension = "xva",
            FileTypeChoices =
            [
                new FilePickerFileType("XVA backup") { Patterns = ["*.xva"] },
                ShellFilePicker.AllFiles
            ]
        });
        return ShellFilePicker.LocalPathOrNull(files);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "vm" : name.Trim();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
