using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ShareWorkin.SMB;

namespace ShareWorkin;

public partial class App : System.Windows.Application
{
    internal const string AppUserModelId = SwkNotificationIdentity.AppUserModelId;

    private const string SingleInstanceMutexName = "Local\\ShareWorkin_SingleInstance_1";
    private const string ActivateWindowMessageName = "ShareWorkin_ActivateMainWindow_1";
    internal static uint ActivateWindowMessage { get; private set; }

    private static Mutex? _instanceMutex;
    private static bool _mutexAcquired;

    private const int TokenAssignPrimary = 0x0001;
    private const int TokenDuplicate = 0x0002;
    private const int TokenQuery = 0x0008;
    private const int TokenAdjustDefault = 0x0080;
    private const int TokenAdjustSessionId = 0x0100;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int LogonWithProfile = 0x00000001;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingTokenHandle,
        int desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr duplicateTokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr token,
        int logonFlags,
        string? applicationName,
        string commandLine,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUserW(
        IntPtr token,
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (ShouldRelaunchUnelevated(e.Args) && TryRelaunchUnelevated(e.Args))
        {
            Shutdown();
            return;
        }

        ActivateWindowMessage = RegisterWindowMessage(ActivateWindowMessageName);
        _instanceMutex = new Mutex(false, SingleInstanceMutexName);
        bool acquired;
        try { acquired = _instanceMutex.WaitOne(0, false); }
        catch (AbandonedMutexException) { acquired = true; }

        if (!acquired)
        {
            PostMessage(new IntPtr(-1) /* HWND_BROADCAST */, ActivateWindowMessage, IntPtr.Zero, IntPtr.Zero);
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }
        _mutexAcquired = true;

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

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutexAcquired)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch (Exception) { }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static bool ShouldRelaunchUnelevated(string[] args)
    {
        if (args.Any(static arg => string.Equals(arg, "--swk-unelevated", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool TryRelaunchUnelevated(string[] args)
    {
        IntPtr shellProcessHandle = IntPtr.Zero;
        IntPtr shellToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        PROCESS_INFORMATION processInfo = default;

        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return false;
            }

            IntPtr shellWindow = GetShellWindow();
            if (shellWindow == IntPtr.Zero)
            {
                return false;
            }

            GetWindowThreadProcessId(shellWindow, out uint shellProcessId);
            if (shellProcessId == 0)
            {
                SwkLogger.Warn("TryRelaunchUnelevated failed: shell process id was 0");
                return false;
            }

            shellProcessHandle = OpenProcess(0x0400, false, shellProcessId);
            if (shellProcessHandle == IntPtr.Zero)
            {
                SwkLogger.Warn($"TryRelaunchUnelevated: OpenProcess failed ({Marshal.GetLastWin32Error()})");
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!OpenProcessToken(
                    shellProcessHandle,
                    TokenAssignPrimary | TokenDuplicate | TokenQuery | TokenAdjustDefault | TokenAdjustSessionId,
                    out shellToken))
            {
                SwkLogger.Warn($"TryRelaunchUnelevated: OpenProcessToken failed ({Marshal.GetLastWin32Error()})");
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!DuplicateTokenEx(
                    shellToken,
                    TokenAssignPrimary | TokenDuplicate | TokenQuery | TokenAdjustDefault | TokenAdjustSessionId,
                    IntPtr.Zero,
                    2,
                    1,
                    out primaryToken))
            {
                SwkLogger.Warn($"TryRelaunchUnelevated: DuplicateTokenEx failed ({Marshal.GetLastWin32Error()})");
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            string workingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
            string commandLine = BuildCommandLine(exePath, args.Append("--swk-unelevated"));

            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
            {
                SwkLogger.Warn($"TryRelaunchUnelevated: CreateEnvironmentBlock failed ({Marshal.GetLastWin32Error()})");
                environment = IntPtr.Zero;
            }

            STARTUPINFO startupInfo = new()
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default"
            };

            if (!CreateProcessWithTokenW(
                    primaryToken,
                    LogonWithProfile,
                    null,
                    commandLine,
                    CreateUnicodeEnvironment,
                    environment,
                    workingDirectory,
                    ref startupInfo,
                    out processInfo))
            {
                int withTokenError = Marshal.GetLastWin32Error();
                SwkLogger.Warn($"TryRelaunchUnelevated: CreateProcessWithTokenW failed ({withTokenError}), trying CreateProcessAsUserW");

                if (!CreateProcessAsUserW(
                        primaryToken,
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        CreateUnicodeEnvironment,
                        environment,
                        workingDirectory,
                        ref startupInfo,
                        out processInfo))
                {
                    SwkLogger.Warn($"TryRelaunchUnelevated: CreateProcessAsUserW failed ({Marshal.GetLastWin32Error()})");
                    throw new Win32Exception(withTokenError);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"TryRelaunchUnelevated failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (processInfo.hThread != IntPtr.Zero) CloseHandle(processInfo.hThread);
            if (processInfo.hProcess != IntPtr.Zero) CloseHandle(processInfo.hProcess);
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (shellToken != IntPtr.Zero) CloseHandle(shellToken);
            if (shellProcessHandle != IntPtr.Zero) CloseHandle(shellProcessHandle);
        }
    }

    private static string BuildCommandLine(string exePath, IEnumerable<string> args)
        => string.Join(" ", new[] { QuoteArgument(exePath) }.Concat(args.Select(QuoteArgument)));

    private static string QuoteArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return "\"\"";
        }

        if (!arg.Any(static ch => char.IsWhiteSpace(ch) || ch == '"'))
        {
            return arg;
        }

        return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
