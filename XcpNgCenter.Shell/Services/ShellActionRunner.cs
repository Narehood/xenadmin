using Avalonia.Threading;
using XenAdmin.Actions;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Starts XenModel <see cref="AsyncAction"/>s and surfaces status on the UI thread.
/// History population is handled by <see cref="ShellActionHistory"/> via <see cref="ActionBase.NewAction"/>.
/// </summary>
public static class ShellActionRunner
{
    public static Task<bool> RunAndWaitAsync(AsyncAction action, Action<string>? status = null)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Start(action, status, () => completion.TrySetResult(action.Succeeded));
        return completion.Task;
    }

    public static void Run(AsyncAction action, Action<string>? status = null)
        => Start(action, status, null);

    private static void Start(AsyncAction action, Action<string>? status, Action? completed)
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

        void Changed(ActionBase a)
        {
            var text = string.IsNullOrWhiteSpace(a.Description)
                ? a.Title
                : $"{a.Title} — {a.Description}";
            if (a.ShowProgress && a.PercentComplete is > 0 and < 100)
                text += $" ({a.PercentComplete}%)";
            Report(text ?? string.Empty);
        }

        void Completed(ActionBase a)
        {
            // History retains actions. Release editor/status closures when the task ends.
            action.Changed -= Changed;
            action.Completed -= Completed;
            if (a.Succeeded)
                Report(string.IsNullOrWhiteSpace(a.Description) ? $"{a.Title} — done." : a.Description);
            else if (a.IsCancelled)
                Report($"{a.Title} — cancelled.");
            else
                Report($"{a.Title} — failed: {a.Exception?.Message ?? "unknown error"}");
            // Queue the final status before releasing an awaiting editor.
            completed?.Invoke();
        }

        action.Changed += Changed;
        action.Completed += Completed;
        try { action.RunAsync(); }
        catch
        {
            action.Changed -= Changed;
            action.Completed -= Completed;
            throw;
        }
    }
}
