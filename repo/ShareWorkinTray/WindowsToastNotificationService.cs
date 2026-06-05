using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Xml.Linq;
using ShareWorkin.SMB;

namespace ShareWorkinTray;

internal static class WindowsToastNotificationService
{
    private const string AppUserModelId = "ShareWorkin.MediaHouse";
    private const string ShortcutName = "ShareWorkin.lnk";

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
        var toast = new XElement("toast",
            new XAttribute("scenario", "default"),
            new XElement("visual",
                new XElement("binding",
                    new XAttribute("template", "ToastGeneric"),
                    new XElement("text", title ?? string.Empty),
                    new XElement("text", text ?? string.Empty))));

        return toast.ToString(SaveOptions.DisableFormatting);
    }

    private static bool ShowDirectToast(string xml, string title)
    {
        SwkLogger.Info(
            $"WindowsToastNotificationService.ShowDirectToast start: appId={AppUserModelId} " +
            $"processPath={Environment.ProcessPath ?? "null"} shortcutPath={GetShortcutPath()} title={title}");

        Type xmlDocumentType = Type.GetType(
            "Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType=WindowsRuntime",
            throwOnError: true)!;
        object xmlDocument = Activator.CreateInstance(xmlDocumentType)!;
        xmlDocumentType.GetMethod("LoadXml")!.Invoke(xmlDocument, [xml]);

        Type toastNotificationType = Type.GetType(
            "Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType=WindowsRuntime",
            throwOnError: true)!;
        object toast = Activator.CreateInstance(toastNotificationType, xmlDocument)!;
        toastNotificationType.GetProperty("ExpirationTime")!.SetValue(toast, DateTimeOffset.Now.AddMinutes(10));

        AttachToastEventHandler(toast, toastNotificationType, "Activated", nameof(OnToastActivated));
        AttachToastEventHandler(toast, toastNotificationType, "Dismissed", nameof(OnToastDismissed));
        AttachToastEventHandler(toast, toastNotificationType, "Failed", nameof(OnToastFailed));

        Type toastManagerType = Type.GetType(
            "Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime",
            throwOnError: true)!;
        object notifier = toastManagerType.GetMethod("CreateToastNotifier", [typeof(string)])!
            .Invoke(null, [AppUserModelId])!;
        notifier.GetType().GetMethod("Show")!.Invoke(notifier, [toast]);
        SwkLogger.Info(
            $"WindowsToastNotificationService.ShowDirectToast requested: appId={AppUserModelId} " +
            $"processPath={Environment.ProcessPath ?? "null"} title={title}");
        return true;
    }

    private static void AttachToastEventHandler(object toast, Type toastNotificationType, string eventName, string handlerMethodName)
    {
        EventInfo? eventInfo = toastNotificationType.GetEvent(eventName);
        if (eventInfo?.EventHandlerType is null)
        {
            SwkLogger.Warn($"WindowsToastNotificationService.AttachToastEventHandler skipped: event={eventName}");
            return;
        }

        MethodInfo handlerMethod = typeof(WindowsToastNotificationService).GetMethod(
            handlerMethodName,
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Delegate handler = Delegate.CreateDelegate(eventInfo.EventHandlerType, handlerMethod);
        eventInfo.AddEventHandler(toast, handler);
    }

    private static void OnToastActivated(object sender, object args)
    {
        SwkLogger.Info($"WindowsToastNotificationService.ToastActivated: appId={AppUserModelId}");
    }

    private static void OnToastDismissed(object sender, object args)
    {
        string reason = args.GetType().GetProperty("Reason")?.GetValue(args)?.ToString() ?? "unknown";
        SwkLogger.Info($"WindowsToastNotificationService.ToastDismissed: appId={AppUserModelId} reason={reason}");
    }

    private static void OnToastFailed(object sender, object args)
    {
        object? error = args.GetType().GetProperty("ErrorCode")?.GetValue(args);
        string errorText = error?.ToString() ?? "unknown";
        string? message = error?.GetType().GetProperty("Message")?.GetValue(error)?.ToString();
        int? hResult = error?.GetType().GetProperty("HResult")?.GetValue(error) as int?;

        SwkLogger.Warn(
            $"WindowsToastNotificationService.ToastFailed: appId={AppUserModelId} " +
            $"hr={(hResult.HasValue ? $"0x{hResult.Value:X8}" : "unknown")} error={errorText} message={message ?? "null"}");
    }

    private static void EnsureStartMenuShortcut()
    {
        string shortcutPath = GetShortcutPath();
        string executablePath = ResolveNotificationShortcutExecutablePath();

        if (ShortcutMatchesExpectedConfiguration(shortcutPath, executablePath))
        {
            SwkLogger.Info($"WindowsToastNotificationService shortcut kept: path={shortcutPath} target={executablePath} appId={AppUserModelId}");
            return;
        }

        if (File.Exists(shortcutPath))
        {
            SwkLogger.Info($"WindowsToastNotificationService shortcut recreated: path={shortcutPath} reason=configuration-mismatch");
            File.Delete(shortcutPath);
        }

        CreateShortcut(shortcutPath, executablePath);
        SwkLogger.Info(
            $"WindowsToastNotificationService shortcut created: path={shortcutPath} target={executablePath} appId={AppUserModelId}");
    }

    private static string ResolveNotificationShortcutExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        string? processDirectory = Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(processDirectory))
        {
            throw new InvalidOperationException("Process path could not be resolved.");
        }

        string sameDirectoryUiPath = Path.Combine(processDirectory, "ShareWorkin.exe");
        if (File.Exists(sameDirectoryUiPath))
        {
            return sameDirectoryUiPath;
        }

        DirectoryInfo? current = new DirectoryInfo(processDirectory);
        while (current is not null)
        {
            string siblingBuildUiPath = Path.Combine(
                current.FullName,
                "ShareWorkin",
                "bin",
                "Debug",
                "net8.0-windows",
                "ShareWorkin.exe");
            if (File.Exists(siblingBuildUiPath))
            {
                return siblingBuildUiPath;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("ShareWorkin.exe for notification shortcut could not be resolved.");
    }

    private static string GetShortcutPath()
    {
        string startMenuPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        return Path.Combine(startMenuPrograms, ShortcutName);
    }

    private static bool ShortcutMatchesExpectedConfiguration(string shortcutPath, string executablePath)
    {
        object? shellLink = null;
        IShellLinkW? shellLinkInterface = null;
        IPersistFile? persistFile = null;
        IPropertyStore? propertyStore = null;

        try
        {
            shellLink = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)
                ?? throw new InvalidOperationException("ShellLink COM object could not be created.");
            shellLinkInterface = (IShellLinkW)shellLink;
            persistFile = (IPersistFile)shellLink;
            persistFile.Load(shortcutPath, 0);
            propertyStore = (IPropertyStore)shellLink;

            var pathBuffer = new System.Text.StringBuilder(260);
            shellLinkInterface.GetPath(pathBuffer, pathBuffer.Capacity, out _, 0);
            string? existingTarget = pathBuffer.Length > 0 ? pathBuffer.ToString() : null;

            PropertyKey appUserModelId = PropertyKeys.AppUserModelId;
            propertyStore.GetValue(ref appUserModelId, out PropVariant value);
            using (value)
            {
                string? existing = value.GetValue();
                return string.Equals(existing, AppUserModelId, StringComparison.Ordinal) &&
                       PathsEqual(existingTarget, executablePath);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (propertyStore is not null) Marshal.ReleaseComObject(propertyStore);
            if (persistFile is not null) Marshal.ReleaseComObject(persistFile);
            if (shellLink is not null) Marshal.ReleaseComObject(shellLink);
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void CreateShortcut(string shortcutPath, string executablePath)
    {
        object shellLink = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)
            ?? throw new InvalidOperationException("ShellLink COM object could not be created.");
        try
        {
            IShellLinkW shellLinkInterface = (IShellLinkW)shellLink;
            shellLinkInterface.SetPath(executablePath);
            shellLinkInterface.SetWorkingDirectory(Path.GetDirectoryName(executablePath));
            shellLinkInterface.SetIconLocation(executablePath, 0);
            shellLinkInterface.SetDescription("ShareWorkin");

            IPropertyStore propertyStore = (IPropertyStore)shellLink;
            using (PropVariant appId = new(AppUserModelId))
            {
                PropertyKey appUserModelId = PropertyKeys.AppUserModelId;
                propertyStore.SetValue(ref appUserModelId, appId);
            }
            propertyStore.Commit();

            ((IPersistFile)shellLink).Save(shortcutPath, true);
            Marshal.ReleaseComObject(propertyStore);
        }
        finally
        {
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
