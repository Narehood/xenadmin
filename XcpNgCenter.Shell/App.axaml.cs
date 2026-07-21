using System.Diagnostics;
using Avalonia;
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
    }
}
