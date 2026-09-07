using Avalonia.Controls;
using Avalonia.Interactivity;

namespace XcpNgCenter.Shell.Views;

public partial class SetMainPasswordWindow : Window
{
    public string? PasswordPlain { get; private set; }

    public SetMainPasswordWindow()
    {
        InitializeComponent();
        ErrorText.IsVisible = false;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Text ?? string.Empty;
        var confirm = ConfirmBox.Text ?? string.Empty;

        if (!string.IsNullOrEmpty(password) && password == confirm)
        {
            PasswordPlain = password;
            PasswordBox.Text = ConfirmBox.Text = string.Empty;
            Close(true);
            return;
        }

        ErrorText.Text = password != confirm
            ? "Passwords do not match."
            : "Password cannot be empty.";
        ErrorText.IsVisible = true;
        PasswordBox.Focus();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        PasswordBox.Text = ConfirmBox.Text = string.Empty;
        Close(false);
    }
}
