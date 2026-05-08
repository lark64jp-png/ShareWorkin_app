using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using ShareWorkin.SMB;

namespace ShareWorkin;

public partial class App : System.Windows.Application
{
    internal const string AppUserModelId = "ShareWorkin.MediaHouse";

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            SwkLogger.Error("Unhandled dispatcher exception", args.Exception);
            args.Handled = true;
            System.Windows.MessageBox.Show(
                "ShareWorkin の画面処理で問題が起きました。アプリは続行します。\nもう一度操作してください。",
                "ShareWorkin",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                SwkLogger.Error("Unhandled app-domain exception", ex);
            }
            else
            {
                SwkLogger.Error($"Unhandled app-domain exception: {args.ExceptionObject}");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            SwkLogger.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        base.OnStartup(e);
    }
}
