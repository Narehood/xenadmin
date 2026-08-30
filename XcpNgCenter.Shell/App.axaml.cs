using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using XcpNgCenter.Shell.Views;

namespace XcpNgCenter.Shell;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Prevent "last window closed" from exiting while we swap splash → main.
            // That race shows the splash, then immediately quits with no error dialog.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();
            TryAppendStartupTrace("splash-shown");

            Dispatcher.UIThread.Post(() => StartAfterSplashPaint(desktop, splash), DispatcherPriority.Loaded);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async void StartAfterSplashPaint(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash)
    {
        var visibleFor = Stopwatch.StartNew();

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Yield();
            TryAppendStartupTrace("bootstrap-begin");

            ShellBootstrap.Initialize();
            TryAppendStartupTrace("bootstrap-done");

            var viewModel = new MainViewModel();
            TryAppendStartupTrace("viewmodel-created");

            var main = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += (_, _) =>
            {
                try { viewModel.Dispose(); }
                catch { /* best-effort */ }
            };
            TryAppendStartupTrace("mainwindow-created");

            const int minimumVisibleMs = 1800;
            var remaining = minimumVisibleMs - (int)visibleFor.ElapsedMilliseconds;
            if (remaining > 0)
                await Task.Delay(remaining);

            desktop.MainWindow = main;
            main.Show();
            main.Activate();
            TryAppendStartupTrace("mainwindow-shown");

            // Wait until the main window has completed its first layout pass before
            // dismissing the splash — otherwise OnLastWindowClose can quit the process.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Task.Yield();

            try
            {
                splash.Hide();
                splash.Close();
            }
            catch (Exception ex)
            {
                TryAppendStartupTrace("splash-close-error: " + ex.Message);
            }

            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            TryAppendStartupTrace("shutdown-mode-mainwindow");

            viewModel.StartPostWindowStartup();
            TryAppendStartupTrace("post-window-startup");
        }
        catch (Exception ex)
        {
            TryWriteStartupCrashLog(ex);
            TryAppendStartupTrace("fatal: " + ex.GetType().Name + ": " + ex.Message);
            try { splash.Hide(); splash.Close(); }
            catch { /* best-effort */ }

            var details =
                "XCP-ng Center failed to start.\n\n" +
                ex.GetType().Name + ": " + ex.Message + "\n\n" +
                "If this followed an in-app update, delete the install folder and re-extract the latest release zip.\n\n" +
                "Log:\n" + GetStartupCrashLogPath();

            try
            {
                var dialog = new Window
                {
                    Title = "XCP-ng Center — startup failed",
                    Width = 560,
                    Height = 340,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    CanResize = true,
                    Content = new ScrollViewer
                    {
                        Content = new TextBlock
                        {
                            Text = details + "\n\n" + ex,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            Margin = new Thickness(16)
                        }
                    }
                };
                desktop.MainWindow = dialog;
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                dialog.Show();
            }
            catch
            {
                desktop.Shutdown(1);
            }
        }
    }

    private static string GetStartupLogDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XCP-ng",
            "XCP-ng Center Shell");

    private static string GetStartupCrashLogPath() =>
        Path.Combine(GetStartupLogDirectory(), "startup-crash.log");

    private static string GetStartupTracePath() =>
        Path.Combine(GetStartupLogDirectory(), "startup-trace.log");

    private static void TryWriteStartupCrashLog(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(GetStartupLogDirectory());
            File.WriteAllText(
                GetStartupCrashLogPath(),
                DateTimeOffset.Now.ToString("u") + Environment.NewLine + ex);
        }
        catch
        {
            // Best-effort diagnostics.
        }
    }

    private static void TryAppendStartupTrace(string step)
    {
        try
        {
            Directory.CreateDirectory(GetStartupLogDirectory());
            File.AppendAllText(
                GetStartupTracePath(),
                DateTimeOffset.Now.ToString("u") + "  " + step + Environment.NewLine);
        }
        catch
        {
            // Best-effort diagnostics.
        }
    }
}
