using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using XcpNgCenter.Shell.Views;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Blocking Avalonia confirm/alert dialogs usable from XenModel action worker threads
/// (same pattern as <see cref="TofuTrustPrompt"/>).
/// </summary>
public static class ShellConfirmPrompt
{
    /// <summary>
    /// Shows a modal confirm dialog. Safe to call from a background thread.
    /// Returns false if cancelled or if no main window is available.
    /// When called on the UI thread, returns false without blocking (avoids deadlock).
    /// Prefer <see cref="ConfirmAsync"/> from UI-thread commands.
    /// </summary>
    public static bool Confirm(ShellConfirmRequest request)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return false;

        return Dispatcher.UIThread.InvokeAsync(async () => await ConfirmCoreAsync(request))
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// Async confirm for UI-thread callers (RelayCommands, menu handlers).
    /// </summary>
    public static Task<bool> ConfirmAsync(ShellConfirmRequest request) => ConfirmCoreAsync(request);

    private static async Task<bool> ConfirmCoreAsync(ShellConfirmRequest request)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime
            || lifetime.MainWindow is not { } owner)
        {
            return false;
        }

        var activeOwner = lifetime.Windows.FirstOrDefault(window => window.IsActive) ?? owner;
        var window = new ShellConfirmWindow(request);
        return await window.ShowDialog<bool>(activeOwner);
    }

    public static void Alert(string title, string message)
    {
        Confirm(new ShellConfirmRequest
        {
            Title = title,
            Message = message,
            AcceptLabel = "OK",
            ShowCancel = false
        });
    }

    public static Task AlertAsync(string title, string message) =>
        ConfirmAsync(new ShellConfirmRequest
        {
            Title = title,
            Message = message,
            AcceptLabel = "OK",
            ShowCancel = false
        }).ContinueWith(_ => { });
}
