using System.Runtime.InteropServices;
using System.Windows;

namespace ShareWorkin;

public partial class App : System.Windows.Application
{
    internal const string AppUserModelId = "ShareWorkin.MediaHouse";

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);

    protected override void OnStartup(StartupEventArgs e)
    {
        SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        base.OnStartup(e);
    }
}
