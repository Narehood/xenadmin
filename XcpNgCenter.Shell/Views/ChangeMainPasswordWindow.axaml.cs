using Avalonia.Controls;
using Avalonia.Interactivity;

namespace XcpNgCenter.Shell.Views;

public partial class ChangeMainPasswordWindow : Window
{
    private readonly Func<string, Task<bool>> _unlock;
    private bool _unlocking;

    /// <summary>New password returned only for the immediate vault migration.</summary>
    public string? NewPasswordPlain { get; private set; }

    public ChangeMainPasswordWindow() : this(_ => Task.FromResult(false))
    {
    }

    public ChangeMainPasswordWindow(Func<string, Task<bool>> unlock)
    {
        InitializeComponent();
        _unlock = unlock;
        Closing += (_, e) => e.Cancel = _unlocking;
        CurrentError.IsVisible = false;
        NewError.IsVisible = false;
    }

    private async void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (_unlocking)
            return;
        var current = CurrentBox.Text ?? string.Empty;
        var next = NewBox.Text ?? string.Empty;
        var confirm = ConfirmBox.Text ?? string.Empty;

        var currentOk = false;
        // Validate the new inputs before unlocking/migrating a legacy credential file.
        if (!string.IsNullOrEmpty(next) && next == confirm)
        {
            _unlocking = true;
            IsEnabled = false;
            try { currentOk = !string.IsNullOrEmpty(current) && await _unlock(current); }
            catch (Exception ex)
            {
                CurrentError.Text = ex.Message;
                CurrentError.IsVisible = true;
                return;
            }
            finally { _unlocking = false; IsEnabled = true; }
        }

        if (currentOk && !string.IsNullOrEmpty(next) && next == confirm)
        {
            NewPasswordPlain = next;
            CurrentBox.Text = NewBox.Text = ConfirmBox.Text = string.Empty;
            Close(true);
            return;
        }

        if (!currentOk && !string.IsNullOrEmpty(next) && next == confirm)
        {
            CurrentError.Text = "Incorrect password.";
            CurrentError.IsVisible = true;
            NewError.IsVisible = false;
        }
        else if (next != confirm)
        {
            CurrentError.IsVisible = false;
            NewError.Text = "Passwords do not match.";
            NewError.IsVisible = true;
        }
        else
        {
            CurrentError.IsVisible = false;
            NewError.Text = "Password cannot be empty.";
            NewError.IsVisible = true;
        }

        CurrentBox.Focus();
        CurrentBox.SelectAll();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        CurrentBox.Text = NewBox.Text = ConfirmBox.Text = string.Empty;
        Close(false);
    }
}
