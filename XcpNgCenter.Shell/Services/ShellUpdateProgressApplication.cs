using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace XcpNgCenter.Shell.Services;

/// <summary>A separate, unelevated window that survives the main application's exit.</summary>
internal sealed class ShellUpdateProgressApplication : Application
{
    internal static Func<Action<string>, int> Operation { get; set; } = null!;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var message = new TextBlock { Text = "Preparing to restart XCP-ng Center…", TextWrapping = TextWrapping.Wrap };
            var progress = new ProgressBar { IsIndeterminate = true, Height = 6 };
            var close = new Button { Content = "Close", IsVisible = false, HorizontalAlignment = HorizontalAlignment.Right };
            var window = new Window
            {
                Title = "XCP-ng Center — updating", Width = 540, Height = 240,
                CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new StackPanel { Margin = new Thickness(24), Spacing = 20, Children = { message, progress, close } }
            };
            var finished = false;
            window.Closing += (_, e) => e.Cancel = !finished;
            close.Click += (_, _) => desktop.Shutdown(1);
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                var code = await Task.Run(() =>
                {
                    try { return Operation(text => Dispatcher.UIThread.Post(() => message.Text = text)); }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.Post(() => message.Text = "The update could not complete: " + ex.Message);
                        return 1;
                    }
                });
                finished = true;
                if (code == 0) desktop.Shutdown(0);
                else
                {
                    window.Title = "XCP-ng Center — update needs attention";
                    progress.IsVisible = false;
                    close.IsVisible = true;
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
