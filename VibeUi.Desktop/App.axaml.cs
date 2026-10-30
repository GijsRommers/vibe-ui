using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using VibeUi.Acp;
using VibeUi.Desktop.Services;
using VibeUi.Desktop.ViewModels;
using VibeUi.Desktop.Views;

namespace VibeUi.Desktop;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = DesktopSettings.Load();
            var model = new MainViewModel(
                locateAgent: () => VibeAcpLauncher.Locate(),
                startAgent: (executable, workspace, handler, log) => VibeAcpLauncher.Start(executable, workspace, handler, log),
                openUrl: SystemBrowser.Open,
                dispatch: action => Dispatcher.UIThread.Post(action),
                settings: settings,
                clientVersion: typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
            desktop.MainWindow = new MainWindow { DataContext = model };
            desktop.Exit += (_, _) => model.ShutdownAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
