using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using ShareWorkin.SMB;
using Forms = System.Windows.Forms;

namespace ShareWorkin;

public partial class MainWindow : Window
{
    private const string OwnerCertificateMismatchMessage = "店主の証明書が以前と違います。乗っ取りの可能性があるため接続を中止しました。";
    private static readonly System.Windows.Media.Brush UnselectedShopBackgroundBrush =
        new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 248, 204));

    private const int MaxArrivedItemCount = 100;
    private const string HoldFolderName = "保留";
    internal const string InternalDragPathFormat = "ShareWorkin.InternalPath";
    internal const string InternalDragPathsFormat = "ShareWorkin.InternalPaths";
    private static readonly bool EnableExplorerOleDropTarget = true;

    // Explorer からのドロップを受けやすくするため、WM_DROPFILES 系メッセージも許可しておく。
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(
        IntPtr hwnd, uint message, uint action, ref CHANGEFILTERSTRUCT pChangeFilterStruct);
    [StructLayout(LayoutKind.Sequential)]
    private struct CHANGEFILTERSTRUCT { public uint cbSize; public uint ExtStatus; }
    private const uint MSGFLT_ALLOW = 1;
    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_COPYGLOBALDATA = 0x0049;
    private const uint WM_COPYDATA = 0x004A;
    private const uint SHCNE_RENAMEITEM = 0x00000001;
    private const uint SHCNE_CREATE = 0x00000002;
    private const uint SHCNE_DELETE = 0x00000004;
    private const uint SHCNE_ATTRIBUTES = 0x00000800;
    private const uint SHCNE_UPDATEDIR = 0x00001000;
    private const uint SHCNE_UPDATEITEM = 0x00002000;
    private const uint SHCNE_RENAMEFOLDER = 0x00020000;
    private const uint SHCNF_PATHW = 0x0005;

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, System.Text.StringBuilder? lpszFile, uint cch);
    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, string? dwItem1, string? dwItem2);
    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(IntPtr hwnd);
    [DllImport("ole32.dll")]
    private static extern int RegisterDragDrop(IntPtr hwnd, IOleDropTarget pDropTarget);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private static readonly TimeSpan NotificationQuietTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TransientStatusDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan FriendReconnectRetryCooldown = TimeSpan.FromSeconds(8);
    private const int FriendShopOfflineMissThreshold = 2;

    // 草案4 §A: アプリは自分のアプリホルダーの外に書き込まない。
    // すべてのデータ(settings / secure / hold / logs)はアプリホルダー直下に置く。
    private static readonly string AppHomeDirectory = AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static readonly string SettingsPath = Path.Combine(AppHomeDirectory, "settings.json");
    private static readonly string PermissionsPath = Path.Combine(AppHomeDirectory, "permissions.json");

    private static readonly string DefaultHoldFolderPath = Path.Combine(AppHomeDirectory, "hold");

    // v1.04 までは %LocalAppData%\ShareWorkin に置いていた。アップグレード時に一度だけ移行する。
    private static readonly string LegacyLocalAppDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShareWorkin");

    private static readonly string LegacySettingsPath = Path.Combine(LegacyLocalAppDataDirectory, "settings.json");

    private static readonly string LegacyHoldFolderPath = Path.Combine(LegacyLocalAppDataDirectory, "hold");

    private const string SettingsVersion = "1.04";

    private readonly UiPipeClient _pipeClient = new();
    private readonly DispatcherTimer _notificationTimer;
    private readonly DispatcherTimer _pollingTimer;
    private readonly DispatcherTimer _transientStatusTimer;
    private readonly List<ArrivedItem> _pendingNotificationItems = [];
    private readonly Dictionary<string, DateTime> _recentExternalReceiveAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _recentIncomingInteractionAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (bool IsReadOnly, bool IsSharedOff)> _friendShopReadOnlyState = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private readonly Dictionary<string, long> _folderSizeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _subfolderCountCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<HistoryChannel, HistoryWindow> _historyWindows = [];
    private readonly HashSet<string> _friendRefreshInFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _friendRefreshCooldownUntil = new(StringComparer.Ordinal);
    // セッション中のアイテム別許可設定。AllowedUsers は ShopItem ごとに in-memory のため
    // ナビゲーションで再生成されるたびに消えるのを防ぐ。キー = FullPath。
    private readonly Dictionary<string, (List<string> Users, bool IsReadOnly, bool IsSharedOff)> _permissionMap
        = new(StringComparer.OrdinalIgnoreCase);
    // 保留中は実権限を OFF に固定しつつ、移動前に見えていた共有設定を表示・復帰判定用に保持する。
    private readonly Dictionary<string, (List<string> Users, bool IsReadOnly, bool IsSharedOff)> _holdDisplayPermissionMap
        = new(StringComparer.OrdinalIgnoreCase);
    // 現在フォルダーを基点に祖先を遡って得た有効な許可設定（継承用）。
    private (List<string> Users, bool IsReadOnly, bool IsSharedOff)? _effectiveParentPerm;
    private FileSystemWatcher? _arrivalSensor;
    private FileSystemWatcher? _contentsSensor;
    private CancellationTokenSource? _folderSizeCancellation;
    private CancellationTokenSource? _subfolderCountCancellation;
    private DispatcherTimer? _friendShopPollTimer;
    private string? _shopFolder;
    private string? _currentFolder;
    private string? _activeFriendShopRootPath;
    private string? _pcOwnerSid;
    private string? _pcOwnerAccount;
    private string? _pendingFocusName;
    private ShopItem? _dropTargetItem;
    private Popup? _dragPreviewPopup;
    private TextBlock? _dragPreviewTextBlock;
    private ExplorerOleDropTarget? _explorerOleDropTarget;
    private string[]? _activeInternalDragPaths;
    private bool _externalDragDropInitialized;
    private System.Windows.Point _dragStartPoint;
    private ShopItem? _dragStartItem;
    private bool _isRubberBanding;
    private System.Windows.Point _rubberBandOrigin;
    private System.Windows.Threading.DispatcherTimer? _renameTimer;
    private ShopItem? _renameTimerTarget;
    private DispatcherTimer? _deferredRefreshTimer;
    private string? _deferredRefreshFolderPath;
    private string _breadcrumbFullText = string.Empty;
    private DateTime _suppressExternalChangeNotificationsUntil = DateTime.MinValue;
    private DateTime _lastExternalChangeNotificationAt = DateTime.MinValue;
    private JsonElement? _loadedReservedForV22;
    private NotificationMode _notificationMode = NotificationMode.All;
    private bool _isSizeCalcEnabled;
    private bool _isShopOpen;
    private bool _isPollingMode;
    private bool _exitRequested;
    private bool _uiUnlocked;
    private bool _startupHandled;
    private bool _wasOpenAtLastShutdown;
    private ShareAccessRight _shareAccessRight = ShareAccessRight.Full;
    private DisplayMode _currentMode = DisplayMode.Shop;
    private Friend? _activeFriendShop;
    private bool _suppressDropdownChange;
    private bool _clickSelectionPending;
    private bool _itemWasSelectedAtPress;
    private bool _shopItemsContextMenuCommandArmed;
    private bool _suppressInternalDragUntilLeftButtonRelease;
    private DateTime _ignoreShopItemsCommandsUntilUtc = DateTime.MinValue;
    private ShopSortField _sortField = ShopSortField.Name;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;
    private ShopItem? _permissionPopupTarget;
    private System.Windows.Controls.Button? _permissionPopupAnchor;
    private bool _permissionPopupReadOnly;
    private string _permissionPopupBeforeStatus = string.Empty;
    private bool _permissionPopupInitializing;
    private bool _permissionPopupBound;
    private readonly List<string> _permissionPopupInitialUsers = [];
    private bool _permissionPopupInitialReadOnly;
    private bool _permissionPopupInitialSharedOff;
    private readonly ObservableCollection<string> _permissionAllowed = new();
    private readonly ObservableCollection<string> _permissionUnset = new();
    private string? _pendingInteractionMessage;
    private int _processingDepth;
    private int _deferredPermissionSaveDepth;
    private bool _permissionSavePending;

    public ObservableCollection<ArrivedItem> ArrivedItems { get; } = [];

    public ObservableCollection<ShopItem> ShopItems { get; } = [];

    public ObservableCollection<SidebarRow> SidebarItems { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _pipeClient.TrayExiting += () =>
            _ = Dispatcher.InvokeAsync(() =>
            {
                _exitRequested = true;
                Close();
            }, DispatcherPriority.Background);
        _pipeClient.ShowRequested += () =>
            _ = Dispatcher.InvokeAsync(ShowMainWindow, DispatcherPriority.Background);
        _pipeClient.FriendShopClosingReceived += (machine, share) =>
            _ = Dispatcher.InvokeAsync(
                () => HandleFriendShopClosingReceived(machine, share),
                DispatcherPriority.Background);
        _pipeClient.IncomingInteractionReceived += entry =>
            _ = Dispatcher.InvokeAsync(
                () => AcceptIncomingInteraction(entry),
                DispatcherPriority.Background);

        _notificationTimer = new DispatcherTimer { Interval = NotificationQuietTime };
        _notificationTimer.Tick += NotificationTimer_Tick;

        _pollingTimer = new DispatcherTimer { Interval = PollingInterval };
        _pollingTimer.Tick += PollingTimer_Tick;

        _transientStatusTimer = new DispatcherTimer { Interval = TransientStatusDuration };
        _transientStatusTimer.Tick += TransientStatusTimer_Tick;

        LoadSettings();
        LoadPermissionMap();
        NotificationModeComboBox.SelectionChanged += NotificationModeComboBox_SelectionChanged;

        // ExplorerTargetComboBox.SelectionChanged は XAML 側で登録済み (二重発火防止のため code-behind 登録は外す)
        InitializeExplorerDropdownForStartup();
        UpdateShopState(false);
        UpdateColumnHeaders();
        string? ver = (System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                typeof(MainWindow).Assembly))
            ?.InformationalVersion;
        AppVersionTextBlock.Text = FormatVersionLabel(ver);
        AllowDrop = true;
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
    }

    // InformationalVersion の "+<git sha>[.dirty]" を短く表示する。
    // SHAが付かないビルド（git なし環境）では "v<ver>" だけ返す。
    private static string FormatVersionLabel(string? ver)
    {
        if (string.IsNullOrEmpty(ver)) return string.Empty;
        int plus = ver.IndexOf('+');
        if (plus < 0) return $"v{ver}";
        string baseVer = ver[..plus];
        string metadata = ver[(plus + 1)..];
        bool dirty = metadata.Contains("dirty", StringComparison.OrdinalIgnoreCase);
        string sha = metadata.Split('.')[0];
        if (sha.Length > 7) sha = sha[..7];
        if (string.IsNullOrEmpty(sha)) return $"v{baseVer}";
        return dirty ? $"v{baseVer}+{sha}-dirty" : $"v{baseVer}+{sha}";
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        InitializeExternalDragDrop();
    }

    private void InitializeExternalDragDrop()
    {
        if (_externalDragDropInitialized)
        {
            return;
        }

        AllowExternalDragDrop();
        if (EnableExplorerOleDropTarget)
        {
            RegisterExplorerOleDropTarget();
        }
        _externalDragDropInitialized = true;
    }


    private void AllowExternalDragDrop()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        CHANGEFILTERSTRUCT cfs = new() { cbSize = (uint)Marshal.SizeOf<CHANGEFILTERSTRUCT>() };
        ChangeWindowMessageFilterEx(handle, WM_DROPFILES,      MSGFLT_ALLOW, ref cfs);
        ChangeWindowMessageFilterEx(handle, WM_COPYGLOBALDATA, MSGFLT_ALLOW, ref cfs);
        ChangeWindowMessageFilterEx(handle, WM_COPYDATA,       MSGFLT_ALLOW, ref cfs);
        DragAcceptFiles(handle, true);
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)WM_DROPFILES)
        {
            HandleWmDropFiles(wParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void HandleWmDropFiles(IntPtr hDrop)
    {
        uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
        var paths = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            uint len = DragQueryFile(hDrop, i, null, 0);
            if (len == 0) continue;
            var sb = new System.Text.StringBuilder((int)(len + 1));
            DragQueryFile(hDrop, i, sb, len + 1);
            paths.Add(sb.ToString());
        }
        DragFinish(hDrop);

        if (paths.Count == 0 || string.IsNullOrWhiteSpace(_currentFolder)) return;
        string destination = _currentFolder!;
        try
        {
            if (GetCursorPos(out POINT cursorPos))
            {
                destination = ResolveExternalDropDestinationFromScreenPoint(
                    new System.Windows.Point(cursorPos.X, cursorPos.Y)) ?? destination;
            }
        }
        catch
        {
        }

        string capturedDestination = destination;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            BeginProcessing();
            try
            {
                SwkLogger.Info(
                    $"Trace.ExternalFlow.Sender.Entry: route=WM_DROPFILES count={paths.Count} names={DescribeExternalPaths(paths)} dest={capturedDestination}");
                await PlaceExternalFilesAsync(paths, capturedDestination);
            }
            finally { EndProcessing(); }
        });
    }

    private void RegisterExplorerOleDropTarget()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            RevokeDragDrop(handle);
        }
        catch
        {
        }

        _explorerOleDropTarget = new ExplorerOleDropTarget(this);
        int hr = RegisterDragDrop(handle, _explorerOleDropTarget);
        if (hr != 0)
        {
            SwkLogger.Warn($"RegisterDragDrop failed: hr=0x{hr:X8}");
        }
    }

    internal string? ResolveExternalDropDestinationFromScreenPoint(System.Windows.Point screenPoint)
    {
        if (string.IsNullOrWhiteSpace(_currentFolder))
        {
            return null;
        }

        try
        {
            if (!ShopItemsListView.IsVisible)
            {
                return _currentFolder;
            }

            System.Windows.Point localPoint = ShopItemsListView.PointFromScreen(screenPoint);
            if (double.IsNaN(localPoint.X) || double.IsNaN(localPoint.Y))
            {
                return _currentFolder;
            }

            HitTestResult? hit = VisualTreeHelper.HitTest(ShopItemsListView, localPoint);
            ShopItem? item = GetShopItemFromSource(hit?.VisualHit as DependencyObject);
            if (item is not null && item.IsDirectory)
            {
                return item.FullPath;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            SwkLogger.Debug($"ResolveExternalDropDestinationFromScreenPoint failed: {ex.Message}");
        }

        return _currentFolder;
    }

    internal void UpdateExternalDropTargetHighlightFromScreenPoint(
        System.Windows.Point screenPoint,
        Forms.IDataObject? data = null,
        DragDropKeyStates keyStates = DragDropKeyStates.None)
    {
        if (string.IsNullOrWhiteSpace(_currentFolder))
        {
            ClearDropTargetHighlight();
            return;
        }

        try
        {
            ShopItem? target = GetOleDropDestinationItemFromScreenPoint(screenPoint, data, keyStates);
            if (target is not null)
            {
                if (ReferenceEquals(target, _dropTargetItem))
                {
                    return;
                }

                ClearDropTargetHighlight();
                target.IsDropTarget = true;
                _dropTargetItem = target;
                return;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            SwkLogger.Debug($"UpdateExternalDropTargetHighlightFromScreenPoint failed: {ex.Message}");
        }

        ClearDropTargetHighlight();
    }

    internal void ClearExternalDropTargetHighlight() => ClearDropTargetHighlight();

    internal int GetOleDropEffect(Forms.IDataObject data, DragDropKeyStates keyStates, string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return (int)System.Windows.DragDropEffects.None;
        }

        if (HasInternalDraggedPaths(data))
        {
            if (!IsInternalDropTargetAllowed(destinationFolder, data, keyStates))
            {
                return (int)System.Windows.DragDropEffects.None;
            }

            if (IsHoldFolderPath(destinationFolder))
            {
                return (int)System.Windows.DragDropEffects.Move;
            }

            return (keyStates & DragDropKeyStates.ControlKey) != 0
                ? (int)System.Windows.DragDropEffects.Copy
                : (int)System.Windows.DragDropEffects.Move;
        }

        return data.GetDataPresent(Forms.DataFormats.FileDrop)
            ? (int)System.Windows.DragDropEffects.Copy
            : (int)System.Windows.DragDropEffects.None;
    }

    internal async void HandleOleDrop(Forms.IDataObject data, DragDropKeyStates keyStates, System.Windows.Point screenPoint)
    {
        bool hasInternalDragData = HasInternalDraggedPaths(data);
        string destinationFolder = hasInternalDragData
            ? ResolveOleInternalDropDestinationFromScreenPoint(screenPoint, data, keyStates) ?? string.Empty
            : ResolveExternalDropDestinationFromScreenPoint(screenPoint) ?? _currentFolder ?? string.Empty;
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return;
        }

        if (hasInternalDragData && !IsInternalDropTargetAllowed(destinationFolder, data, keyStates))
        {
            ClearDropTargetHighlight();
            return;
        }

        BeginProcessing();
        try
        {
            if (hasInternalDragData)
            {
                string[] sourcePaths = GetInternalDraggedPaths(data);
                if (sourcePaths.Length == 0)
                {
                    return;
                }

                if (sourcePaths.Length == 1)
                {
                    string sourcePath = sourcePaths[0];
                    if (IsHoldFolderPath(destinationFolder))
                        await HoldInternalDraggedItemsAsync([sourcePath]);
                    else if ((keyStates & DragDropKeyStates.ControlKey) != 0)
                        await CopyInternalDraggedItemAsync(sourcePath, destinationFolder);
                    else
                        await MoveInternalDraggedItemAsync(sourcePath, destinationFolder);
                }
                else
                {
                    if (IsHoldFolderPath(destinationFolder))
                        await HoldInternalDraggedItemsAsync(sourcePaths);
                    else if ((keyStates & DragDropKeyStates.ControlKey) != 0)
                        await CopyInternalDraggedItemsAsync(sourcePaths, destinationFolder);
                    else
                        await MoveInternalDraggedItemsAsync(sourcePaths, destinationFolder);
                }
                return;
            }

            if (data.GetDataPresent(Forms.DataFormats.FileDrop))
            {
                string[] paths = data.GetData(Forms.DataFormats.FileDrop) as string[] ?? [];
                if (paths.Length == 0)
                {
                    return;
                }

                SwkLogger.Info(
                    $"Trace.ExternalFlow.Sender.Entry: route=OLE count={paths.Length} names={DescribeExternalPaths(paths)} dest={destinationFolder}");
                await PlaceExternalFilesAsync(paths, destinationFolder);
            }
        }
        finally
        {
            EndProcessing();
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeExternalDragDrop();

        if (_startupHandled)
        {
            return;
        }

        _startupHandled = true;
        Hide();
        Dispatcher.BeginInvoke(new Action(() => _ = HandleStartupAsync()));
    }

    private async Task HandleStartupAsync()
    {
        if (!EnsureUiUnlocked())
        {
            System.Windows.Application.Current.Shutdown();
            return;
        }

        if (!await EnsureTrayConnectedAsync())
        {
            System.Windows.MessageBox.Show(
                "ShareWorkinTray の起動が間に合いませんでした。\n少し待ってから、もう一度 ShareWorkin を起動してください。",
                "ShareWorkin", MessageBoxButton.OK, MessageBoxImage.Warning);
            System.Windows.Application.Current.Shutdown();
            return;
        }

        TrayStatus? trayStatus = _pipeClient.GetStatus(timeoutMs: 500);
        if (trayStatus != null)
        {
            _isShopOpen = trayStatus.IsShopOpen;
            if (!string.IsNullOrWhiteSpace(trayStatus.ShopFolder))
            {
                _shopFolder = trayStatus.ShopFolder;
                MyShopTextBox.Text = _shopFolder;
            }
            _wasOpenAtLastShutdown = _isShopOpen;
        }
        else
        {
            _isShopOpen = _wasOpenAtLastShutdown;
        }
        ImportPendingIncomingInteractions();
        ShowMainWindow();
        UpdateShopState(_isShopOpen);
        _ = Dispatcher.BeginInvoke(new Action(CompleteStartupAfterFirstPaint), DispatcherPriority.Background);
    }

    private async Task<bool> EnsureTrayConnectedAsync()
    {
        if (_pipeClient.Connect(timeoutMs: 150))
            return true;

        bool trayAlreadyRunning = Process.GetProcessesByName("ShareWorkinTray").Length > 0;
        if (!trayAlreadyRunning)
        {
            if (!StartTrayFromScheduledTask())
            {
                StartTrayProcess();
            }
        }

        // Tray のコールドスタート(JIT・Defender スキャン・WPF 初期化)で
        // パイプ受け入れまで数秒かかる場合がある。時間ベースで最大 20 秒待つ。
        bool fallbackStarted = trayAlreadyRunning;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            if (_pipeClient.Connect(timeoutMs: 100))
                return true;

            if (!fallbackStarted && Process.GetProcessesByName("ShareWorkinTray").Length == 0)
            {
                fallbackStarted = StartTrayProcess();
            }
        }

        return false;
    }

    private static bool StartTrayFromScheduledTask()
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Run /TN \"ShareWorkin\\ShareWorkinTray\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            return process != null;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"StartTrayFromScheduledTask failed: {ex.Message}");
            return false;
        }
    }

    private static bool StartTrayProcess()
    {
        string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        string trayExe = Path.Combine(exeDir ?? string.Empty, "ShareWorkinTray.exe");
        if (!File.Exists(trayExe))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(trayExe) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"StartTrayProcess failed: {ex.Message}");
            return false;
        }
    }

    private void CompleteStartupAfterFirstPaint()
    {
        NavigateToStartupShopFolder();
        _ = MigrateLegacyAppHomeHoldAsync();
        _ = RefreshExternalFriendDataAfterStartupAsync();
    }

    private async Task RefreshExternalFriendDataAfterStartupAsync()
    {
        PopulateExplorerDropdown();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await SwkNetworkCache.RefreshAsync(ScanMode.Quick, cts.Token);
            await Dispatcher.InvokeAsync(PopulateExplorerDropdown, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SwkLogger.Warn($"RefreshExternalFriendDataAfterStartupAsync failed: {ex.Message}");
        }
    }

    private void NavigateToStartupShopFolder()
    {
        if (!_isShopOpen || string.IsNullOrWhiteSpace(_shopFolder))
        {
            return;
        }

        _currentMode = DisplayMode.Shop;
        _activeFriendShop = null;
        _activeFriendShopRootPath = null;
        _missingFriendShopStatus = null;
        NavigateToShopRoot(addHistory: false);
    }

    private void InitializeExplorerDropdownForStartup()
    {
        _suppressDropdownChange = true;
        try
        {
            ExplorerTargetComboBox.Items.Clear();
            ExplorerTargetComboBox.Items.Add(new ExplorerTarget("自分の共有", null, null));
            ExplorerTargetComboBox.SelectedIndex = 0;
        }
        finally
        {
            _suppressDropdownChange = false;
        }
    }

    // v1.04〜v1.08 では GetHoldFolderPath() が AppHomeDirectory\hold を返す実装だった。
    // v1.09 で _shopFolder\保留 に戻したため、AppHomeDirectory\hold にあるファイルを移動する。
    private async Task MigrateLegacyAppHomeHoldAsync()
    {
        string? shopFolder = _shopFolder;
        if (string.IsNullOrWhiteSpace(shopFolder))
        {
            return;
        }

        int movedCount = await Task.Run(() => MigrateLegacyAppHomeHold(shopFolder));
        if (movedCount > 0)
        {
            SwkLogger.Info($"Hold migration: moved {movedCount} entries from AppHome to shop hold folder");
            SetTransientStatus("保留領域を移しました。");
        }
    }

    private static int MigrateLegacyAppHomeHold(string shopFolder)
    {
        if (!Directory.Exists(shopFolder))
            return 0;

        if (!Directory.Exists(DefaultHoldFolderPath))
            return 0;

        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(DefaultHoldFolderPath).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SwkLogger.Warn($"Hold migration: enumerate failed ({ex.Message})");
            return 0;
        }

        if (entries.Count == 0)
        {
            TryDeleteEmptyDirectory(DefaultHoldFolderPath);
            return 0;
        }

        string holdFolderPath = Path.Combine(shopFolder, HoldFolderName);
        if (!TryEnsureHoldFolder(holdFolderPath))
        {
            SwkLogger.Warn("Hold migration: could not create hold folder, skipping");
            return 0;
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        int movedCount = 0;
        foreach (string entry in entries)
        {
            string baseName = Path.GetFileName(entry);
            string destinationPath = ResolveNonConflictingPath(holdFolderPath, baseName, timestamp);

            try
            {
                if (Directory.Exists(entry))
                    Directory.Move(entry, destinationPath);
                else
                    File.Move(entry, destinationPath);
                movedCount++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SwkLogger.Warn($"Hold migration: could not move ({ex.Message})");
            }
        }

        TryDeleteEmptyDirectory(DefaultHoldFolderPath);
        return movedCount;
    }

    private static string ResolveNonConflictingPath(string directory, string baseName, string timestamp)
    {
        string candidate = Path.Combine(directory, baseName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        string stamped = Path.Combine(directory, $"{baseName}_{timestamp}");
        if (!File.Exists(stamped) && !Directory.Exists(stamped))
        {
            return stamped;
        }

        for (int suffix = 1; suffix < 1000; suffix++)
        {
            string alt = Path.Combine(directory, $"{baseName}_{timestamp}_{suffix}");
            if (!File.Exists(alt) && !Directory.Exists(alt))
            {
                return alt;
            }
        }

        return Path.Combine(directory, $"{baseName}_{timestamp}_{Guid.NewGuid():N}");
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SwkLogger.Warn($"TryDeleteEmptyDirectory failed: {ex.Message}");
        }
    }

    private bool TryEnsureHoldFolder()
        => TryEnsureHoldFolder(GetHoldFolderPath());

    private static bool TryEnsureHoldFolder(string holdFolderPath)
    {
        try
        {
            Directory.CreateDirectory(holdFolderPath);
            ClearHiddenFolderAttribute(holdFolderPath);
            SetPrivateHoldFolderPermissions(holdFolderPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            SwkLogger.Warn($"TryEnsureHoldFolder first attempt failed: path={holdFolderPath} message={ex.Message}");
        }

        try
        {
            string? repairTarget = Directory.Exists(holdFolderPath)
                ? holdFolderPath
                : Path.GetDirectoryName(holdFolderPath);

            if (!string.IsNullOrWhiteSpace(repairTarget) && Directory.Exists(repairTarget))
            {
                SwkLogger.Info($"TryEnsureHoldFolder: repairing ownership via takeown ({repairTarget})");
                if (!SmbNtfsManager.TakeOwnershipPath(repairTarget))
                {
                    SwkLogger.Warn($"TryEnsureHoldFolder: takeown failed ({repairTarget})");
                }
            }

            Directory.CreateDirectory(holdFolderPath);
            ClearHiddenFolderAttribute(holdFolderPath);
            SetPrivateHoldFolderPermissions(holdFolderPath);
            SwkLogger.Info($"TryEnsureHoldFolder recovered after ownership repair: {holdFolderPath}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            SwkLogger.Warn($"TryEnsureHoldFolder failed: path={holdFolderPath} message={ex.Message}");
            return false;
        }
    }

    private string GetHoldFolderPath() =>
        !string.IsNullOrWhiteSpace(_shopFolder)
            ? Path.Combine(_shopFolder, HoldFolderName)
            : DefaultHoldFolderPath;

    private static string DeriveShareName(string? shopFolder)
    {
        if (string.IsNullOrWhiteSpace(shopFolder))
        {
            return string.Empty;
        }

        string trimmed = shopFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        string root = Path.GetPathRoot(shopFolder) ?? string.Empty;
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).TrimEnd(':');
    }

    private static readonly char[] ForbiddenShareNameChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    private static bool ValidateShareName(string name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "お店の名前を決められませんでした。フォルダー名を確認してください。";
            return false;
        }

        if (name.IndexOfAny(ForbiddenShareNameChars) >= 0)
        {
            error = "お店の名前にこの記号は使えません: \\ / : * ? \" < > |";
            return false;
        }

        if (name.Length > 80)
        {
            error = "お店の名前が長すぎます(80文字まで)。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsRunningAsAdmin()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsDesktopFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string normalized = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string user = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(normalized, user, StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrEmpty(common) &&
                    string.Equals(normalized, common, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

private static void ClearHiddenFolderAttribute(string folderPath)
    {
        FileAttributes attributes = File.GetAttributes(folderPath);
        if ((attributes & FileAttributes.Hidden) != 0)
        {
            File.SetAttributes(folderPath, attributes & ~FileAttributes.Hidden);
        }
    }

    private static void SetPrivateHoldFolderPermissions(string folderPath)
    {
        if (!SmbNtfsManager.SetPrivateHoldFolderPermissions(folderPath))
        {
            throw new UnauthorizedAccessException("Hold folder permissions could not be set.");
        }
    }

    private bool IsHoldFolderPath(string path)
    {
        try
        {
            string holdFolderPath = Path.GetFullPath(GetHoldFolderPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string targetPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(holdFolderPath, targetPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool EnsureHoldFolderForShopChange(bool notifyWhenRecreated)
    {
        if (!_isShopOpen ||
            _currentMode != DisplayMode.Shop ||
            string.IsNullOrWhiteSpace(_shopFolder) ||
            string.IsNullOrWhiteSpace(_currentFolder) ||
            !Directory.Exists(_shopFolder) ||
            !string.Equals(
                Path.GetFullPath(_currentFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(_shopFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            SwkLogger.Debug(
                $"EnsureHoldFolderForShopChange skipped: isOpen={_isShopOpen} mode={_currentMode} " +
                $"shopFolder={_shopFolder ?? "-"} currentFolder={_currentFolder ?? "-"}");
            return false;
        }

        string holdFolderPath = GetHoldFolderPath();
        bool wasMissing = !Directory.Exists(holdFolderPath);
        SwkLogger.Debug(
            $"EnsureHoldFolderForShopChange start: hold={holdFolderPath} wasMissing={wasMissing} notifyWhenRecreated={notifyWhenRecreated}");
        if (!TryEnsureHoldFolder())
        {
            SwkLogger.Warn($"EnsureHoldFolderForShopChange failed: hold={holdFolderPath}");
            SetTransientStatus("保留を準備できません。");
            return false;
        }

        if (notifyWhenRecreated && wasMissing)
        {
            SwkLogger.Info($"EnsureHoldFolderForShopChange recreated: hold={holdFolderPath}");
            NotifyShopMaintenance("保留を作り直しました。", "保留ホルダーは再作成されます。");
        }

        SwkLogger.Debug($"EnsureHoldFolderForShopChange complete: hold={holdFolderPath} recreated={wasMissing}");
        return wasMissing;
    }

    private void SuppressExternalChangeNotifications()
    {
        _suppressExternalChangeNotificationsUntil = DateTime.Now.AddSeconds(3);
        SwkLogger.Debug($"ExternalChange.Suppress: until={_suppressExternalChangeNotificationsUntil:O}");
    }

    private bool ShouldSuppressExternalChangeNotification()
    {
        return DateTime.Now <= _suppressExternalChangeNotificationsUntil;
    }

    private bool CanShowNotification(bool externalOnly)
    {
        return _notificationMode switch
        {
            NotificationMode.All => true,
            NotificationMode.ExternalOnly => externalOnly,
            _ => false,
        };
    }

    private static string FormatWatcherEvent(FileSystemEventArgs e)
        => $"type={e.ChangeType} path={e.FullPath}";

    private static string FormatWatcherEvent(RenamedEventArgs e)
        => $"type={e.ChangeType} old={e.OldFullPath} new={e.FullPath}";

    private bool TryRegisterExternalReceive(string fullPath, string source)
    {
        SwkLogger.Info($"Trace.ExternalFlow.Receive.Entry: path={fullPath ?? "-"} source={source}");
        if (string.IsNullOrWhiteSpace(fullPath) ||
            !File.Exists(fullPath) ||
            IsHoldFolderPath(fullPath))
        {
            SwkLogger.Debug($"TryRegisterExternalReceive skipped: path={fullPath ?? "-"} source={source}");
            SwkLogger.Info($"Trace.ExternalFlow.Receive.Skip: path={fullPath ?? "-"} source={source} reason=missing-or-hold");
            return false;
        }

        DateTime now = DateTime.Now;
        if (_recentExternalReceiveAt.TryGetValue(fullPath, out DateTime lastAt) &&
            now - lastAt < TimeSpan.FromSeconds(10))
        {
            SwkLogger.Debug(
                $"TryRegisterExternalReceive skipped: duplicate path={fullPath} source={source} lastAt={lastAt:O}");
            SwkLogger.Info($"Trace.ExternalFlow.Receive.Skip: path={fullPath} source={source} reason=duplicate");
            return false;
        }

        if (_recentIncomingInteractionAt.TryGetValue(fullPath, out DateTime incomingAt) &&
            now - incomingAt < TimeSpan.FromSeconds(30))
        {
            SwkLogger.Info(
                $"Trace.ExternalFlow.Receive.Skip: path={fullPath} source={source} reason=matched-confirmed-interaction");
            return false;
        }

        foreach (string stalePath in _recentExternalReceiveAt
                     .Where(pair => now - pair.Value >= TimeSpan.FromMinutes(3))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _recentExternalReceiveAt.Remove(stalePath);
        }

        foreach (string stalePath in _recentIncomingInteractionAt
                     .Where(pair => now - pair.Value >= TimeSpan.FromMinutes(3))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _recentIncomingInteractionAt.Remove(stalePath);
        }

        _recentExternalReceiveAt[fullPath] = now;
        ArrivedItem item = new(
            Path.GetFileName(fullPath),
            Path.GetDirectoryName(fullPath) ?? string.Empty,
            now);
        ArrivedItems.Insert(0, item);
        while (ArrivedItems.Count > MaxArrivedItemCount)
        {
            ArrivedItems.RemoveAt(ArrivedItems.Count - 1);
        }

        AppendHistory(
            HistoryChannel.Update,
            $"{item.Name} の受け取りを未照合の受信として検知しました。",
            "OutOfSyncDetected",
            HistoryOutcome.Info,
            targetName: item.Name,
            pathText: item.FolderPath,
            note: "交流通知と照合できなかったため、送信元未特定の受信として扱いました。",
            destinationPath: fullPath,
            destinationFolder: item.FolderPath,
            source: source);
        QueueNotification(item);
        string loggedPath = NormalizeHistoryPathText(
            item.FolderPath,
            sourcePath: null,
            destinationPath: fullPath,
            destinationFolder: item.FolderPath) ?? item.FolderPath;
        SwkLogger.Info($"Out-of-sync detected: {item.Name} -> {loggedPath} source={source}");
        SwkLogger.Info($"TryRegisterExternalReceive appended: path={fullPath} source={source}");
        SwkLogger.Info($"Trace.ExternalFlow.Receive.Success: target={item.Name} path={loggedPath} source={source}");
        return true;
    }

    private void AppendHistory(
        HistoryChannel channel,
        string message,
        string eventType,
        HistoryOutcome outcome = HistoryOutcome.Info,
        HistoryDirection direction = HistoryDirection.None,
        Friend? friend = null,
        string? targetName = null,
        string? pathText = null,
        string? note = null,
        string? interactionEventId = null,
        string? source = null,
        string? sourcePath = null,
        string? destinationPath = null,
        string? destinationFolder = null)
    {
        Friend? historyFriend = friend;
        Friend? pathFriend = friend ?? ResolveHistoryPathFriend(pathText, sourcePath, destinationPath, destinationFolder);
        string? normalizedTargetName = NormalizeHistoryTargetName(targetName, destinationPath, sourcePath);
        string? normalizedPathText = NormalizeHistoryPathText(
            pathText,
            pathFriend,
            sourcePath,
            destinationPath,
            destinationFolder);
        SwkLogger.Debug(
            $"AppendHistory: channel={channel} eventType={eventType} outcome={outcome} " +
            $"direction={direction} target={normalizedTargetName ?? "-"} path={normalizedPathText ?? "-"} source={source ?? "-"}");
        HistoryRepository.Append(new HistoryEntry
        {
            Channel = channel,
            Message = message,
            EventType = eventType,
            Outcome = outcome,
            Direction = direction,
            InteractionEventId = interactionEventId,
            FriendId = historyFriend?.Id,
            FriendName = historyFriend is null ? null : GetFriendLabel(historyFriend),
            TargetName = normalizedTargetName,
            PathText = normalizedPathText,
            Note = note,
            Source = source,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            DestinationFolder = destinationFolder,
        });
    }

    private void AppendUpdateUiHistory(
        string message,
        string eventType,
        HistoryOutcome outcome,
        string? targetName = null,
        string? pathText = null,
        string? note = null,
        string? source = null,
        string? sourcePath = null,
        string? destinationPath = null,
        string? destinationFolder = null)
    {
        AppendHistory(
            HistoryChannel.Update,
            message,
            eventType,
            outcome,
            targetName: targetName,
            pathText: pathText,
            note: note,
            source: source,
            sourcePath: sourcePath,
            destinationPath: destinationPath,
            destinationFolder: destinationFolder);
    }

    private static string? NormalizeHistoryTargetName(
        string? targetName,
        string? destinationPath,
        string? sourcePath)
    {
        string? candidate = targetName;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = !string.IsNullOrWhiteSpace(destinationPath)
                ? destinationPath
                : sourcePath;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        string trimmed = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fileName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? candidate : fileName;
    }

    private void ApplyExplorerActionResult(ExplorerActionResult result, bool showAlertDialog = true)
    {
        InteractionEventEntry? interactionEvent = null;
        Friend? interactionFriend = null;
        if (result.State == ExplorerActionState.Success &&
            TryCreateConfirmedInteraction(result, out InteractionEventEntry? preparedInteractionEvent, out Friend? preparedFriend))
        {
            interactionEvent = preparedInteractionEvent;
            interactionFriend = preparedFriend;
        }

        if (string.IsNullOrWhiteSpace(result.LogMessage) is false)
        {
            if (result.State == ExplorerActionState.Failure)
            {
                SwkLogger.Warn(result.LogMessage);
            }
            else
            {
                SwkLogger.Info(result.LogMessage);
            }
        }

        if (string.IsNullOrWhiteSpace(result.UserMessage) is false)
        {
            SetTransientStatus(result.UserMessage);

            if (showAlertDialog &&
                (result.State == ExplorerActionState.Blocked || result.State == ExplorerActionState.Failure))
            {
                System.Windows.MessageBox.Show(
                    this,
                    result.UserMessage,
                    "ShareWorkin",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        if (result.ShouldWriteHistory)
        {
            AppendHistory(
                HistoryChannel.Update,
                result.HistoryMessage!,
                result.EventType,
                result.HistoryOutcome,
                interactionEventId: interactionEvent?.Id,
                targetName: result.TargetName,
                pathText: result.PathText,
                note: result.Note,
                source: result.Source,
                sourcePath: result.SourcePath,
                destinationPath: result.DestinationPath,
                destinationFolder: result.DestinationFolder);
        }

        if (interactionEvent is not null)
        {
            RecordConfirmedInteraction(interactionEvent, interactionFriend);
        }

        if (result.State == ExplorerActionState.Success)
        {
            NotifyShellOfExplorerAction(result);
        }
    }

    private void NotifyShellOfExplorerAction(ExplorerActionResult result)
    {
        try
        {
            switch (result.EventType)
            {
                case "Rename":
                {
                    bool isDirectory = !string.IsNullOrWhiteSpace(result.DestinationPath) &&
                        Directory.Exists(result.DestinationPath);
                    SHChangeNotify(
                        isDirectory ? SHCNE_RENAMEFOLDER : SHCNE_RENAMEITEM,
                        SHCNF_PATHW,
                        result.SourcePath,
                        result.DestinationPath);
                    break;
                }
                case "CreateFolder":
                case "CreateFile":
                    SHChangeNotify(SHCNE_CREATE, SHCNF_PATHW, result.DestinationPath, null);
                    break;
                case "Delete":
                    SHChangeNotify(SHCNE_DELETE, SHCNF_PATHW, result.SourcePath, null);
                    break;
            }

            string? parent = result.DestinationFolder ??
                             result.SourceParent ??
                             Path.GetDirectoryName(result.SourcePath ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, parent, null);
                SHChangeNotify(SHCNE_ATTRIBUTES, SHCNF_PATHW, parent, null);
            }

            if (!string.IsNullOrWhiteSpace(result.DestinationPath))
            {
                SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, result.DestinationPath, null);
            }
        }
        catch (Exception ex)
        {
            SwkLogger.Debug($"NotifyShellOfExplorerAction skipped: {ex.Message}");
        }
    }

    private void CancelDeferredRefresh()
    {
        _deferredRefreshTimer?.Stop();
        _deferredRefreshFolderPath = null;
    }

    private void ScheduleRefreshShopItemsIfCurrentFolder(string folderPath, TimeSpan delay)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        string targetFolderPath;
        try
        {
            targetFolderPath = Path.GetFullPath(folderPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (_deferredRefreshTimer is null)
        {
            _deferredRefreshTimer = new DispatcherTimer();
            _deferredRefreshTimer.Tick += DeferredRefreshTimer_Tick;
        }

        _deferredRefreshFolderPath = targetFolderPath;
        _deferredRefreshTimer.Stop();
        _deferredRefreshTimer.Interval = delay;
        _deferredRefreshTimer.Start();
    }

    private void DeferredRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_deferredRefreshTimer is null)
        {
            return;
        }

        _deferredRefreshTimer.Stop();
        string? targetFolderPath = _deferredRefreshFolderPath;
        _deferredRefreshFolderPath = null;
        if (string.IsNullOrWhiteSpace(targetFolderPath) ||
            string.IsNullOrWhiteSpace(_currentFolder))
        {
            return;
        }

        try
        {
            if (string.Equals(Path.GetFullPath(_currentFolder), targetFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                RefreshShopItems();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            SwkLogger.Debug($"DeferredRefreshTimer_Tick skipped: {ex.Message}");
        }
    }

    private bool TryValidateBatchOperation(
        IReadOnlyList<string> sourcePaths,
        string operationLabel,
        Func<string, ExplorerActionResult> validate)
    {
        if (sourcePaths.Count <= 1)
        {
            return true;
        }

        HashSet<string> targetNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (string sourcePath in sourcePaths)
        {
            string targetName = NormalizeHistoryTargetName(null, null, sourcePath) ?? sourcePath;
            if (!string.IsNullOrWhiteSpace(targetName) && !targetNames.Add(targetName))
            {
                ShowBatchOperationCancelledWarning(
                    operationLabel,
                    targetName,
                    "同じ名前の項目が含まれているため、まとめて処理できません。");
                return false;
            }

            ExplorerActionResult validation = validate(sourcePath);
            if (validation.State == ExplorerActionState.Success ||
                validation.State == ExplorerActionState.NoChange)
            {
                continue;
            }

            ShowBatchOperationCancelledWarning(
                operationLabel,
                validation.TargetName ?? targetName,
                string.IsNullOrWhiteSpace(validation.UserMessage)
                    ? "まとめて処理できない項目が含まれています。"
                    : validation.UserMessage);
            return false;
        }

        return true;
    }

    private void ShowBatchOperationCancelledWarning(string operationLabel, string? targetName, string detailMessage)
    {
        const string statusMessage = "操作を中断しました。";
        SetTransientStatus(statusMessage);
        AppendUpdateUiHistory(
            "複数項目の操作を中断しました。",
            "BatchCancelled",
            HistoryOutcome.Warning,
            targetName: targetName,
            pathText: _currentFolder,
            note: $"操作: {operationLabel} / 理由: {detailMessage}",
            source: "MainWindow.batch");

        string message =
            $"{operationLabel}できない項目が含まれていたため、今回の操作は中断しました。\r\n" +
            "他の項目も処理していません。";

        if (!string.IsNullOrWhiteSpace(targetName))
        {
            message += $"\r\n\r\n対象: {targetName}";
        }

        if (!string.IsNullOrWhiteSpace(detailMessage))
        {
            message += $"\r\n理由: {detailMessage}";
        }

        SwkLogger.Warn($"Batch {operationLabel} cancelled: target={targetName ?? "-"} detail={detailMessage}");
        System.Windows.MessageBox.Show(this, message, "ShareWorkin", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string GetFriendLabel(Friend friend) =>
        string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName;

    private static string DescribeExternalPaths(IReadOnlyList<string> paths)
        => paths.Count == 0 ? "-" : string.Join(" | ", paths.Select(static path => Path.GetFileName(path)));

    private Friend? ResolveHistoryPathFriend(
        string? pathText = null,
        string? sourcePath = null,
        string? destinationPath = null,
        string? destinationFolder = null)
    {
        if (_activeFriendShop is null || _currentMode != DisplayMode.FriendShop)
        {
            return null;
        }

        string[] candidates =
        [
            pathText ?? string.Empty,
            sourcePath ?? string.Empty,
            destinationPath ?? string.Empty,
            destinationFolder ?? string.Empty,
        ];

        foreach (string candidate in candidates)
        {
            if (TryMapToCanonicalFriendPath(candidate, _activeFriendShop, out _))
            {
                return _activeFriendShop;
            }
        }

        return null;
    }

    private string? NormalizeHistoryPathText(
        string? pathText,
        Friend? friend = null,
        string? sourcePath = null,
        string? destinationPath = null,
        string? destinationFolder = null)
    {
        string? candidate = !string.IsNullOrWhiteSpace(pathText)
            ? pathText
            : !string.IsNullOrWhiteSpace(destinationFolder)
                ? destinationFolder
                : !string.IsNullOrWhiteSpace(destinationPath)
                    ? Path.GetDirectoryName(destinationPath)
                    : !string.IsNullOrWhiteSpace(sourcePath)
                        ? Path.GetDirectoryName(sourcePath)
                        : null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        if (friend is not null &&
            TryMapToCanonicalFriendPath(candidate, friend, out string? canonicalFriendPath))
        {
            return canonicalFriendPath;
        }

        if (TryMapToLocalSharePath(candidate, out string? canonicalLocalPath))
        {
            return canonicalLocalPath;
        }

        return candidate;
    }

    private bool TryMapToCanonicalFriendPath(string path, Friend friend, out string canonicalPath)
    {
        canonicalPath = path;
        if (string.IsNullOrWhiteSpace(path) ||
            string.IsNullOrWhiteSpace(friend.HostMachineName) ||
            string.IsNullOrWhiteSpace(friend.ShareName))
        {
            return false;
        }

        List<string> roots =
        [
            $@"\\{friend.HostMachineName}\{friend.ShareName}",
        ];

        if (!string.IsNullOrWhiteSpace(friend.LastKnownAddress))
        {
            roots.Add($@"\\{friend.LastKnownAddress}\{friend.ShareName}");
        }

        if (!string.IsNullOrWhiteSpace(friend.ConnectUncPath))
        {
            roots.Add(friend.ConnectUncPath);
        }

        if (!string.IsNullOrWhiteSpace(_activeFriendShopRootPath))
        {
            roots.Add(_activeFriendShopRootPath);
        }

        string canonicalRoot = $@"\\{friend.HostMachineName}\{friend.ShareName}";
        foreach (string root in roots
                     .Where(static root => !string.IsNullOrWhiteSpace(root))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryBuildCanonicalPathFromRoot(path, root, canonicalRoot, out canonicalPath))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private bool TryMapToLocalSharePath(string path, out string canonicalPath)
    {
        canonicalPath = path;
        if (string.IsNullOrWhiteSpace(path) ||
            string.IsNullOrWhiteSpace(_shopFolder))
        {
            return false;
        }

        string shareName = DeriveShareName(_shopFolder);
        if (string.IsNullOrWhiteSpace(shareName))
        {
            return false;
        }

        string canonicalRoot = $@"\\{Environment.MachineName}\{shareName}";
        return TryBuildCanonicalPathFromRoot(path, _shopFolder, canonicalRoot, out canonicalPath);
    }

    private static bool TryBuildCanonicalPathFromRoot(
        string path,
        string sourceRoot,
        string canonicalRoot,
        out string canonicalPath)
    {
        canonicalPath = path;
        if (string.IsNullOrWhiteSpace(path) ||
            string.IsNullOrWhiteSpace(sourceRoot) ||
            string.IsNullOrWhiteSpace(canonicalRoot))
        {
            return false;
        }

        string normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedSourceRoot = sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!normalizedPath.Equals(normalizedSourceRoot, StringComparison.OrdinalIgnoreCase) &&
            !normalizedPath.StartsWith(normalizedSourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !normalizedPath.StartsWith(normalizedSourceRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string relative = normalizedPath.Length == normalizedSourceRoot.Length
            ? string.Empty
            : normalizedPath[normalizedSourceRoot.Length..]
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        canonicalPath = string.IsNullOrWhiteSpace(relative)
            ? canonicalRoot
            : $@"{canonicalRoot}\{relative.Replace('/', '\\')}";
        return true;
    }

    private void ShowHistoryDialog(HistoryChannel channel, string title)
    {
        int maxCount = channel == HistoryChannel.Update ? 200 : 40;
        if (_historyWindows.TryGetValue(channel, out HistoryWindow? existingWindow))
        {
            if (existingWindow.WindowState == WindowState.Minimized)
            {
                existingWindow.WindowState = WindowState.Normal;
            }

            existingWindow.Activate();
            existingWindow.Focus();
            return;
        }

        string subtitle = channel switch
        {
            HistoryChannel.Access => "自分とお友達の出入りや接続の履歴です。",
            HistoryChannel.Notification => "通知対象になった出来事の履歴です。",
            _ => "自分のお店で起きた更新の履歴です。",
        };
        HistoryWindow window = new(title, subtitle, channel, maxCount) { Owner = this };
        window.Closed += (_, _) => _historyWindows.Remove(channel);
        _historyWindows[channel] = window;
        window.Show();
        window.Activate();
    }

    private void NotifyExternalShopChange()
    {
        bool suppressed = ShouldSuppressExternalChangeNotification();
        bool canNotify = CanShowNotification(externalOnly: true);
        if (suppressed || !canNotify)
        {
            SwkLogger.Debug(
                $"NotifyExternalShopChange skipped: suppressed={suppressed} canNotify={canNotify} mode={_notificationMode}");
            return;
        }

        DateTime now = DateTime.Now;
        if (now - _lastExternalChangeNotificationAt < TimeSpan.FromSeconds(2))
        {
            SwkLogger.Debug(
                $"NotifyExternalShopChange skipped: quietPeriod last={_lastExternalChangeNotificationAt:O} now={now:O}");
            return;
        }

        _lastExternalChangeNotificationAt = now;
        SwkLogger.Info("NotifyExternalShopChange observed: external change stays out of notification/main history flow.");
    }

    private void AppendExternalChangeHistory(FileSystemEventArgs e)
    {
        bool suppressed = ShouldSuppressExternalChangeNotification();
        bool isHoldPath = !string.IsNullOrWhiteSpace(e.FullPath) && IsHoldFolderPath(e.FullPath);
        if (suppressed ||
            string.IsNullOrWhiteSpace(e.FullPath) ||
            isHoldPath)
        {
            SwkLogger.Debug(
                $"AppendExternalChangeHistory skipped: {FormatWatcherEvent(e)} suppressed={suppressed} isHoldPath={isHoldPath}");
            return;
        }

        string itemName = e.Name ?? Path.GetFileName(e.FullPath);
        if (string.IsNullOrWhiteSpace(itemName))
        {
            SwkLogger.Debug($"AppendExternalChangeHistory skipped: empty item name {FormatWatcherEvent(e)}");
            return;
        }

        string folder = Path.GetDirectoryName(e.FullPath) ?? _shopFolder ?? string.Empty;
        SwkLogger.Debug($"AppendExternalChangeHistory start: {FormatWatcherEvent(e)} folder={folder}");
        switch (e.ChangeType)
        {
            case WatcherChangeTypes.Created:
                // File creation is already captured by the receive history path.
                if (!Directory.Exists(e.FullPath))
                {
                    SwkLogger.Debug(
                        $"AppendExternalChangeHistory file-create skipped: handled by receive path path={e.FullPath}");
                    return;
                }

                AppendHistory(
                    HistoryChannel.Update,
                    $"{itemName} フォルダーが追加されました。",
                    "ExternalCreateFolder",
                    HistoryOutcome.Info,
                    targetName: itemName,
                    pathText: folder,
                    destinationPath: e.FullPath,
                    destinationFolder: folder,
                    source: "Watcher.External");
                SwkLogger.Info($"AppendExternalChangeHistory appended: event=ExternalCreateFolder path={e.FullPath}");
                break;

            case WatcherChangeTypes.Deleted:
                AppendHistory(
                    HistoryChannel.Update,
                    $"{itemName} が削除されました。",
                    "ExternalDelete",
                    HistoryOutcome.Info,
                    targetName: itemName,
                    pathText: folder,
                    note: $"削除前: {e.FullPath}",
                    sourcePath: e.FullPath,
                    source: "Watcher.External");
                SwkLogger.Info($"AppendExternalChangeHistory appended: event=ExternalDelete path={e.FullPath}");
                break;
        }
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(iconPath))
            {
                return new System.Drawing.Icon(iconPath);
            }
        }
        catch (IOException) { }
        catch (ArgumentException) { }

        return System.Drawing.SystemIcons.Application;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using Forms.FolderBrowserDialog dialog = new()
        {
            Description = "あなたのお店にする場所を選ぶ",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_shopFolder) ? _shopFolder : string.Empty
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        string selected = dialog.SelectedPath;
        bool isSameAsCurrent = !string.IsNullOrWhiteSpace(_shopFolder) &&
            string.Equals(
                Path.GetFullPath(selected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(_shopFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

        if (isSameAsCurrent)
        {
            return;
        }

        if (IsDesktopFolder(selected))
        {
            MessageBoxResult confirm = System.Windows.MessageBox.Show(
                this,
                "デスクトップは普段使うファイルが集まる場所なので、お店として開くと誤って動かしやすくなります。\n続けますか?",
                "ShareWorkin",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        if (_isShopOpen)
        {
            SwitchShopFolder(selected);
        }
        else
        {
            _shopFolder = selected;
            MyShopTextBox.Text = _shopFolder;
            SaveSettings();
            UpdateShopState(false);
        }
    }

    private void SwitchShopFolder(string newFolder)
    {
        if (!Directory.Exists(newFolder))
        {
            CloseShop();
            UpdateShopState(false, "その場所が見つかりません。");
            return;
        }

        CloseShop();
        _folderSizeCache.Clear();
        _subfolderCountCache.Clear();
        _backStack.Clear();
        _forwardStack.Clear();

        _shopFolder = newFolder;
        MyShopTextBox.Text = _shopFolder;
        SaveSettings();

        OpenShop();
    }

    private void ShopDoorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isShopOpen)
            ExecuteCloseShop();
        else
            ExecuteOpenShop();
    }

    private void ExecuteCloseShop()
    {
        _pipeClient.BroadcastClosing();
        CloseShop();
    }

    private void ExecuteOpenShop()
    {
        OpenShop();
    }

    private void HandleFriendShopClosingReceived(string machineName, string shareName)
    {
        if (_currentMode != DisplayMode.FriendShop || _activeFriendShop is null) return;
        if (!string.Equals(_activeFriendShop.HostMachineName, machineName, StringComparison.OrdinalIgnoreCase)) return;
        if (!string.Equals(_activeFriendShop.ShareName, shareName, StringComparison.OrdinalIgnoreCase)) return;

        ShopItems.Clear();
        string label = string.IsNullOrWhiteSpace(_activeFriendShop.DisplayName)
            ? machineName
            : _activeFriendShop.DisplayName;
        SetTransientStatus($"{label} のお店が閉じました。");
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string? targetPath = _currentFolder ?? _shopFolder;
        string friendLabel = _activeFriendShop is null
            ? "-"
            : $"{_activeFriendShop.DisplayName} ({_activeFriendShop.HostMachineName}/{_activeFriendShop.ShareName})";
        SwkLogger.Info(
            $"Investigation.OpenFolderButton_Click: mode={_currentMode} target={targetPath ?? "(null)"} activeFriend={friendLabel}");
        VisitShop(_currentFolder ?? _shopFolder);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await ExecuteRefreshAsync();

    private async Task ExecuteRefreshAsync()
    {
        using var _ = Processing();
        CancelDeferredRefresh();
        ShopItems.Clear();

        if (_currentMode != DisplayMode.FriendShop || _activeFriendShop is null)
        {
            // Shop/Hold mode: キャッシュと監視も立て直して確実に再取得
            if (!string.IsNullOrWhiteSpace(_currentFolder))
            {
                InvalidateSizeCacheUnder(_currentFolder);
                InvalidateSubfolderCountCacheUnder(_currentFolder);
                StartContentsSensor(_currentFolder);
                RefreshShopItems();
            }
            if (_isShopOpen)
            {
                ReinitializeShopChangeMonitoring();
            }
            return;
        }

        // FriendShop mode ① 相手側の最新ショップ情報を優先して取り直す
        await Task.Delay(80);
        SwkNotificationListener.ShopInfo? probedLiveShop = await ProbeActiveFriendShopAsync(_activeFriendShop);
        if (probedLiveShop is not null)
        {
            SwkNetworkCache.UpsertShop(probedLiveShop);

            bool externalChanged =
                !string.Equals(_activeFriendShop.HostMachineName, probedLiveShop.MachineName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_activeFriendShop.ShareName, probedLiveShop.ShareName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_activeFriendShop.LastKnownAddress, probedLiveShop.IpAddress ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            if (externalChanged)
            {
                UpdateFriendExternalState(_activeFriendShop, probedLiveShop);
                PopulateExplorerDropdown();
            }

            bool canStayOnCurrentFolder = !string.IsNullOrWhiteSpace(_currentFolder) &&
                await Task.Run(() => { try { return Directory.Exists(_currentFolder); } catch { return false; } });
            if (externalChanged || !canStayOnCurrentFolder)
            {
                await NavigateToFriendShopAsync(_activeFriendShop, probedLiveShop);
                return;
            }
        }

        // ② 単純再取得
        bool accessible = !string.IsNullOrWhiteSpace(_currentFolder) &&
            await Task.Run(() => { try { return Directory.Exists(_currentFolder); } catch { return false; } });

        if (accessible)
        {
            InvalidateSizeCacheUnder(_currentFolder!);
            InvalidateSubfolderCountCacheUnder(_currentFolder!);
            StartContentsSensor(_currentFolder!);
            RefreshShopItems();
            return;
        }

        // ③ MachineName のみで UDP キャッシュを探索して再接続
        var liveShop = SwkNetworkCache.ShopInfos.FirstOrDefault(s =>
            string.Equals(s.MachineName, _activeFriendShop.HostMachineName, StringComparison.OrdinalIgnoreCase));

        if (liveShop is not null)
        {
            bool changed = false;
            if (!string.Equals(_activeFriendShop.ShareName, liveShop.ShareName, StringComparison.OrdinalIgnoreCase))
            {
                _activeFriendShop.ShareName = liveShop.ShareName;
                changed = true;
            }
            if (!string.IsNullOrEmpty(liveShop.IpAddress) &&
                !string.Equals(_activeFriendShop.LastKnownAddress, liveShop.IpAddress, StringComparison.OrdinalIgnoreCase))
            {
                _activeFriendShop.LastKnownAddress = liveShop.IpAddress;
                changed = true;
            }
            if (changed)
            {
                var all = FriendsRepository.LoadAll().ToList();
                var stored = all.FirstOrDefault(f => f.Id == _activeFriendShop.Id);
                if (stored is not null)
                {
                    stored.ShareName = _activeFriendShop.ShareName;
                    stored.LastKnownAddress = _activeFriendShop.LastKnownAddress;
                    FriendsRepository.SaveAll(all);
                }
            }
            await NavigateToFriendShopAsync(_activeFriendShop);
            return;
        }

        // ④ 最後にクイックスキャンで自動復帰を試す。ここで見つかるなら
        // ユーザー判断は不要なので、登録情報を更新してそのまま開く。
        SwkNotificationListener.ShopInfo? scannedLiveShop = await ResolveLiveFriendShopAsync(_activeFriendShop);
        if (scannedLiveShop is not null)
        {
            UpdateFriendExternalState(_activeFriendShop, scannedLiveShop);
            PopulateExplorerDropdown();
            await NavigateToFriendShopAsync(_activeFriendShop, scannedLiveShop);
            return;
        }

        // ⑤ 見つからない → ユーザー一覧を促す
        string label = string.IsNullOrWhiteSpace(_activeFriendShop.DisplayName)
            ? _activeFriendShop.HostMachineName
            : _activeFriendShop.DisplayName;
        SetTransientStatus($"{label} が見つかりません。ユーザー一覧で確認してください。");
        UserListWindow window = new(this) { Owner = this };
        bool? result = window.ShowDialog();
        PopulateExplorerDropdown();
        if (result == true)
        {
            await ExecuteRefreshAsync();
        }
    }

    private void SectionTitleButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToShopRoot(addHistory: true);
    }

    private void SearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        SearchFromShopRoot(SearchTextBox.Text);
    }

    private void NotificationModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _notificationMode = GetSelectedNotificationMode();
        if (_notificationMode == NotificationMode.Off)
        {
            _notificationTimer.Stop();
            _pendingNotificationItems.Clear();
        }

        SaveSettings();
    }

    private async void UserListButton_Click(object sender, RoutedEventArgs e)
    {
        UserListWindow window = new(this) { Owner = this };
        bool? result = window.ShowDialog();
        UpdateUserListButtonHighlight();
        PopulateExplorerDropdown();
        if (result == true)
        {
            await ExecuteRefreshAsync();
        }
    }

    private DispatcherTimer? _processingShowDelayTimer;
    private const int ProcessingShowDelayMs = 500;
    private static readonly TimeSpan ProcessingProgressUpdateInterval = TimeSpan.FromMilliseconds(80);

    private void BeginProcessing(string? label = null, bool showImmediately = false)
    {
        if (_processingDepth++ == 0)
        {
            ProcessingLabel.Text = label ?? "● 処理中…";
            ProcessingProgressBar.IsIndeterminate = true;
            ProcessingProgressBar.Value = 0;
            _processingShowDelayTimer?.Stop();

            if (showImmediately)
            {
                ProcessingBar.Visibility = Visibility.Visible;
                _processingShowDelayTimer = null;
                return;
            }

            // 0.5秒以内に終わる処理では出さない（点滅ノイズ回避）
            _processingShowDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ProcessingShowDelayMs) };
            _processingShowDelayTimer.Tick += (_, _) =>
            {
                _processingShowDelayTimer?.Stop();
                _processingShowDelayTimer = null;
                if (_processingDepth > 0)
                {
                    ProcessingBar.Visibility = Visibility.Visible;
                }
            };
            _processingShowDelayTimer.Start();
        }
        else if (label != null)
        {
            ProcessingLabel.Text = label;
        }
    }

    private void EndProcessing()
    {
        if (--_processingDepth <= 0)
        {
            _processingDepth = 0;
            _processingShowDelayTimer?.Stop();
            _processingShowDelayTimer = null;
            ProcessingBar.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowProcessingBarNow(string? label = null)
    {
        if (_processingDepth <= 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(label))
        {
            ProcessingLabel.Text = label;
        }

        _processingShowDelayTimer?.Stop();
        _processingShowDelayTimer = null;
        ProcessingBar.Visibility = Visibility.Visible;
    }

    private async Task<IProgress<(int current, int total, string name)>> CreateFileProgressAsync(string verb)
    {
        if (_processingDepth > 0)
        {
            ShowProcessingBarNow($"● {verb}中…");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        }

        return new FileProgressReporter(this, verb);
    }

    private IDisposable Processing() => new ProcessingScope(this);

    private sealed class ProcessingScope : IDisposable
    {
        private readonly MainWindow _owner;
        public ProcessingScope(MainWindow owner) { _owner = owner; _owner.BeginProcessing(); }
        public void Dispose() => _owner.EndProcessing();
    }

    private void UpdateProcessingProgress(string verb, int current, int total, string name)
    {
        ProcessingProgressBar.IsIndeterminate = false;
        ProcessingProgressBar.Value = total > 0 ? (double)current / total * 100 : 0;
        ProcessingLabel.Text = $"● {verb}中… {current}/{total}  {name}";
    }

    private sealed class FileProgressReporter : IProgress<(int current, int total, string name)>
    {
        private readonly MainWindow _owner;
        private readonly string _verb;
        private long _lastUpdateTicks;
        private int _lastCurrent = -1;

        public FileProgressReporter(MainWindow owner, string verb)
        {
            _owner = owner;
            _verb = verb;
        }

        public void Report((int current, int total, string name) value)
        {
            long now = Environment.TickCount64;
            bool isBoundary = value.current <= 0 || value.current >= value.total;
            bool currentChanged = value.current != _lastCurrent;
            bool due = now - _lastUpdateTicks >= (long)ProcessingProgressUpdateInterval.TotalMilliseconds;

            if (!isBoundary && (!currentChanged || !due))
            {
                return;
            }

            _lastCurrent = value.current;
            _lastUpdateTicks = now;

            if (_owner.Dispatcher.CheckAccess())
            {
                _owner.UpdateProcessingProgress(_verb, value.current, value.total, value.name);
            }
            else
            {
                _ = _owner.Dispatcher.InvokeAsync(
                    () => _owner.UpdateProcessingProgress(_verb, value.current, value.total, value.name),
                    DispatcherPriority.Background);
            }
        }
    }

    private void ShareStatusButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not ShopItem item)
        {
            return;
        }

        if (item.IsHoldFolder)
        {
            return;
        }

        bool readOnly = _currentMode == DisplayMode.FriendShop;
        OpenPermissionPopup(item, button, readOnly);
    }

    private void OpenPermissionPopup(ShopItem item, System.Windows.Controls.Button anchor, bool readOnly)
    {
        EnsurePermissionPopupBindings();
        _permissionPopupInitializing = true;
        _permissionPopupTarget = item;
        _permissionPopupAnchor = anchor;
        _permissionPopupReadOnly = readOnly;
        _permissionPopupBeforeStatus = item.ShareStatusText;
        _permissionPopupInitialUsers.Clear();
        _permissionPopupInitialUsers.AddRange(item.AllowedUsers
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        _permissionPopupInitialReadOnly = item.IsReadOnly;
        _permissionPopupInitialSharedOff = item.IsSharedOff;

        _permissionAllowed.Clear();
        if (readOnly && _currentMode == DisplayMode.FriendShop && _activeFriendShop is not null)
        {
            string ownerLabel = string.IsNullOrWhiteSpace(_activeFriendShop.DisplayName)
                ? _activeFriendShop.HostMachineName
                : _activeFriendShop.DisplayName;
            if (!string.IsNullOrWhiteSpace(ownerLabel))
            {
                _permissionAllowed.Add(ownerLabel);
            }
        }
        foreach (string user in item.AllowedUsers
                     .Where(u => !string.IsNullOrWhiteSpace(u))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_permissionAllowed.Contains(user, StringComparer.OrdinalIgnoreCase))
            {
                _permissionAllowed.Add(user);
            }
        }

        ReloadPermissionUnset();

        PermissionReadWriteRadio.IsChecked = !item.IsReadOnly && !item.IsSharedOff;
        PermissionReadOnlyRadio.IsChecked = item.IsReadOnly && !item.IsSharedOff;
        PermissionSharedOffRadio.IsChecked = item.IsSharedOff;

        Visibility editVis = readOnly ? Visibility.Collapsed : Visibility.Visible;
        PermissionOkButton.Visibility = editVis;
        PermissionClearButton.Visibility = editVis;
        PermissionUnsetBorder.Visibility = editVis;
        PermissionUnsetHeader.Visibility = editVis;
        PermissionUnsetColumn.Width = readOnly ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        PermissionHeaderUnsetColumn.Width = readOnly ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        PermissionEveryoneChip.Cursor = readOnly ? null : System.Windows.Input.Cursors.Hand;

        bool enabled = !readOnly;
        PermissionAllowedListBox.IsEnabled = enabled;
        PermissionUnsetListBox.IsEnabled = enabled;
        PermissionReadWriteRadio.IsEnabled = enabled;
        PermissionReadOnlyRadio.IsEnabled = enabled;
        PermissionSharedOffRadio.IsEnabled = enabled;

        UpdateEveryoneChipState();

        _permissionPopupInitializing = false;
        ApplyPermissionAccessLevelUiState();
        UpdatePermissionOkButtonState();

        PermissionPopup.PlacementTarget = anchor;
        PermissionPopup.IsOpen = false;
        PermissionPopup.IsOpen = true;
    }

    private void UpdateEveryoneChipState()
    {
        bool isEveryoneState = _permissionAllowed.Count == 0 && PermissionSharedOffRadio.IsChecked != true;
        if (isEveryoneState)
        {
            PermissionEveryoneChip.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0x7A, 0x66));
            PermissionEveryoneChip.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0x7A, 0x66));
            PermissionEveryoneChipText.Foreground = System.Windows.Media.Brushes.White;
        }
        else
        {
            PermissionEveryoneChip.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE4, 0xF0, 0xE8));
            PermissionEveryoneChip.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA8, 0xC3, 0xB0));
            PermissionEveryoneChipText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x54, 0x74, 0x63));
        }
    }

    private void UpdatePermissionOkButtonState()
    {
        bool canSubmit = !_permissionPopupReadOnly && HasPermissionPopupChanges();
        PermissionOkButton.IsEnabled = canSubmit;
        PermissionOkButton.Background = canSubmit
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2F, 0x66, 0x50))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xBF, 0xC7, 0xC2));
        PermissionOkButton.Cursor = canSubmit ? System.Windows.Input.Cursors.Hand : null;
    }

    private bool HasPermissionPopupChanges()
    {
        bool newSharedOff = PermissionSharedOffRadio.IsChecked == true;
        bool newReadOnly = PermissionReadOnlyRadio.IsChecked == true;
        List<string> requestedUsers = _permissionAllowed
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return newSharedOff != _permissionPopupInitialSharedOff
               || newReadOnly != _permissionPopupInitialReadOnly
               || !requestedUsers.SequenceEqual(_permissionPopupInitialUsers, StringComparer.OrdinalIgnoreCase);
    }

    private void EnsurePermissionPopupBindings()
    {
        if (_permissionPopupBound) return;
        PermissionAllowedListBox.ItemsSource = _permissionAllowed;
        PermissionUnsetListBox.ItemsSource = _permissionUnset;
        _permissionAllowed.CollectionChanged += (_, _) =>
        {
            UpdateEveryoneChipState();
            UpdatePermissionOkButtonState();
        };
        _permissionPopupBound = true;
    }

    private void ReloadPermissionUnset()
    {
        _permissionUnset.Clear();
        HashSet<string> already = new(_permissionAllowed, StringComparer.OrdinalIgnoreCase);
        foreach (Friend f in FriendsRepository.LoadAll())
        {
            string name = string.IsNullOrWhiteSpace(f.DisplayName) ? f.HostMachineName : f.DisplayName;
            if (string.IsNullOrWhiteSpace(name) || already.Contains(name)) continue;
            _permissionUnset.Add(name);
        }
    }

    private void ApplyPermissionAccessLevelUiState()
    {
        if (_permissionPopupReadOnly) return;
        bool canSpecifyUsers = PermissionSharedOffRadio.IsChecked != true;
        PermissionAllowedListBox.IsEnabled = canSpecifyUsers;
        PermissionUnsetListBox.IsEnabled = canSpecifyUsers;
        PermissionEveryoneChip.IsEnabled = canSpecifyUsers;
        UpdateEveryoneChipState();
        UpdatePermissionOkButtonState();
    }

    private void PermissionAccessLevelRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_permissionPopupInitializing || _permissionPopupReadOnly) return;
        if (PermissionSharedOffRadio.IsChecked == true)
        {
            _permissionAllowed.Clear();
            ReloadPermissionUnset();
        }
        ApplyPermissionAccessLevelUiState();
    }

    private void PermissionEveryoneChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (_permissionPopupReadOnly) return;
        if (PermissionSharedOffRadio.IsChecked == true) return;
        if (_permissionAllowed.Count == 0) return;
        _permissionAllowed.Clear();
        ReloadPermissionUnset();
        e.Handled = true;
    }

    private void PermissionClearButton_Click(object sender, MouseButtonEventArgs e)
    {
        if (_permissionPopupReadOnly) return;
        _permissionAllowed.Clear();
        ReloadPermissionUnset();
        e.Handled = true;
    }

    private void PermissionAllowedListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => MovePermissionAllowedToUnset();
    private void PermissionUnsetListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => MovePermissionUnsetToAllowed();

    private void MovePermissionUnsetToAllowed()
    {
        if (_permissionPopupReadOnly) return;
        List<string> picked = PermissionUnsetListBox.SelectedItems.Cast<string>().ToList();
        foreach (string name in picked)
        {
            _permissionUnset.Remove(name);
            if (!_permissionAllowed.Contains(name, StringComparer.OrdinalIgnoreCase))
                _permissionAllowed.Add(name);
        }
    }

    private void MovePermissionAllowedToUnset()
    {
        if (_permissionPopupReadOnly) return;
        List<string> picked = PermissionAllowedListBox.SelectedItems.Cast<string>().ToList();
        foreach (string name in picked)
        {
            _permissionAllowed.Remove(name);
            if (!_permissionUnset.Contains(name, StringComparer.OrdinalIgnoreCase))
                _permissionUnset.Add(name);
        }
    }

    private void PermissionCloseButton_Click(object sender, MouseButtonEventArgs e)
    {
        PermissionPopup.IsOpen = false;
        e.Handled = true;
    }

    private void PermissionPopup_Closed(object? sender, EventArgs e)
    {
        _permissionPopupTarget = null;
        _permissionPopupAnchor = null;
        _permissionPopupBeforeStatus = string.Empty;
    }

    private async void PermissionOkButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_permissionPopupReadOnly) return;
        if (!PermissionOkButton.IsEnabled) return;
        ShopItem? item = _permissionPopupTarget;
        System.Windows.Controls.Button? button = _permissionPopupAnchor;
        string beforeStatus = _permissionPopupBeforeStatus;
        if (item is null) { PermissionPopup.IsOpen = false; return; }

        try
        {
            bool newSharedOff = PermissionSharedOffRadio.IsChecked == true;
            bool newReadOnly = PermissionReadOnlyRadio.IsChecked == true;
            List<string> requestedUsers = _permissionAllowed
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool changed = newSharedOff != item.IsSharedOff
                           || newReadOnly != item.IsReadOnly
                           || !item.AllowedUsers.SequenceEqual(requestedUsers, StringComparer.OrdinalIgnoreCase);

            if (!changed)
            {
                PermissionPopup.IsOpen = false;
                return;
            }

            string sourceParent = Path.GetDirectoryName(item.FullPath) ?? string.Empty;
            var parentPerm = FindEffectiveAncestorPermission(sourceParent);
            var requestedPerm = (requestedUsers, newReadOnly, newSharedOff);
            if (!IsWithinRange(requestedPerm, parentPerm))
            {
                string parentStatus = PermissionToStatusText(parentPerm);
                SetTransientStatus($"上位フォルダーの共有条件（{parentStatus}）を超えるため、この設定にはできません。");
                return;
            }

            item.IsSharedOff = newSharedOff;
            item.IsReadOnly = newReadOnly;
            item.AllowedUsers.Clear();
            foreach (string name in requestedUsers)
            {
                item.AllowedUsers.Add(name);
            }

            PermissionPopup.IsOpen = false;
            {
                bool isHeldItem = IsHeldItemPath(item.FullPath);
                StorePermission(item);
                if (item.IsDirectory && Directory.Exists(item.FullPath))
                {
                    ClearDescendantPermissionOverrides(item.FullPath);
                }
                SavePermissionMap();
                item.RefreshShareStatus();

                string afterStatus = item.ShareStatusText;
                string permNote = afterStatus.StartsWith("指定", StringComparison.Ordinal) && item.AllowedUsers.Count > 0
                    ? $"対象: {string.Join("、", item.AllowedUsers)}"
                    : afterStatus;
                AppendHistory(
                    HistoryChannel.Update,
                    $"{item.Name} の共有設定を変更しました。（{beforeStatus} → {afterStatus}）",
                    eventType: "PermissionChanged",
                    outcome: HistoryOutcome.Success,
                    targetName: item.Name,
                    pathText: Path.GetDirectoryName(item.FullPath) ?? string.Empty,
                    sourcePath: item.FullPath,
                    note: permNote,
                    source: "MainWindow");

                if (item.IsDirectory && Directory.Exists(item.FullPath))
                {
                    string cascadeFolder = item.Name;
                    string cascadePath = item.FullPath;
                    string cascadeNote = $"上位フォルダー: {cascadeFolder}（{afterStatus}）";
                    foreach (string childPath in Directory.EnumerateFileSystemEntries(
                        cascadePath, "*", SearchOption.AllDirectories))
                    {
                        string childName = Path.GetFileName(childPath);
                        string childParent = Path.GetDirectoryName(childPath) ?? cascadePath;
                        AppendHistory(
                            HistoryChannel.Update,
                            $"{childName} の共有設定が上位フォルダー「{cascadeFolder}」の変更により影響を受けました。",
                            eventType: "PermissionCascade",
                            outcome: HistoryOutcome.Info,
                            targetName: childName,
                            pathText: childParent,
                            sourcePath: childPath,
                            note: cascadeNote,
                            source: "MainWindow.cascade");
                    }
                }

                if (_isShopOpen && _currentMode != DisplayMode.FriendShop && !item.IsHoldFolder)
                {
                    string path = item.FullPath;
                    bool isSharedOff = isHeldItem || item.IsSharedOff;
                    bool isReadOnly = isHeldItem ? false : item.IsReadOnly;

                    if (button is not null) button.IsEnabled = false;
                    Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                    try
                    {
                        bool ok = await Task.Run(() => _pipeClient.SetSubfolderPermission(path, isSharedOff, isReadOnly));
                        if (!ok)
                            SetTransientStatus("権限の設定に失敗しました。");
                        else
                            _pipeClient.BroadcastPermission();
                    }
                    finally
                    {
                        Mouse.OverrideCursor = null;
                        if (button is not null) button.IsEnabled = true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            SwkLogger.Error("Share permission UI failed", ex);
            SetTransientStatus("共有設定を変更できませんでした。");
        }
    }

    private void StorePermission(ShopItem item)
    {
        List<string> users = item.AllowedUsers
            .Where(user => !string.IsNullOrWhiteSpace(user))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (IsHeldItemPath(item.FullPath))
        {
            _holdDisplayPermissionMap[item.FullPath] = (users, item.IsReadOnly, item.IsSharedOff);
            _permissionMap[item.FullPath] = ([], false, true);
            return;
        }

        if (users.Count == 0 && !item.IsReadOnly && !item.IsSharedOff)
        {
            _permissionMap.Remove(item.FullPath);
            return;
        }

        _permissionMap[item.FullPath] = (users, item.IsReadOnly, item.IsSharedOff);
    }

    private bool IsHeldItemPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !IsHoldFolderPath(path) &&
        IsUnderFolder(path, GetHoldFolderPath());

    private static void ApplyPermissionToItem(
        ShopItem item,
        (List<string> Users, bool IsReadOnly, bool IsSharedOff) perm)
    {
        item.AllowedUsers.Clear();
        foreach (string user in perm.Users.Where(user => !string.IsNullOrWhiteSpace(user)))
        {
            item.AllowedUsers.Add(user);
        }

        item.IsReadOnly = perm.IsReadOnly;
        item.IsSharedOff = perm.IsSharedOff;
    }

    private (List<string> Users, bool IsReadOnly, bool IsSharedOff)? GetDisplayedPermissionForPath(
        string sourcePath,
        string sourceParent)
    {
        if (IsHeldItemPath(sourcePath) &&
            _holdDisplayPermissionMap.TryGetValue(sourcePath, out var holdDisplay))
        {
            return holdDisplay;
        }

        bool hasOwnEntry = _permissionMap.TryGetValue(sourcePath, out var ownPerm)
            && (ownPerm.Users.Count > 0 || ownPerm.IsReadOnly || ownPerm.IsSharedOff);

        return hasOwnEntry
            ? ownPerm
            : FindEffectiveAncestorPermission(sourceParent);
    }

    private void MoveHoldDisplayPermission(string sourcePath, string destinationPath)
    {
        if (!_holdDisplayPermissionMap.Remove(sourcePath, out var displayPerm))
        {
            return;
        }

        if (IsHeldItemPath(destinationPath))
        {
            _holdDisplayPermissionMap[destinationPath] = displayPerm;
        }
    }

    private void MovePermissionEntries(string sourcePath, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
        {
            return;
        }

        MovePermissionEntriesInMap(_permissionMap, sourcePath, destinationPath);
        MovePermissionEntriesInMap(_holdDisplayPermissionMap, sourcePath, destinationPath);
    }

    private static void MovePermissionEntriesInMap(
        Dictionary<string, (List<string> Users, bool IsReadOnly, bool IsSharedOff)> map,
        string sourcePath,
        string destinationPath)
    {
        List<(string SourceKey, string DestinationKey, (List<string> Users, bool IsReadOnly, bool IsSharedOff) Permission)> moves = [];

        foreach (var (key, permission) in map)
        {
            string? movedKey = TryMovePermissionPath(key, sourcePath, destinationPath);
            if (movedKey is null)
            {
                continue;
            }

            moves.Add((key, movedKey, ([.. permission.Users], permission.IsReadOnly, permission.IsSharedOff)));
        }

        foreach (var (sourceKey, _, _) in moves)
        {
            map.Remove(sourceKey);
        }

        foreach (var (_, destinationKey, permission) in moves)
        {
            map[destinationKey] = permission;
        }
    }

    private static string? TryMovePermissionPath(string path, string sourcePath, string destinationPath)
    {
        if (string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return destinationPath;
        }

        if (!IsUnderFolder(path, sourcePath))
        {
            return null;
        }

        string suffix = path[sourcePath.Length..]
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(suffix)
            ? destinationPath
            : Path.Combine(destinationPath, suffix);
    }

    private void ClearDescendantPermissionOverrides(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        List<string> permissionPaths = _permissionMap.Keys
            .Where(path => IsUnderFolder(path, folderPath) &&
                           !string.Equals(path, folderPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (string path in permissionPaths)
        {
            _permissionMap.Remove(path);
        }

        List<string> holdDisplayPaths = _holdDisplayPermissionMap.Keys
            .Where(path => IsUnderFolder(path, folderPath) &&
                           !string.Equals(path, folderPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (string path in holdDisplayPaths)
        {
            _holdDisplayPermissionMap.Remove(path);
        }
    }

    private void UpdateSidebar(bool isOpen)
    {
        // The inline sidebar was superseded by the ユーザー一覧 popup.
        // The XAML element is preserved (Collapsed) so its name field stays bound.
        _ = isOpen;
        SidebarBorder.Visibility = Visibility.Collapsed;
        SidebarItems.Clear();
        SidebarStatusTextBlock.Text = string.Empty;
    }

    private void SidebarListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Sidebar is hidden; the handler stays so XAML hookup compiles.
        _ = sender;
        _ = e;
    }

    private void OpenInviteDialog()
    {
        if (string.IsNullOrWhiteSpace(_shopFolder))
        {
            SetTransientStatus("お店を選んでから招待を発行してください。");
            return;
        }

        string shareName = DeriveShareName(_shopFolder);
        if (string.IsNullOrWhiteSpace(shareName))
        {
            SetTransientStatus("お店の名前が決められませんでした。");
            return;
        }

        if (!SmbAccountManager.EnsureStoredShopKey())
        {
            SetTransientStatus("招待を用意できませんでした。");
            return;
        }

        InviteDialog dialog = new(shareName, _shareAccessRight)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private NotificationMode GetSelectedNotificationMode()
    {
        if (NotificationModeComboBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse(tag, out NotificationMode mode))
        {
            return mode;
        }

        return NotificationMode.All;
    }

    private void SelectNotificationMode(NotificationMode mode)
    {
        foreach (object item in NotificationModeComboBox.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && string.Equals(tag, mode.ToString(), StringComparison.Ordinal))
            {
                NotificationModeComboBox.SelectedItem = item;
                return;
            }
        }

        NotificationModeComboBox.SelectedIndex = 0;
    }

    private void SizeCalcCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        bool isOn = SizeCalcCheckBox.IsChecked == true;
        if (isOn == _isSizeCalcEnabled)
        {
            return;
        }

        _isSizeCalcEnabled = isOn;
        SaveSettings();

        if (_isSizeCalcEnabled)
        {
            StartFolderSizeCalculation();
        }
        else
        {
            CancelFolderSizeCalculation();
            ClearFolderSizeDisplay();
        }
    }

    private void ShopItemsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        CancelRenameTimer();
        if (ShopItemsListView.SelectedItem is ShopItem item)
        {
            OpenShopItem(item);
        }
    }

    private void ShopItemsListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ShopItemsListView.ContextMenu?.IsOpen == true)
        {
            ShopItem? clickedItem = GetShopItemFromSource(e.OriginalSource as DependencyObject);
            ShopItemsListView.ContextMenu.IsOpen = false;
            SuppressShopItemsCommandsBriefly();

            if (clickedItem is null)
            {
                ShopItemsListView.SelectedItems.Clear();
            }
            else if (!ShopItemsListView.SelectedItems.Contains(clickedItem))
            {
                ShopItemsListView.SelectedItem = clickedItem;
            }

            _clickSelectionPending = false;
            _itemWasSelectedAtPress = false;
            _dragStartItem = null;
            EndRubberBand();
            e.Handled = true;
            return;
        }

        _dragStartPoint = e.GetPosition(null);
        _dragStartItem = GetShopItemFromSource(e.OriginalSource as DependencyObject);
        _itemWasSelectedAtPress = false;

        if (IsClickOnItemButton(e.OriginalSource as DependencyObject))
        {
            CancelRenameTimer();
            _dragStartItem = null;
            _clickSelectionPending = false;
            _isRubberBanding = false;
            return;
        }

        // リネーム中に TextBox 以外をクリック → キャンセルして選択解除（ブランチ共通）
        if (ShopItems.Any(i => i.IsRenaming) && !IsClickOnRenameTextBox(e.OriginalSource as DependencyObject))
        {
            CommitCurrentInlineRename(confirm: false);
            ShopItemsListView.SelectedItems.Clear();
            e.Handled = true;
            return;
        }

        if (_dragStartItem is null)
        {
            ShopItemsListView.SelectedItems.Clear();
            CancelRenameTimer();
            _rubberBandOrigin = e.GetPosition(ShopItemsListView);
            _isRubberBanding = true;
        }
        else if (ShopItemsListView.SelectedItems.Contains(_dragStartItem))
        {
            // 選択済みアイテムをクリック → リネームタイマー or D&D へ
            CancelRenameTimer();
            _itemWasSelectedAtPress = !_dragStartItem.IsHoldFolder &&
                !_dragStartItem.IsRenaming &&
                ShopItemsListView.SelectedItems.Count == 1 &&
                _currentMode != DisplayMode.FriendShop;
            if (ShopItemsListView.SelectedItems.Count > 1)
            {
                // 複数選択中のアイテムをクリック → D&D 開始まで選択解除を保留
                _clickSelectionPending = true;
                e.Handled = true;
            }
        }
        else
        {
            // 未選択アイテムをクリック
            CancelRenameTimer();
            // Shift/Ctrl あり: WPF に任せる（範囲選択・トグル選択）
            // Shift/Ctrl なし + 行余白: ラバーバンド範囲選択
            // Shift/Ctrl なし + コンテンツ部分(アイコン・ファイル名等): 通常選択→D&D を許可
            if ((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) == 0)
            {
                if (!IsClickOnItemContent(e.OriginalSource as DependencyObject))
                {
                    // 行余白クリック → リネーム中断・選択解除してラバーバンド開始
                    CommitCurrentInlineRename(confirm: false);
                    ShopItemsListView.SelectedItems.Clear();
                    _rubberBandOrigin = e.GetPosition(ShopItemsListView);
                    _isRubberBanding = true;
                    e.Handled = true;
                }
                else
                {
                    // コンテンツクリック（アイコン・名前）→ WPF にアイテム選択させつつラバーバンドも開始
                    // MouseMove では _isRubberBanding が優先されドラッグは起動しない
                    // 単独クリックでドラッグなし → WPF の選択のみ残る
                    _rubberBandOrigin = e.GetPosition(ShopItemsListView);
                    _isRubberBanding = true;
                }
            }
        }
    }

    private void ShopItemsListView_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_suppressInternalDragUntilLeftButtonRelease)
        {
            _suppressInternalDragUntilLeftButtonRelease = false;
            _clickSelectionPending = false;
            _itemWasSelectedAtPress = false;
            _dragStartItem = null;
            EndRubberBand();
            return;
        }

        bool wasSelected = _itemWasSelectedAtPress;
        ShopItem? pressedItem = _dragStartItem;
        _itemWasSelectedAtPress = false;

        CancelRenameTimer();
        EndRubberBand();

        if (_clickSelectionPending && pressedItem is not null)
        {
            ShopItemsListView.SelectedItem = pressedItem;
            _clickSelectionPending = false;
        }

        if (wasSelected && pressedItem is not null && !pressedItem.IsRenaming)
        {
            StartRenameTimer(pressedItem);
        }
    }

    private void ShopItemsListView_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndRubberBand();
            return;
        }

        if (_suppressInternalDragUntilLeftButtonRelease)
        {
            _clickSelectionPending = false;
            _itemWasSelectedAtPress = false;
            _dragStartItem = null;
            EndRubberBand();
            return;
        }

        if (_isRubberBanding)
        {
            UpdateRubberBand(e.GetPosition(ShopItemsListView));
            return;
        }

        if (_dragStartItem is null || _dragStartItem.IsHoldFolder)
        {
            return;
        }

        System.Windows.Point currentPosition = e.GetPosition(null);
        if (Math.Abs(currentPosition.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPosition.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _clickSelectionPending = false;
        _itemWasSelectedAtPress = false;
        CancelRenameTimer();

        List<ShopItem> itemsToDrag;
        if (ShopItemsListView.SelectedItems.Contains(_dragStartItem))
        {
            itemsToDrag = ShopItemsListView.SelectedItems.Cast<ShopItem>()
                .Where(i => !i.IsHoldFolder)
                .ToList();
        }
        else
        {
            itemsToDrag = new List<ShopItem> { _dragStartItem };
        }

        if (itemsToDrag.Count == 0)
        {
            return;
        }

        ClearDropTargetHighlight();
        System.Windows.DataObject data = new();
        string[] dragPaths = itemsToDrag.Select(i => i.FullPath).ToArray();
        data.SetData(System.Windows.DataFormats.FileDrop, dragPaths);
        if (itemsToDrag.Count == 1)
        {
            data.SetData(InternalDragPathFormat, dragPaths[0]);
        }
        else
        {
            data.SetData(InternalDragPathsFormat, dragPaths);
        }

        _activeInternalDragPaths = dragPaths;
        string hint = itemsToDrag.Count == 1 ? itemsToDrag[0].Name : $"{itemsToDrag.Count} つのアイテム";
        ShowDragHint(hint);
        try
        {
            System.Windows.DragDrop.DoDragDrop(ShopItemsListView, data, System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move);
        }
        finally
        {
            _activeInternalDragPaths = null;
            HideDragHint();
            ClearDropTargetHighlight();
            _dragStartItem = null;
        }
    }

    private void ShopItemsListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _suppressInternalDragUntilLeftButtonRelease = true;
        _clickSelectionPending = false;
        _itemWasSelectedAtPress = false;
        EndRubberBand();

        ShopItem? item = GetShopItemFromSource(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            ShopItemsListView.SelectedItem = null;
        }
        else if (!ShopItemsListView.SelectedItems.Contains(item))
        {
            ShopItemsListView.SelectedItem = item;
        }
    }

    private void ShopItemsListView_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            PasteExternalFilesFromClipboard();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        if (ShopItemsListView.SelectedItem is not ShopItem item)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(item.Name);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            SetTransientStatus("コピーできませんでした。");
        }

        e.Handled = true;
    }

    private void PasteFromWindowsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!ShouldProcessShopItemsContextMenuCommand(sender))
        {
            return;
        }

        PasteExternalFilesFromClipboard();
    }

    private async void PasteExternalFilesFromClipboard()
    {
        if (string.IsNullOrWhiteSpace(_currentFolder) || !Directory.Exists(_currentFolder))
        {
            SetTransientStatus("コピー先のフォルダーが見つかりません。");
            AppendUpdateUiHistory(
                "Windows の貼り付けを始められませんでした。",
                "PasteFromWindows",
                HistoryOutcome.Warning,
                pathText: _currentFolder,
                note: "コピー先のフォルダーが見つかりません。",
                source: "MainWindow.paste");
            return;
        }

        StringCollection? fileDropList;
        try
        {
            if (!System.Windows.Clipboard.ContainsFileDropList())
            {
                SetTransientStatus("Windows のコピー内容がありません。");
                AppendUpdateUiHistory(
                    "Windows の貼り付けを始められませんでした。",
                    "PasteFromWindows",
                    HistoryOutcome.Warning,
                    pathText: _currentFolder,
                    note: "Windows のコピー内容がありません。",
                    source: "MainWindow.paste");
                return;
            }

            fileDropList = System.Windows.Clipboard.GetFileDropList();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            SwkLogger.Warn($"PasteExternalFilesFromClipboard failed: {ex.Message}");
            SetTransientStatus("Windows のコピー内容を読み取れませんでした。");
            AppendUpdateUiHistory(
                "Windows の貼り付けを始められませんでした。",
                "PasteFromWindows",
                HistoryOutcome.Warning,
                pathText: _currentFolder,
                note: $"Windows のコピー内容を読み取れませんでした。({ex.Message})",
                source: "MainWindow.paste");
            return;
        }

        List<string> paths = fileDropList.Cast<string>()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        if (paths.Count == 0)
        {
            SetTransientStatus("Windows のコピー内容がありません。");
            AppendUpdateUiHistory(
                "Windows の貼り付けを始められませんでした。",
                "PasteFromWindows",
                HistoryOutcome.Warning,
                pathText: _currentFolder,
                note: "Windows のコピー内容がありません。",
                source: "MainWindow.paste");
            return;
        }

        BeginProcessing();
        try { await PlaceExternalFilesAsync(paths, _currentFolder); }
        finally { EndProcessing(); }
    }

    private void UpdateRubberBand(System.Windows.Point current)
    {
        double left   = Math.Min(_rubberBandOrigin.X, current.X);
        double top    = Math.Min(_rubberBandOrigin.Y, current.Y);
        double width  = Math.Abs(current.X - _rubberBandOrigin.X);
        double height = Math.Abs(current.Y - _rubberBandOrigin.Y);

        Canvas.SetLeft(RubberBandRect, left);
        Canvas.SetTop(RubberBandRect, top);
        RubberBandRect.Width  = Math.Max(width,  1);
        RubberBandRect.Height = Math.Max(height, 1);
        RubberBandRect.Visibility = Visibility.Visible;

        if (width >= 2 || height >= 2)
            SelectItemsInRect(new Rect(left, top, width, height));
    }

    private void EndRubberBand()
    {
        if (!_isRubberBanding) return;
        _isRubberBanding = false;
        RubberBandRect.Visibility = Visibility.Collapsed;
    }

    private void SelectItemsInRect(Rect rect)
    {
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        foreach (ShopItem item in ShopItems)
        {
            var container = ShopItemsListView.ItemContainerGenerator.ContainerFromItem(item) as System.Windows.Controls.ListViewItem;
            if (container == null) continue;
            GeneralTransform tf = container.TransformToAncestor(ShopItemsListView);
            Rect itemRect = new(tf.Transform(new System.Windows.Point(0, 0)),
                                new System.Windows.Size(container.ActualWidth, container.ActualHeight));
            bool hit = rect.IntersectsWith(itemRect);
            if (hit)
                ShopItemsListView.SelectedItems.Add(item);
            else if (!shift)
                ShopItemsListView.SelectedItems.Remove(item);
        }
    }

    private string GetItemShareStatus(string path) =>
        ShopItems.FirstOrDefault(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase))
            ?.ShareStatusText ?? "不明";

    private static ShopItem? GetShopItemFromSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.ListViewItem { DataContext: ShopItem item })
            {
                return item;
            }
            if (source is System.Windows.Controls.ListView)
            {
                break;
            }
            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void ShopItemsListView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        AdjustNameColumnWidth();
    }

    private void AdjustNameColumnWidth()
    {
        if (NameColumn is null)
        {
            return;
        }

        double total = ShopItemsListView.ActualWidth;
        if (total <= 0)
        {
            return;
        }

        double scrollbar = SystemParameters.VerticalScrollBarWidth;
        double padding = 8;
        double otherCols = ShareStatusColumn.Width + KindColumn.Width + UpdatedAtColumn.Width + SizeColumn.Width;
        double nameWidth = total - otherCols - scrollbar - padding;
        if (nameWidth < 80)
        {
            nameWidth = 80;
        }

        NameColumn.Width = nameWidth;
    }

    private void ShopContentsBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateBreadcrumbDisplay();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_backStack.Count == 0 || string.IsNullOrWhiteSpace(_currentFolder))
        {
            return;
        }

        _forwardStack.Push(_currentFolder);
        NavigateTo(_backStack.Pop(), addHistory: false, syncModeToPath: true);
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_forwardStack.Count == 0 || string.IsNullOrWhiteSpace(_currentFolder))
        {
            return;
        }

        _backStack.Push(_currentFolder);
        NavigateTo(_forwardStack.Pop(), addHistory: false, syncModeToPath: true);
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        ShowHistoryDialog(HistoryChannel.Update, "履歴");
    }

    private void ShopItemsListView_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        bool hasInternalDragData = HasInternalDraggedPaths(e.Data);
        e.Effects = GetDropEffect(e);
        UpdateDropTargetHighlight(e);
        string? dragDestinationFolder = hasInternalDragData
            ? GetDropDestinationFolder(e) ?? _dropTargetItem?.FullPath
            : GetDropDestinationFolder(e) ?? _dropTargetItem?.FullPath ?? _currentFolder;
        bool droppingToHold = !string.IsNullOrWhiteSpace(dragDestinationFolder) && IsHoldFolderPath(dragDestinationFolder);

        if (hasInternalDragData &&
            e.Effects != System.Windows.DragDropEffects.None)
        {
            bool isCopy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            DragHintTextBlock.Text = droppingToHold
                ? "ここに離すと保留にしまいます"
                : isCopy
                    ? "Ctrl を押しています — フォルダーに重ねて離すとコピーします"
                    : "移動先のフォルダーに重ねて離すと移動します";
            if (e.Data.GetDataPresent(InternalDragPathFormat))
            {
                ShowDragHint(Path.GetFileName((string)e.Data.GetData(InternalDragPathFormat)));
            }
            else
            {
                string[] ps = (string[])e.Data.GetData(InternalDragPathsFormat);
                ShowDragHint($"{ps.Length} つのアイテム");
            }
        }
        else if (e.Effects == System.Windows.DragDropEffects.None)
        {
            HideDragHint();
        }
        else if (e.Effects != System.Windows.DragDropEffects.None)
        {
            DragHintTextBlock.Text = "フォルダーに重ねて離すとコピーします";
            ShowDragHint(GetExternalDragHintLabel(e));
        }

        e.Handled = true;
    }

    private void ShopItemsListView_GiveFeedback(object sender, System.Windows.GiveFeedbackEventArgs e)
    {
        UpdateDragPreviewPosition();
        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void ShopItemsListView_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (!ShopItemsListView.IsMouseOver)
        {
            ClearDropTargetHighlight();
            HideDragHint();
        }
    }

    private async void ShopItemsListView_Drop(object sender, System.Windows.DragEventArgs e)
    {
        // DragOver で特定済みのフォルダを Drop 前にキャプチャ（ClearDropTargetHighlight で消える前に）
        ShopItem? highlightedFolder = _dropTargetItem;
        ClearDropTargetHighlight();
        HideDragHint();

        bool hasInternalDragData = HasInternalDraggedPaths(e.Data);
        if (!CanAcceptDrop(e) || string.IsNullOrWhiteSpace(_currentFolder))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        // 内部ドラッグでは DragOver 時のハイライト済みフォルダを使う場合も、Drop 直前に必ず再検証する。
        string? destinationFolder = hasInternalDragData
            ? GetDropDestinationFolder(e) ?? (IsValidDropTargetItem(highlightedFolder, e.Data, e.KeyStates) ? highlightedFolder?.FullPath : null)
            : GetDropDestinationFolder(e) ?? highlightedFolder?.FullPath ?? _currentFolder;

        if (hasInternalDragData &&
            (string.IsNullOrWhiteSpace(destinationFolder) ||
             !IsInternalDropTargetAllowed(destinationFolder, e.Data, e.KeyStates)))
        {
            return;
        }

        BeginProcessing();
        try
        {
            if (e.Data.GetDataPresent(InternalDragPathFormat))
            {
                string sourcePath = (string)e.Data.GetData(InternalDragPathFormat);
                if (IsHoldFolderPath(destinationFolder!))
                    await HoldInternalDraggedItemsAsync([sourcePath]);
                else if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0)
                    await CopyInternalDraggedItemAsync(sourcePath, destinationFolder!);
                else
                    await MoveInternalDraggedItemAsync(sourcePath, destinationFolder!);
                return;
            }

            if (e.Data.GetDataPresent(InternalDragPathsFormat))
            {
                string[] sourcePaths = (string[])e.Data.GetData(InternalDragPathsFormat);
                if (IsHoldFolderPath(destinationFolder!))
                    await HoldInternalDraggedItemsAsync(sourcePaths);
                else if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0)
                    await CopyInternalDraggedItemsAsync(sourcePaths, destinationFolder!);
                else
                    await MoveInternalDraggedItemsAsync(sourcePaths, destinationFolder!);
                return;
            }

            if (string.IsNullOrWhiteSpace(destinationFolder) || !Directory.Exists(destinationFolder))
            {
                SetTransientStatus("その場所が見つかりません。");
                return;
            }

            string[] paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            SwkLogger.Info(
                $"Trace.ExternalFlow.Sender.Entry: route=WPF_DROP count={paths.Length} names={DescribeExternalPaths(paths)} dest={destinationFolder}");
            await PlaceExternalFilesAsync(paths, destinationFolder);
        }
        finally
        {
            EndProcessing();
        }
    }

    private async Task PlaceExternalFilesAsync(IReadOnlyList<string> paths, string destinationFolder)
    {
        if (!Directory.Exists(destinationFolder)) return;

        if (!TryConfirmInteractionAction("置く", paths.Select(path => Path.GetFileName(path) ?? path).ToList(), out string? interactionMessage))
        {
            return;
        }

        string modeLabel = _currentMode.ToString();
        bool alertShown = false;
        SwkLogger.Info(
            $"Trace.ExternalFlow.Sender.Start: mode={modeLabel} count={paths.Count} names={DescribeExternalPaths(paths)} dest={destinationFolder}");

        if (_currentMode == DisplayMode.FriendShop && !IsDirectoryWritable(destinationFolder))
        {
            SwkLogger.Info($"Explorer[{_currentMode}]: Place blocked - not writable: {destinationFolder}");
            SwkLogger.Info($"Trace.ExternalFlow.Sender.Blocked: mode={modeLabel} dest={destinationFolder} reason=not-writable");
            SetTransientStatus("このフォルダーにはコピーできません。");
            return;
        }

        if (!TryValidateBatchOperation(
                paths,
                "配置",
                sourcePath => ExplorerActionService.ValidatePlaceTarget(new PlaceExternalItemRequest
                {
                    ModeLabel = modeLabel,
                    SourcePath = sourcePath,
                    DestinationFolder = destinationFolder,
                    BeforeWrite = SuppressExternalChangeNotifications,
                })))
        {
            SwkLogger.Info($"Trace.ExternalFlow.Sender.Blocked: mode={modeLabel} dest={destinationFolder} reason=validation");
            return;
        }

        int placedCount = 0;
        string? lastPlacedName = null;
        var progress = await CreateFileProgressAsync("コピー");
        int total = paths.Count;
        BeginDeferredPermissionSave();
        _pendingInteractionMessage = interactionMessage;
        try
        {
            for (int i = 0; i < total; i++)
            {
                string sourcePath = paths[i];
                string fileName = Path.GetFileName(sourcePath);
                progress.Report((i, total, fileName));
                await Task.Yield(); // 高速完了時の同期化を防ぎ Progress callback を確実に描画させる

                ExplorerActionResult result = await Task.Run(() => ExplorerActionService.PlaceExternalItem(new PlaceExternalItemRequest
                {
                    ModeLabel = modeLabel,
                    SourcePath = sourcePath,
                    DestinationFolder = destinationFolder,
                    BeforeWrite = SuppressExternalChangeNotifications,
                }));

                bool shouldShowAlert = !alertShown;
                ApplyExplorerActionResult(result, showAlertDialog: shouldShowAlert);
                if (shouldShowAlert &&
                    !string.IsNullOrWhiteSpace(result.UserMessage) &&
                    (result.State == ExplorerActionState.Blocked || result.State == ExplorerActionState.Failure))
                {
                    alertShown = true;
                }
                SwkLogger.Info(
                    $"Trace.ExternalFlow.Sender.Result: state={result.State} event={result.EventType} target={result.TargetName ?? "-"} " +
                    $"source={result.Source ?? "-"} sourcePath={result.SourcePath ?? "-"} destPath={result.DestinationPath ?? "-"} " +
                    $"destFolder={result.DestinationFolder ?? "-"} refresh={result.ShouldRefreshUi}");

                if (result.ShouldRefreshUi &&
                    !string.IsNullOrWhiteSpace(result.DestinationPath) &&
                    !string.IsNullOrWhiteSpace(result.DestinationFolder))
                {
                    NoteFutureSharePolicyRepair(result.DestinationPath, result.DestinationFolder, SharePolicyRepairReason.Placed);
                    MaybeAppendPermissionInheritedOnArrival(result.DestinationPath!, result.DestinationFolder!, "MainWindow.place");
                    placedCount++;
                    lastPlacedName = result.TargetName;
                }
                else
                {
                    SwkLogger.Info(
                        $"Trace.ExternalFlow.Sender.NoAftercare: target={result.TargetName ?? fileName} state={result.State} " +
                        $"destPath={result.DestinationPath ?? "-"} destFolder={result.DestinationFolder ?? "-"}");
                }

                progress.Report((i + 1, total, fileName));
            }
        }
        finally
        {
            _pendingInteractionMessage = null;
            EndDeferredPermissionSave();
        }

        if (placedCount > 0)
        {
            string statusMessage = placedCount == 1 && lastPlacedName is not null
                ? $"{lastPlacedName} を置きました。"
                : $"{placedCount} つ置きました。";
            SetTransientStatus(statusMessage);

            if (_currentFolder is not null &&
                string.Equals(
                    Path.GetFullPath(destinationFolder),
                    Path.GetFullPath(_currentFolder),
                    StringComparison.OrdinalIgnoreCase) &&
                lastPlacedName is not null)
            {
                _pendingFocusName = lastPlacedName;
            }

            InvalidateSizeCacheUnder(destinationFolder);
            RefreshShopItems();
        }
    }

    private async Task MoveInternalDraggedItemAsync(string sourcePath, string destinationFolder)
    {
        if (!TryConfirmInteractionAction("移す", [Path.GetFileName(sourcePath)], out string? interactionMessage))
        {
            return;
        }

        string modeLabel = _currentMode.ToString();
        _pendingInteractionMessage = interactionMessage;
        try
        {
            ExplorerActionResult result = await Task.Run(() => ExplorerActionService.MoveItem(new MoveItemRequest
            {
                ModeLabel = modeLabel,
                SourcePath = sourcePath,
                DestinationFolder = destinationFolder,
                IsHoldFolderPath = IsHoldFolderPath,
                IsUnderFolder = IsUnderFolder,
                GetShareStatus = GetItemShareStatus,
                BeforeWrite = SuppressExternalChangeNotifications,
            }));

            if (result.State == ExplorerActionState.NoChange)
            {
                return;
            }

            ApplyExplorerActionResult(result);
            if (!result.ShouldRefreshUi ||
                string.IsNullOrWhiteSpace(result.SourceParent) ||
                string.IsNullOrWhiteSpace(result.DestinationFolder) ||
                string.IsNullOrWhiteSpace(result.DestinationPath) ||
                string.IsNullOrWhiteSpace(result.SourcePath))
            {
                return;
            }

            bool preserved = PreservePermissionOnArrival(
                result.SourcePath,
                result.SourceParent,
                result.DestinationPath,
                result.DestinationFolder,
                removeSourceEntry: true,
                historySource: "MainWindow.move");
            if (!preserved)
            {
                NoteFutureSharePolicyRepair(result.DestinationPath, result.DestinationFolder, SharePolicyRepairReason.Moved);
            }
            InvalidateSizeCacheUnder(result.SourceParent);
            InvalidateSizeCacheUnder(result.DestinationFolder);
            RefreshShopItems();
        }
        finally
        {
            _pendingInteractionMessage = null;
        }
    }

    private async Task MoveInternalDraggedItemsAsync(IReadOnlyList<string> sourcePaths, string destinationFolder)
    {
        if (!TryConfirmInteractionAction("移す", sourcePaths.Select(path => Path.GetFileName(path) ?? path).ToList(), out string? interactionMessage))
        {
            return;
        }

        var progress = await CreateFileProgressAsync("移動");
        _pendingInteractionMessage = interactionMessage;
        try
        {
            (int movedCount, string? lastName) = await ExecuteMoveExplorerActionsAsync(
                sourcePaths,
                destinationFolder,
                progress);

            if (movedCount > 0)
            {
                string statusMessage = movedCount == 1 && lastName is not null
                    ? $"{lastName} を移しました。"
                    : $"{movedCount} つ移しました。";
                SetTransientStatus(statusMessage);
                InvalidateSizeCacheUnder(destinationFolder);
                RefreshShopItems();
            }
        }
        finally
        {
            _pendingInteractionMessage = null;
        }
    }

    private async Task CopyInternalDraggedItemAsync(string sourcePath, string destinationFolder)
    {
        if (!TryConfirmInteractionAction("コピーする", [Path.GetFileName(sourcePath)], out string? interactionMessage))
        {
            return;
        }

        string modeLabel = _currentMode.ToString();
        _pendingInteractionMessage = interactionMessage;
        try
        {
            ExplorerActionResult result = await Task.Run(() => ExplorerActionService.CopyItem(new CopyItemRequest
            {
                ModeLabel = modeLabel,
                SourcePath = sourcePath,
                DestinationFolder = destinationFolder,
                IsHoldFolderPath = IsHoldFolderPath,
                IsUnderFolder = IsUnderFolder,
                GetShareStatus = GetItemShareStatus,
                BeforeWrite = SuppressExternalChangeNotifications,
            }));

            if (result.State == ExplorerActionState.NoChange)
            {
                return;
            }

            ApplyExplorerActionResult(result);
            if (!result.ShouldRefreshUi ||
                string.IsNullOrWhiteSpace(result.SourcePath) ||
                string.IsNullOrWhiteSpace(result.SourceParent) ||
                string.IsNullOrWhiteSpace(result.DestinationPath) ||
                string.IsNullOrWhiteSpace(result.DestinationFolder))
            {
                return;
            }

            bool preserved = PreservePermissionOnArrival(
                result.SourcePath,
                result.SourceParent,
                result.DestinationPath,
                result.DestinationFolder,
                removeSourceEntry: false,
                historySource: "MainWindow.copy");
            if (!preserved)
            {
                NoteFutureSharePolicyRepair(result.DestinationPath, result.DestinationFolder, SharePolicyRepairReason.Placed);
                MaybeAppendPermissionInheritedOnArrival(result.DestinationPath, result.DestinationFolder, "MainWindow.copy");
            }
            InvalidateSizeCacheUnder(result.DestinationFolder);
            if (string.Equals(
                    Path.GetFullPath(result.DestinationFolder),
                    Path.GetFullPath(_currentFolder ?? string.Empty),
                    StringComparison.OrdinalIgnoreCase))
            {
                _pendingFocusName = result.TargetName;
            }
            RefreshShopItems();
        }
        finally
        {
            _pendingInteractionMessage = null;
        }
    }

    private async Task CopyInternalDraggedItemsAsync(IReadOnlyList<string> sourcePaths, string destinationFolder)
    {
        if (!TryConfirmInteractionAction("コピーする", sourcePaths.Select(path => Path.GetFileName(path) ?? path).ToList(), out string? interactionMessage))
        {
            return;
        }

        int copiedCount = 0;
        string? lastName = null;
        int total = sourcePaths.Count;
        string modeLabel = _currentMode.ToString();
        bool alertShown = false;

        if (!TryValidateBatchOperation(
                sourcePaths,
                "コピー",
                sourcePath => ExplorerActionService.ValidateCopyTarget(new CopyItemRequest
                {
                    ModeLabel = modeLabel,
                    SourcePath = sourcePath,
                    DestinationFolder = destinationFolder,
                    IsHoldFolderPath = IsHoldFolderPath,
                    IsUnderFolder = IsUnderFolder,
                    GetShareStatus = GetItemShareStatus,
                    BeforeWrite = SuppressExternalChangeNotifications,
                })))
        {
            return;
        }

        var progress = await CreateFileProgressAsync("コピー");
        BeginDeferredPermissionSave();
        _pendingInteractionMessage = interactionMessage;
        try
        {
            for (int i = 0; i < total; i++)
            {
                string sourcePath = sourcePaths[i];
                string fileName = Path.GetFileName(sourcePath);
                progress.Report((i, total, fileName));
                await Task.Yield();

                ExplorerActionResult result = await Task.Run(() => ExplorerActionService.CopyItem(new CopyItemRequest
                {
                    ModeLabel = modeLabel,
                    SourcePath = sourcePath,
                    DestinationFolder = destinationFolder,
                    IsHoldFolderPath = IsHoldFolderPath,
                    IsUnderFolder = IsUnderFolder,
                    GetShareStatus = GetItemShareStatus,
                    BeforeWrite = SuppressExternalChangeNotifications,
                }));

                if (result.State == ExplorerActionState.NoChange)
                {
                    progress.Report((i + 1, total, fileName));
                    continue;
                }

                bool shouldShowAlert = !alertShown;
                ApplyExplorerActionResult(result, showAlertDialog: shouldShowAlert);
                if (shouldShowAlert &&
                    !string.IsNullOrWhiteSpace(result.UserMessage) &&
                    (result.State == ExplorerActionState.Blocked || result.State == ExplorerActionState.Failure))
                {
                    alertShown = true;
                }
                if (!result.ShouldRefreshUi ||
                    string.IsNullOrWhiteSpace(result.SourcePath) ||
                    string.IsNullOrWhiteSpace(result.SourceParent) ||
                    string.IsNullOrWhiteSpace(result.DestinationPath) ||
                    string.IsNullOrWhiteSpace(result.DestinationFolder))
                {
                    progress.Report((i + 1, total, fileName));
                    continue;
                }

                bool preserved = PreservePermissionOnArrival(
                    result.SourcePath,
                    result.SourceParent,
                    result.DestinationPath,
                    result.DestinationFolder,
                    removeSourceEntry: false,
                    historySource: "MainWindow.copy");
                if (!preserved)
                {
                    NoteFutureSharePolicyRepair(result.DestinationPath, result.DestinationFolder, SharePolicyRepairReason.Placed);
                    MaybeAppendPermissionInheritedOnArrival(result.DestinationPath, result.DestinationFolder, "MainWindow.copy");
                }

                copiedCount++;
                lastName = result.TargetName;
                progress.Report((i + 1, total, fileName));
            }
        }
        finally
        {
            _pendingInteractionMessage = null;
            EndDeferredPermissionSave();
        }

        if (copiedCount > 0)
        {
            string statusMessage = copiedCount == 1 && lastName is not null
                ? $"{lastName} をコピーしました。"
                : $"{copiedCount} つコピーしました。";
            SetTransientStatus(statusMessage);
            InvalidateSizeCacheUnder(destinationFolder);
            if (string.Equals(
                    Path.GetFullPath(destinationFolder),
                    Path.GetFullPath(_currentFolder ?? string.Empty),
                    StringComparison.OrdinalIgnoreCase) &&
                lastName is not null)
            {
                _pendingFocusName = lastName;
            }

            RefreshShopItems();
        }
    }

    private void ShopItemsContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        _suppressInternalDragUntilLeftButtonRelease = true;
        _clickSelectionPending = false;
        _itemWasSelectedAtPress = false;
        _dragStartItem = null;
        _shopItemsContextMenuCommandArmed = false;
        EndRubberBand();

        ShopItem? selected = ShopItemsListView.SelectedItem as ShopItem;
        int selectedCount = ShopItemsListView.SelectedItems.Count;
        bool inHoldMode = _currentMode == DisplayMode.Hold;
        bool canAddFolder = !string.IsNullOrWhiteSpace(_currentFolder);

        if (selected is null)
        {
            MoveShopItemMenuItem.Visibility = Visibility.Collapsed;
            MoveToFolderMenuItem.Visibility = Visibility.Collapsed;
            HoldShopItemMenuItem.Visibility = Visibility.Collapsed;
            DeleteShopItemMenuItem.Visibility = Visibility.Collapsed;
            AddFolderSeparator.Visibility = Visibility.Collapsed;
            AddFolderMenuItem.Visibility = canAddFolder ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (selectedCount > 1)
        {
            MoveShopItemMenuItem.Visibility = Visibility.Collapsed;
            MoveToFolderMenuItem.Visibility = inHoldMode ? Visibility.Collapsed : Visibility.Visible;
            HoldShopItemMenuItem.Visibility = inHoldMode ? Visibility.Collapsed : Visibility.Visible;
            DeleteShopItemMenuItem.Visibility = Visibility.Visible;
            AddFolderSeparator.Visibility = Visibility.Collapsed;
            AddFolderMenuItem.Visibility = Visibility.Collapsed;
        }
        else if (selected.IsDirectory)
        {
            MoveShopItemMenuItem.Visibility = selected.IsHoldFolder ? Visibility.Collapsed : Visibility.Visible;
            MoveToFolderMenuItem.Visibility = selected.IsHoldFolder ? Visibility.Collapsed : Visibility.Visible;
            HoldShopItemMenuItem.Visibility = inHoldMode || selected.IsHoldFolder ? Visibility.Collapsed : Visibility.Visible;
            DeleteShopItemMenuItem.Visibility = selected.IsHoldFolder ? Visibility.Collapsed : Visibility.Visible;
            AddFolderSeparator.Visibility = canAddFolder ? Visibility.Visible : Visibility.Collapsed;
            AddFolderMenuItem.Visibility = canAddFolder ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            MoveShopItemMenuItem.Visibility = Visibility.Visible;
            MoveToFolderMenuItem.Visibility = Visibility.Visible;
            HoldShopItemMenuItem.Visibility = inHoldMode ? Visibility.Collapsed : Visibility.Visible;
            DeleteShopItemMenuItem.Visibility = Visibility.Visible;
            AddFolderSeparator.Visibility = canAddFolder ? Visibility.Visible : Visibility.Collapsed;
            AddFolderMenuItem.Visibility = canAddFolder ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ShopItemsContextMenu_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _shopItemsContextMenuCommandArmed = FindAncestor<MenuItem>(e.OriginalSource as DependencyObject) is not null;
    }

    private void RenameShopItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!ShouldProcessShopItemsContextMenuCommand(sender))
        {
            return;
        }

        if (ShopItemsListView.SelectedItem is not ShopItem item)
        {
            return;
        }

        string? sourceParent = Path.GetDirectoryName(item.FullPath);
        if (string.IsNullOrWhiteSpace(sourceParent))
        {
            return;
        }

        NameInputDialog dialog = new("新しい名前", item.Name)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string newName = dialog.EnteredName.Trim();
        if (string.IsNullOrEmpty(newName))
        {
            return;
        }

        if (!TryConfirmInteractionAction("名前を変える", [newName], out string? interactionMessage))
        {
            return;
        }

        _pendingInteractionMessage = interactionMessage;
        try
        {
            ExplorerActionResult result = ExplorerActionService.RenameItem(new RenameItemRequest
            {
                ModeLabel = _currentMode.ToString(),
                SourcePath = item.FullPath,
                NewName = newName,
                IsDirectory = item.IsDirectory,
                GetShareStatus = GetItemShareStatus,
                BeforeWrite = SuppressExternalChangeNotifications,
            });

            if (result.State == ExplorerActionState.NoChange)
            {
                return;
            }

            string originalPath = item.FullPath;
            ApplyExplorerActionResult(result);
            if (!result.ShouldRefreshUi ||
                string.IsNullOrWhiteSpace(result.DestinationPath) ||
                string.IsNullOrWhiteSpace(result.SourceParent))
            {
                return;
            }

            MovePermissionEntries(originalPath, result.DestinationPath);
            SavePermissionMap();
            NoteFutureSharePolicyRepair(result.DestinationPath, result.SourceParent, SharePolicyRepairReason.Renamed);
            InvalidateSizeCacheUnder(result.SourceParent);
            _pendingFocusName = newName;
            RefreshShopItems();
            ScheduleRefreshShopItemsIfCurrentFolder(result.SourceParent, TimeSpan.FromMilliseconds(300));
        }
        finally
        {
            _pendingInteractionMessage = null;
        }
    }

    private async void MoveToFolderMenuItem_Click(object sender, RoutedEventArgs e) =>
        await MoveSelectedItemsToFolderAsync(sender);

    private async Task MoveSelectedItemsToFolderAsync(object? sender = null)
    {
        if (!ShouldProcessShopItemsContextMenuCommand(sender))
        {
            return;
        }

        List<ShopItem> items = ShopItemsListView.SelectedItems.Cast<ShopItem>()
            .Where(i => !i.IsHoldFolder)
            .ToList();
        if (items.Count == 0) return;

        List<string> sourcePaths = items.Select(static item => item.FullPath).ToList();

        MoveDestinationDialog dialog = new(_shopFolder, sourcePaths)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true) return;

        string? destinationFolder = dialog.SelectedFolderPath;
        if (string.IsNullOrWhiteSpace(destinationFolder) || !Directory.Exists(destinationFolder)) return;

        if (!TryConfirmInteractionAction("移す", sourcePaths.Select(path => Path.GetFileName(path) ?? path).ToList(), out string? interactionMessage))
        {
            return;
        }

        BeginProcessing();
        try
        {
            var progress = await CreateFileProgressAsync("移動");
            _pendingInteractionMessage = interactionMessage;
            try
            {
                (int movedCount, string? lastName) = await ExecuteMoveExplorerActionsAsync(
                    sourcePaths,
                    destinationFolder,
                    progress);

                if (movedCount > 0)
                {
                    string statusMessage = movedCount == 1 && lastName is not null
                        ? $"{lastName} を移しました。"
                        : $"{movedCount} つ移しました。";
                    SetTransientStatus(statusMessage);
                    RefreshShopItems();
                }
            }
            finally
            {
                _pendingInteractionMessage = null;
            }
        }
        finally
        {
            EndProcessing();
        }
    }

    private async void HoldShopItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!ShouldProcessShopItemsContextMenuCommand(sender))
        {
            return;
        }

        List<ShopItem> items = ShopItemsListView.SelectedItems.Cast<ShopItem>()
            .Where(i => !i.IsHoldFolder)
            .ToList();
        if (items.Count == 0) return;

        BeginProcessing();
        try
        {
            await HoldInternalDraggedItemsAsync(items.Select(static item => item.FullPath).ToList());
        }
        finally
        {
            EndProcessing();
        }
    }

    private async Task HoldInternalDraggedItemsAsync(IReadOnlyList<string> sourcePaths)
    {
        if (sourcePaths.Count == 0)
        {
            return;
        }

        if (!TryEnsureHoldFolder())
        {
            SetTransientStatus("保留を準備できません。");
            return;
        }

        string holdFolderPath = GetHoldFolderPath();
        int movedCount = 0;
        string? lastName = null;
        var progress = await CreateFileProgressAsync("保留");
        int total = sourcePaths.Count;
        string modeLabel = _currentMode.ToString();

        if (!TryValidateBatchOperation(
                sourcePaths,
                "保留",
                sourcePath => ExplorerActionService.ValidateHoldTarget(new HoldItemRequest
                {
                    ModeLabel = modeLabel,
                    SourcePath = sourcePath,
                    HoldFolderPath = holdFolderPath,
                    GetShareStatus = GetItemShareStatus,
                    BeforeWrite = SuppressExternalChangeNotifications,
                })))
        {
            return;
        }

        for (int i = 0; i < total; i++)
        {
            string sourcePath = sourcePaths[i];
            ShopItem? item = ShopItems.FirstOrDefault(it => string.Equals(it.FullPath, sourcePath, StringComparison.OrdinalIgnoreCase));
            if (item is null || item.IsHoldFolder)
            {
                continue;
            }

            string fileName = item.Name;
            progress.Report((i, total, fileName));
            await Task.Yield();

            var displayPermBeforeHold = (
                item.AllowedUsers
                    .Where(user => !string.IsNullOrWhiteSpace(user))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                item.IsReadOnly,
                item.IsSharedOff);

            string itemPath = item.FullPath;
            ExplorerActionResult result = await Task.Run(() => ExplorerActionService.HoldItem(new HoldItemRequest
            {
                ModeLabel = modeLabel,
                SourcePath = itemPath,
                HoldFolderPath = holdFolderPath,
                GetShareStatus = GetItemShareStatus,
                BeforeWrite = SuppressExternalChangeNotifications,
            }));

            ApplyExplorerActionResult(result, showAlertDialog: false);

            if (result.ShouldRefreshUi && !string.IsNullOrWhiteSpace(result.SourceParent))
            {
                if (!string.IsNullOrWhiteSpace(result.SourcePath) &&
                    !string.IsNullOrWhiteSpace(result.DestinationPath))
                {
                    _permissionMap.Remove(result.SourcePath);
                    _permissionMap[result.DestinationPath] = ([], false, true);
                    _holdDisplayPermissionMap[result.DestinationPath] = displayPermBeforeHold;
                    SavePermissionMap();
                    _ = Task.Run(() => _pipeClient.SetSubfolderPermission(result.DestinationPath, true, false));
                }

                InvalidateSizeCacheUnder(result.SourceParent);
                movedCount++;
                lastName = result.TargetName;
            }

            progress.Report((i + 1, total, fileName));
        }

        if (movedCount > 0)
        {
            InvalidateSizeCacheUnder(holdFolderPath);
            SetTransientStatus(movedCount == 1 && lastName is not null
                ? $"{lastName} を保留にしまいました。"
                : $"{movedCount} つ保留にしまいました。");
            RefreshShopItems();
        }
    }

    private async void DeleteShopItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!ShouldProcessShopItemsContextMenuCommand(sender))
        {
            return;
        }

        List<ShopItem> items = ShopItemsListView.SelectedItems.Cast<ShopItem>()
            .Where(i => !i.IsHoldFolder)
            .ToList();
        if (items.Count == 0) return;

        string? interactionMessage = null;
        if (_currentMode == DisplayMode.FriendShop)
        {
            if (!TryConfirmInteractionAction("削除する", items.Select(item => item.Name).ToList(), out interactionMessage))
            {
                return;
            }
        }
        else
        {
            string confirmMsg = items.Count == 1
                ? $"{items[0].Name} を完全に消します。よろしいですか?"
                : $"{items.Count} つのアイテムを完全に消します。よろしいですか?";
            MessageBoxResult confirmResult = System.Windows.MessageBox.Show(
                this, confirmMsg, "削除", MessageBoxButton.OKCancel, MessageBoxImage.None);
            if (confirmResult != MessageBoxResult.OK) return;
        }

        BeginProcessing();
        try
        {
            var progress = await CreateFileProgressAsync("削除");
            int deletedCount = 0;
            string? lastName = null;
            int total = items.Count;
            string modeLabel = _currentMode.ToString();
            _pendingInteractionMessage = interactionMessage;

            for (int i = 0; i < total; i++)
            {
                ShopItem item = items[i];
                string itemPath = item.FullPath;
                bool isDirectory = item.IsDirectory;
                string fileName = item.Name;
                progress.Report((i, total, fileName));
                await Task.Yield();

                ExplorerActionResult result = await Task.Run(() => ExplorerActionService.DeleteItem(new DeleteItemRequest
                {
                    ModeLabel = modeLabel,
                    ItemPath = itemPath,
                    IsDirectory = isDirectory,
                    GetShareStatus = GetItemShareStatus,
                    BeforeWrite = SuppressExternalChangeNotifications,
                }));

                ApplyExplorerActionResult(result, showAlertDialog: false);

                if (result.ShouldRefreshUi && !string.IsNullOrWhiteSpace(result.SourceParent))
                {
                    InvalidateSizeCacheUnder(result.SourceParent);
                    deletedCount++;
                    lastName = result.TargetName;
                }

                progress.Report((i + 1, total, fileName));
            }

            if (deletedCount > 0)
            {
                string statusMessage = deletedCount == 1 && lastName is not null
                    ? $"{lastName} を消しました。"
                    : $"{deletedCount} つ消しました。";
                SetTransientStatus(statusMessage);
                RefreshShopItems();
            }
        }
        finally
        {
            _pendingInteractionMessage = null;
            EndProcessing();
        }
    }

    private void AddFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!ShouldProcessShopItemsContextMenuCommand(sender))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentFolder))
        {
            return;
        }

        ShopItem? selected = ShopItemsListView.SelectedItem as ShopItem;
        string targetFolder = selected is { IsDirectory: true } folderItem
            ? folderItem.FullPath
            : _currentFolder;

        NameInputDialog dialog = new("新しいフォルダーの名前", string.Empty)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string folderName = dialog.EnteredName.Trim();
        if (string.IsNullOrEmpty(folderName))
        {
            return;
        }

        if (!TryConfirmInteractionAction("フォルダーを作る", [folderName], out string? interactionMessage))
        {
            return;
        }

        _pendingInteractionMessage = interactionMessage;
        try
        {
            ExplorerActionResult result = ExplorerActionService.CreateFolder(new CreateFolderRequest
            {
                ModeLabel = _currentMode.ToString(),
                ParentFolder = targetFolder,
                FolderName = folderName,
                GetShareStatus = GetItemShareStatus,
                BeforeWrite = SuppressExternalChangeNotifications,
            });

            if (result.State == ExplorerActionState.NoChange)
            {
                return;
            }

            ApplyExplorerActionResult(result);

            if (!result.ShouldRefreshUi ||
                string.IsNullOrWhiteSpace(result.DestinationPath) ||
                string.IsNullOrWhiteSpace(result.DestinationFolder))
            {
                return;
            }

            NoteFutureSharePolicyRepair(result.DestinationPath, result.DestinationFolder, SharePolicyRepairReason.FolderCreated);
            InvalidateSizeCacheUnder(result.DestinationFolder);
            if (string.Equals(
                    Path.GetFullPath(result.DestinationFolder),
                    Path.GetFullPath(_currentFolder),
                    StringComparison.OrdinalIgnoreCase))
            {
                _pendingFocusName = result.TargetName;
            }
            RefreshShopItems();
        }
        finally
        {
            _pendingInteractionMessage = null;
        }
    }

    // --- 選択アクションバー ---

    private void ShopItemsListView_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateSelectionActionBar();
    }

    private void UpdateSelectionActionBar()
    {
        int count = ShopItemsListView.SelectedItems.Count;

        if (count == 0)
        {
            SelectionActionBar.Visibility = Visibility.Collapsed;
            return;
        }

        SelectionActionBar.Visibility = Visibility.Visible;
        SelectionCountText.Text = $"{count}個選択中";

        bool inHoldMode = _currentMode == DisplayMode.Hold;
        bool singleSelect = count == 1;
        bool hasHoldFolder = ShopItemsListView.SelectedItems.Cast<ShopItem>().Any(i => i.IsHoldFolder);

        ActionBarRenameButton.Visibility =
            singleSelect && !hasHoldFolder && !inHoldMode
            ? Visibility.Visible : Visibility.Collapsed;

        ActionBarMoveButton.Visibility =
            !hasHoldFolder && !inHoldMode
            ? Visibility.Visible : Visibility.Collapsed;

        ActionBarHoldButton.Visibility =
            !hasHoldFolder && !inHoldMode
            ? Visibility.Visible : Visibility.Collapsed;

        ActionBarPlaceButton.Visibility =
            inHoldMode
            ? Visibility.Visible : Visibility.Collapsed;

        ActionBarDeleteButton.Visibility =
            !hasHoldFolder
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
    {
        ShopItemsListView.SelectedItems.Clear();
    }

    private void ActionBarRenameButton_Click(object sender, RoutedEventArgs e) =>
        RenameShopItemMenuItem_Click(sender, e);

    private async void ActionBarMoveButton_Click(object sender, RoutedEventArgs e) =>
        await MoveSelectedItemsToFolderAsync(sender);

    private void ActionBarHoldButton_Click(object sender, RoutedEventArgs e) =>
        HoldShopItemMenuItem_Click(sender, e);

    private void ActionBarDeleteButton_Click(object sender, RoutedEventArgs e) =>
        DeleteShopItemMenuItem_Click(sender, e);

    private async void ActionBarPlaceButton_Click(object sender, RoutedEventArgs e) =>
        await PlaceBackSelectedItemsAsync();

    private async Task PlaceBackSelectedItemsAsync()
    {
        if (string.IsNullOrWhiteSpace(_shopFolder)) return;

        List<ShopItem> items = ShopItemsListView.SelectedItems.Cast<ShopItem>().ToList();
        if (items.Count == 0) return;

        BeginProcessing();
        int movedCount;
        string? lastName;
        try
        {
            var progress = await CreateFileProgressAsync("戻し");
            (movedCount, lastName) = await ExecuteMoveExplorerActionsAsync(
                items.Select(static item => item.FullPath).ToList(),
                _shopFolder!,
                progress);
        }
        finally
        {
            EndProcessing();
        }

        if (movedCount > 0)
        {
            string statusMessage = movedCount == 1 && lastName is not null
                ? $"{lastName} をお店に戻しました。"
                : $"{movedCount} つお店に戻しました。";
            SetTransientStatus(statusMessage);
            RefreshShopItems();
        }
    }

    private async Task<(int movedCount, string? lastName)> ExecuteMoveExplorerActionsAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationFolder,
        IProgress<(int current, int total, string name)>? progress = null)
    {
        int movedCount = 0;
        string? lastName = null;
        int total = sourcePaths.Count;
        string modeLabel = _currentMode.ToString();
        bool alertShown = false;

        if (!TryValidateBatchOperation(
                sourcePaths,
                "移動",
                sourcePath => ExplorerActionService.ValidateMoveTarget(new MoveItemRequest
                {
                    ModeLabel = modeLabel,
                    SourcePath = sourcePath,
                    DestinationFolder = destinationFolder,
                    IsHoldFolderPath = IsHoldFolderPath,
                    IsUnderFolder = IsUnderFolder,
                    GetShareStatus = GetItemShareStatus,
                    BeforeWrite = SuppressExternalChangeNotifications,
                })))
        {
            return (0, null);
        }

        BeginDeferredPermissionSave();
        try
        {
            for (int i = 0; i < total; i++)
            {
                string sourcePath = sourcePaths[i];
                string fileName = Path.GetFileName(sourcePath);
                progress?.Report((i, total, fileName));
                await Task.Yield();

                ExplorerActionResult result = await Task.Run(() => ExplorerActionService.MoveItem(new MoveItemRequest
                {
                    ModeLabel = modeLabel,
                    SourcePath = sourcePath,
                    DestinationFolder = destinationFolder,
                    IsHoldFolderPath = IsHoldFolderPath,
                    IsUnderFolder = IsUnderFolder,
                    GetShareStatus = GetItemShareStatus,
                    BeforeWrite = SuppressExternalChangeNotifications,
                }));

                if (result.State == ExplorerActionState.NoChange)
                {
                    progress?.Report((i + 1, total, fileName));
                    continue;
                }

                bool shouldShowAlert = !alertShown;
                ApplyExplorerActionResult(result, showAlertDialog: shouldShowAlert);
                if (shouldShowAlert &&
                    !string.IsNullOrWhiteSpace(result.UserMessage) &&
                    (result.State == ExplorerActionState.Blocked || result.State == ExplorerActionState.Failure))
                {
                    alertShown = true;
                }

                if (!result.ShouldRefreshUi ||
                    string.IsNullOrWhiteSpace(result.SourcePath) ||
                    string.IsNullOrWhiteSpace(result.SourceParent) ||
                    string.IsNullOrWhiteSpace(result.DestinationPath) ||
                    string.IsNullOrWhiteSpace(result.DestinationFolder))
                {
                    progress?.Report((i + 1, total, fileName));
                    continue;
                }

                bool preserved = PreservePermissionOnArrival(
                    result.SourcePath,
                    result.SourceParent,
                    result.DestinationPath,
                    result.DestinationFolder,
                    removeSourceEntry: true,
                    historySource: "MainWindow.move");
                if (!preserved)
                {
                    NoteFutureSharePolicyRepair(result.DestinationPath, result.DestinationFolder, SharePolicyRepairReason.Moved);
                }
                InvalidateSizeCacheUnder(result.SourceParent);
                movedCount++;
                lastName = result.TargetName;
                progress?.Report((i + 1, total, fileName));
            }
        }
        finally
        {
            EndDeferredPermissionSave();
        }

        if (movedCount > 0)
        {
            InvalidateSizeCacheUnder(destinationFolder);
        }

        return (movedCount, lastName);
    }

    private void BeginDeferredPermissionSave()
    {
        _deferredPermissionSaveDepth++;
    }

    private void EndDeferredPermissionSave()
    {
        if (_deferredPermissionSaveDepth <= 0)
        {
            _deferredPermissionSaveDepth = 0;
            return;
        }

        _deferredPermissionSaveDepth--;
        if (_deferredPermissionSaveDepth == 0 && _permissionSavePending)
        {
            _permissionSavePending = false;
            SavePermissionMapCore();
        }
    }

    // --- インラインリネーム ---

    private void StartRenameTimer(ShopItem item)
    {
        CancelRenameTimer();
        _renameTimerTarget = item;
        _renameTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _renameTimer.Tick += (_, _) =>
        {
            ShopItem? target = _renameTimerTarget;
            CancelRenameTimer();
            BeginInlineRename(target);
        };
        _renameTimer.Start();
    }

    private void CancelRenameTimer()
    {
        _renameTimer?.Stop();
        _renameTimer = null;
        _renameTimerTarget = null;
    }

    private void BeginInlineRename(ShopItem? item)
    {
        if (item is null || item.IsHoldFolder || item.IsRenaming) return;
        if (ShopItemsListView.SelectedItem != item) return;

        CommitCurrentInlineRename(confirm: false);
        item.IsRenaming = true;
    }

    private void CommitCurrentInlineRename(bool confirm, System.Windows.Controls.TextBox? textBox = null)
    {
        ShopItem? item = ShopItems.FirstOrDefault(i => i.IsRenaming);
        if (item is null) return;

        string newName = textBox?.Text?.Trim() ?? string.Empty;
        item.IsRenaming = false;

        if (!confirm || string.IsNullOrEmpty(newName)) return;

        ExplorerActionResult result = ExplorerActionService.RenameItem(new RenameItemRequest
        {
            ModeLabel = _currentMode.ToString(),
            SourcePath = item.FullPath,
            NewName = newName,
            IsDirectory = item.IsDirectory,
            GetShareStatus = GetItemShareStatus,
            BeforeWrite = SuppressExternalChangeNotifications,
        });

        if (result.State == ExplorerActionState.NoChange)
        {
            return;
        }

        string originalPath = item.FullPath;
        ApplyExplorerActionResult(result);
        if (!result.ShouldRefreshUi ||
            string.IsNullOrWhiteSpace(result.DestinationPath) ||
            string.IsNullOrWhiteSpace(result.SourceParent))
        {
            return;
        }

        MovePermissionEntries(originalPath, result.DestinationPath);
        SavePermissionMap();
        NoteFutureSharePolicyRepair(result.DestinationPath, result.SourceParent, SharePolicyRepairReason.Renamed);
        InvalidateSizeCacheUnder(result.SourceParent);
        _pendingFocusName = newName;
        RefreshShopItems();
        ScheduleRefreshShopItemsIfCurrentFolder(result.SourceParent, TimeSpan.FromMilliseconds(300));
    }

    private void InlineRenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue && sender is System.Windows.Controls.TextBox tb)
        {
            Dispatcher.InvokeAsync(() =>
            {
                tb.SelectAll();
                tb.Focus();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void InlineRenameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;

        if (e.Key == System.Windows.Input.Key.Return)
        {
            CommitCurrentInlineRename(confirm: true, tb);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            CommitCurrentInlineRename(confirm: false);
            e.Handled = true;
        }
    }

    private void InlineRenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitCurrentInlineRename(confirm: true, sender as System.Windows.Controls.TextBox);
    }

    private void EnterHoldMode()
    {
        NavigateToHoldRoot(addHistory: true);
    }

    private void NavigateToHoldRoot(bool addHistory)
    {
        if (!TryEnsureHoldFolder())
        {
            SetTransientStatus("保留を準備できません。");
            return;
        }
        CancelFolderSizeCalculation();
        _currentMode = DisplayMode.Hold;
        _activeFriendShop = null;
        _activeFriendShopRootPath = null;
        _missingFriendShopStatus = null;
        NavigateTo(GetHoldFolderPath(), addHistory: addHistory, clearForward: true);
    }

    private void EnterShopMode()
    {
        NavigateToShopRoot(addHistory: true);
    }

    private void NavigateToShopRoot(bool addHistory)
    {
        CancelFolderSizeCalculation();
        _currentMode = DisplayMode.Shop;
        _activeFriendShop = null;
        _activeFriendShopRootPath = null;
        _missingFriendShopStatus = null;

        if (_isShopOpen && !string.IsNullOrWhiteSpace(_shopFolder) && Directory.Exists(_shopFolder))
        {
            NavigateTo(_shopFolder, addHistory: addHistory, clearForward: true);
        }
        else
        {
            DisposeContentsWatcher();
            _currentFolder = null;
            ShopItems.Clear();
            UpdateBreadcrumb();
            UpdateNavigationState();
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitRequested && _pipeClient.IsConnected)
            _pipeClient.SendExitApp();
        _uiUnlocked = false;
        CancelFolderSizeCalculation();
        CloseShop(removeSmbShare: false);
        _notificationTimer.Stop();
        _notificationTimer.Tick -= NotificationTimer_Tick;
        _pollingTimer.Stop();
        _pollingTimer.Tick -= PollingTimer_Tick;
        StopFriendShopPolling();
        _transientStatusTimer.Stop();
        _transientStatusTimer.Tick -= TransientStatusTimer_Tick;
        _pipeClient.Dispose();
    }

    private void ShowMainWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private void ShowMainWindowWithPassword()
    {
        if (!EnsureUiUnlocked())
        {
            return;
        }

        ShowMainWindow();
    }

    private void ExitApp()
    {
        _exitRequested = true;
        Close();
    }

    // RestoreOpenShopIfNeeded は TrayApp に移動。HandleStartup から呼ばない。

    private bool EnsureUiUnlocked()
    {
        if (_uiUnlocked)
        {
            return true;
        }

        bool setup = !EntryPasswordManager.IsConfigured;
        string? status = null;
        while (true)
        {
            EntryPasswordDialog dialog = new(setup, status);
            if (IsVisible)
            {
                dialog.Owner = this;
            }
            bool? result = dialog.ShowDialog();
            if (result != true)
            {
                return false;
            }

            string password = dialog.EnteredPassword;
            if (setup)
            {
                if (EntryPasswordManager.SetPassword(password))
                {
                    _uiUnlocked = true;
                    return true;
                }

                status = "パスワードを入力してください。";
                setup = true;
                continue;
            }

            if (EntryPasswordManager.Verify(password))
            {
                _uiUnlocked = true;
                return true;
            }

            status = "パスワードが違います。";
        }
    }

    private void OpenShop()
    {
        if (string.IsNullOrWhiteSpace(_shopFolder))
        {
            UpdateShopState(false, "共有する場所を選んでください。");
            return;
        }

        if (!Directory.Exists(_shopFolder))
        {
            UpdateShopState(false, "その場所が見つかりません。");
            return;
        }

        string shareName = DeriveShareName(_shopFolder);
        if (!ValidateShareName(shareName, out string nameError))
        {
            UpdateShopState(false, nameError);
            return;
        }

        ShopOpenOutcome? outcome;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            outcome = _pipeClient.OpenShop(_shopFolder, shareName, shareName, (int)_shareAccessRight, false);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (outcome == null)
        {
            UpdateShopState(false, "トレイとの通信に失敗しました。");
            return;
        }

        // 共有開始時に公開対象を利用者オーナー基準へ揃える。
        if (outcome.NeedsOwnership)
        {
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                outcome = _pipeClient.OpenShop(_shopFolder, shareName, shareName, (int)_shareAccessRight, true);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            if (outcome == null)
            {
                UpdateShopState(false, "トレイとの通信に失敗しました。");
                return;
            }
        }

        if (!outcome.Ok)
        {
            string message = string.IsNullOrWhiteSpace(outcome.Error)
                ? "お店を開けませんでした。"
                : outcome.Error!;
            if (outcome.BlockedPaths is { Count: > 0 } blocked)
            {
                int show = Math.Min(blocked.Count, 5);
                string list = string.Join("\n", blocked.Take(show).Select(p => "・" + p));
                if (blocked.Count > show)
                {
                    list += $"\nほか {blocked.Count - show} 件";
                }
                message += "\n\n対象:\n" + list;
            }
            UpdateShopState(false, message);
            return;
        }

        // 共有開始で所有権/ACL の回復が済んだあとに保留を作る。
        // 先に保留を触ると、別 OS 由来の所有者ずれで開店前に失敗してしまう。
        if (!TryEnsureHoldFolder())
        {
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                _pipeClient.CloseShop();
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            UpdateShopState(false, "保留を準備できません。");
            return;
        }

        ReinitializeShopChangeMonitoring();

        _isShopOpen = true;
        _wasOpenAtLastShutdown = true;
        SaveSettings();

        ReapplyPermissionMapToNtfs(_shopFolder, runInBackground: false);

        UpdateShopState(true);
        if (_currentMode == DisplayMode.Shop)
        {
            NavigateTo(_shopFolder, addHistory: false, clearForward: true);
        }
    }

    private void ReapplyPermissionMapToNtfs(string shopRoot, bool runInBackground = true)
    {
        if (string.IsNullOrEmpty(shopRoot)) return;
        string root = shopRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // UI スレッドでスナップショットを取ってから Task.Run へ渡す
        var snapshot = _permissionMap
            .Where(kv => kv.Key.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        void Apply()
        {
            if (!Directory.Exists(root)) return;
            // permissionMap に記録済みのフォルダは保存状態を適用
            foreach (var (path, (_, isReadOnly, isSharedOff)) in snapshot)
            {
                if (!Directory.Exists(path)) continue;
                _pipeClient.SetSubfolderPermission(path, isSharedOff, isReadOnly);
            }
            // permissionMap に未登録のトップレベルフォルダは継承リセット
            // （旧セッションで全員R が設定され permissionMap に残らなかった場合の修復）
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                if (snapshot.ContainsKey(dir)) continue;
                if (string.Equals(Path.GetFileName(dir), HoldFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                _pipeClient.ResetPathToInherited(dir);
            }
            if (Dispatcher.CheckAccess())
                PublishPermissionManifest();
            else
                Dispatcher.Invoke(PublishPermissionManifest);
        }

        if (runInBackground)
        {
            _ = Task.Run(Apply);
        }
        else
        {
            Apply();
        }
    }

    private void CloseShop(bool removeSmbShare = true)
    {
        DisposeWatcher();
        _pollingTimer.Stop();
        CancelDeferredRefresh();
        bool wasOpen = _isShopOpen;
        _isShopOpen = false;
        _isPollingMode = false;
        CancelFolderSizeCalculation();
        CancelSubfolderCountLoad();

        if (wasOpen && removeSmbShare)
        {
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                _pipeClient.CloseShop();
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        if (_currentMode == DisplayMode.Shop || _currentMode == DisplayMode.Hold)
        {
            DisposeContentsWatcher();
            _currentFolder = null;
            _activeFriendShopRootPath = null;
            _backStack.Clear();
            _forwardStack.Clear();
            ShopItems.Clear();
            UpdateBreadcrumb();
            UpdateNavigationState();
        }

        _wasOpenAtLastShutdown = !removeSmbShare && wasOpen;
        SaveSettings();

        UpdateShopState(false);
    }

    private void DisposeWatcher()
    {
        if (_arrivalSensor is null)
        {
            return;
        }

        _arrivalSensor.EnableRaisingEvents = false;
        _arrivalSensor.Created -= ArrivalSensor_Created;
        _arrivalSensor.Deleted -= ArrivalSensor_Created;
        _arrivalSensor.Renamed -= ArrivalSensor_Renamed;
        _arrivalSensor.Error -= ArrivalSensor_Error;
        _arrivalSensor.Dispose();
        _arrivalSensor = null;
    }

    private void ReinitializeShopChangeMonitoring()
    {
        if (string.IsNullOrWhiteSpace(_shopFolder) || !Directory.Exists(_shopFolder))
        {
            return;
        }

        DisposeWatcher();
        _pollingTimer.Stop();

        try
        {
            _arrivalSensor = new FileSystemWatcher(_shopFolder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };
            _arrivalSensor.Created += ArrivalSensor_Created;
            _arrivalSensor.Deleted += ArrivalSensor_Created;
            _arrivalSensor.Renamed += ArrivalSensor_Renamed;
            _arrivalSensor.Error += ArrivalSensor_Error;
            _isPollingMode = false;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            DisposeWatcher();
            BeginPolling();
        }
    }

    private void DisposeContentsWatcher()
    {
        FileSystemWatcher? watcher = _contentsSensor;
        if (watcher is null)
        {
            return;
        }
        _contentsSensor = null;

        watcher.Created -= ContentsSensor_Changed;
        watcher.Deleted -= ContentsSensor_Changed;
        watcher.Changed -= ContentsSensor_Changed;
        watcher.Renamed -= ContentsSensor_Renamed;
        watcher.Error -= ContentsSensor_Error;

        // 死んだ UNC では EnableRaisingEvents=false / Dispose() が SMB タイムアウト待ちで
        // 数秒〜十数秒 UI スレッドをブロックする。後始末だけ別スレッドに逃がす。
        Task.Run(() =>
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                SwkLogger.Debug($"DisposeContentsWatcher background: {ex.Message}");
            }
        });
    }

    private void BeginPolling()
    {
        if (string.IsNullOrWhiteSpace(_shopFolder) || !Directory.Exists(_shopFolder))
        {
            UpdateShopState(false, "その場所が見つかりません。");
            return;
        }

        _knownFiles.Clear();
        try
        {
            foreach (string path in EnumerateShopSnapshot(_shopFolder))
            {
                _knownFiles.Add(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UpdateShopState(false, "その場所を開けませんでした。");
            return;
        }

        _pollingTimer.Stop();
        _pollingTimer.Start();
        _isPollingMode = true;
        _isShopOpen = true;
        UpdateShopState(true);
        if (_currentMode == DisplayMode.Shop)
        {
            NavigateTo(_shopFolder, addHistory: false, clearForward: true);
        }
    }

    private void PollingTimer_Tick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_shopFolder) || !Directory.Exists(_shopFolder))
        {
            return;
        }

        IEnumerable<string> snapshot;
        try
        {
            snapshot = EnumerateShopSnapshot(_shopFolder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var current = new HashSet<string>(snapshot, StringComparer.OrdinalIgnoreCase);
        var newcomers = current.Except(_knownFiles).ToList();
        var removed = _knownFiles.Except(current).ToList();

        foreach (string path in newcomers)
        {
            TryRegisterExternalReceive(path, "Polling");
        }

        if (newcomers.Count > 0 || removed.Count > 0)
        {
            NotifyExternalShopChange();
        }

        _knownFiles.Clear();
        foreach (string path in current)
        {
            _knownFiles.Add(path);
        }

        if (EnsureHoldFolderForShopChange(notifyWhenRecreated: true) && _currentMode == DisplayMode.Shop)
        {
            RefreshShopItemsIfCurrentFolder(_shopFolder);
        }
    }

    private void ArrivalSensor_Created(object sender, FileSystemEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            SwkLogger.Debug($"ArrivalSensor_Created: {FormatWatcherEvent(e)}");
            SwkLogger.Info($"Trace.ExternalFlow.Receive.Sensor: sensor=Arrival change={e.ChangeType} path={e.FullPath}");
            if (e.ChangeType == WatcherChangeTypes.Created && !Directory.Exists(e.FullPath))
            {
                SwkLogger.Info($"Trace.ExternalFlow.Receive.Branch: sensor=Arrival action=TryRegisterExternalReceive source=Watcher path={e.FullPath}");
                TryRegisterExternalReceive(e.FullPath, "Watcher");
            }

            AppendExternalChangeHistory(e);
            NotifyExternalShopChange();
            if (!ShouldSuppressExternalChangeNotification())
            {
                SwkLogger.Debug($"ArrivalSensor_Created aftercare mark: path={e.FullPath}");
                SwkLogger.Info($"Trace.ExternalFlow.Receive.Branch: sensor=Arrival action=NoteFutureSharePolicyRepair path={e.FullPath}");
                NoteFutureSharePolicyRepair(
                    e.FullPath,
                    Path.GetDirectoryName(e.FullPath) ?? _shopFolder ?? string.Empty,
                    SharePolicyRepairReason.ExternalCreated);
            }
            else
            {
                SwkLogger.Debug($"ArrivalSensor_Created suppressed after detection: path={e.FullPath}");
            }
            RefreshShopItemsIfCurrentFolder(Path.GetDirectoryName(e.FullPath) ?? string.Empty);
            ScheduleRefreshShopItemsIfCurrentFolder(
                Path.GetDirectoryName(e.FullPath) ?? string.Empty,
                TimeSpan.FromMilliseconds(300));
        });
    }

    private void ArrivalSensor_Renamed(object sender, RenamedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            SwkLogger.Debug($"ArrivalSensor_Renamed: {FormatWatcherEvent(e)}");
            NotifyExternalShopChange();
            if (!ShouldSuppressExternalChangeNotification())
            {
                string folder = Path.GetDirectoryName(e.FullPath) ?? _shopFolder ?? string.Empty;
                AppendHistory(
                    HistoryChannel.Update,
                    $"{e.OldName} → {e.Name} に名前が変わりました。",
                    "ExternalRename",
                    HistoryOutcome.Info,
                    targetName: e.Name,
                    pathText: folder,
                    note: $"旧名: {e.OldName}",
                    sourcePath: e.OldFullPath,
                    destinationPath: e.FullPath,
                    destinationFolder: folder,
                    source: "Watcher");
                SwkLogger.Info($"ArrivalSensor_Renamed history-appended: old={e.OldFullPath} new={e.FullPath}");
                NoteFutureSharePolicyRepair(
                    e.FullPath,
                    folder,
                    SharePolicyRepairReason.ExternalRenamed);
            }
            else
            {
                SwkLogger.Debug($"ArrivalSensor_Renamed suppressed: old={e.OldFullPath} new={e.FullPath}");
            }
            RefreshShopItemsIfCurrentFolder(Path.GetDirectoryName(e.FullPath) ?? string.Empty);
        });
    }

    private void ArrivalSensor_Error(object sender, ErrorEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            DisposeWatcher();
            BeginPolling();
        });
    }

    private static IEnumerable<string> EnumerateShopSnapshot(string shopFolder)
    {
        foreach (string directory in Directory.EnumerateDirectories(shopFolder, "*", SearchOption.AllDirectories))
        {
            yield return directory;
        }

        foreach (string file in Directory.EnumerateFiles(shopFolder, "*", SearchOption.AllDirectories))
        {
            yield return file;
        }
    }

    private void QueueNotification(ArrivedItem item)
    {
        if (!CanShowNotification(externalOnly: true))
        {
            SwkLogger.Debug($"QueueNotification skipped: canNotify=false item={item.Name} folder={item.FolderPath}");
            return;
        }

        SwkLogger.Debug($"QueueNotification enqueue: item={item.Name} folder={item.FolderPath}");
        _pendingNotificationItems.Add(item);
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    private void NotifyShopMaintenance(string statusMessage, string notificationText)
    {
        SetTransientStatus(statusMessage);
        if (!CanShowNotification(externalOnly: true))
        {
            SwkLogger.Debug(
                $"NotifyShopMaintenance skipped: canNotify=false status={statusMessage} text={notificationText}");
            return;
        }

        SwkLogger.Info($"NotifyShopMaintenance: status={statusMessage} text={notificationText}");
        AppendHistory(
            HistoryChannel.Notification,
            notificationText,
            "Notify",
            HistoryOutcome.Info);
        _pipeClient.ShowBalloonTip("ShareWorkin のお知らせ", notificationText, _shopFolder);
    }

    private void NotificationTimer_Tick(object? sender, EventArgs e)
    {
        _notificationTimer.Stop();
        ShowNotification();
    }

    private void ShowNotification()
    {
        if (_pendingNotificationItems.Count == 0)
        {
            SwkLogger.Debug("ShowNotification skipped: queue-empty");
            return;
        }

        ArrivedItem firstItem = _pendingNotificationItems[0];
        string notifFolder = firstItem.FolderPath;
        int count = _pendingNotificationItems.Count;
        SwkLogger.Info($"ShowNotification: count={count} folder={notifFolder}");
        _pendingNotificationItems.Clear();

        if (count == 1)
        {
            string notificationText = $"{firstItem.Name} を受け取りました。";
            AppendHistory(
                HistoryChannel.Notification,
                notificationText,
                "Receive",
                HistoryOutcome.Info,
                HistoryDirection.Incoming,
                targetName: firstItem.Name,
                pathText: firstItem.FolderPath,
                note: "交流通知と未照合のため、送信元はまだ特定できていません。",
                destinationFolder: firstItem.FolderPath);
            _pipeClient.ShowBalloonTip("ShareWorkin の受信", notificationText, notifFolder);
            return;
        }

        string summaryText = $"{count} 件の受け取りを検知しました。";
        AppendHistory(
            HistoryChannel.Notification,
            summaryText,
            "Notify",
            HistoryOutcome.Info,
            HistoryDirection.Incoming,
            pathText: notifFolder,
            note: "交流通知と未照合の受信です。詳細は更新履歴で確認できます。",
            destinationFolder: notifFolder);
        _pipeClient.ShowBalloonTip("ShareWorkin の受信", summaryText, notifFolder);
    }

    private void RecordConfirmedInteraction(InteractionEventEntry interactionEvent, Friend? friend)
    {
        InteractionEventRepository.Append(interactionEvent);

        if (!CanShowNotification(externalOnly: false))
        {
            SwkLogger.Debug(
                $"MaybeRecordConfirmedInteraction stored-without-notify: event={interactionEvent.EventType} target={interactionEvent.TargetName ?? "-"}");
            return;
        }

        string friendLabel = interactionEvent.ReceiverName ?? interactionEvent.ReceiverMachineName ?? "相手";
        string actionLabel = GetInteractionActionLabel(interactionEvent.EventType);
        string notificationText = $"{friendLabel} への{actionLabel}を実行しました。";
        AppendHistory(
            HistoryChannel.Notification,
            notificationText,
            "InteractionNotify",
            HistoryOutcome.Success,
            HistoryDirection.Outgoing,
            friend: friend,
            targetName: interactionEvent.TargetName,
            pathText: interactionEvent.TargetFolder,
            note: interactionEvent.Message,
            interactionEventId: interactionEvent.Id,
            source: interactionEvent.SourceRoute,
            destinationPath: interactionEvent.TargetPath,
            destinationFolder: interactionEvent.TargetFolder);
        _pipeClient.ShowBalloonTip("ShareWorkin のお知らせ", notificationText, interactionEvent.TargetFolder);
        InteractionEventRepository.MarkDisplayed(interactionEvent.Id, DateTime.Now);
        _ = SendConfirmedInteractionToFriendAsync(interactionEvent, friend);
    }

    private bool TryCreateConfirmedInteraction(
        ExplorerActionResult result,
        out InteractionEventEntry interactionEvent,
        out Friend? friend)
    {
        interactionEvent = null!;
        friend = null;

        if (result.State != ExplorerActionState.Success ||
            _currentMode != DisplayMode.FriendShop ||
            _activeFriendShop is null ||
            string.IsNullOrWhiteSpace(result.EventType))
        {
            return false;
        }

        string? targetPath = !string.IsNullOrWhiteSpace(result.DestinationPath)
            ? result.DestinationPath
            : result.SourcePath;
        string? targetFolder = !string.IsNullOrWhiteSpace(result.DestinationFolder)
            ? result.DestinationFolder
            : Path.GetDirectoryName(targetPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        friend = _activeFriendShop;
        interactionEvent = new InteractionEventEntry
        {
            OccurredAt = DateTime.Now,
            EventType = result.EventType,
            Direction = InteractionEventDirection.Outgoing,
            SenderName = Environment.MachineName,
            SenderMachineName = Environment.MachineName,
            ReceiverId = friend.Id,
            ReceiverName = GetFriendLabel(friend),
            ReceiverMachineName = friend.HostMachineName,
            TargetName = result.TargetName,
            TargetPath = targetPath,
            TargetFolder = targetFolder,
            TargetKind = Directory.Exists(targetPath) ? "Folder" : "File",
            NotificationType = "ConfirmedInteraction",
            Message = string.IsNullOrWhiteSpace(_pendingInteractionMessage) ? null : _pendingInteractionMessage,
            MessageEnabled = !string.IsNullOrWhiteSpace(_pendingInteractionMessage),
            SourceRoute = result.Source
        };
        return true;
    }

    private async Task SendConfirmedInteractionToFriendAsync(InteractionEventEntry interactionEvent, Friend? friend)
    {
        if (friend is null)
        {
            return;
        }

        try
        {
            SwkNotificationListener.ShopInfo? shop = FindLiveShopInfo(friend) ?? await ResolveLiveFriendShopAsync(friend);
            if (shop is null)
            {
                AppendHistory(
                    HistoryChannel.Update,
                    $"{interactionEvent.TargetName ?? "項目"} の交流通知を相手へ届けられませんでした。",
                    "InteractionDispatchSkipped",
                    HistoryOutcome.Warning,
                    HistoryDirection.Outgoing,
                    friend: friend,
                    targetName: interactionEvent.TargetName,
                    pathText: interactionEvent.TargetFolder,
                    note: "相手のお店が見つからなかったため、正規交流イベントを送れませんでした。",
                    interactionEventId: interactionEvent.Id,
                    source: "InteractionDispatch",
                    destinationPath: interactionEvent.TargetPath,
                    destinationFolder: interactionEvent.TargetFolder);
                return;
            }

            string? relativePath = BuildShareRelativePath(_activeFriendShopRootPath ?? friend.ConnectUncPath, interactionEvent.TargetPath);
            var notice = new SwkNotificationProtocol.InteractionEventNotice
            {
                EventId = interactionEvent.Id,
                EventType = interactionEvent.EventType,
                SenderMachineName = Environment.MachineName,
                SenderDisplayName = Environment.MachineName,
                SenderSwkInstanceId = SwkInstanceIdentity.GetOrCreateId(),
                SenderShareName = _shopFolder is null ? null : Path.GetFileName(_shopFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                ReceiverShareName = friend.ShareName,
                TargetName = interactionEvent.TargetName ?? "項目",
                TargetRelativePath = relativePath,
                TargetKind = interactionEvent.TargetKind,
                NotificationType = interactionEvent.NotificationType,
                Message = interactionEvent.Message,
                IssuedAt = interactionEvent.OccurredAt.ToUniversalTime().ToString("o")
            };

            var listener = new SwkNotificationListener();
            SwkNotificationListener.InteractionEventSendResult sendResult = await listener.SendInteractionEventAsync(
                shop,
                notice,
                friend.OwnerCertThumbprint,
                CancellationToken.None);
            if (!sendResult.Success)
            {
                AppendHistory(
                    HistoryChannel.Update,
                    $"{interactionEvent.TargetName ?? "項目"} の交流通知を相手へ届けられませんでした。",
                    "InteractionDispatchFailed",
                    HistoryOutcome.Warning,
                    HistoryDirection.Outgoing,
                    friend: friend,
                    targetName: interactionEvent.TargetName,
                    pathText: interactionEvent.TargetFolder,
                    note: sendResult.ErrorMessage,
                    interactionEventId: interactionEvent.Id,
                    source: "InteractionDispatch",
                    destinationPath: interactionEvent.TargetPath,
                    destinationFolder: interactionEvent.TargetFolder);
            }
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"SendConfirmedInteractionToFriendAsync failed: {ex.Message}");
        }
    }

    private void ImportPendingIncomingInteractions()
    {
        foreach (SwkIncomingInteractionRecord entry in SwkIncomingInteractionInbox.GetUnprocessed())
        {
            AcceptIncomingInteraction(entry);
        }
    }

    private void AcceptIncomingInteraction(SwkIncomingInteractionRecord entry)
    {
        if (string.IsNullOrWhiteSpace(entry.EventId))
        {
            return;
        }

        if (InteractionEventRepository.Exists(entry.EventId))
        {
            SwkIncomingInteractionInbox.MarkProcessed(entry.EventId, DateTime.UtcNow);
            return;
        }

        Friend? friend = ResolveVerifiedIncomingInteractionFriend(entry);
        bool isVerified = friend is not null;
        string senderLabel = ResolveIncomingSenderLabel(entry, friend);
        DateTime occurredAt = DateTime.TryParse(entry.OccurredAt, out DateTime parsedOccurredAt)
            ? parsedOccurredAt.ToLocalTime()
            : DateTime.Now;
        DateTime? receivedAt = DateTime.TryParse(entry.ReceivedAt, out DateTime parsedReceivedAt)
            ? parsedReceivedAt.ToLocalTime()
            : DateTime.Now;
        DateTime? displayedAt = DateTime.TryParse(entry.DisplayedAt, out DateTime parsedDisplayedAt)
            ? parsedDisplayedAt.ToLocalTime()
            : null;

        entry.IsSenderVerified = isVerified;
        entry.VerifiedFriendId = friend?.Id;
        entry.VerifiedFriendName = friend is null ? null : GetFriendLabel(friend);

        InteractionEventRepository.Append(new InteractionEventEntry
        {
            Id = entry.EventId,
            OccurredAt = occurredAt,
            EventType = entry.EventType,
            Direction = InteractionEventDirection.Incoming,
            SenderName = senderLabel,
            SenderMachineName = entry.SenderMachineName,
            ReceiverName = Environment.MachineName,
            ReceiverMachineName = Environment.MachineName,
            TargetName = entry.TargetName,
            TargetPath = entry.TargetFullPath,
            TargetFolder = entry.TargetFolder,
            TargetKind = entry.TargetKind,
            NotificationType = entry.NotificationType,
            Message = entry.Message,
            MessageEnabled = !string.IsNullOrWhiteSpace(entry.Message),
            ReceivedAt = receivedAt,
            DisplayedAt = displayedAt,
            SourceRoute = entry.SourceRoute
        });

        string targetName = string.IsNullOrWhiteSpace(entry.TargetName) ? "項目" : entry.TargetName;
        string updateMessage = isVerified
            ? $"{senderLabel} から {targetName} を受け取りました。"
            : $"送信元を確認できない交流通知があります。対象: {targetName}";
        string? senderNote = isVerified
            ? null
            : BuildUnverifiedSenderNote(entry);

        if (isVerified && !string.IsNullOrWhiteSpace(entry.TargetName) && !string.IsNullOrWhiteSpace(entry.TargetFolder))
        {
            if (!string.IsNullOrWhiteSpace(entry.TargetFullPath))
            {
                _recentIncomingInteractionAt[entry.TargetFullPath] = DateTime.Now;
            }

            ArrivedItems.Insert(0, new ArrivedItem(entry.TargetName, entry.TargetFolder, DateTime.Now));
            while (ArrivedItems.Count > MaxArrivedItemCount)
            {
                ArrivedItems.RemoveAt(ArrivedItems.Count - 1);
            }
        }

        if (CanShowNotification(externalOnly: true))
        {
            string notificationText = string.IsNullOrWhiteSpace(entry.Message)
                ? updateMessage
                : $"{updateMessage}\r\nメッセージ: {entry.Message}";
            AppendHistory(
                HistoryChannel.Notification,
                updateMessage,
                isVerified ? "InteractionNotify" : "InteractionNotifyUnverified",
                isVerified ? HistoryOutcome.Success : HistoryOutcome.Warning,
                HistoryDirection.Incoming,
                friend: friend,
                targetName: entry.TargetName,
                pathText: entry.TargetFolder,
                note: BuildInteractionReceiveNote(senderNote, entry.Message),
                interactionEventId: entry.EventId,
                source: entry.SourceRoute,
                destinationPath: entry.TargetFullPath,
                destinationFolder: entry.TargetFolder);
            if (displayedAt is null)
            {
                _pipeClient.ShowBalloonTip("ShareWorkin の受信", notificationText, entry.TargetFolder ?? _shopFolder);
                DateTime now = DateTime.Now;
                InteractionEventRepository.MarkDisplayed(entry.EventId, now);
                SwkIncomingInteractionInbox.MarkDisplayed(entry.EventId, now.ToUniversalTime());
            }
        }

        SwkIncomingInteractionInbox.MarkProcessed(entry.EventId, DateTime.UtcNow);
    }

    private static string ResolveIncomingSenderLabel(SwkIncomingInteractionRecord entry, Friend? verifiedFriend)
    {
        if (verifiedFriend is not null)
        {
            return GetFriendLabel(verifiedFriend);
        }

        if (!string.IsNullOrWhiteSpace(entry.SenderDisplayName))
        {
            return entry.SenderDisplayName;
        }

        if (!string.IsNullOrWhiteSpace(entry.SenderMachineName))
        {
            return entry.SenderMachineName;
        }

        return "相手";
    }

    private static Friend? ResolveVerifiedIncomingInteractionFriend(SwkIncomingInteractionRecord entry)
    {
        IReadOnlyList<Friend> friends = FriendsRepository.LoadAll();
        if (!string.IsNullOrWhiteSpace(entry.SenderSwkInstanceId))
        {
            Friend? byInstance = friends.FirstOrDefault(f =>
                string.Equals(f.RemoteSwkInstanceId, entry.SenderSwkInstanceId, StringComparison.OrdinalIgnoreCase));
            if (byInstance is not null)
            {
                return byInstance;
            }
        }

        return null;
    }

    private static string? BuildUnverifiedSenderNote(SwkIncomingInteractionRecord entry)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(entry.SenderMachineName))
        {
            parts.Add($"送信元PC: {entry.SenderMachineName}");
        }

        if (!string.IsNullOrWhiteSpace(entry.SenderSwkInstanceId))
        {
            parts.Add($"送信元ID: {entry.SenderSwkInstanceId}");
        }

        if (!string.IsNullOrWhiteSpace(entry.SenderDisplayName))
        {
            parts.Add($"通知本文名: {entry.SenderDisplayName}");
        }

        return parts.Count == 0
            ? "登録済み Friend と照合できませんでした。"
            : "未照合イベント: " + string.Join(" / ", parts);
    }

    private static string? BuildInteractionReceiveNote(string? senderNote, string? message)
    {
        if (string.IsNullOrWhiteSpace(senderNote) && string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(senderNote))
        {
            return $"メッセージ: {message}";
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return senderNote;
        }

        return $"{senderNote}\r\nメッセージ: {message}";
    }

    private static string? BuildShareRelativePath(string? rootPath, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        try
        {
            string normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedTarget = targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!normalizedTarget.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string relative = normalizedTarget[normalizedRoot.Length..]
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrWhiteSpace(relative) ? null : relative.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch
        {
            return null;
        }
    }

    private static string GetInteractionActionLabel(string eventType) => eventType switch
    {
        "Place" => "送付",
        "Copy" => "コピー",
        "Move" => "移動",
        "CreateFolder" => "共有フォルダー作成",
        "Rename" => "名前変更",
        "Delete" => "削除",
        "Hold" => "保留操作",
        _ => "操作"
    };

    private bool TryConfirmInteractionAction(
        string actionLabel,
        IReadOnlyList<string> targetNames,
        out string? interactionMessage)
    {
        interactionMessage = null;

        if (_currentMode != DisplayMode.FriendShop || _activeFriendShop is null)
        {
            return true;
        }

        InteractionActionConfirmDialog dialog = new(
            GetFriendLabel(_activeFriendShop),
            actionLabel,
            targetNames)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        interactionMessage = string.IsNullOrWhiteSpace(dialog.NotificationMessage)
            ? null
            : dialog.NotificationMessage;
        return true;
    }


    private void OpenShopItem(ShopItem item)
    {
        if (item.IsHoldFolder)
        {
            NavigateToHoldRoot(addHistory: true);
            return;
        }

        if (item.IsDirectory &&
            _currentMode == DisplayMode.FriendShop &&
            string.Equals(item.Name, HoldFolderName, StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.MessageBox.Show("保留ホルダーは見られません。", "ShareWorkin",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        if (item.IsDirectory)
        {
            NavigateTo(item.FullPath, addHistory: true, clearForward: true);
            return;
        }

        OpenPath(item.FullPath);
    }

    private void VisitShop(string? folderPath)
    {
        bool isUnc = !string.IsNullOrWhiteSpace(folderPath) && folderPath.StartsWith(@"\\", StringComparison.Ordinal);
        SwkLogger.Info($"Investigation.VisitShop: mode={_currentMode} path={folderPath ?? "(null)"} isUnc={isUnc}");
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            SetTransientStatus("見に行ける場所がありません。");
            AppendUpdateUiHistory(
                "フォルダーを開けませんでした。",
                "VisitShop",
                HistoryOutcome.Warning,
                pathText: folderPath,
                note: "見に行ける場所がありません。",
                source: "MainWindow.visit");
            return;
        }

        if (!Directory.Exists(folderPath))
        {
            SetTransientStatus("その場所が見つかりません。");
            AppendUpdateUiHistory(
                "フォルダーを開けませんでした。",
                "VisitShop",
                HistoryOutcome.Warning,
                pathText: folderPath,
                note: "その場所が見つかりません。",
                source: "MainWindow.visit");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetTransientStatus("その場所へ行けませんでした。");
            AppendUpdateUiHistory(
                "フォルダーを開けませんでした。",
                "VisitShop",
                HistoryOutcome.Warning,
                pathText: folderPath,
                note: $"その場所へ行けませんでした。({ex.Message})",
                source: "MainWindow.visit");
        }
    }

    private void ShopItemsContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (!_shopItemsContextMenuCommandArmed)
        {
            SuppressShopItemsCommandsBriefly();
        }

        _clickSelectionPending = false;
        _itemWasSelectedAtPress = false;
        _dragStartItem = null;
        _shopItemsContextMenuCommandArmed = false;
        EndRubberBand();
    }

    private bool ShouldProcessShopItemsContextMenuCommand(object? sender)
    {
        if (sender is not MenuItem)
        {
            if (DateTime.UtcNow < _ignoreShopItemsCommandsUntilUtc)
            {
                _ignoreShopItemsCommandsUntilUtc = DateTime.MinValue;
                return false;
            }

            return true;
        }

        bool armed = _shopItemsContextMenuCommandArmed;
        _shopItemsContextMenuCommandArmed = false;
        _ignoreShopItemsCommandsUntilUtc = DateTime.MinValue;
        return armed;
    }

    private void SuppressShopItemsCommandsBriefly()
    {
        _ignoreShopItemsCommandsUntilUtc = DateTime.UtcNow.AddMilliseconds(400);
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T matched)
            {
                return matched;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void SearchFromShopRoot(string searchText)
    {
        string query = searchText.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        string? searchRoot = _currentMode == DisplayMode.FriendShop
            ? _activeFriendShopRootPath
            : _shopFolder;

        if (string.IsNullOrWhiteSpace(searchRoot) || !Directory.Exists(searchRoot))
        {
            SetTransientStatus("検索できるお店がありません。");
            AppendUpdateUiHistory(
                $"「{query}」を検索できませんでした。",
                "Search",
                HistoryOutcome.Warning,
                pathText: searchRoot,
                note: "検索できるお店がありません。",
                source: "MainWindow.search");
            return;
        }

        string? match = FindFirstShopItem(searchRoot, query);
        if (string.IsNullOrWhiteSpace(match))
        {
            SetTransientStatus("見つかりませんでした。");
            AppendUpdateUiHistory(
                $"「{query}」は見つかりませんでした。",
                "Search",
                HistoryOutcome.Info,
                pathText: searchRoot,
                note: $"検索語: {query}",
                source: "MainWindow.search");
            return;
        }

        string? destinationFolder = Directory.Exists(match)
            ? Path.GetDirectoryName(match)
            : Path.GetDirectoryName(match);
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return;
        }

        _pendingFocusName = Path.GetFileName(match);
        NavigateTo(destinationFolder, addHistory: true, clearForward: true, syncModeToPath: true);
    }

    private static string? FindFirstShopItem(string rootFolder, string query)
    {
        Stack<string> pendingFolders = new();
        pendingFolders.Push(rootFolder);

        while (pendingFolders.Count > 0)
        {
            string currentFolder = pendingFolders.Pop();

            List<string> directories;
            List<string> files;
            try
            {
                directories = Directory.EnumerateDirectories(currentFolder)
                    .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                files = Directory.EnumerateFiles(currentFolder)
                    .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string path in directories.Concat(files))
            {
                if (Path.GetFileName(path).Contains(query, StringComparison.CurrentCultureIgnoreCase))
                {
                    return path;
                }
            }

            for (int i = directories.Count - 1; i >= 0; i--)
            {
                pendingFolders.Push(directories[i]);
            }
        }

        return null;
    }

    private void OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            SetTransientStatus("その場所が見つかりません。");
            AppendUpdateUiHistory(
                "項目を開けませんでした。",
                "OpenPath",
                HistoryOutcome.Warning,
                pathText: Path.GetDirectoryName(path),
                sourcePath: path,
                note: "その場所が見つかりません。",
                source: "MainWindow.open");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetTransientStatus("開けませんでした。");
            AppendUpdateUiHistory(
                "項目を開けませんでした。",
                "OpenPath",
                HistoryOutcome.Warning,
                pathText: Path.GetDirectoryName(path),
                sourcePath: path,
                note: $"開けませんでした。({ex.Message})",
                source: "MainWindow.open");
        }
    }

    private void NavigateTo(
        string folderPath,
        bool addHistory,
        bool clearForward = false,
        bool syncModeToPath = false)
    {
        if (!Directory.Exists(folderPath))
        {
            SetTransientStatus("その場所が見つかりません。");
            AppendUpdateUiHistory(
                "移動先を開けませんでした。",
                "Navigate",
                HistoryOutcome.Warning,
                pathText: Path.GetDirectoryName(folderPath),
                destinationPath: folderPath,
                note: "その場所が見つかりません。",
                source: "MainWindow.navigate");
            return;
        }

        if (addHistory && !string.IsNullOrWhiteSpace(_currentFolder) &&
            !string.Equals(_currentFolder, folderPath, StringComparison.OrdinalIgnoreCase))
        {
            _backStack.Push(_currentFolder);
        }

        if (clearForward)
        {
            _forwardStack.Clear();
        }

        if (syncModeToPath)
        {
            SyncModeToFolderPath(folderPath);
        }

        _currentFolder = folderPath;
        RefreshShopItems();
        StartContentsSensor(folderPath);
        UpdateNavigationState();
    }

    private void SyncModeToFolderPath(string folderPath)
    {
        if (_activeFriendShop != null && !string.IsNullOrWhiteSpace(_activeFriendShop.ConnectUncPath) &&
            IsUnderFolder(folderPath, _activeFriendShop.ConnectUncPath))
        {
            _currentMode = DisplayMode.FriendShop;
        }
        else if (IsUnderFolder(folderPath, GetHoldFolderPath()))
        {
            _currentMode = DisplayMode.Hold;
            _activeFriendShop = null;
            _activeFriendShopRootPath = null;
            _missingFriendShopStatus = null;
        }
        else
        {
            _currentMode = DisplayMode.Shop;
            _activeFriendShop = null;
            _activeFriendShopRootPath = null;
            _missingFriendShopStatus = null;
        }
    }

    private static bool IsUnderFolder(string folderPath, string rootFolderPath)
    {
        try
        {
            string root = Path.GetFullPath(rootFolderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string current = Path.GetFullPath(folderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(root, current, StringComparison.OrdinalIgnoreCase) ||
                   current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void RefreshShopItems()
    {
        CancelFolderSizeCalculation();
        ShopItems.Clear();

        if (string.IsNullOrWhiteSpace(_currentFolder) || !Directory.Exists(_currentFolder))
        {
            if (_currentMode == DisplayMode.FriendShop && !string.IsNullOrWhiteSpace(_currentFolder))
                SetTransientStatus("接続できません");
            UpdateBreadcrumb();
            return;
        }

        _effectiveParentPerm = FindEffectiveAncestorPermission(_currentFolder);
        ShopPermissionManifest? friendPermissionManifest = LoadFriendPermissionManifest();

        try
        {
            EnsureHoldFolderForShopChange(notifyWhenRecreated: false);

            List<ShopItem> all = Directory.EnumerateDirectories(_currentFolder)
                .Where(path => !IsHoldFolderPath(path) &&
                               !(_currentMode == DisplayMode.FriendShop &&
                                 string.Equals(Path.GetFileName(path), HoldFolderName, StringComparison.OrdinalIgnoreCase)) &&
                               !(_currentMode == DisplayMode.FriendShop &&
                                 _friendShopReadOnlyState.TryGetValue(path, out var cached) && cached.IsSharedOff))
                .Select(path => ShopItem.FromPath(path, isDirectory: true, isHoldFolder: false))
                .Concat(Directory.EnumerateFiles(_currentFolder)
                    .Where(path => !string.Equals(Path.GetFileName(path), ShopPermissionManifest.FileName, StringComparison.OrdinalIgnoreCase))
                    .Select(path => ShopItem.FromPath(path, isDirectory: false)))
                .ToList();

            foreach (ShopItem item in all)
            {
                if (item.IsDirectory && _folderSizeCache.TryGetValue(item.FullPath, out long cached))
                {
                    item.SetSize(cached);
                }

                if (item.IsDirectory && !item.IsHoldFolder && _subfolderCountCache.TryGetValue(item.FullPath, out int cachedCount))
                {
                    item.SetSubfolderCount(cachedCount);
                }

                if (_currentMode == DisplayMode.FriendShop)
                {
                    item.IsFromFriendShop = true;
                    if (_friendShopReadOnlyState.TryGetValue(item.FullPath, out var prevState))
                    {
                        item.IsReadOnly = prevState.IsReadOnly;
                        item.IsSharedOff = prevState.IsSharedOff;
                    }
                    ApplyFriendPermissionManifest(item, friendPermissionManifest);
                }
                else if (IsHeldItemPath(item.FullPath) &&
                         _holdDisplayPermissionMap.TryGetValue(item.FullPath, out var holdDisplayPerm))
                {
                    ApplyPermissionToItem(item, holdDisplayPerm);
                }
                else if (_permissionMap.TryGetValue(item.FullPath, out var perm))
                {
                    if (!item.IsHoldFolder &&
                        _effectiveParentPerm.HasValue &&
                        !IsWithinRange(perm, _effectiveParentPerm))
                    {
                        ApplyPermissionToItem(item, _effectiveParentPerm.Value);
                    }
                    else
                    {
                        ApplyPermissionToItem(item, perm);
                    }
                }
                else if (!item.IsHoldFolder && _effectiveParentPerm.HasValue)
                {
                    ApplyPermissionToItem(item, _effectiveParentPerm.Value);
                }
            }

            if (_currentMode == DisplayMode.FriendShop)
            {
                all = all.Where(item => !item.IsSharedOff).ToList();
            }

            IEnumerable<ShopItem> sorted = SortShopItems(all);
            if (_isShopOpen && _currentMode == DisplayMode.Shop &&
                !string.IsNullOrWhiteSpace(_shopFolder) &&
                string.Equals(
                    Path.GetFullPath(_currentFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(_shopFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                string holdPath = GetHoldFolderPath();
                DateTime holdUpdatedAt = Directory.Exists(holdPath) ? Directory.GetLastWriteTime(holdPath) : DateTime.MinValue;
                sorted = sorted.Prepend(new ShopItem(HoldFolderName, holdPath, true, true, holdUpdatedAt, null));
            }
            foreach (ShopItem item in sorted)
            {
                if (_currentMode == DisplayMode.FriendShop && item.IsSharedOff)
                {
                    continue;
                }
                ShopItems.Add(item);
            }

            UpdateBreadcrumb();
            ApplyPendingFocus();
            if (_currentMode == DisplayMode.FriendShop)
                StartFriendShopPolling();
            else
                StopFriendShopPolling();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetTransientStatus("中身を見られませんでした。");
        }

        if (_isSizeCalcEnabled)
        {
            StartFolderSizeCalculation();
        }

        StartSubfolderCountLoad();
    }

    private ShopPermissionManifest? LoadFriendPermissionManifest()
    {
        if (_currentMode != DisplayMode.FriendShop)
        {
            return null;
        }

        string? root = GetCurrentRootPath();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return null;
        }

        return ShopPermissionManifest.Load(root);
    }

    private void ApplyFriendPermissionManifest(ShopItem item, ShopPermissionManifest? manifest)
    {
        if (manifest is null)
        {
            return;
        }

        string? root = GetCurrentRootPath();
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        string relativePath = ToRelativeShopPath(root, item.FullPath);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        ShopPermissionManifestEntry? entry = manifest.FindEffectiveEntry(relativePath);
        if (entry is null)
        {
            return;
        }

        bool isAllowed = IsCurrentMachineAllowed(entry);
        if (entry.IsSharedOff || !isAllowed)
        {
            item.IsSharedOff = true;
            item.IsReadOnly = false;
            return;
        }

        item.IsSharedOff = false;
        item.IsReadOnly = entry.IsReadOnly;
        item.AllowedUsers.Clear();
        foreach (string user in entry.Users.Where(user => !string.IsNullOrWhiteSpace(user)))
        {
            item.AllowedUsers.Add(user);
        }
    }

    private static bool IsCurrentMachineAllowed(ShopPermissionManifestEntry entry)
    {
        if (entry.AllowedMachineNames.Count == 0 && entry.Users.Count == 0)
        {
            return true;
        }

        string machineName = Environment.MachineName;
        return entry.AllowedMachineNames.Any(name =>
                   string.Equals(name, machineName, StringComparison.OrdinalIgnoreCase)) ||
               entry.Users.Any(name =>
                   string.Equals(name, machineName, StringComparison.OrdinalIgnoreCase));
    }

    private void StartFriendShopPolling()
    {
        if (_friendShopPollTimer != null) return;
        _friendShopPollTimer = new DispatcherTimer { Interval = PollingInterval };
        _friendShopPollTimer.Tick += FriendShopPollTimer_Tick;
        _friendShopPollTimer.Start();
        StartFriendShopPermissionListener(_activeFriendShop?.HostMachineName ?? "");
    }

    private void StopFriendShopPolling()
    {
        if (_friendShopPollTimer == null) return;
        _friendShopPollTimer.Stop();
        _friendShopPollTimer.Tick -= FriendShopPollTimer_Tick;
        _friendShopPollTimer = null;
        StopFriendShopPermissionListener();
    }

    private bool _friendShopPollRunning;
    private int _friendShopLiveMissCount;
    private CancellationTokenSource? _friendShopNotifCts;
    private string? _missingFriendShopStatus;

    private async void FriendShopPollTimer_Tick(object? sender, EventArgs e)
        => await RunFriendShopPollAsync();

    private async Task RunFriendShopPollAsync()
    {
        if (_currentMode != DisplayMode.FriendShop || _activeFriendShop is null) return;
        Friend activeFriend = _activeFriendShop;
        string activeFriendId = activeFriend.Id;
        if (_friendShopPollRunning) return;
        _friendShopPollRunning = true;
        try
        {
            SwkNotificationListener.ShopInfo? liveShop = await ProbeActiveFriendShopAsync(activeFriend);
            if (_currentMode != DisplayMode.FriendShop ||
                _activeFriendShop is null ||
                !string.Equals(_activeFriendShop.Id, activeFriendId, StringComparison.Ordinal))
            {
                return;
            }

            if (liveShop is null)
            {
                _friendShopLiveMissCount++;
                if (_friendShopLiveMissCount >= FriendShopOfflineMissThreshold)
                {
                    MarkActiveFriendShopOffline();
                }
                return;
            }

            _friendShopLiveMissCount = 0;
            SwkNetworkCache.UpsertShop(liveShop);

            bool needsReconnect = string.IsNullOrWhiteSpace(_currentFolder) ||
                !string.Equals(activeFriend.HostMachineName, liveShop.MachineName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(activeFriend.ShareName, liveShop.ShareName, StringComparison.OrdinalIgnoreCase);
            if (needsReconnect)
            {
                UpdateFriendExternalState(activeFriend, liveShop);
                PopulateExplorerDropdown();
                await NavigateToFriendShopAsync(activeFriend, liveShop);
                return;
            }

            RefreshShopItems();
            // 非表示中の OFF フォルダも復帰検知のために検査リストへ追加する
            string folder = _currentFolder!;
            List<ShopItem> hiddenOff = _friendShopReadOnlyState
                .Where(kv => kv.Value.IsSharedOff)
                .Select(kv => kv.Key)
                .Where(path => path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                .Select(path => ShopItem.FromPath(path, isDirectory: true, isHoldFolder: false))
                .ToList();
            List<ShopItem> allItems = [.. ShopItems, .. hiddenOff];
            await ApplyFriendShopReadOnlyAsync(folder, allItems);
        }
        finally
        {
            _friendShopPollRunning = false;
        }
    }

    private async Task<SwkNotificationListener.ShopInfo?> ProbeActiveFriendShopAsync(Friend friend)
    {
        List<LanCandidate> candidates = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void AddAddress(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!IPAddress.TryParse(value, out IPAddress? address)) return;
            if (address.AddressFamily != AddressFamily.InterNetwork) return;
            string key = address.ToString();
            if (seen.Add(key))
                candidates.Add(new LanCandidate(address, friend.HostMachineName));
        }

        SwkNotificationListener.ShopInfo? cached = FindLiveShopInfo(friend);
        AddAddress(cached?.IpAddress);
        AddAddress(friend.LastKnownAddress);

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(friend.HostMachineName);
            foreach (IPAddress address in addresses)
                AddAddress(address.ToString());
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            SwkLogger.Debug($"ProbeActiveFriendShopAsync DNS failed for {friend.HostMachineName}: {ex.Message}");
        }

        if (candidates.Count == 0) return null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            IReadOnlyList<SwkNotificationListener.ShopInfo> found =
                await SwkNotificationListener.ProbeHostsAsync(candidates, cts.Token);
            SwkNotificationListener.ShopInfo? byId = found.FirstOrDefault(s =>
                SameSwkInstance(friend, s) &&
                string.Equals(s.ShareName, friend.ShareName, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId;

            SwkNotificationListener.ShopInfo? exact = found.FirstOrDefault(s =>
                string.Equals(NormalizeHostName(s.MachineName), NormalizeHostName(friend.HostMachineName), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.ShareName, friend.ShareName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(friend.RemoteSwkInstanceId) &&
                !string.IsNullOrWhiteSpace(exact?.SwkInstanceId))
            {
                return null;
            }
            if (exact is not null) return exact;

            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            SwkLogger.Debug($"ProbeActiveFriendShopAsync failed: {ex.Message}");
            return null;
        }
    }

    private void MarkActiveFriendShopOffline()
    {
        if (_activeFriendShop is null) return;

        string label = string.IsNullOrWhiteSpace(_activeFriendShop.DisplayName)
            ? _activeFriendShop.HostMachineName
            : _activeFriendShop.DisplayName;

        SwkNetworkCache.RemoveShop(_activeFriendShop.HostMachineName, _activeFriendShop.ShareName);
        DisposeContentsWatcher();
        ShopItems.Clear();
        _friendShopReadOnlyState.Clear();
        _activeFriendShopRootPath = null;
        _currentFolder = null;
        _backStack.Clear();
        _forwardStack.Clear();
        UpdateBreadcrumb();
        UpdateNavigationState();
        PopulateExplorerDropdown();
        string offlineMessage = $"{label} のお店が見つかりません。";
        _missingFriendShopStatus = offlineMessage;
        SetTransientStatus(offlineMessage);
    }

    private void StartFriendShopPermissionListener(string friendMachineName)
    {
        _friendShopNotifCts?.Cancel();
        _friendShopNotifCts = new CancellationTokenSource();
        var token = _friendShopNotifCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                using var udp = new System.Net.Sockets.UdpClient();
                udp.Client.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, SwkNotificationBroadcaster.UdpDiscoveryPort));
                SwkLogger.Debug($"FriendShop permission listener started for {friendMachineName}");
                while (!token.IsCancellationRequested)
                {
                    var recv = await udp.ReceiveAsync(token);
                    string json = System.Text.Encoding.UTF8.GetString(recv.Buffer);
                    if (!json.Contains("\"SharePermissionChanged\"")) continue;
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("machineName", out var mn)) continue;
                    if (!string.Equals(mn.GetString(), friendMachineName, StringComparison.OrdinalIgnoreCase)) continue;
                    SwkLogger.Info($"SharePermissionChanged received from {friendMachineName}: triggering immediate poll");
                    await Dispatcher.InvokeAsync(() => _ = RunFriendShopPollAsync());
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SwkLogger.Debug($"FriendShop permission listener error: {ex.Message}"); }
        }, token);
    }

    private void StopFriendShopPermissionListener()
    {
        _friendShopNotifCts?.Cancel();
        _friendShopNotifCts = null;
    }


    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    private static bool IsDirectoryWritable(string path)
    {
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_ALL = 7;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        using var handle = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_ALL,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        bool writable = !handle.IsInvalid;
        if (!writable)
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            SwkLogger.Debug($"IsDirectoryWritable: denied path={path} win32err={err}");
        }
        return writable;
    }

    private static bool IsDirectoryReadable(string path)
    {
        const uint GENERIC_READ = 0x80000000;
        const uint FILE_SHARE_ALL = 7;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        using var handle = CreateFileW(path, GENERIC_READ, FILE_SHARE_ALL,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        return !handle.IsInvalid;
    }

    private async Task ApplyFriendShopReadOnlyAsync(string folder, List<ShopItem> items, bool silent = false)
    {
        bool folderWritable = await Task.Run(() => IsDirectoryWritable(folder));
        bool folderReadable = folderWritable || await Task.Run(() => IsDirectoryReadable(folder));
        SwkLogger.Debug($"ApplyFriendShopReadOnly: folder={folder} writable={folderWritable} readable={folderReadable} silent={silent} items={items.Count}");
        bool anyChanged = false;
        foreach (ShopItem item in items)
        {
            bool isWritable;
            bool isReadable;
            if (item.IsDirectory)
            {
                isWritable = await Task.Run(() => IsDirectoryWritable(item.FullPath));
                isReadable = isWritable || await Task.Run(() => IsDirectoryReadable(item.FullPath));
            }
            else
            {
                isWritable = folderWritable;
                isReadable = folderReadable;
            }
            bool isReadOnly = !isWritable && isReadable;
            bool isSharedOff = !isWritable && !isReadable;

            _friendShopReadOnlyState.TryGetValue(item.FullPath, out var prevState);
            bool stateChanged = prevState.IsReadOnly != isReadOnly || prevState.IsSharedOff != isSharedOff;

            SwkLogger.Debug($"  {item.Name}: isReadOnly={isReadOnly} isSharedOff={isSharedOff} prev=({prevState.IsReadOnly},{prevState.IsSharedOff}) changed={stateChanged}");

            _friendShopReadOnlyState[item.FullPath] = (isReadOnly, isSharedOff);
            bool itemChanged = item.IsReadOnly != isReadOnly || item.IsSharedOff != isSharedOff;
            item.IsReadOnly = isReadOnly;
            item.IsSharedOff = isSharedOff;
            if (isSharedOff)
            {
                // OFF → FriendShop では非表示
                await Dispatcher.InvokeAsync(() => ShopItems.Remove(item));
            }
            else if (prevState.IsSharedOff)
            {
                // OFF から復帰 → リストを再構築して表示
                await Dispatcher.InvokeAsync(RefreshShopItems);
            }
            else if (itemChanged)
            {
                await Dispatcher.InvokeAsync(item.RefreshShareStatus);
            }
            if (!silent && stateChanged)
                anyChanged = true;
        }
        if (anyChanged)
            await Dispatcher.InvokeAsync(() =>
                NotifyShopMaintenance("共有状況が変わりました。", "共有状況が変わりました。"));
    }

    private IEnumerable<ShopItem> SortShopItems(IEnumerable<ShopItem> items)
    {
        bool ascending = _sortDirection == ListSortDirection.Ascending;
        return _sortField switch
        {
            ShopSortField.Name => ascending
                ? items.OrderBy(i => i.SortKey).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                : items.OrderBy(i => i.SortKey).ThenByDescending(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            ShopSortField.ShareStatus => ascending
                ? items.OrderBy(i => i.ShareStatusText, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                : items.OrderByDescending(i => i.ShareStatusText, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            ShopSortField.Kind => ascending
                ? items.OrderBy(i => i.KindText).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                : items.OrderByDescending(i => i.KindText).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            ShopSortField.UpdatedAt => ascending
                ? items.OrderBy(i => i.UpdatedAt).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                : items.OrderByDescending(i => i.UpdatedAt).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            ShopSortField.Size => ascending
                ? items.OrderBy(i => i.SizeBytes ?? -1).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                : items.OrderByDescending(i => i.SizeBytes ?? -1).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => items,
        };
    }

    private void ShopItemsListView_HeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Tag is not string tagValue)
        {
            return;
        }

        if (!Enum.TryParse(tagValue, out ShopSortField field))
        {
            return;
        }

        if (_sortField == field)
        {
            _sortDirection = _sortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            _sortField = field;
            _sortDirection = ListSortDirection.Ascending;
        }

        UpdateColumnHeaders();
        ResortShopItems();
    }

    private void ResortShopItems()
    {
        if (ShopItems.Count == 0)
        {
            return;
        }

        List<ShopItem> sorted = SortShopItems(ShopItems.ToList()).ToList();
        ShopItems.Clear();
        foreach (ShopItem item in sorted)
        {
            ShopItems.Add(item);
        }
    }

    private void UpdateColumnHeaders()
    {
        SetColumnHeader(NameColumnHeader, "名前", ShopSortField.Name);
        SetColumnHeader(ShareStatusColumnHeader, "共有状況", ShopSortField.ShareStatus);
        SetColumnHeader(KindColumnHeader, "種類", ShopSortField.Kind);
        SetColumnHeader(UpdatedAtColumnHeader, "更新日時", ShopSortField.UpdatedAt);
        SetColumnHeader(SizeColumnHeader, "サイズ", ShopSortField.Size);
    }

    private void SetColumnHeader(GridViewColumnHeader header, string label, ShopSortField field)
    {
        bool active = _sortField == field;
        string arrow = active ? (_sortDirection == ListSortDirection.Ascending ? " ↑" : " ↓") : string.Empty;
        header.Content = new TextBlock
        {
            Text = label + arrow,
            FontWeight = active ? FontWeights.Bold : FontWeights.Normal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = System.Windows.TextAlignment.Center,
        };
    }

    private void ApplyPendingFocus()
    {
        if (string.IsNullOrEmpty(_pendingFocusName))
        {
            return;
        }

        ShopItem? target = ShopItems.FirstOrDefault(item =>
            string.Equals(item.Name, _pendingFocusName, StringComparison.OrdinalIgnoreCase));
        _pendingFocusName = null;
        if (target is null)
        {
            return;
        }

        ShopItemsListView.SelectedItem = target;
        ShopItemsListView.ScrollIntoView(target);
    }

    private void StartContentsSensor(string folderPath)
    {
        DisposeContentsWatcher();

        try
        {
            _contentsSensor = new FileSystemWatcher(folderPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _contentsSensor.Created += ContentsSensor_Changed;
            _contentsSensor.Deleted += ContentsSensor_Changed;
            _contentsSensor.Changed += ContentsSensor_Changed;
            _contentsSensor.Renamed += ContentsSensor_Renamed;
            _contentsSensor.Error += ContentsSensor_Error;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _contentsSensor = null;
        }
    }

    private void ContentsSensor_Changed(object sender, FileSystemEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            SwkLogger.Debug($"ContentsSensor_Changed: {FormatWatcherEvent(e)} currentFolder={_currentFolder ?? "-"}");
            SwkLogger.Info($"Trace.ExternalFlow.Receive.Sensor: sensor=Contents change={e.ChangeType} path={e.FullPath}");
            if (!string.IsNullOrWhiteSpace(_currentFolder))
            {
                InvalidateSizeCacheUnder(_currentFolder);
            }
            bool recreatedHoldFolder = EnsureHoldFolderForShopChange(notifyWhenRecreated: true);
            bool shouldNotifyChange =
                e.ChangeType == WatcherChangeTypes.Deleted ||
                e.ChangeType == WatcherChangeTypes.Created;
            SwkLogger.Debug(
                $"ContentsSensor_Changed decision: recreatedHoldFolder={recreatedHoldFolder} shouldNotifyChange={shouldNotifyChange}");
            if (!recreatedHoldFolder && shouldNotifyChange)
            {
                NotifyExternalShopChange();
            }
            if (e.ChangeType == WatcherChangeTypes.Created && !ShouldSuppressExternalChangeNotification())
            {
                SwkLogger.Debug($"ContentsSensor_Changed aftercare mark: path={e.FullPath}");
                SwkLogger.Info($"Trace.ExternalFlow.Receive.Branch: sensor=Contents action=NoteFutureSharePolicyRepair path={e.FullPath}");
                NoteFutureSharePolicyRepair(
                    e.FullPath,
                    Path.GetDirectoryName(e.FullPath) ?? _currentFolder ?? string.Empty,
                    SharePolicyRepairReason.ExternalCreated);
            }
            else if (e.ChangeType == WatcherChangeTypes.Created)
            {
                SwkLogger.Debug($"ContentsSensor_Changed create suppressed: path={e.FullPath}");
            }
            RefreshShopItems();
            ScheduleRefreshShopItemsIfCurrentFolder(_currentFolder ?? string.Empty, TimeSpan.FromMilliseconds(300));
        });
    }

    private void ContentsSensor_Renamed(object sender, RenamedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            SwkLogger.Debug($"ContentsSensor_Renamed: {FormatWatcherEvent(e)} currentFolder={_currentFolder ?? "-"}");
            if (!string.IsNullOrWhiteSpace(_currentFolder))
            {
                InvalidateSizeCacheUnder(_currentFolder);
            }
            EnsureHoldFolderForShopChange(notifyWhenRecreated: true);
            NotifyExternalShopChange();
            if (!ShouldSuppressExternalChangeNotification())
            {
                SwkLogger.Debug($"ContentsSensor_Renamed aftercare mark: old={e.OldFullPath} new={e.FullPath}");
                NoteFutureSharePolicyRepair(
                    e.FullPath,
                    Path.GetDirectoryName(e.FullPath) ?? _currentFolder ?? string.Empty,
                    SharePolicyRepairReason.ExternalRenamed);
            }
            else
            {
                SwkLogger.Debug($"ContentsSensor_Renamed suppressed: old={e.OldFullPath} new={e.FullPath}");
            }
            RefreshShopItems();
        });
    }

    private void NoteFutureSharePolicyRepair(
        string affectedPath,
        string policySourceFolder,
        SharePolicyRepairReason reason)
    {
        if (string.IsNullOrWhiteSpace(_shopFolder) ||
            string.IsNullOrWhiteSpace(affectedPath) ||
            string.IsNullOrWhiteSpace(policySourceFolder) ||
            !IsUnderFolder(affectedPath, _shopFolder) ||
            IsHoldFolderPath(affectedPath))
        {
            return;
        }

        if (reason == SharePolicyRepairReason.ExternalCreated && File.Exists(affectedPath))
        {
            SwkLogger.Info(
                $"Trace.ExternalFlow.Receive.Aftercare: action=TryRegisterExternalReceive source=Aftercare.ExternalCreated path={affectedPath} folder={policySourceFolder}");
            TryRegisterExternalReceive(affectedPath, "Aftercare.ExternalCreated");
        }

        _pipeClient.MarkActionAftercare(_shopFolder, affectedPath, policySourceFolder, reason);
    }

    private void ContentsSensor_Error(object sender, ErrorEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            EnsureHoldFolderForShopChange(notifyWhenRecreated: true);
            RefreshShopItems();
        });
    }

    private void RefreshShopItemsIfCurrentFolder(string folderPath)
    {
        if (!string.IsNullOrWhiteSpace(_currentFolder) &&
            string.Equals(Path.GetFullPath(_currentFolder), Path.GetFullPath(folderPath), StringComparison.OrdinalIgnoreCase))
        {
            RefreshShopItems();
        }
    }

    private void UpdateNavigationState()
    {
        BackButton.IsEnabled = _backStack.Count > 0;
        ForwardButton.IsEnabled = _forwardStack.Count > 0;
    }

    private (List<string> Users, bool IsReadOnly, bool IsSharedOff)? FindEffectiveAncestorPermission(string folderPath)
    {
        string? root = GetCurrentRootPath();
        string? p = folderPath;
        while (!string.IsNullOrEmpty(p))
        {
            if (_permissionMap.TryGetValue(p, out var perm) && (perm.Users.Count > 0 || perm.IsReadOnly || perm.IsSharedOff))
                return perm;
            if (root != null && string.Equals(p, root, StringComparison.OrdinalIgnoreCase))
                break;
            string? parent = Path.GetDirectoryName(p);
            if (parent == null || string.Equals(parent, p, StringComparison.OrdinalIgnoreCase))
                break;
            p = parent;
        }
        return null;
    }

    private static int PermissionLevel((List<string> Users, bool IsReadOnly, bool IsSharedOff)? perm)
    {
        if (perm == null) return 0;
        var (users, isReadOnly, isSharedOff) = perm.Value;
        if (isSharedOff) return 4;
        if (users.Count > 0 && isReadOnly) return 3;
        if (users.Count > 0) return 2;
        if (isReadOnly) return 1;
        return 0;
    }

    // item の権限が dest フォルダーの範囲内（同じか制限が強い）なら true。
    // ユーザー範囲とアクセス種別の両方で判定する。
    // true  → そのまま維持  /  false → 移動先フォルダーに揃える
    private static bool IsWithinRange(
        (List<string> Users, bool IsReadOnly, bool IsSharedOff) item,
        (List<string> Users, bool IsReadOnly, bool IsSharedOff)? dest)
    {
        if (dest == null) return true;
        var (destUsers, destReadOnly, destOff) = dest.Value;
        var (itemUsers, itemReadOnly, itemOff) = item;
        if (itemOff) return true;
        if (destOff) return false;
        if (!itemReadOnly && destReadOnly) return false;
        if (destUsers.Count == 0) return true;
        if (itemUsers.Count == 0) return false;

        var destUserSet = new HashSet<string>(destUsers, StringComparer.OrdinalIgnoreCase);
        return itemUsers.All(destUserSet.Contains);
    }

    private bool PreservePermissionOnArrival(
        string sourcePath,
        string sourceParent,
        string destinationPath,
        string destinationFolder,
        bool removeSourceEntry,
        string historySource)
    {
        bool hasOwnEntry = _permissionMap.TryGetValue(sourcePath, out var ownPerm)
            && (ownPerm.Users.Count > 0 || ownPerm.IsReadOnly || ownPerm.IsSharedOff);

        (List<string> Users, bool IsReadOnly, bool IsSharedOff)? effectiveSource =
            GetDisplayedPermissionForPath(sourcePath, sourceParent);

        if (effectiveSource == null)
        {
            var destPermForNull = FindEffectiveAncestorPermission(destinationFolder);
            if (destPermForNull != null)
                AppendPermissionChangedByMoveHistory(sourcePath, destinationPath, destinationFolder, null, destPermForNull.Value, historySource);
            if (removeSourceEntry && hasOwnEntry && _permissionMap.Remove(sourcePath))
            {
                MoveHoldDisplayPermission(sourcePath, destinationPath);
                SavePermissionMap();
            }
            return false;
        }

        var effectiveDest = FindEffectiveAncestorPermission(destinationFolder);

        if (IsWithinRange(effectiveSource.Value, effectiveDest))
        {
            if (removeSourceEntry)
            {
                _permissionMap.Remove(sourcePath);
                MoveHoldDisplayPermission(sourcePath, destinationPath);
            }

            _permissionMap[destinationPath] = effectiveSource.Value;
            SavePermissionMap();
            string capturedDest = destinationPath;
            var capturedPerm = effectiveSource.Value;
            _ = Task.Run(() => _pipeClient.SetSubfolderPermission(capturedDest, capturedPerm.IsSharedOff, capturedPerm.IsReadOnly));
            return true;
        }

        if (removeSourceEntry)
        {
            if (hasOwnEntry)
            {
                _permissionMap.Remove(sourcePath);
            }

            MoveHoldDisplayPermission(sourcePath, destinationPath);
        }

        if (effectiveDest == null)
        {
            SavePermissionMap();
            return false;
        }

        _permissionMap[destinationPath] = effectiveDest.Value;
        SavePermissionMap();
        AppendPermissionChangedByMoveHistory(sourcePath, destinationPath, destinationFolder, effectiveSource, effectiveDest.Value, historySource);

        string enforcedDestPath = destinationPath;
        var enforcedDestPerm = effectiveDest.Value;
        _ = Task.Run(() => _pipeClient.SetSubfolderPermission(enforcedDestPath, enforcedDestPerm.IsSharedOff, enforcedDestPerm.IsReadOnly));
        return true;
    }

    private void AppendPermissionChangedByMoveHistory(
        string sourcePath,
        string destinationPath,
        string destinationFolder,
        (List<string> Users, bool IsReadOnly, bool IsSharedOff)? beforePerm,
        (List<string> Users, bool IsReadOnly, bool IsSharedOff) afterPerm,
        string source)
    {
        string fileName = Path.GetFileName(destinationPath);
        string beforeStatus = PermissionToStatusText(beforePerm);
        string afterStatus = PermissionToStatusText(afterPerm);
        string destFolderName = Path.GetFileName(destinationFolder.TrimEnd(Path.DirectorySeparatorChar)) ?? destinationFolder;
        AppendHistory(
            HistoryChannel.Update,
            $"{fileName} の共有設定が移動先のフォルダー設定を引き継ぎました。（{beforeStatus} → {afterStatus}）",
            eventType: "PermissionChanged",
            outcome: HistoryOutcome.Info,
            targetName: fileName,
            pathText: destinationFolder,
            sourcePath: sourcePath,
            destinationPath: destinationPath,
            note: $"移動先: {destFolderName}（{afterStatus}）",
            source: source);
    }

    private void MaybeAppendPermissionInheritedOnArrival(string destinationPath, string destinationFolder, string source)
    {
        var destPerm = FindEffectiveAncestorPermission(destinationFolder);
        if (destPerm == null) return;
        string fileName = Path.GetFileName(destinationPath);
        string afterStatus = PermissionToStatusText(destPerm);
        string destFolderName = Path.GetFileName(destinationFolder.TrimEnd(Path.DirectorySeparatorChar)) ?? destinationFolder;
        AppendHistory(
            HistoryChannel.Update,
            $"{fileName} の共有設定が配置先のフォルダー設定を引き継ぎました。（全員 → {afterStatus}）",
            eventType: "PermissionChanged",
            outcome: HistoryOutcome.Info,
            targetName: fileName,
            pathText: destinationFolder,
            sourcePath: destinationPath,
            destinationPath: destinationPath,
            note: $"配置先: {destFolderName}（{afterStatus}）",
            source: source);
    }

    private static string PermissionToStatusText((List<string> Users, bool IsReadOnly, bool IsSharedOff)? perm)
    {
        if (perm == null) return "全員";
        var (users, isReadOnly, isSharedOff) = perm.Value;
        if (isSharedOff) return "OFF";
        if (isReadOnly) return users.Count > 0 ? "指定R" : "全員R";
        return users.Count > 0 ? "指定" : "全員";
    }

    private void UpdateBreadcrumb()
    {
        _breadcrumbFullText = BuildRelativeLocationText();
        UpdateBreadcrumbDisplay();
    }

    private string BuildRelativeLocationText()
    {
        if (string.IsNullOrWhiteSpace(_currentFolder)) return string.Empty;

        string? rootPath = GetCurrentRootPath();
        if (string.IsNullOrWhiteSpace(rootPath)) return string.Empty;
        string rootLabel = GetRootDisplayLabel(rootPath);
        if (string.IsNullOrWhiteSpace(rootLabel)) return string.Empty;

        try
        {
            string root = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string current = _currentFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(root, current, StringComparison.OrdinalIgnoreCase)) return rootLabel;

            if (current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                string relative = current[(root.Length + 1)..];
                string[] segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                return segments.Length == 0
                    ? rootLabel
                    : $"{rootLabel} / {string.Join(" / ", segments)}";
            }

            return rootLabel;
        }
        catch
        {
            return rootLabel;
        }
    }

    private static string GetRootDisplayLabel(string rootPath)
    {
        try
        {
            string normalized = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(normalized);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            string root = Path.GetPathRoot(normalized) ?? normalized;
            return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return rootPath;
        }
    }

    private string? GetCurrentRootPath()
    {
        if (_currentMode == DisplayMode.FriendShop && _activeFriendShop != null)
            return string.IsNullOrWhiteSpace(_activeFriendShopRootPath)
                ? _activeFriendShop.ConnectUncPath
                : _activeFriendShopRootPath;
        return string.IsNullOrWhiteSpace(_shopFolder) ? null : Path.GetFullPath(_shopFolder);
    }

    private void UpdateBreadcrumbDisplay()
    {
        SyncDropdownToCurrentMode();
        CurrentPathTextBlock.Text = _breadcrumbFullText;
        CurrentPathTextBlock.ToolTip = string.IsNullOrWhiteSpace(_currentFolder) ? null : _currentFolder;
    }

    private void SyncDropdownToCurrentMode()
    {
        _suppressDropdownChange = true;
        try
        {
            if (_currentMode == DisplayMode.FriendShop && _activeFriendShop != null)
            {
                foreach (object item in ExplorerTargetComboBox.Items)
                {
                    if (item is ExplorerTarget t && t.Friend?.Id == _activeFriendShop.Id)
                    {
                        ExplorerTargetComboBox.SelectedItem = item;
                        return;
                    }
                }
            }
            if (ExplorerTargetComboBox.Items.Count > 0)
                ExplorerTargetComboBox.SelectedIndex = 0;
        }
        finally
        {
            _suppressDropdownChange = false;
        }
    }

    private void PopulateExplorerDropdown()
    {
        _suppressDropdownChange = true;
        try
        {
            ExplorerTargetComboBox.Items.Clear();
            ExplorerTargetComboBox.Items.Add(new ExplorerTarget("自分の共有", null, null));

            IReadOnlyList<Friend> friends = FriendsRepository.LoadAll();
            foreach (Friend f in friends
                .OrderBy(f => string.IsNullOrWhiteSpace(f.DisplayName) ? f.HostMachineName : f.DisplayName,
                         StringComparer.CurrentCultureIgnoreCase))
            {
                string label = string.IsNullOrWhiteSpace(f.DisplayName) ? f.HostMachineName : f.DisplayName;
                ExplorerTargetComboBox.Items.Add(new ExplorerTarget(label, f, FindLiveShopInfo(f)));
            }

            SyncDropdownToCurrentMode();
        }
        finally
        {
            _suppressDropdownChange = false;
        }
    }

    private async void ExplorerTargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDropdownChange) return;
        if (ExplorerTargetComboBox.SelectedItem is not ExplorerTarget target) return;

        SwkLogger.Debug($"ExplorerTargetComboBox_SelectionChanged: {target.Label}");
        SwkLogger.Info(
            $"Investigation.ExplorerTargetChanged: label={target.Label} friendId={target.Friend?.Id ?? "-"} " +
            $"friendHost={target.Friend?.HostMachineName ?? "-"} share={target.Friend?.ShareName ?? "-"}");

        if (target.Friend is not Friend friend)
        {
            StopFriendShopPolling();
            _activeFriendShop = null;
            _activeFriendShopRootPath = null;
            _missingFriendShopStatus = null;
            EnterShopMode();
            return;
        }

        await NavigateToFriendShopAsync(friend, target.ShopInfo);
    }

    private static List<string> BuildFriendUncCandidates(Friend friend, SwkNotificationListener.ShopInfo liveShop)
    {
        List<string> candidates = [];
        if (!string.IsNullOrWhiteSpace(liveShop.IpAddress))
        {
            AddFriendUncCandidate(candidates, liveShop.IpAddress, liveShop.ShareName);
        }

        AddFriendUncCandidate(candidates, liveShop.MachineName, liveShop.ShareName);
        AddFriendUncCandidate(candidates, friend.HostMachineName, liveShop.ShareName);
        return candidates;
    }

    private static void AddFriendUncCandidate(List<string> candidates, string? host, string shareName)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(shareName))
        {
            return;
        }

        string uncPath = $@"\\{host.TrimStart('\\')}\{shareName}";
        if (!candidates.Contains(uncPath, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(uncPath);
        }
    }

    private static SwkNotificationListener.ShopInfo? FindLiveShopInfo(Friend friend)
    {
        if (!string.IsNullOrWhiteSpace(friend.RemoteSwkInstanceId))
        {
            SwkNotificationListener.ShopInfo? byId = SwkNetworkCache.ShopInfos.FirstOrDefault(s =>
                SameSwkInstance(friend, s) &&
                string.Equals(s.ShareName, friend.ShareName, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId;

            SwkNotificationListener.ShopInfo? hostShareFallback = FindLiveShopByHostAndShare(friend, SwkNetworkCache.ShopInfos);
            return string.IsNullOrWhiteSpace(hostShareFallback?.SwkInstanceId) ? hostShareFallback : null;
        }

        return FindLiveShopByHostAndShare(friend, SwkNetworkCache.ShopInfos);
    }

    private static bool IsCompatibleLiveShopForFriend(Friend friend, SwkNotificationListener.ShopInfo liveShop)
    {
        if (!string.IsNullOrWhiteSpace(friend.RemoteSwkInstanceId) &&
            !string.IsNullOrWhiteSpace(liveShop.SwkInstanceId))
        {
            return SameSwkInstance(friend, liveShop) &&
                   string.Equals(friend.ShareName, liveShop.ShareName, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(NormalizeHostName(friend.HostMachineName), NormalizeHostName(liveShop.MachineName), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(friend.ShareName, liveShop.ShareName, StringComparison.OrdinalIgnoreCase);
    }

    private static SwkNotificationListener.ShopInfo? FindLiveShopByHostAndShare(
        Friend friend,
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos)
    {
        string normalizedHost = NormalizeHostName(friend.HostMachineName);
        SwkNotificationListener.ShopInfo? exact = shopInfos.FirstOrDefault(s =>
            string.Equals(NormalizeHostName(s.MachineName), normalizedHost, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.ShareName, friend.ShareName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        return null;
    }

    private static string NormalizeHostName(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        string trimmed = host.Trim();
        int dot = trimmed.IndexOf('.');
        return dot > 0 ? trimmed[..dot] : trimmed;
    }

    private static bool SameSwkInstance(Friend friend, SwkNotificationListener.ShopInfo shopInfo) =>
        !string.IsNullOrWhiteSpace(friend.RemoteSwkInstanceId) &&
        !string.IsNullOrWhiteSpace(shopInfo.SwkInstanceId) &&
        string.Equals(friend.RemoteSwkInstanceId, shopInfo.SwkInstanceId, StringComparison.OrdinalIgnoreCase);

    private static async Task<SwkNotificationListener.ShopInfo?> ResolveLiveFriendShopAsync(Friend friend)
    {
        SwkNotificationListener.ShopInfo? hit = FindLiveShopInfo(friend);
        if (hit is not null)
        {
            return hit;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await SwkNetworkCache.RefreshAsync(ScanMode.Quick, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SwkLogger.Warn($"ResolveLiveFriendShopAsync scan failed: {ex.Message}");
        }

        return FindLiveShopInfo(friend);
    }

    private static void UpdateFriendExternalState(Friend friend, SwkNotificationListener.ShopInfo liveShop)
    {
        if (!IsCompatibleLiveShopForFriend(friend, liveShop))
        {
            SwkLogger.Warn(
                $"UpdateFriendExternalState skipped incompatible shop: friend={friend.Id}/{friend.DisplayName} " +
                $"friendHost={friend.HostMachineName} friendShare={friend.ShareName} friendInstance={friend.RemoteSwkInstanceId ?? "-"} " +
                $"shopHost={liveShop.MachineName} shopShare={liveShop.ShareName} shopInstance={liveShop.SwkInstanceId ?? "-"}");
            return;
        }

        string nowIso = DateTime.UtcNow.ToString("o");
        friend.LastKnownAddress = liveShop.IpAddress ?? string.Empty;
        friend.LastFoundAt = nowIso;
        friend.LastCheckedAt = nowIso;
        friend.LastSeenAt = nowIso;
        if (!string.IsNullOrWhiteSpace(liveShop.SwkInstanceId))
        {
            friend.RemoteSwkInstanceId = liveShop.SwkInstanceId;
        }

        var all = FriendsRepository.LoadAll().ToList();
        Friend? stored = all.FirstOrDefault(f => f.Id == friend.Id);
        if (stored is null)
        {
            return;
        }

        stored.PasswordProtected = friend.PasswordProtected;
        stored.OwnerCertThumbprint = friend.OwnerCertThumbprint;
        stored.RemoteSwkInstanceId = friend.RemoteSwkInstanceId;
        stored.LastKnownAddress = friend.LastKnownAddress;
        stored.LastFoundAt = friend.LastFoundAt;
        stored.LastCheckedAt = friend.LastCheckedAt;
        stored.LastSeenAt = friend.LastSeenAt;
        stored.LastAccessIssue = friend.LastAccessIssue;
        stored.LastShareAccessVerifiedAt = friend.LastShareAccessVerifiedAt;
        stored.LastShareAccessHost = friend.LastShareAccessHost;
        stored.LastShareAccessSwkInstanceId = friend.LastShareAccessSwkInstanceId;
        FriendsRepository.SaveAll(all);
    }

    private static void PersistFriendAccessIssue(Friend friend, string? issue)
    {
        if (string.Equals(friend.LastAccessIssue, issue, StringComparison.Ordinal))
        {
            return;
        }

        friend.LastAccessIssue = issue;
        var all = FriendsRepository.LoadAll().ToList();
        Friend? stored = all.FirstOrDefault(f => f.Id == friend.Id);
        if (stored is null)
        {
            return;
        }

        stored.LastAccessIssue = issue;
        FriendsRepository.SaveAll(all);
    }

    private async Task NavigateToFriendShopAsync(Friend friend, SwkNotificationListener.ShopInfo? knownLiveShop = null)
    {
        string label = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName;
        SwkLogger.Debug($"NavigateToFriendShopAsync: {label} ({friend.ConnectUncPath})");
        SwkLogger.Info(
            $"Investigation.NavigateToFriendShopAsync.Start: friend={label} host={friend.HostMachineName} " +
            $"share={friend.ShareName} knownLiveShop={(knownLiveShop is null ? "no" : "yes")}");

        _activeFriendShop = friend;
        _activeFriendShopRootPath = null;
        _friendShopLiveMissCount = 0;
        _missingFriendShopStatus = null;
        _currentMode = DisplayMode.FriendShop;
        CancelFolderSizeCalculation();
        DisposeContentsWatcher();
        _currentFolder = null;
        ShopItems.Clear();
        _friendShopReadOnlyState.Clear();
        _backStack.Clear();
        _forwardStack.Clear();
        UpdateBreadcrumb();
        UpdateNavigationState();

        SwkNotificationListener.ShopInfo? liveShop = knownLiveShop;
        if (liveShop is not null && !IsCompatibleLiveShopForFriend(friend, liveShop))
        {
            SwkLogger.Warn(
                $"NavigateToFriendShopAsync ignored incompatible knownLiveShop: friend={friend.Id}/{friend.DisplayName} " +
                $"friendHost={friend.HostMachineName} friendShare={friend.ShareName} friendInstance={friend.RemoteSwkInstanceId ?? "-"} " +
                $"shopHost={liveShop.MachineName} shopShare={liveShop.ShareName} shopInstance={liveShop.SwkInstanceId ?? "-"}");
            liveShop = null;
        }

        liveShop ??= await ResolveLiveFriendShopAsync(friend);
        if (liveShop is null)
        {
            SetTransientStatus("接続できません");
            return;
        }

        List<string> uncCandidates = BuildFriendUncCandidates(friend, liveShop);
        if (uncCandidates.Count == 0)
        {
            SetTransientStatus("接続できません");
            return;
        }

        SwkLogger.Debug($"NavigateToFriendShopAsync: candidates={string.Join(", ", uncCandidates)}");
        string password = FriendsRepository.UnprotectPassword(friend.PasswordProtected);
        string? accessiblePath = await TryFindAccessibleFriendPathAsync(uncCandidates, friend, liveShop, password);
        string? refreshBlockedMessage = null;
        if (string.IsNullOrWhiteSpace(accessiblePath) &&
            TryBeginFriendReconnectRefresh(friend, out refreshBlockedMessage))
        {
            bool refreshed = false;
            try
            {
                refreshed = await TryRefreshFriendPasswordAsync(friend, liveShop);
            }
            finally
            {
                CompleteFriendReconnectRefresh(friend, refreshed);
            }

            if (refreshed)
            {
                password = FriendsRepository.UnprotectPassword(friend.PasswordProtected);
                accessiblePath = await TryFindAccessibleFriendPathAsync(uncCandidates, friend, liveShop, password);
            }
        }
        else if (string.IsNullOrWhiteSpace(accessiblePath) && !string.IsNullOrWhiteSpace(refreshBlockedMessage))
        {
            SetTransientStatus(refreshBlockedMessage);
        }

        if (string.IsNullOrWhiteSpace(accessiblePath))
        {
            SwkLogger.Warn($"NavigateToFriendShopAsync: not accessible: {string.Join(", ", uncCandidates)}");
            SwkLogger.Info(
                $"Investigation.NavigateToFriendShopAsync.Fail: friend={label} candidates={string.Join(" | ", uncCandidates)}");
            FriendShareAccessTracker.ClearVerified(friend);
            UpdateFriendExternalState(friend, liveShop);
            AppendHistory(
                HistoryChannel.Access,
                $"{label} に接続できませんでした。",
                "Connect",
                HistoryOutcome.Failure,
                HistoryDirection.Outgoing,
                friend,
                targetName: label,
                pathText: string.Join(", ", uncCandidates),
                source: "MainWindow");
            SetTransientStatus("接続できません");
            return;
        }

        SwkLogger.Debug($"NavigateToFriendShopAsync: resolved={accessiblePath}");
        SwkLogger.Info($"Investigation.NavigateToFriendShopAsync.Success: friend={label} resolved={accessiblePath}");
        FriendShareAccessTracker.MarkVerified(friend, liveShop);
        UpdateFriendExternalState(friend, liveShop);
        _activeFriendShopRootPath = accessiblePath;
        bool hadMissingStatus = _missingFriendShopStatus != null;
        _missingFriendShopStatus = null;
        if (hadMissingStatus)
        {
            UpdateShopState(_isShopOpen);
        }
        SuppressExternalChangeNotifications();
        NavigateTo(accessiblePath, addHistory: false, clearForward: true);
        AppendHistory(
            HistoryChannel.Access,
            $"{label} のお店に入りました。",
            "Connect",
            HistoryOutcome.Success,
            HistoryDirection.Outgoing,
            friend,
            targetName: liveShop.ShareName,
            pathText: accessiblePath,
            source: "MainWindow");
        await ApplyFriendShopReadOnlyAsync(accessiblePath, ShopItems.ToList(), silent: true);
    }

    private static Task<string?> TryFindAccessibleFriendPathAsync(
        IReadOnlyList<string> uncCandidates,
        Friend friend,
        SwkNotificationListener.ShopInfo liveShop,
        string password)
    {
        return Task.Run(() =>
        {
            foreach (string candidate in uncCandidates)
            {
                SwkLogger.Debug($"NavigateToFriendShopAsync: trying={candidate}");
                if (!string.IsNullOrEmpty(password))
                    SmbConnectionHelper.EnsureConnection(candidate, friend.UserName, password, liveShop.MachineName);
                try
                {
                    if (Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            return null;
        });
    }

    private static async Task<bool> TryRefreshFriendPasswordAsync(
        Friend friend,
        SwkNotificationListener.ShopInfo liveShop)
    {
        if (!IsCompatibleLiveShopForFriend(friend, liveShop))
        {
            SwkLogger.Warn(
                $"TryRefreshFriendPasswordAsync skipped incompatible shop: friend={friend.Id}/{friend.DisplayName} " +
                $"friendHost={friend.HostMachineName} friendShare={friend.ShareName} friendInstance={friend.RemoteSwkInstanceId ?? "-"} " +
                $"shopHost={liveShop.MachineName} shopShare={liveShop.ShareName} shopInstance={liveShop.SwkInstanceId ?? "-"}");
            return false;
        }

        if (string.IsNullOrWhiteSpace(friend.OwnerCertThumbprint))
        {
            SwkLogger.Warn($"TryRefreshFriendPasswordAsync skipped: no pinned certificate for friend id={friend.Id}");
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var listener = new SwkNotificationListener();
            SwkNotificationListener.InviteCodeResult result = await listener.RequestInviteCodeAsync(
                liveShop,
                inviteId: null,
                expectedThumbprint: friend.OwnerCertThumbprint,
                cts.Token);

            if (!result.Success || string.IsNullOrEmpty(result.Password))
            {
                if (string.Equals(result.ErrorMessage, OwnerCertificateMismatchMessage, StringComparison.Ordinal))
                {
                    PersistFriendAccessIssue(friend, Friend.AccessIssueCertMismatch);
                }
                FriendShareAccessTracker.ClearVerified(friend);
                SwkLogger.Warn($"TryRefreshFriendPasswordAsync failed: {result.ErrorMessage ?? "empty password"}");
                return false;
            }

            friend.PasswordProtected = FriendsRepository.ProtectPassword(result.Password);
            if (!string.IsNullOrWhiteSpace(result.CertThumbprint))
            {
                friend.OwnerCertThumbprint = result.CertThumbprint;
            }
            if (!string.IsNullOrWhiteSpace(result.SwkInstanceId))
            {
                friend.RemoteSwkInstanceId = result.SwkInstanceId;
            }
            friend.LastAccessIssue = null;
            FriendShareAccessTracker.ClearVerified(friend);
            UpdateFriendExternalState(friend, liveShop);
            SwkLogger.Info($"TryRefreshFriendPasswordAsync: refreshed stored SMB password for friend id={friend.Id}");
            return true;
        }
        catch (OperationCanceledException)
        {
            FriendShareAccessTracker.ClearVerified(friend);
            SwkLogger.Warn("TryRefreshFriendPasswordAsync timed out");
            return false;
        }
        catch (Exception ex)
        {
            FriendShareAccessTracker.ClearVerified(friend);
            SwkLogger.Warn($"TryRefreshFriendPasswordAsync failed: {ex.Message}");
            return false;
        }
    }

    private bool TryBeginFriendReconnectRefresh(Friend friend, out string? blockedMessage)
    {
        blockedMessage = null;

        if (_friendRefreshInFlight.Contains(friend.Id))
        {
            blockedMessage = "接続情報を確認中です。少し待ってからもう一度確認します。";
            return false;
        }

        if (_friendRefreshCooldownUntil.TryGetValue(friend.Id, out DateTime cooldownUntilUtc))
        {
            DateTime nowUtc = DateTime.UtcNow;
            if (cooldownUntilUtc > nowUtc)
            {
                blockedMessage = "接続を確認中です。しばらく待ってから再試行します。";
                return false;
            }

            _friendRefreshCooldownUntil.Remove(friend.Id);
        }

        _friendRefreshInFlight.Add(friend.Id);
        return true;
    }

    private void CompleteFriendReconnectRefresh(Friend friend, bool success)
    {
        _friendRefreshInFlight.Remove(friend.Id);
        if (success)
        {
            _friendRefreshCooldownUntil.Remove(friend.Id);
            return;
        }

        _friendRefreshCooldownUntil[friend.Id] = DateTime.UtcNow + FriendReconnectRetryCooldown;
    }

    private void TopButton_Click(object sender, RoutedEventArgs e)
    {
        SwkLogger.Debug($"TopButton_Click: mode={_currentMode}");
        if (_currentMode == DisplayMode.FriendShop && _activeFriendShop != null)
        {
            _ = NavigateToFriendShopAsync(_activeFriendShop);
        }
        else
        {
            NavigateToShopRoot(addHistory: true);
        }
    }

    private void AccessHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        ShowHistoryDialog(HistoryChannel.Access, "アクセス履歴");
    }

    private void UpdateHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        ShowHistoryDialog(HistoryChannel.Update, "更新履歴");
    }

    private void NotificationHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        ShowHistoryDialog(HistoryChannel.Notification, "通知履歴");
    }

    private void StartFolderSizeCalculation()
    {
        CancelFolderSizeCalculation();
        if (!_isSizeCalcEnabled)
        {
            return;
        }

        List<ShopItem> targets = ShopItems
            .Where(item => item.IsDirectory && item.SizeBytes is null)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        CancellationTokenSource cts = new();
        _folderSizeCancellation = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (ShopItem item in targets)
                {
                    if (cts.IsCancellationRequested)
                    {
                        break;
                    }

                    await Dispatcher.InvokeAsync(() => item.SetSizeCalculating());

                    long size;
                    try
                    {
                        size = CalculateFolderSize(item.FullPath, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (cts.IsCancellationRequested)
                    {
                        break;
                    }

                    _folderSizeCache[item.FullPath] = size;
                    await Dispatcher.InvokeAsync(() => item.SetSize(size));
                }
            }
            catch
            {
            }
        }, cts.Token);
    }

    private static long CalculateFolderSize(string path, CancellationToken token)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    FileInfo info = new(file);
                    total += info.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return total;
    }

    private void CancelFolderSizeCalculation()
    {
        if (_folderSizeCancellation is null)
        {
            return;
        }

        try
        {
            _folderSizeCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _folderSizeCancellation.Dispose();
        _folderSizeCancellation = null;
    }

    private void StartSubfolderCountLoad()
    {
        CancelSubfolderCountLoad();

        List<ShopItem> targets = ShopItems
            .Where(item => item.IsDirectory && !item.IsHoldFolder)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        CancellationTokenSource cts = new();
        _subfolderCountCancellation = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (ShopItem item in targets)
                {
                    if (cts.IsCancellationRequested) break;

                    int count;
                    try
                    {
                        count = CountSubfoldersUpTo10(item.FullPath);
                    }
                    catch
                    {
                        continue;
                    }

                    if (cts.IsCancellationRequested) break;

                    _subfolderCountCache[item.FullPath] = count;
                    await Dispatcher.InvokeAsync(() => item.SetSubfolderCount(count));
                }
            }
            catch
            {
            }
        }, cts.Token);
    }

    private static int CountSubfoldersUpTo10(string path)
    {
        int count = 0;
        foreach (string _ in Directory.EnumerateDirectories(path))
        {
            count++;
            if (count >= 10) return count;
        }
        return count;
    }

    private void CancelSubfolderCountLoad()
    {
        if (_subfolderCountCancellation is null) return;
        try
        {
            _subfolderCountCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _subfolderCountCancellation.Dispose();
        _subfolderCountCancellation = null;
    }

    private void ClearFolderSizeDisplay()
    {
        foreach (ShopItem item in ShopItems)
        {
            if (item.IsDirectory)
            {
                item.ClearSize();
            }
        }
    }

    private void InvalidateSizeCacheUnder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return;
        }

        string prefix = fullPath + Path.DirectorySeparatorChar;
        List<string> toRemove = _folderSizeCache.Keys
            .Where(key => string.Equals(key, fullPath, StringComparison.OrdinalIgnoreCase) ||
                          key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (string key in toRemove)
        {
            _folderSizeCache.Remove(key);
        }
    }

    private void InvalidateSubfolderCountCacheUnder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return;
        }

        string prefix = fullPath + Path.DirectorySeparatorChar;
        List<string> toRemove = _subfolderCountCache.Keys
            .Where(key => string.Equals(key, fullPath, StringComparison.OrdinalIgnoreCase) ||
                          key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (string key in toRemove)
        {
            _subfolderCountCache.Remove(key);
        }
    }

    private void SetTransientStatus(string message)
    {
        StatusTextBlock.Text = message;
        OpenStatusTextBlock.Text = message;
        _transientStatusTimer.Stop();
        _transientStatusTimer.Start();
    }

    private void TransientStatusTimer_Tick(object? sender, EventArgs e)
    {
        _transientStatusTimer.Stop();
        RestoreBaseStatus();
    }

    private void RestoreBaseStatus()
    {
        UpdateShopState(_isShopOpen);
    }

    private bool CanAcceptDrop(System.Windows.DragEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentFolder) || !Directory.Exists(_currentFolder))
        {
            return false;
        }

        bool hasInternalDragData = HasInternalDraggedPaths(e.Data);
        bool hasSupportedData =
            hasInternalDragData ||
            e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop);

        if (!hasSupportedData)
        {
            return false;
        }

        ShopItem? hoveredItem = GetRawDropDestinationItem(e);
        ShopItem? destinationItem = GetDropDestinationItem(e);
        if (hasInternalDragData)
        {
            if (hoveredItem is not null && destinationItem is null)
            {
                return false;
            }

            return destinationItem is not null && Directory.Exists(destinationItem.FullPath);
        }

        string? destination = destinationItem?.FullPath ?? _dropTargetItem?.FullPath ?? _currentFolder;
        if (string.IsNullOrWhiteSpace(destination) || !Directory.Exists(destination))
        {
            return false;
        }

        if (_currentMode == DisplayMode.FriendShop &&
            e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            return IsDirectoryWritable(destination);
        }

        return true;
    }

    private System.Windows.DragDropEffects GetDropEffect(System.Windows.DragEventArgs e)
    {
        if (!CanAcceptDrop(e))
        {
            return System.Windows.DragDropEffects.None;
        }

        bool hasInternalDragData = HasInternalDraggedPaths(e.Data);
        string? destinationFolder = hasInternalDragData
            ? GetDropDestinationFolder(e) ?? _dropTargetItem?.FullPath
            : GetDropDestinationFolder(e) ?? _dropTargetItem?.FullPath ?? _currentFolder;
        if (!string.IsNullOrWhiteSpace(destinationFolder) && IsHoldFolderPath(destinationFolder))
        {
            return System.Windows.DragDropEffects.Move;
        }

        if (hasInternalDragData)
        {
            if (string.IsNullOrWhiteSpace(destinationFolder) ||
                !IsInternalDropTargetAllowed(destinationFolder, e.Data, e.KeyStates))
            {
                return System.Windows.DragDropEffects.None;
            }

            return (e.KeyStates & DragDropKeyStates.ControlKey) != 0
                ? System.Windows.DragDropEffects.Copy
                : System.Windows.DragDropEffects.Move;
        }

        return System.Windows.DragDropEffects.Copy;
    }

    // クリック位置がリネーム TextBox の上かどうかを判定する
    private static bool IsClickOnRenameTextBox(DependencyObject? source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is System.Windows.Controls.TextBox) return true;
            if (current is System.Windows.Controls.ListViewItem) return false;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    // アイコン・ファイル名など実コンテンツ上のクリックかどうかを判定する
    // 行余白（ListViewItem の背景部分）は false を返す
    private static bool IsClickOnItemContent(DependencyObject? source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is TextBlock or System.Windows.Controls.Image or System.Windows.Shapes.Shape)
                return true;
            if (current is System.Windows.Controls.ListViewItem)
                return false;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static bool IsClickOnItemButton(DependencyObject? source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Button)
            {
                return true;
            }

            if (current is System.Windows.Controls.ListViewItem)
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private string? GetDropDestinationFolder(System.Windows.DragEventArgs e)
    {
        return GetDropDestinationItem(e)?.FullPath;
    }

    private void UpdateDropTargetHighlight(System.Windows.DragEventArgs e)
    {
        if (!HasInternalDraggedPaths(e.Data) &&
            !e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            ClearDropTargetHighlight();
            return;
        }

        ShopItem? target = GetDropDestinationItem(e);
        if (ReferenceEquals(target, _dropTargetItem))
        {
            return;
        }

        ClearDropTargetHighlight();
        if (target is not null)
        {
            target.IsDropTarget = true;
            _dropTargetItem = target;
        }
    }

    private ShopItem? GetDropDestinationItem(System.Windows.DragEventArgs e)
    {
        ShopItem? target = GetRawDropDestinationItem(e);
        return IsValidDropTargetItem(target, e.Data, e.KeyStates) ? target : null;
    }

    private static ShopItem? GetRawDropDestinationItem(System.Windows.DragEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null)
        {
            if (current is System.Windows.Controls.ListViewItem { DataContext: ShopItem { IsDirectory: true } item })
            {
                return item;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private ShopItem? GetOleDropDestinationItemFromScreenPoint(
        System.Windows.Point screenPoint,
        Forms.IDataObject? data,
        DragDropKeyStates keyStates)
    {
        if (!ShopItemsListView.IsVisible)
        {
            return null;
        }

        System.Windows.Point localPoint = ShopItemsListView.PointFromScreen(screenPoint);
        if (double.IsNaN(localPoint.X) || double.IsNaN(localPoint.Y))
        {
            return null;
        }

        HitTestResult? hit = VisualTreeHelper.HitTest(ShopItemsListView, localPoint);
        ShopItem? item = GetShopItemFromSource(hit?.VisualHit as DependencyObject);
        return IsValidDropTargetItem(item, data, keyStates) ? item : null;
    }

    internal string? ResolveOleInternalDropDestinationFromScreenPoint(
        System.Windows.Point screenPoint,
        Forms.IDataObject data,
        DragDropKeyStates keyStates)
    {
        return GetOleDropDestinationItemFromScreenPoint(screenPoint, data, keyStates)?.FullPath;
    }

    private bool HasInternalDraggedPaths(System.Windows.IDataObject data)
    {
        if (_activeInternalDragPaths is { Length: > 0 })
        {
            return true;
        }

        return data.GetDataPresent(InternalDragPathFormat) || data.GetDataPresent(InternalDragPathsFormat);
    }

    private string[] GetInternalDraggedPaths(System.Windows.IDataObject data)
    {
        if (_activeInternalDragPaths is { Length: > 0 })
        {
            return _activeInternalDragPaths;
        }

        if (data.GetDataPresent(InternalDragPathFormat))
        {
            string? singlePath = data.GetData(InternalDragPathFormat) as string;
            return string.IsNullOrWhiteSpace(singlePath) ? [] : [singlePath];
        }

        if (data.GetDataPresent(InternalDragPathsFormat))
        {
            return data.GetData(InternalDragPathsFormat) as string[] ?? [];
        }

        return [];
    }

    internal bool HasInternalDraggedPaths(Forms.IDataObject data)
    {
        if (_activeInternalDragPaths is { Length: > 0 })
        {
            return true;
        }

        return data.GetDataPresent(InternalDragPathFormat) || data.GetDataPresent(InternalDragPathsFormat);
    }

    private string[] GetInternalDraggedPaths(Forms.IDataObject data)
    {
        if (_activeInternalDragPaths is { Length: > 0 })
        {
            return _activeInternalDragPaths;
        }

        if (data.GetDataPresent(InternalDragPathFormat))
        {
            string? singlePath = data.GetData(InternalDragPathFormat) as string;
            return string.IsNullOrWhiteSpace(singlePath) ? [] : [singlePath];
        }

        if (data.GetDataPresent(InternalDragPathsFormat))
        {
            return data.GetData(InternalDragPathsFormat) as string[] ?? [];
        }

        return [];
    }

    private static string NormalizeComparablePath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private IEnumerable<string> EnumerateCurrentDraggedPaths()
    {
        if (_activeInternalDragPaths is { Length: > 0 })
        {
            return _activeInternalDragPaths;
        }

        return ShopItemsListView.SelectedItems.Cast<ShopItem>()
            .Where(static item => !string.IsNullOrWhiteSpace(item.FullPath))
            .Select(static item => item.FullPath);
    }

    private bool IsTargetInsideCurrentSelection(ShopItem? target)
    {
        if (target is null)
        {
            return false;
        }

        string targetPath;
        try
        {
            targetPath = NormalizeComparablePath(target.FullPath);
        }
        catch (Exception)
        {
            return false;
        }

        foreach (string draggedPath in EnumerateCurrentDraggedPaths())
        {
            try
            {
                string selectedPath = NormalizeComparablePath(draggedPath);
                if (string.Equals(selectedPath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (Directory.Exists(draggedPath) &&
                    targetPath.StartsWith(selectedPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string? selectedParent = Path.GetDirectoryName(selectedPath);
                if (!string.IsNullOrWhiteSpace(selectedParent) &&
                    string.Equals(NormalizeComparablePath(selectedParent), targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                continue;
            }
        }

        return false;
    }

    private bool IsInternalDropTargetAllowed(string destinationFolder, System.Windows.IDataObject data, DragDropKeyStates keyStates)
    {
        return IsInternalDropTargetAllowed(destinationFolder, GetInternalDraggedPaths(data), keyStates);
    }

    private bool IsInternalDropTargetAllowed(string destinationFolder, Forms.IDataObject data, DragDropKeyStates keyStates)
    {
        return IsInternalDropTargetAllowed(destinationFolder, GetInternalDraggedPaths(data), keyStates);
    }

    private bool IsInternalDropTargetAllowed(string destinationFolder, IReadOnlyList<string> sourcePaths, DragDropKeyStates keyStates)
    {
        if (sourcePaths.Count == 0 || !Directory.Exists(destinationFolder))
        {
            return false;
        }

        bool isCopy = (keyStates & DragDropKeyStates.ControlKey) != 0;
        foreach (string sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return false;
            }

            ExplorerActionResult validation = IsHoldFolderPath(destinationFolder)
                ? ExplorerActionService.ValidateHoldTarget(new HoldItemRequest
                {
                    ModeLabel = _currentMode.ToString(),
                    SourcePath = sourcePath,
                    HoldFolderPath = destinationFolder,
                    GetShareStatus = GetItemShareStatus,
                    BeforeWrite = SuppressExternalChangeNotifications,
                })
                : isCopy
                    ? ExplorerActionService.ValidateCopyTarget(new CopyItemRequest
                    {
                        ModeLabel = _currentMode.ToString(),
                        SourcePath = sourcePath,
                        DestinationFolder = destinationFolder,
                        IsHoldFolderPath = IsHoldFolderPath,
                        IsUnderFolder = IsUnderFolder,
                        GetShareStatus = GetItemShareStatus,
                        BeforeWrite = SuppressExternalChangeNotifications,
                    })
                    : ExplorerActionService.ValidateMoveTarget(new MoveItemRequest
                    {
                        ModeLabel = _currentMode.ToString(),
                        SourcePath = sourcePath,
                        DestinationFolder = destinationFolder,
                        IsHoldFolderPath = IsHoldFolderPath,
                        IsUnderFolder = IsUnderFolder,
                        GetShareStatus = GetItemShareStatus,
                        BeforeWrite = SuppressExternalChangeNotifications,
                    });

            if (validation.State != ExplorerActionState.Success)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsValidDropTargetItem(ShopItem? target, System.Windows.IDataObject? data, DragDropKeyStates keyStates = DragDropKeyStates.None)
    {
        if (data is null || !HasInternalDraggedPaths(data))
        {
            return target is not null && target.IsDirectory;
        }

        return IsValidDropTargetItem(target, GetInternalDraggedPaths(data), keyStates);
    }

    private bool IsValidDropTargetItem(ShopItem? target, Forms.IDataObject? data, DragDropKeyStates keyStates = DragDropKeyStates.None)
    {
        if (data is null || !HasInternalDraggedPaths(data))
        {
            return target is not null && target.IsDirectory;
        }

        return IsValidDropTargetItem(target, GetInternalDraggedPaths(data), keyStates);
    }

    private bool IsValidDropTargetItem(ShopItem? target, IReadOnlyList<string> sourcePaths, DragDropKeyStates keyStates)
    {
        if (target is null || !target.IsDirectory)
        {
            return false;
        }

        if (sourcePaths.Count == 0)
        {
            return true;
        }

        if (IsTargetInsideCurrentSelection(target))
        {
            return false;
        }

        string targetPath;
        try
        {
            targetPath = NormalizeComparablePath(target.FullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            SwkLogger.Debug($"IsValidDropTargetItem skipped invalid target path: {ex.Message}");
            return false;
        }

        foreach (string sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            try
            {
                string normalizedSourcePath = NormalizeComparablePath(sourcePath);
                if (string.Equals(normalizedSourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (Directory.Exists(sourcePath) &&
                    (string.Equals(normalizedSourcePath, targetPath, StringComparison.OrdinalIgnoreCase) ||
                     targetPath.StartsWith(normalizedSourcePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                SwkLogger.Debug($"IsValidDropTargetItem skipped invalid source path: {ex.Message}");
            }
        }

        return IsInternalDropTargetAllowed(target.FullPath, sourcePaths, keyStates);
    }

    private void ClearDropTargetHighlight()
    {
        if (_dropTargetItem is null)
        {
            return;
        }

        _dropTargetItem.IsDropTarget = false;
        _dropTargetItem = null;
    }

    private void ShowDragHint(string? itemName = null)
    {
        DragHintBorder.Visibility = Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(itemName))
        {
            EnsureDragPreviewPopup();
            DragPreviewTextBlock.Text = itemName;
            _dragPreviewPopup!.IsOpen = true;
            UpdateDragPreviewPosition();
        }
    }

    private void HideDragHint()
    {
        DragHintBorder.Visibility = Visibility.Collapsed;
        if (_dragPreviewPopup is not null)
        {
            _dragPreviewPopup.IsOpen = false;
        }
    }

    private static string? GetExternalDragHintLabel(System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            return null;
        }

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return null;
        }

        if (paths.Length == 1)
        {
            return Path.GetFileName(paths[0]);
        }

        return $"{paths.Length} 個の項目";
    }

    private TextBlock DragPreviewTextBlock
    {
        get
        {
            EnsureDragPreviewPopup();
            return _dragPreviewTextBlock!;
        }
    }

    private void EnsureDragPreviewPopup()
    {
        if (_dragPreviewPopup is not null)
        {
            return;
        }

        _dragPreviewTextBlock = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 24, 39)),
            MaxWidth = 260,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        Border previewBorder = new()
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 246, 226)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 216, 152)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9, 5, 9, 5),
            Child = _dragPreviewTextBlock
        };

        _dragPreviewPopup = new Popup
        {
            AllowsTransparency = true,
            Child = previewBorder,
            IsHitTestVisible = false,
            Placement = PlacementMode.AbsolutePoint,
            StaysOpen = true
        };
    }

    private void UpdateDragPreviewPosition()
    {
        if (_dragPreviewPopup is null || !_dragPreviewPopup.IsOpen)
        {
            return;
        }

        System.Drawing.Point cursorPosition = Forms.Cursor.Position;
        _dragPreviewPopup.HorizontalOffset = cursorPosition.X + 18;
        _dragPreviewPopup.VerticalOffset = cursorPosition.Y + 18;
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
        {
            string destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
            File.Copy(file, destinationFile);
        }

        foreach (string directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            string destinationSubdirectory = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            CopyDirectory(directory, destinationSubdirectory);
        }
    }

    private void LoadSettings()
    {
        TryMigrateLegacyData();

        if (!File.Exists(SettingsPath))
        {
            ApplyLoadedSettings(null);
            return;
        }

        try
        {
            string json = File.ReadAllText(SettingsPath);
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json);
            ApplyLoadedSettings(settings);
        }
        catch (IOException)
        {
            ApplyLoadedSettings(null);
            StatusTextBlock.Text = "設定を読み込めません。";
        }
        catch (JsonException)
        {
            ApplyLoadedSettings(null);
            StatusTextBlock.Text = "設定を読み込めません。";
        }
    }

    private static void TryMigrateLegacyData()
    {
        // %LocalAppData%\ShareWorkin から AppHomeDirectory への一回限り移行(v1.04 → v1.05)。
        // secure.dat は SecureStorage 側で(DPAPI スコープ再暗号化が必要なため)別経路で扱う。
        try
        {
            Directory.CreateDirectory(AppHomeDirectory);

            if (!File.Exists(SettingsPath) && File.Exists(LegacySettingsPath))
            {
                File.Move(LegacySettingsPath, SettingsPath);
                SwkLogger.Info("Migrated settings.json from %LocalAppData% to app folder");
            }

            if (!Directory.Exists(DefaultHoldFolderPath) && Directory.Exists(LegacyHoldFolderPath))
            {
                Directory.Move(LegacyHoldFolderPath, DefaultHoldFolderPath);
                SwkLogger.Info("Migrated hold folder from %LocalAppData% to app folder");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SwkLogger.Warn($"Legacy data migration skipped: {ex.Message}");
        }
    }

    private void ApplyLoadedSettings(AppSettings? settings)
    {
        bool needsOwnerPersistence = string.IsNullOrWhiteSpace(settings?.PcOwnerSid) ||
                                     string.IsNullOrWhiteSpace(settings?.PcOwnerAccount);
        _shopFolder = settings?.ShopFolder ?? settings?.WatchFolder;
        MyShopTextBox.Text = _shopFolder ?? string.Empty;
        _notificationMode = ResolveNotificationMode(settings);
        SelectNotificationMode(_notificationMode);
        _isSizeCalcEnabled = settings?.FolderSizeCalcEnabled ?? false;
        SizeCalcCheckBox.IsChecked = _isSizeCalcEnabled;
        _wasOpenAtLastShutdown = settings?.IsOpenAtLastShutdown ?? false;
        _shareAccessRight = ParseAccessLevel(settings?.AccessLevel);
        SelectAccessLevel(_shareAccessRight);
        _loadedReservedForV22 = settings?.ReservedForV22;
        _pcOwnerSid = settings?.PcOwnerSid ?? PcOwnerIdentity.TryGetCurrentUserSid();
        _pcOwnerAccount = settings?.PcOwnerAccount ?? PcOwnerIdentity.TryGetCurrentUserAccount();
        PcOwnerIdentity.Configure(_pcOwnerSid, _pcOwnerAccount);

        if (needsOwnerPersistence && !string.IsNullOrWhiteSpace(_pcOwnerSid))
        {
            SaveSettings();
        }
    }

    private static ShareAccessRight ParseAccessLevel(string? value)
    {
        return string.Equals(value, "Read", StringComparison.OrdinalIgnoreCase)
            ? ShareAccessRight.Read
            : ShareAccessRight.Full;
    }

    private static void SelectAccessLevel(ShareAccessRight right)
    {
        // The global access-level UI was retired in favor of per-item 共有状況.
        // The state is still loaded from settings for the SMB-share-level grant.
        _ = right;
    }

    private static NotificationMode ResolveNotificationMode(AppSettings? settings)
    {
        if (!string.IsNullOrWhiteSpace(settings?.NotificationMode) &&
            Enum.TryParse(settings.NotificationMode, out NotificationMode mode))
        {
            return mode;
        }

        return settings?.NotificationEnabled == false
            ? NotificationMode.Off
            : NotificationMode.All;
    }

    private void SavePermissionMap()
    {
        if (_deferredPermissionSaveDepth > 0)
        {
            _permissionSavePending = true;
            return;
        }

        SavePermissionMapCore();
    }

    private void SavePermissionMapCore()
    {
        try
        {
            var entries = _permissionMap
                .Where(kv => kv.Value.Users.Count > 0 || kv.Value.IsReadOnly || kv.Value.IsSharedOff)
                .Select(kv => new PermissionEntry
                {
                    Path = kv.Key,
                    Users = kv.Value.Users,
                    IsReadOnly = kv.Value.IsReadOnly,
                    IsSharedOff = kv.Value.IsSharedOff,
                    HasHoldDisplayPermission = _holdDisplayPermissionMap.ContainsKey(kv.Key),
                    HoldDisplayUsers = _holdDisplayPermissionMap.TryGetValue(kv.Key, out var holdDisplayPerm)
                        ? holdDisplayPerm.Users
                        : [],
                    HoldDisplayReadOnly = _holdDisplayPermissionMap.TryGetValue(kv.Key, out holdDisplayPerm)
                        && holdDisplayPerm.IsReadOnly,
                    HoldDisplaySharedOff = _holdDisplayPermissionMap.TryGetValue(kv.Key, out holdDisplayPerm)
                        && holdDisplayPerm.IsSharedOff
                }).ToList();
            File.WriteAllText(PermissionsPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            PublishPermissionManifest();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SwkLogger.Warn($"SavePermissionMap failed: {ex.Message}");
            SetTransientStatus("共有設定を保存できませんでした。");
        }
    }

    private void PublishPermissionManifest()
    {
        if (!_isShopOpen || string.IsNullOrWhiteSpace(_shopFolder) || !Directory.Exists(_shopFolder))
        {
            return;
        }

        try
        {
            string root = Path.GetFullPath(_shopFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            IReadOnlyList<Friend> friends = FriendsRepository.LoadAll();
            List<ShopPermissionManifestEntry> entries = [];

            foreach (var (path, (users, isReadOnly, isSharedOff)) in _permissionMap)
            {
                if (users.Count == 0 && !isReadOnly && !isSharedOff)
                {
                    continue;
                }

                if (!IsUnderFolder(path, root) || IsHoldFolderPath(path))
                {
                    continue;
                }

                string relativePath = ToRelativeShopPath(root, path);
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                List<string> allowedMachines = ResolveAllowedMachineNames(users, friends);
                entries.Add(new ShopPermissionManifestEntry
                {
                    RelativePath = relativePath,
                    Users = users.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    AllowedMachineNames = allowedMachines,
                    IsReadOnly = isReadOnly,
                    IsSharedOff = isSharedOff
                });
            }

            if (!ShopPermissionManifest.Save(root, entries))
            {
                SetTransientStatus("共有設定の配布情報を保存できませんでした。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SwkLogger.Warn($"PublishPermissionManifest failed: {ex.Message}");
        }
    }

    private static List<string> ResolveAllowedMachineNames(IReadOnlyList<string> users, IReadOnlyList<Friend> friends)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string user in users)
        {
            if (string.IsNullOrWhiteSpace(user))
            {
                continue;
            }

            foreach (Friend friend in friends)
            {
                string display = string.IsNullOrWhiteSpace(friend.DisplayName)
                    ? friend.HostMachineName
                    : friend.DisplayName;
                if (string.Equals(user, display, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(user, friend.HostMachineName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(friend.HostMachineName))
                    {
                        result.Add(friend.HostMachineName);
                    }
                }
            }

            result.Add(user);
        }

        return result.ToList();
    }

    private static string ToRelativeShopPath(string root, string path)
    {
        string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedPath[(normalizedRoot.Length + 1)..].Replace('/', '\\');
        }

        return string.Empty;
    }

    private void LoadPermissionMap()
    {
        _permissionMap.Clear();
        _holdDisplayPermissionMap.Clear();
        if (!File.Exists(PermissionsPath)) return;
        try
        {
            var entries = JsonSerializer.Deserialize<List<PermissionEntry>>(File.ReadAllText(PermissionsPath));
            if (entries is null) return;
            foreach (var e in entries)
            {
                if (!string.IsNullOrEmpty(e.Path))
                {
                    List<string> users = e.Users ?? [];
                    if (users.Count == 0 && !e.IsReadOnly && !e.IsSharedOff)
                    {
                        continue;
                    }
                    _permissionMap[e.Path] = (users, e.IsReadOnly, e.IsSharedOff);
                    if (e.HasHoldDisplayPermission)
                    {
                        _holdDisplayPermissionMap[e.Path] = (e.HoldDisplayUsers ?? [], e.HoldDisplayReadOnly, e.HoldDisplaySharedOff);
                    }
                }
            }
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(AppHomeDirectory);
            AppSettings settings = new()
            {
                ShopFolder = _shopFolder,
                NotificationMode = _notificationMode.ToString(),
                NotificationEnabled = _notificationMode != NotificationMode.Off,
                FolderSizeCalcEnabled = _isSizeCalcEnabled,
                IsOpenAtLastShutdown = _wasOpenAtLastShutdown,
                AccessLevel = _shareAccessRight == ShareAccessRight.Read ? "Read" : "Full",
                ShareMode = "Everyone",
                Version = SettingsVersion,
                PcOwnerSid = _pcOwnerSid,
                PcOwnerAccount = _pcOwnerAccount,
                ReservedForV22 = _loadedReservedForV22
            };
            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = true
            });
            File.WriteAllText(SettingsPath, json);
        }
        catch (IOException)
        {
            StatusTextBlock.Text = "設定を保存できません。お店は開けます。";
        }
        catch (UnauthorizedAccessException)
        {
            StatusTextBlock.Text = "設定を保存できません。お店は開けます。";
        }
    }

    private void UpdateShopState(bool isOpen, string? statusMessage = null)
    {
        ShopDoorButton.Content = isOpen ? "共有を閉じる" : "共有開始";
        ClosedShopPanel.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
        OpenShopPanel.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        MyShopTextBox.Background = string.IsNullOrWhiteSpace(_shopFolder)
            ? UnselectedShopBackgroundBrush
            : System.Windows.SystemColors.WindowBrush;
        UpdateUserListButtonHighlight();

        string closedText;
        string openText;
        string openTooltip = _shopFolder ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            closedText = statusMessage;
            openText = statusMessage;
            openTooltip = statusMessage;
        }
        else if (!string.IsNullOrWhiteSpace(_missingFriendShopStatus))
        {
            closedText = _missingFriendShopStatus;
            openText = _missingFriendShopStatus;
            openTooltip = _missingFriendShopStatus;
        }
        else if (isOpen)
        {
            openText = _shopFolder ?? string.Empty;
            if (_isPollingMode)
            {
                openText += "(気配の届け方を変えました)";
            }
            closedText = $"共有: {openText}";
            openTooltip = openText;
        }
        else if (string.IsNullOrWhiteSpace(_shopFolder))
        {
            closedText = "共有する場所を選んでください。";
            openText = "共有する場所を選んでください。";
            openTooltip = openText;
        }
        else
        {
            closedText = $"共有: {_shopFolder}(共有していません)";
            openText = _shopFolder!;
            openTooltip = _shopFolder!;
        }

        StatusTextBlock.Text = closedText;
        OpenStatusTextBlock.Text = openText;
        OpenStatusTextBlock.ToolTip = string.IsNullOrEmpty(openTooltip) ? null : openTooltip;

        UpdateSidebar(isOpen);
    }

    private void UpdateUserListButtonHighlight()
    {
        bool hasRegisteredUsers = FriendsRepository.LoadAll().Count > 0;
        if (hasRegisteredUsers)
        {
            UserListButton.ClearValue(BackgroundProperty);
            UserListButton.ClearValue(BorderBrushProperty);
            return;
        }

        UserListButton.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(214, 236, 216));
        UserListButton.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(126, 176, 138));
    }
}

public sealed class SidebarRow
{
    public string DisplayName { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public bool IsFriend { get; init; }
    public Friend? Friend { get; init; }
    public string? LanHost { get; init; }
    public string? LanAddress { get; init; }
}

public sealed class ShopItem : INotifyPropertyChanged
{
    public string Name { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    public bool IsHoldFolder { get; }

    public DateTime UpdatedAt { get; }

    public long? SizeBytes { get; private set; }

    private string _sizeText = string.Empty;
    private string _subfolderBadge = string.Empty;
    private bool _isDropTarget;

    public string SizeText
    {
        get => _sizeText;
        private set
        {
            if (_sizeText == value)
            {
                return;
            }
            _sizeText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
        }
    }

    public string SubfolderBadgeText
    {
        get => _subfolderBadge;
        private set
        {
            if (_subfolderBadge == value) return;
            _subfolderBadge = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubfolderBadgeText)));
        }
    }

    public void SetSubfolderCount(int count)
    {
        SubfolderBadgeText = count switch
        {
            < 2 => string.Empty,
            <= 9 => count.ToString(),
            _ => "*"
        };
    }

    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            if (_isDropTarget == value)
            {
                return;
            }

            _isDropTarget = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDropTarget)));
        }
    }

    private bool _isRenaming;
    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (_isRenaming == value) return;
            _isRenaming = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRenaming)));
        }
    }

    public string KindText => IsDirectory ? "フォルダー" : "ファイル";

    // Per-item sharing state. Empty list = 全員, non-empty = 指定.
    // 指定は店主側の友達名を保持し、ShareWorkin 同士の表示制御用 manifest へ出力する。
    public ObservableCollection<string> AllowedUsers { get; } = new();

    // R suffix = read-only. W is implicit (omitted) when false.
    public bool IsReadOnly { get; set; }

    public bool IsSharedOff { get; set; }

    public bool IsFromFriendShop { get; set; }

    public string ShareStatusText => IsHoldFolder ? "非公開"
        : IsSharedOff ? "OFF"
        : AllowedUsers.Count == 0
            ? (IsReadOnly ? "全員R" : "全員")
            : (IsReadOnly ? "指定R" : "指定");

    public void RefreshShareStatus()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShareStatusText)));
    }

    public string UpdatedAtText => UpdatedAt.ToString("yyyy/MM/dd HH:mm");

    public string SortKey => IsDirectory ? "0" : "1";

    public string IconPathData => IsHoldFolder
        ? "M2 6 L8 6 L10.2 8.4 L22 8.4 L22 20 L2 20 Z M9 15.4 L9 14.1 C9 12.6 10.2 11.5 11.7 11.5 C13.2 11.5 14.4 12.6 14.4 14.1 L14.4 15.4 M8.2 15.4 L15.2 15.4 L15.2 19.2 L8.2 19.2 Z"
        : IsDirectory
        ? "M2 6 L8 6 L10.2 8.4 L22 8.4 L22 20 L2 20 Z"
        : "M6 2 L15 2 L20 7 L20 22 L6 22 Z M15 2 L15 7 L20 7";

    public System.Windows.Media.Brush IconFillBrush => IsHoldFolder
        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 223, 255))
        : IsDirectory
        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 243, 199))
        : System.Windows.Media.Brushes.White;

    public System.Windows.Media.Brush IconStrokeBrush => IsHoldFolder
        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(108, 78, 180))
        : IsDirectory
        ? System.Windows.Media.Brushes.Goldenrod
        : System.Windows.Media.Brushes.SteelBlue;

    public ShopItem(string name, string fullPath, bool isDirectory, bool isHoldFolder, DateTime updatedAt, long? sizeBytes)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        IsHoldFolder = isHoldFolder;
        UpdatedAt = updatedAt;
        SizeBytes = sizeBytes;
        _sizeText = sizeBytes.HasValue ? FormatSize(sizeBytes.Value) : string.Empty;
    }

    public void SetSize(long bytes)
    {
        SizeBytes = bytes;
        SizeText = FormatSize(bytes);
    }

    public void SetSizeCalculating()
    {
        if (SizeBytes is null)
        {
            SizeText = "計算中…";
        }
    }

    public void ClearSize()
    {
        SizeBytes = null;
        SizeText = string.Empty;
    }

    public static ShopItem FromPath(string path, bool isDirectory, bool isHoldFolder = false)
    {
        if (isDirectory)
        {
            DateTime updatedAt = Directory.GetLastWriteTime(path);
            return new ShopItem(Path.GetFileName(path), path, true, isHoldFolder, updatedAt, null);
        }

        FileInfo info = new(path);
        return new ShopItem(info.Name, path, false, false, info.LastWriteTime, info.Length);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }
        double kb = bytes / 1024.0;
        if (kb < 1024)
        {
            return $"{kb:0.0} KB";
        }
        double mb = kb / 1024.0;
        if (mb < 1024)
        {
            return $"{mb:0.0} MB";
        }
        double gb = mb / 1024.0;
        return $"{gb:0.0} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum ShopSortField
{
    Name,
    ShareStatus,
    Kind,
    UpdatedAt,
    Size,
}

