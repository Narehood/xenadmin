using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Input.Platform;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class ConsolePasteWindow : Window
{
    public ConsolePasteWindow()
    {
        InitializeComponent();
        DraftEditor.AddHandler(TextInputEvent, OnEditorTextInput, RoutingStrategies.Tunnel);
    }

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

    private async void OnEditorPasting(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not ConsolePasteViewModel vm) return;
        var caret = await vm.PasteClipboardIntoDraft(DraftEditor.SelectionStart, DraftEditor.SelectionEnd);
        if (caret.HasValue && ReferenceEquals(DataContext, vm))
        {
            DraftEditor.SelectionStart = caret.Value;
            DraftEditor.SelectionEnd = caret.Value;
            DraftEditor.CaretIndex = caret.Value;
        }
    }

    private void OnEditorTextInput(object? sender, TextInputEventArgs e)
    {
        var remaining = ConsolePasteText.MaxLength - (DraftEditor.Text?.Length ?? 0)
                        + Math.Abs(DraftEditor.SelectionStart - DraftEditor.SelectionEnd);
        if (e.Text?.Length > remaining)
        {
            e.Handled = true;
            (DataContext as ConsolePasteViewModel)?.RejectOversizedEdit();
        }
    }

    private void OnEditorTextChanging(object? sender, TextChangingEventArgs e)
    {
        if (DraftEditor.Text?.Length > ConsolePasteText.MaxLength && DataContext is ConsolePasteViewModel vm)
        {
            // A two-way binding does not restore the target when its setter rejects
            // a value. Keep the visible text equal to the bounded reviewed draft.
            DraftEditor.SetCurrentValue(TextBox.TextProperty, vm.Text);
            vm.RejectOversizedEdit();
        }
    }
}
