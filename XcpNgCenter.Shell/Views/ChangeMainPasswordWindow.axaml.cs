using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAdmin.Core;
using XenCenterLib;

namespace XcpNgCenter.Shell.Views;

public partial class ChangeMainPasswordWindow : Window
{
    private readonly byte[] _currentHash;

    public byte[]? NewPasswordHash { get; private set; }

    /// <summary>Plaintext of the new password (needed to re-encrypt blobs with EncryptString).</summary>
    public string? NewPasswordPlain { get; private set; }

    /// <summary>Plaintext of the current password (needed to decrypt existing blobs).</summary>
    public string? CurrentPasswordPlain { get; private set; }

    public ChangeMainPasswordWindow() : this(Array.Empty<byte>())
    {
    }

    public ChangeMainPasswordWindow(byte[] currentHash)
    {
        InitializeComponent();
        _currentHash = currentHash;
        CurrentError.IsVisible = false;
        NewError.IsVisible = false;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var current = CurrentBox.Text ?? string.Empty;
        var next = NewBox.Text ?? string.Empty;
        var confirm = ConfirmBox.Text ?? string.Empty;

        var currentOk = !string.IsNullOrEmpty(current) &&
                        Helpers.ArrayElementsEqual(EncryptionUtils.ComputeHash(current), _currentHash);

        if (currentOk && !string.IsNullOrEmpty(next) && next == confirm)
        {
            CurrentPasswordPlain = current;
            NewPasswordPlain = next;
            NewPasswordHash = EncryptionUtils.ComputeHash(next);
            Close(true);
            return;
        }

        if (!currentOk)
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

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