public enum NotificationMode
{
    All,
    ExternalOnly,
    Off,
}

public sealed record ArrivedItem(string Name, string FolderPath, DateTime ArrivedAt)
{
    public string ArrivedAtText => ArrivedAt.ToString("yyyy/MM/dd HH:mm:ss");
}

public sealed class PermissionEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("users")] public List<string> Users { get; set; } = [];
    [JsonPropertyName("readOnly")] public bool IsReadOnly { get; set; }
    [JsonPropertyName("sharedOff")] public bool IsSharedOff { get; set; }
    [JsonPropertyName("hasHoldDisplayPermission")] public bool HasHoldDisplayPermission { get; set; }
    [JsonPropertyName("holdDisplayUsers")] public List<string> HoldDisplayUsers { get; set; } = [];
    [JsonPropertyName("holdDisplayReadOnly")] public bool HoldDisplayReadOnly { get; set; }
    [JsonPropertyName("holdDisplaySharedOff")] public bool HoldDisplaySharedOff { get; set; }
}

public sealed class AppSettings
{
    [JsonPropertyName("_v")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShopFolder { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NotificationMode { get; set; }

    public bool NotificationEnabled { get; set; } = true;

    public bool FolderSizeCalcEnabled { get; set; }

    [JsonPropertyName("isOpenAtLastShutdown")]
    public bool IsOpenAtLastShutdown { get; set; }

    [JsonPropertyName("accessLevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccessLevel { get; set; }

    [JsonPropertyName("shareMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShareMode { get; set; }

    [JsonPropertyName("pcOwnerSid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PcOwnerSid { get; set; }

    [JsonPropertyName("pcOwnerAccount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PcOwnerAccount { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WatchFolder { get; set; }

    [JsonPropertyName("_reservedForV22")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ReservedForV22 { get; set; }
}

public sealed record ExplorerTarget(
    string Label,
    Friend? Friend,
    SwkNotificationListener.ShopInfo? ShopInfo);

[ComVisible(true)]
internal sealed class ExplorerOleDropTarget : IOleDropTarget
{
    private readonly MainWindow _owner;
    private Forms.IDataObject? _currentData;
    private int _lastEffect = (int)System.Windows.DragDropEffects.None;

    public ExplorerOleDropTarget(MainWindow owner)
    {
        _owner = owner;
    }

    public int DragEnter(
        System.Runtime.InteropServices.ComTypes.IDataObject pDataObj,
        int grfKeyState,
        POINTL pt,
        ref int pdwEffect)
    {
        _currentData = new Forms.DataObject(pDataObj);
        _ = _owner.Dispatcher.InvokeAsync(() =>
            _owner.UpdateExternalDropTargetHighlightFromScreenPoint(new System.Windows.Point(pt.x, pt.y), _currentData, (DragDropKeyStates)grfKeyState));
        pdwEffect = ResolveEffect(_currentData, (DragDropKeyStates)grfKeyState, pt);
        _lastEffect = pdwEffect;
        return 0;
    }

    public int DragOver(int grfKeyState, POINTL pt, ref int pdwEffect)
    {
        _ = _owner.Dispatcher.InvokeAsync(() =>
            _owner.UpdateExternalDropTargetHighlightFromScreenPoint(new System.Windows.Point(pt.x, pt.y), _currentData, (DragDropKeyStates)grfKeyState));
        pdwEffect = ResolveEffect(_currentData, (DragDropKeyStates)grfKeyState, pt);
        _lastEffect = pdwEffect;
        return 0;
    }

    public int DragLeave()
    {
        _currentData = null;
        _ = _owner.Dispatcher.InvokeAsync(_owner.ClearExternalDropTargetHighlight);
        _lastEffect = (int)System.Windows.DragDropEffects.None;
        return 0;
    }

    public int Drop(
        System.Runtime.InteropServices.ComTypes.IDataObject pDataObj,
        int grfKeyState,
        POINTL pt,
        ref int pdwEffect)
    {
        var data = new Forms.DataObject(pDataObj);
        DragDropKeyStates keyStates = (DragDropKeyStates)grfKeyState;
        _ = _owner.Dispatcher.InvokeAsync(() =>
            _owner.UpdateExternalDropTargetHighlightFromScreenPoint(new System.Windows.Point(pt.x, pt.y), data, keyStates));
        pdwEffect = ResolveEffect(data, keyStates, pt);
        _lastEffect = pdwEffect;
        _ = _owner.Dispatcher.InvokeAsync(() =>
            _owner.HandleOleDrop(data, keyStates, new System.Windows.Point(pt.x, pt.y)));
        _currentData = null;
        return 0;
    }

    private int ResolveEffect(
        Forms.IDataObject? data,
        DragDropKeyStates keyStates,
        POINTL pt)
    {
        try
        {
            if (data is null)
            {
                return _lastEffect;
            }

            System.Windows.Point screenPoint = new(pt.x, pt.y);
            string destinationFolder = _owner.HasInternalDraggedPaths(data)
                ? _owner.ResolveOleInternalDropDestinationFromScreenPoint(screenPoint, data, keyStates) ?? string.Empty
                : _owner.ResolveExternalDropDestinationFromScreenPoint(screenPoint) ?? string.Empty;
            return _owner.GetOleDropEffect(data, keyStates, destinationFolder);
        }
        catch (Exception ex)
        {
            SwkLogger.Debug($"ExplorerOleDropTarget.ResolveEffect failed: {ex.Message}");
            return (int)System.Windows.DragDropEffects.None;
        }
    }

}

[ComImport]
[Guid("00000122-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleDropTarget
{
    [PreserveSig]
    int DragEnter(
        System.Runtime.InteropServices.ComTypes.IDataObject pDataObj,
        int grfKeyState,
        POINTL pt,
        ref int pdwEffect);

    [PreserveSig]
    int DragOver(int grfKeyState, POINTL pt, ref int pdwEffect);

    [PreserveSig]
    int DragLeave();

    [PreserveSig]
    int Drop(
        System.Runtime.InteropServices.ComTypes.IDataObject pDataObj,
        int grfKeyState,
        POINTL pt,
        ref int pdwEffect);
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTL
{
    public int x;
    public int y;
}
