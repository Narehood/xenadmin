using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace XcpNgCenter.Shell.Controls;

public partial class ShellUpdateButton : UserControl
{
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public ShellUpdateButton()
    {
        InitializeComponent();
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            if (!UpdateTrigger.IsPointerOver && !DetailsPanel.IsPointerOver && !DetailsPanel.IsKeyboardFocusWithin)
                DetailsPopup.IsOpen = false;
        };
        DetachedFromVisualTree += (_, _) => { _closeTimer.Stop(); DetailsPopup.IsOpen = false; };
        DetailsPanel.LostFocus += (_, _) => _closeTimer.Start();
    }

    private void OnTriggerEntered(object? sender, PointerEventArgs e) => OpenDetails();
    private void OnTriggerFocused(object? sender, GotFocusEventArgs e)
    {
        if (e.NavigationMethod == NavigationMethod.Tab) OpenDetails();
    }
    private void OpenDetails() { _closeTimer.Stop(); DetailsPopup.IsOpen = true; }
    private void OnPanelEntered(object? sender, PointerEventArgs e) => _closeTimer.Stop();
    private void OnPointerExited(object? sender, PointerEventArgs e) => _closeTimer.Start();
    private void OnTriggerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            OpenDetails();
            DetailsPanel.Focus();
            e.Handled = true;
        }
        if (e.Key == Key.Escape) { DetailsPopup.IsOpen = false; e.Handled = true; }
    }
    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DetailsPopup.IsOpen = false;
        UpdateTrigger.Focus();
        e.Handled = true;
    }
}
