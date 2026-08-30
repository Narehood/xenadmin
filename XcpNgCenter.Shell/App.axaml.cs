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
            // Show splash before Bootstrap / MainViewModel so the first paint is immediate.
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();

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
            // Yield until after the first render pass so the splash is on-screen
            // before XenModel/TOFU bootstrap and MainViewModel construction.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Yield();

            ShellBootstrap.Initialize();

            var viewModel = new MainViewModel();
            var main = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += (_, _) => viewModel.Dispose();

            // Keep splash up long enough to read brand + version (init may finish sooner).
            const int minimumVisibleMs = 2800;
            var remaining = minimumVisibleMs - (int)visibleFor.ElapsedMilliseconds;
            if (remaining > 0)
                await Task.Delay(remaining);

            desktop.MainWindow = main;
            main.Show();
            splash.Close();

            // Unlock after the main window is visible so password dialogs are not owned by the splash.
            viewModel.StartPostWindowStartup();
        }
        catch (Exception ex)
        {
            TryWriteStartupCrashLog(ex);
            try
            {
                splash.Close();
            }
            catch
            {
                // Best-effort.
            }

            var details =
                "XCP-ng Center failed to start.\n\n" +
                ex.GetType().Name + ": " + ex.Message + "\n\n" +
                "If this followed an in-app update, re-extract the latest release zip over the install folder " +
                "(or into a new folder) and try again.\n\n" +
                "Details were also written to:\n" +
                GetStartupCrashLogPath();

            try
            {
                var dialog = new Window
                {
                    Title = "XCP-ng Center — startup failed",
                    Width = 520,
                    Height = 320,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    CanResize = false,
                    Content = new ScrollViewer
                    {
                        Content = new TextBlock
                        {
                            Text = details,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            Margin = new Thickness(16)
                        }
                    }
                };
                desktop.MainWindow = dialog;
                dialog.Show();
            }
            catch
            {
                // Last resort: ensure the process does not sit invisible.
                desktop.Shutdown(1);
            }
        }
    }

    private static string GetStartupCrashLogPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XCP-ng",
            "XCP-ng Center Shell",
            "startup-crash.log");

    private static void TryWriteStartupCrashLog(Exception ex)
    {
        try
        {
            var path = GetStartupCrashLogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = new StringBuilder()
                .AppendLine(DateTimeOffset.Now.ToString("u"))
                .AppendLine(ex.ToString())
                .ToString();
            File.WriteAllText(path, text);
        }
        catch
        {
            // Best-effort diagnostics.
        }
    }
}
