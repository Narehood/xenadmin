using Avalonia.Controls;
using Avalonia.Interactivity;

namespace XcpNgCenter.Shell.Views;

public enum ImportExportChoice
{
    Cancel,
    Import,
    Export
}

public partial class ImportExportChoiceWindow : Window
{
    public ImportExportChoice ResultChoice { get; private set; } = ImportExportChoice.Cancel;

    public ImportExportChoiceWindow()
    {
        InitializeComponent();
    }

    private void OnImportClick(object? sender, RoutedEventArgs e)
    {
        ResultChoice = ImportExportChoice.Import;
        Close();
    }

    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        ResultChoice = ImportExportChoice.Export;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        ResultChoice = ImportExportChoice.Cancel;
        Close();
    }
}
