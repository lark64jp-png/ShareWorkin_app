using System.Windows;

namespace ShareWorkinTray;

public partial class App : System.Windows.Application
{
    private TrayApp? _trayApp;

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        _trayApp = new TrayApp();
        _trayApp.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayApp?.Dispose();
        base.OnExit(e);
    }
}
