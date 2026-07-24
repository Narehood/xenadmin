using Avalonia.Controls;
using Avalonia.Interactivity;
using XenCenterLib;

namespace XcpNgCenter.Shell.Views;

public partial class SetMainPasswordWindow : Window
{
    public byte[]? NewPasswordHash { get; private set; }

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
            NewPasswordHash = EncryptionUtils.ComputeHash(password);
            Close(true);
            return;
        }

        ErrorText.Text = password != confirm
            ? "Passwords do not match."
            : "Password cannot be empty.";
        ErrorText.IsVisible = true;
        PasswordBox.Focus();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
