using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Xml.Linq;
using ShareWorkin.SMB;
using Windows.Data.Xml.Dom;
using Windows.Foundation;
using Windows.UI.Notifications;

namespace ShareWorkinTray;

internal static class WindowsToastNotificationService
{
    private const string AppUserModelId = SwkNotificationIdentity.AppUserModelId;
    private const string ShortcutName = "ShareWorkin.lnk";
    private static readonly TimeSpan ToastFailureDetectionTimeout = TimeSpan.FromMilliseconds(1500);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    private static bool _initialized;
    private static bool _isAvailable;

    public static bool Initialize()
    {
        if (_initialized)
        {
            return _isAvailable;
        }

        _initialized = true;

        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            EnsureStartMenuShortcut();
            _isAvailable = true;
            SwkLogger.Info(
                $"WindowsToastNotificationService initialized: appId={AppUserModelId} processPath={Environment.ProcessPath ?? "null"} " +
                $"shortcutPath={GetShortcutPath()}");
            return true;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"WindowsToastNotificationService.Initialize failed: {ex.Message}");
            _isAvailable = false;
            return false;
        }
    }

    public static bool TryShow(string title, string text)
    {
        if (!_initialized && !Initialize())
        {
            return false;
        }

        if (!_isAvailable)
        {
            return false;
        }

        try
        {
            string xml = BuildToastXml(title, text);
            return ShowDirectToast(xml, title);
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"WindowsToastNotificationService.TryShow failed: {ex.Message}");
            return false;
        }
    }

    private static string BuildToastXml(string title, string text)
    {
        return
            "<toast scenario=\"default\"><visual><binding template=\"ToastGeneric\">" +
            $"<text>{System.Security.SecurityElement.Escape(title) ?? string.Empty}</text>" +
            $"<text>{System.Security.SecurityElement.Escape(text) ?? string.Empty}</text>" +
            "</binding></visual></toast>";
    }

    private static bool ShowDirectToast(string xml, string title)
    {
        TaskCompletionSource<ToastFailedEventArgs>? failedTcs = null;
        ToastNotification? toast = null;
        TypedEventHandler<ToastNotification, object>? activatedHandler = null;
        TypedEventHandler<ToastNotification, ToastDismissedEventArgs>? dismissedHandler = null;
        TypedEventHandler<ToastNotification, ToastFailedEventArgs>? failedHandler = null;

        try
        {
            SwkLogger.Info(
                $"WindowsToastNotificationService.ShowDirectToast start: appId={AppUserModelId} " +
                $"processPath={Environment.ProcessPath ?? "null"} shortcutPath={GetShortcutPath()} title={title}");

            SwkLogger.Info("WindowsToastNotificationService.ShowDirectToast step=create-xmldocument:start");
            var xmlDocument = new XmlDocument();
            SwkLogger.Info("WindowsToastNotificationService.ShowDirectToast step=create-xmldocument:done");

            SwkLogger.Info("WindowsToastNotificationService.ShowDirectToast step=loadxml:start");
            xmlDocument.LoadXml(xml);
            SwkLogger.Info("WindowsToastNotificationService.ShowDirectToast step=loadxml:done");

            SwkLogger.Info("WindowsToastNotificationService.ShowDirectToast step=create-toast:start");
            toast = new ToastNotification(xmlDocument)
            {
                ExpirationTime = DateTimeOffset.Now.AddMinutes(10)
            };
            SwkLogger.Info("WindowsToastNotificationService.ShowDirectToast step=create-toast:done");

            activatedHandler = (_, _) => OnToastActivated();
            dismissedHandler = (_, args) => OnToastDismissed(args.Reason.ToString());
            failedTcs = new TaskCompletionSource<ToastFailedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            failedHandler = (_, args) =>
            {
                OnToastFailed(args.ErrorCode);
                failedTcs.TrySetResult(args);
            };

            toast.Activated += activatedHandler;
            toast.Dismissed += dismissedHandler;
            toast.Failed += failedHandler;

            SwkLogger.Info("WindowsToastNotificationService.ShowDirectToast step=create-notifier:start");
            SwkLogger.Info($"WindowsToastNotificationService.ShowDirectToast notifierAppId={AppUserModelId}");
            ToastNotifier notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
            SwkLogger.Info($"WindowsToastNotificationService.ShowDirectToast step=create-notifier:done notifierType={notifier.GetType().FullName}");

            SwkLogger.Info("WindowsToastNotificationService.ShowDirectToast step=show:start");
            notifier.Show(toast);
            SwkLogger.Info("WindowsToastNotificationService.ShowDirectToast step=show:done");

            Task completedTask = Task.WhenAny(failedTcs.Task, Task.Delay(ToastFailureDetectionTimeout)).GetAwaiter().GetResult();
            if (completedTask == failedTcs.Task)
            {
                SwkLogger.Warn("WindowsToastNotificationService.ShowDirectToast result=failed-detected");
                return false;
            }

            SwkLogger.Info(
                $"WindowsToastNotificationService.ShowDirectToast requested: appId={AppUserModelId} " +
                $"processPath={Environment.ProcessPath ?? "null"} title={title}");
            return true;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn(
                $"WindowsToastNotificationService.ShowDirectToast failed: type={ex.GetType().FullName} " +
                $"message={ex.Message} stack={ex.StackTrace ?? "null"}");
            throw;
        }
        finally
        {
            if (toast is not null)
            {
                if (activatedHandler is not null)
                {
                    toast.Activated -= activatedHandler;
                }

                if (dismissedHandler is not null)
                {
                    toast.Dismissed -= dismissedHandler;
                }

                if (failedHandler is not null)
                {
                    toast.Failed -= failedHandler;
                }
            }
        }
    }

    private static void OnToastActivated()
    {
        SwkLogger.Info($"WindowsToastNotificationService.ToastActivated: appId={AppUserModelId}");
    }

    private static void OnToastDismissed(string reason)
    {
        SwkLogger.Info($"WindowsToastNotificationService.ToastDismissed: appId={AppUserModelId} reason={reason}");
    }

    private static void OnToastFailed(Exception error)
    {
        SwkLogger.Warn(
            $"WindowsToastNotificationService.ToastFailed: appId={AppUserModelId} " +
            $"hr=0x{error.HResult:X8} error={error} message={error.Message}");
    }

    private static void EnsureStartMenuShortcut()
    {
        string shortcutPath = GetShortcutPath();
        string executablePath = ResolveNotificationShortcutExecutablePath();

        if (File.Exists(shortcutPath))
        {
            SwkLogger.Info($"WindowsToastNotificationService shortcut refresh: path={shortcutPath} reason=force-rewrite");
            File.Delete(shortcutPath);
        }

        CreateShortcut(shortcutPath, executablePath);
        SwkLogger.Info(
            $"WindowsToastNotificationService shortcut written: path={shortcutPath} " +
            $"target={executablePath} appId={AppUserModelId}");
    }

    private static string ResolveNotificationShortcutExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("Process path could not be resolved.");
        }

        string trayExecutablePath = Path.GetFullPath(processPath);
        if (File.Exists(trayExecutablePath))
        {
            return trayExecutablePath;
        }

        string? processDirectory = Path.GetDirectoryName(trayExecutablePath);
        if (string.IsNullOrWhiteSpace(processDirectory))
        {
            throw new InvalidOperationException("Process directory could not be resolved.");
        }

        DirectoryInfo? current = new DirectoryInfo(processDirectory);
        while (current is not null)
        {
            string siblingBuildTrayPath = Path.Combine(
                current.FullName,
                "ShareWorkinTray",
                "bin",
                "Debug",
                "net8.0-windows10.0.19041.0",
                "ShareWorkinTray.exe");
            if (File.Exists(siblingBuildTrayPath))
            {
                return siblingBuildTrayPath;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("ShareWorkinTray.exe for notification shortcut could not be resolved.");
    }

    private static string GetShortcutPath()
    {
        string startMenuPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        return Path.Combine(startMenuPrograms, ShortcutName);
    }

    private static void CreateShortcut(string shortcutPath, string executablePath)
    {
        object shellLink = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)
            ?? throw new InvalidOperationException("ShellLink COM object could not be created.");
        IPropertyStore? propertyStore = null;
        try
        {
            IShellLinkW shellLinkInterface = (IShellLinkW)shellLink;
            shellLinkInterface.SetPath(executablePath);
            shellLinkInterface.SetWorkingDirectory(Path.GetDirectoryName(executablePath));
            shellLinkInterface.SetIconLocation(executablePath, 0);
            shellLinkInterface.SetDescription("ShareWorkin");

            propertyStore = (IPropertyStore)shellLink;
            using (PropVariant appId = new(AppUserModelId))
            {
                PropertyKey appUserModelId = PropertyKeys.AppUserModelId;
                propertyStore.SetValue(ref appUserModelId, appId);
            }
            propertyStore.Commit();

            ((IPersistFile)shellLink).Save(shortcutPath, true);
        }
        catch (Exception ex)
        {
            SwkLogger.Warn(
                $"WindowsToastNotificationService.CreateShortcut failed: type={ex.GetType().FullName} " +
                $"message={ex.Message}");
            throw;
        }
        finally
        {
            if (propertyStore is not null)
            {
                Marshal.ReleaseComObject(propertyStore);
            }
            Marshal.ReleaseComObject(shellLink);
        }
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, out WIN32_FIND_DATAW pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string? pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        uint GetCount(out uint cProps);
        uint GetAt(uint iProp, out PropertyKey pkey);
        uint GetValue(ref PropertyKey key, out PropVariant pv);
        uint SetValue(ref PropertyKey key, [In] PropVariant pv);
        uint Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(Guid fmtid, uint pid)
    {
        public readonly Guid FormatId = fmtid;
        public readonly uint PropertyId = pid;
    }

    private static class PropertyKeys
    {
        public static readonly PropertyKey AppUserModelId = new(
            new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            5);
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class PropVariant : IDisposable
    {
        private ushort _valueType;
        private ushort _reserved1;
        private ushort _reserved2;
        private ushort _reserved3;
        private IntPtr _pointerValue;
        private int _valueData;

        public PropVariant(string value)
        {
            _valueType = 31;
            _pointerValue = Marshal.StringToCoTaskMemUni(value);
        }

        public string? GetValue()
        {
            return _valueType == 31 && _pointerValue != IntPtr.Zero
                ? Marshal.PtrToStringUni(_pointerValue)
                : null;
        }

        public void Dispose()
        {
            PropVariantClear(this);
            GC.SuppressFinalize(this);
        }

        ~PropVariant()
        {
            Dispose();
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear([In, Out] PropVariant pvar);
}
