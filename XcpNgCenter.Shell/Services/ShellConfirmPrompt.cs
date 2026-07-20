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
    /// </summary>
    public static bool Confirm(ShellConfirmRequest request)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return false;

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow: { } owner
                })
            {
                return false;
            }

            var window = new ShellConfirmWindow(request);
            return await window.ShowDialog<bool>(owner);
        }).GetAwaiter().GetResult();
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
}
