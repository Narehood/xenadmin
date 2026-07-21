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
        ShellBootstrap.Initialize();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();

            // Mirror WinForms SplashScreenContext: brief splash, then main window.
            Dispatcher.UIThread.Post(async () =>
            {
                await splash.WaitVisibleAsync();

                var viewModel = new MainViewModel();
                var main = new MainWindow { DataContext = viewModel };
                desktop.MainWindow = main;
                desktop.ShutdownRequested += (_, _) => viewModel.Dispose();
                main.Show();
                splash.Close();
            }, DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
