using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAdmin.Core;
using XenCenterLib;

namespace XcpNgCenter.Shell.Views;

public partial class EnterMainPasswordWindow : Window
{
    private readonly byte[] _expectedHash;

    public string? Password { get; private set; }

    public EnterMainPasswordWindow() : this(Array.Empty<byte>())
    {
    }

    public EnterMainPasswordWindow(byte[] expectedHash, string? title = null, string? message = null)
    {
        InitializeComponent();
        _expectedHash = expectedHash;
        if (!string.IsNullOrWhiteSpace(title))
            TitleText.Text = title;
        if (!string.IsNullOrWhiteSpace(message))
            MessageText.Text = message;
        ErrorText.IsVisible = false;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Text ?? string.Empty;
        if (!string.IsNullOrEmpty(password) &&
            Helpers.ArrayElementsEqual(EncryptionUtils.ComputeHash(password), _expectedHash))
        {
            Password = password;
            Close(true);
            return;
        }

        ErrorText.IsVisible = true;
        PasswordBox.Focus();
        PasswordBox.SelectAll();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
