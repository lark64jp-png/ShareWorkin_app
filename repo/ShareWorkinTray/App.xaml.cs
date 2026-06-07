using System.Windows;
using System.Linq;
using System.Security.Principal;
using ShareWorkin.SMB;

namespace ShareWorkinTray;

public partial class App : System.Windows.Application
{
    private TrayApp? _trayApp;

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        string startupSource = e.Args.FirstOrDefault(arg => arg.StartsWith("--startup-source=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2).LastOrDefault() ?? "unknown";
        SwkLogger.Info(
            $"ShareWorkinTray startup: elevated={IsRunningAsAdmin()} source={startupSource} " +
            $"processPath={Environment.ProcessPath ?? "null"} args={string.Join(" ", e.Args)}");
        _trayApp = new TrayApp();
        _trayApp.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayApp?.Dispose();
        base.OnExit(e);
    }

    private static bool IsRunningAsAdmin()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
