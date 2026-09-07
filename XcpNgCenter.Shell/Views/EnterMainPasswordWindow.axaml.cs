using Avalonia.Controls;
using Avalonia.Interactivity;

namespace XcpNgCenter.Shell.Views;

public partial class EnterMainPasswordWindow : Window
{
    private readonly Func<string, Task<bool>> _unlock;
    private bool _unlocking;

    public EnterMainPasswordWindow() : this(_ => Task.FromResult(false))
    {
    }

    public EnterMainPasswordWindow(Func<string, Task<bool>> unlock, string? title = null, string? message = null)
    {
        InitializeComponent();
        _unlock = unlock;
        Closing += (_, e) => e.Cancel = _unlocking;
        if (!string.IsNullOrWhiteSpace(title))
            TitleText.Text = title;
        if (!string.IsNullOrWhiteSpace(message))
            MessageText.Text = message;
        ErrorText.IsVisible = false;
    }

    private async void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (_unlocking)
            return;
        _unlocking = true;
        IsEnabled = false;
        var password = PasswordBox.Text ?? string.Empty;
        try
        {
            if (!string.IsNullOrEmpty(password) && await _unlock(password))
            {
                PasswordBox.Text = string.Empty;
                _unlocking = false;
                Close(true);
                return;
            }
            ErrorText.Text = "Incorrect password.";
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
        finally { _unlocking = false; IsEnabled = true; }
        ErrorText.IsVisible = true;
        PasswordBox.Focus();
        PasswordBox.SelectAll();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        PasswordBox.Text = string.Empty;
        Close(false);
    }
}
