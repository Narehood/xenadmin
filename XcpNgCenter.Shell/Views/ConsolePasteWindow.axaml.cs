using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class ConsolePasteWindow : Window
{
    public ConsolePasteWindow() => InitializeComponent();

    public ConsolePasteWindow(ConsolePasteTarget target, HostedConsoleSession session) : this()
    {
        var viewModel = new ConsolePasteViewModel(target, () => Clipboard?.TryGetTextAsync()
            ?? Task.FromResult<string?>(null));
        DataContext = viewModel;
        session.StateChanged += viewModel.RefreshTarget;
        // The toolbar click explicitly requested paste. No clipboard access happens
        // on focus, connection, inventory refresh, or incoming ServerCutText.
        Opened += async (_, _) =>
        {
            if (DataContext is ConsolePasteViewModel vm)
                await vm.LoadClipboardCommand.ExecuteAsync(null);
        };
        Closed += (_, _) =>
        {
            session.StateChanged -= viewModel.RefreshTarget;
            (DataContext as ConsolePasteViewModel)?.Dispose();
            DataContext = null;
        };
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
