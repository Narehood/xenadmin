using Avalonia.Threading;
using XenAdmin.Actions;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Starts XenModel <see cref="AsyncAction"/>s and surfaces status on the UI thread.
/// History population is handled by <see cref="ShellActionHistory"/> via <see cref="ActionBase.NewAction"/>.
/// </summary>
public static class ShellActionRunner
{
    public static void Run(AsyncAction action, Action<string>? status = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        void Report(string message)
        {
            if (status == null)
                return;
            if (Dispatcher.UIThread.CheckAccess())
                status(message);
            else
                Dispatcher.UIThread.Post(() => status(message));
        }

        Report(action.Title ?? "Starting…");

        action.Changed += a =>
        {
            var text = string.IsNullOrWhiteSpace(a.Description)
                ? a.Title
                : $"{a.Title} — {a.Description}";
            if (a.ShowProgress && a.PercentComplete is > 0 and < 100)
                text += $" ({a.PercentComplete}%)";
            Report(text ?? string.Empty);
        };

        action.Completed += a =>
        {
            if (a.Succeeded)
                Report(string.IsNullOrWhiteSpace(a.Description) ? $"{a.Title} — done." : a.Description);
            else if (a.IsCancelled)
                Report($"{a.Title} — cancelled.");
            else
                Report($"{a.Title} — failed: {a.Exception?.Message ?? "unknown error"}");
        };

        action.RunAsync();
    }
}
