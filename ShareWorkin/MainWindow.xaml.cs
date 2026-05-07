using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using System.Windows.Threading;
using ShareWorkin.SMB;
using Forms = System.Windows.Forms;

namespace ShareWorkin;

public partial class MainWindow : Window
{
    private const int MaxArrivedItemCount = 100;
    private const string HoldFolderName = "保留";
    private const string InternalDragPathFormat = "ShareWorkin.InternalPath";

    private static readonly TimeSpan NotificationQuietTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TransientStatusDuration = TimeSpan.FromSeconds(4);

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

    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly Forms.ToolStripMenuItem _trayShowItem;
    private readonly Forms.ToolStripMenuItem _trayExitItem;
    private readonly DispatcherTimer _notificationTimer;
    private readonly DispatcherTimer _pollingTimer;
    private readonly DispatcherTimer _transientStatusTimer;
    private readonly List<ArrivedItem> _pendingNotificationItems = [];
    private readonly HashSet<string> _knownFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private readonly Dictionary<string, long> _folderSizeCache = new(StringComparer.OrdinalIgnoreCase);
    // セッション中のアイテム別許可設定。AllowedUsers は ShopItem ごとに in-memory のため
    // ナビゲーションで再生成されるたびに消えるのを防ぐ。キー = FullPath。
    private readonly Dictionary<string, (List<string> Users, bool IsReadOnly, bool IsSharedOff)> _permissionMap
        = new(StringComparer.OrdinalIgnoreCase);
    // 現在フォルダーを基点に祖先を遡って得た有効な許可設定（継承用）。
    private (List<string> Users, bool IsReadOnly, bool IsSharedOff)? _effectiveParentPerm;
    private FileSystemWatcher? _arrivalSensor;
    private FileSystemWatcher? _contentsSensor;
    private CancellationTokenSource? _folderSizeCancellation;
    private DispatcherTimer? _friendShopPollTimer;
    private string? _shopFolder;
    private string? _currentFolder;
    private string? _lastNotificationFolder;
    private string? _pendingFocusName;
    private ShopItem? _dropTargetItem;
    private Popup? _dragPreviewPopup;
    private TextBlock? _dragPreviewTextBlock;
    private System.Windows.Point _dragStartPoint;
    private ShopItem? _dragStartItem;
    private string _breadcrumbFullText = string.Empty;
    private DateTime _suppressExternalChangeNotificationsUntil = DateTime.MinValue;
    private DateTime _lastExternalChangeNotificationAt = DateTime.MinValue;
    private JsonElement? _loadedReservedForV22;
    private NotificationMode _notificationMode = NotificationMode.All;
    private bool _isSizeCalcEnabled;
    private bool _isShopOpen;
    private bool _isPollingMode;
    private bool _exitRequested;
    private bool _trayHintShown;
    private bool _uiUnlocked;
    private bool _startupHandled;
    private bool _wasOpenAtLastShutdown;
    private ShareAccessRight _shareAccessRight = ShareAccessRight.Full;
    private DisplayMode _currentMode = DisplayMode.Shop;
    private Friend? _activeFriendShop;
    private bool _suppressDropdownChange;
    private ShopSortField _sortField = ShopSortField.Name;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    public ObservableCollection<ArrivedItem> ArrivedItems { get; } = [];

    public ObservableCollection<ShopItem> ShopItems { get; } = [];

    public ObservableCollection<SidebarRow> SidebarItems { get; } = [];

    public MainWindow()
    {
        if (!IsRunningAsAdmin())
        {
            System.Windows.MessageBox.Show(
                "ShareWorkin は管理者権限で動く必要があります。\nインストールし直すか、Windows の設定をご確認ください。",
                "ShareWorkin",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Environment.Exit(0);
        }

        InitializeComponent();
        DataContext = this;

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "ShareWorkin",
            Visible = true
        };
        _notifyIcon.BalloonTipClicked += NotifyIcon_BalloonTipClicked;
        _notifyIcon.MouseDoubleClick += NotifyIcon_MouseDoubleClick;

        _trayMenu = new Forms.ContextMenuStrip();
        _trayShowItem = new Forms.ToolStripMenuItem("画面を開く");
        _trayShowItem.Click += (_, _) => Dispatcher.Invoke(ShowMainWindowWithPassword);
        _trayExitItem = new Forms.ToolStripMenuItem("アプリを終了");
        _trayExitItem.Click += (_, _) => Dispatcher.Invoke(ExitApp);
        _trayMenu.Items.Add(_trayShowItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(_trayExitItem);
        _notifyIcon.ContextMenuStrip = _trayMenu;

        _notificationTimer = new DispatcherTimer { Interval = NotificationQuietTime };
        _notificationTimer.Tick += NotificationTimer_Tick;

        _pollingTimer = new DispatcherTimer { Interval = PollingInterval };
        _pollingTimer.Tick += PollingTimer_Tick;

        _transientStatusTimer = new DispatcherTimer { Interval = TransientStatusDuration };
        _transientStatusTimer.Tick += TransientStatusTimer_Tick;

        LoadSettings();
        LoadPermissionMap();
        NotificationModeComboBox.SelectionChanged += NotificationModeComboBox_SelectionChanged;
        ExplorerTargetComboBox.SelectionChanged += ExplorerTargetComboBox_SelectionChanged;
        PopulateExplorerDropdown();
        MigrateLegacyAppHomeHold();
        UpdateShopState(false);
        UpdateColumnHeaders();
        string? ver = (System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                typeof(MainWindow).Assembly))
            ?.InformationalVersion;
        AppVersionTextBlock.Text = FormatVersionLabel(ver);
        Loaded += MainWindow_Loaded;
    }

    // SDK 既定で InformationalVersion に "+<full git sha>" が付く。SHAは7文字に短縮し、
    // SHAが付かないビルド（git なし環境）では "v<ver>" だけ返す。
    private static string FormatVersionLabel(string? ver)
    {
        if (string.IsNullOrEmpty(ver)) return string.Empty;
        int plus = ver.IndexOf('+');
        if (plus < 0) return $"v{ver}";
        string baseVer = ver[..plus];
        string sha = ver[(plus + 1)..];
        if (sha.Length > 7) sha = sha[..7];
        return string.IsNullOrEmpty(sha) ? $"v{baseVer}" : $"v{baseVer}+{sha}";
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startupHandled)
        {
            return;
        }

        _startupHandled = true;
        Hide();
        Dispatcher.BeginInvoke(new Action(HandleStartup));
    }

    private void HandleStartup()
    {
        RestoreOpenShopIfNeeded();
        if (!EnsureUiUnlocked())
        {
            if (!_isShopOpen)
                ExitApp();
            return;
        }
        ShowMainWindow();
        _ = SwkNetworkCache.RefreshAsync(ScanMode.Quick);
    }

    // v1.04〜v1.08 では GetHoldFolderPath() が AppHomeDirectory\hold を返す実装だった。
    // v1.09 で _shopFolder\保留 に戻したため、AppHomeDirectory\hold にあるファイルを移動する。
    private void MigrateLegacyAppHomeHold()
    {
        if (string.IsNullOrWhiteSpace(_shopFolder) || !Directory.Exists(_shopFolder))
            return;

        if (!Directory.Exists(DefaultHoldFolderPath))
            return;

        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(DefaultHoldFolderPath).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SwkLogger.Warn($"Hold migration: enumerate failed ({ex.Message})");
            return;
        }

        if (entries.Count == 0)
        {
            TryDeleteEmptyDirectory(DefaultHoldFolderPath);
            return;
        }

        if (!TryEnsureHoldFolder())
        {
            SwkLogger.Warn("Hold migration: could not create hold folder, skipping");
            return;
        }

        string holdFolderPath = GetHoldFolderPath();
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

        if (movedCount > 0)
        {
            SwkLogger.Info($"Hold migration: moved {movedCount} entries from AppHome to shop hold folder");
            SetTransientStatus("保留領域を移しました。");
        }
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
    {
        try
        {
            string holdFolderPath = GetHoldFolderPath();
            Directory.CreateDirectory(holdFolderPath);
            ClearHiddenFolderAttribute(holdFolderPath);
            SetPrivateHoldFolderPermissions(holdFolderPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
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
        string? ownerSid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(ownerSid))
        {
            throw new InvalidOperationException("Current Windows user could not be resolved.");
        }

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "icacls.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        process.StartInfo.ArgumentList.Add(folderPath);
        process.StartInfo.ArgumentList.Add("/inheritance:r");
        process.StartInfo.ArgumentList.Add("/grant:r");
        process.StartInfo.ArgumentList.Add($"*{ownerSid}:(OI)(CI)F");
        process.StartInfo.ArgumentList.Add("/grant:r");
        process.StartInfo.ArgumentList.Add("*S-1-5-18:(OI)(CI)F");
        process.StartInfo.ArgumentList.Add("/grant:r");
        process.StartInfo.ArgumentList.Add("*S-1-5-32-544:(OI)(CI)F");

        if (!process.Start())
        {
            throw new InvalidOperationException("icacls could not be started.");
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
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
            return false;
        }

        string holdFolderPath = GetHoldFolderPath();
        bool wasMissing = !Directory.Exists(holdFolderPath);
        if (!TryEnsureHoldFolder())
        {
            SetTransientStatus("保留を準備できません。");
            return false;
        }

        if (notifyWhenRecreated && wasMissing)
        {
            NotifyShopMaintenance("保留を作り直しました。", "保留ホルダーは再作成されます。");
        }

        return wasMissing;
    }

    private void SuppressExternalChangeNotifications()
    {
        _suppressExternalChangeNotificationsUntil = DateTime.Now.AddSeconds(3);
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

    private void NotifyExternalShopChange()
    {
        if (ShouldSuppressExternalChangeNotification() || !CanShowNotification(externalOnly: true))
        {
            return;
        }

        DateTime now = DateTime.Now;
        if (now - _lastExternalChangeNotificationAt < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastExternalChangeNotificationAt = now;
        NotifyShopMaintenance("お店の中身が変更されました。", "お店の中身が変更されました。");
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
        {
            CloseShop();
        }
        else
        {
            OpenShop();
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        VisitShop(_shopFolder);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentFolder))
            return;

        if (_currentMode == DisplayMode.FriendShop && _activeFriendShop is not null)
        {
            string password = FriendsRepository.UnprotectPassword(_activeFriendShop.PasswordProtected);
            if (!string.IsNullOrEmpty(password))
            {
                string uncRoot = _activeFriendShop.ConnectUncPath;
                var liveShop = SwkNetworkCache.ShopInfos.FirstOrDefault(s =>
                    string.Equals(s.MachineName, _activeFriendShop.HostMachineName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.ShareName, _activeFriendShop.ShareName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(liveShop?.IpAddress))
                    uncRoot = $@"\\{liveShop.IpAddress}\{_activeFriendShop.ShareName}";
                await Task.Run(() => SmbConnectionHelper.EnsureConnection(uncRoot, _activeFriendShop.UserName, password, _activeFriendShop.HostMachineName));
            }
        }

        InvalidateSizeCacheUnder(_currentFolder);
        RefreshShopItems();
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

    private void UserListButton_Click(object sender, RoutedEventArgs e)
    {
        UserListWindow window = new(this) { Owner = this };
        window.ShowDialog();
        PopulateExplorerDropdown();
    }

    private void ShareStatusButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ShopItem item })
        {
            return;
        }

        if (item.IsHoldFolder || _currentMode == DisplayMode.FriendShop)
        {
            return;
        }

        PermissionWindow window = new(item) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _permissionMap[item.FullPath] = (item.AllowedUsers.ToList(), item.IsReadOnly, item.IsSharedOff);
            if (_isShopOpen && _currentMode != DisplayMode.FriendShop && !item.IsHoldFolder)
            {
                if (!SmbNtfsManager.SetSubfolderPermission(item.FullPath, item.IsSharedOff, item.IsReadOnly))
                    SetTransientStatus("権限の設定に失敗しました。");
            }
            SavePermissionMap();
            item.RefreshShareStatus();
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
        if (ShopItemsListView.SelectedItem is ShopItem item)
        {
            OpenShopItem(item);
        }
    }

    private void ShopItemsListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _dragStartItem = GetShopItemFromSource(e.OriginalSource as DependencyObject);
    }

    private void ShopItemsListView_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStartItem is null || _dragStartItem.IsHoldFolder)
        {
            return;
        }

        System.Windows.Point currentPosition = e.GetPosition(null);
        if (Math.Abs(currentPosition.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPosition.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

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

        System.Windows.DataObject data = new();
        data.SetData(System.Windows.DataFormats.FileDrop, itemsToDrag.Select(i => i.FullPath).ToArray());
        if (itemsToDrag.Count == 1)
        {
            data.SetData(InternalDragPathFormat, itemsToDrag[0].FullPath);
        }

        string hint = itemsToDrag.Count == 1 ? itemsToDrag[0].Name : $"{itemsToDrag.Count} つのアイテム";
        ShowDragHint(hint);
        System.Windows.DragDrop.DoDragDrop(ShopItemsListView, data, System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move);
        HideDragHint();
        ClearDropTargetHighlight();
        _dragStartItem = null;
    }

    private void ShopItemsListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
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
        if (ArrivedItems.Count == 0)
        {
            SetTransientStatus("履歴はまだありません。");
            return;
        }

        string historyText = string.Join(
            Environment.NewLine,
            ArrivedItems.Take(20).Select(item => $"{item.ArrivedAtText}  {item.Name}"));
        System.Windows.MessageBox.Show(this, historyText, "履歴", MessageBoxButton.OK, MessageBoxImage.None);
    }

    private void ShopItemsListView_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = GetDropEffect(e);
        UpdateDropTargetHighlight(e);
        if (e.Data.GetDataPresent(InternalDragPathFormat))
        {
            ShowDragHint(Path.GetFileName((string)e.Data.GetData(InternalDragPathFormat)));
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

    private void ShopItemsListView_Drop(object sender, System.Windows.DragEventArgs e)
    {
        ClearDropTargetHighlight();
        HideDragHint();

        if (!CanAcceptDrop(e) || string.IsNullOrWhiteSpace(_currentFolder))
        {
            e.Handled = true;
            return;
        }

        string destinationFolder = GetDropDestinationFolder(e) ?? _currentFolder;
        if (e.Data.GetDataPresent(InternalDragPathFormat))
        {
            MoveInternalDraggedItem((string)e.Data.GetData(InternalDragPathFormat), destinationFolder);
            e.Handled = true;
            return;
        }

        if (!Directory.Exists(destinationFolder))
        {
            SetTransientStatus("その場所が見つかりません。");
            e.Handled = true;
            return;
        }

        string[] paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
        int placedCount = 0;
        string? lastPlacedName = null;
        bool sawSameName = false;
        List<string> placedPaths = new();

        foreach (string sourcePath in paths)
        {
            string sourceName = Path.GetFileName(sourcePath);
            string destinationPath = Path.Combine(destinationFolder, sourceName);
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            {
                sawSameName = true;
                continue;
            }

            try
            {
                SuppressExternalChangeNotifications();
                if (Directory.Exists(sourcePath))
                {
                    CopyDirectory(sourcePath, destinationPath);
                    placedCount++;
                    lastPlacedName = sourceName;
                    placedPaths.Add(destinationPath);
                }
                else if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, destinationPath);
                    placedCount++;
                    lastPlacedName = sourceName;
                    placedPaths.Add(destinationPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetTransientStatus("置けませんでした。");
            }
        }

        if (placedCount > 0)
        {
            string message = placedCount == 1 && lastPlacedName is not null
                ? $"{lastPlacedName} を置きました。"
                : $"{placedCount} つ置きました。";
            SetTransientStatus(message);

            if (string.Equals(
                    Path.GetFullPath(destinationFolder),
                    Path.GetFullPath(_currentFolder),
                    StringComparison.OrdinalIgnoreCase) &&
                lastPlacedName is not null)
            {
                _pendingFocusName = lastPlacedName;
            }

            foreach (string placedPath in placedPaths)
            {
                NoteFutureSharePolicyRepair(placedPath, destinationFolder, SharePolicyRepairReason.Placed);
            }

            InvalidateSizeCacheUnder(destinationFolder);
            RefreshShopItems();
        }
        else if (sawSameName)
        {
            SetTransientStatus("同じ名前があるので置けません。");
        }

        e.Handled = true;
    }

    private void MoveInternalDraggedItem(string sourcePath, string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(destinationFolder))
        {
            SetTransientStatus("その場所が見つかりません。");
            return;
        }

        if (IsHoldFolderPath(sourcePath))
        {
            SetTransientStatus("保留は移せません。");
            return;
        }

        bool sourceIsDirectory = Directory.Exists(sourcePath);
        bool sourceIsFile = File.Exists(sourcePath);
        if (!sourceIsDirectory && !sourceIsFile)
        {
            SetTransientStatus("その場所が見つかりません。");
            return;
        }

        string sourceParent = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        if (string.Equals(
                Path.GetFullPath(sourceParent),
                Path.GetFullPath(destinationFolder),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (sourceIsDirectory && IsUnderFolder(destinationFolder, sourcePath))
        {
            SetTransientStatus("その中へは移せません。");
            return;
        }

        string destinationPath = Path.Combine(destinationFolder, Path.GetFileName(sourcePath));
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            SetTransientStatus("同じ名前があるので移せません。");
            return;
        }

        try
        {
            SuppressExternalChangeNotifications();
            if (sourceIsDirectory)
            {
                Directory.Move(sourcePath, destinationPath);
            }
            else
            {
                File.Move(sourcePath, destinationPath);
            }

            SetTransientStatus($"{Path.GetFileName(sourcePath)} を移しました。");
            NoteFutureSharePolicyRepair(destinationPath, destinationFolder, SharePolicyRepairReason.Moved);
            InvalidateSizeCacheUnder(sourceParent);
            InvalidateSizeCacheUnder(destinationFolder);
            RefreshShopItems();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetTransientStatus("移せませんでした。");
        }
    }

    private void ShopItemsContextMenu_Opened(object sender, RoutedEventArgs e)
    {
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
            MoveToFolderMenuItem.Visibility = Visibility.Collapsed;
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

    private void RenameShopItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
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
        if (string.IsNullOrEmpty(newName) ||
            string.Equals(newName, item.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            SetTransientStatus("その名前には変えられません。");
            return;
        }

        string destinationPath = Path.Combine(sourceParent, newName);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            SetTransientStatus("同じ名前があるので変えられません。");
            return;
        }

        try
        {
            SuppressExternalChangeNotifications();
            if (item.IsDirectory)
            {
                Directory.Move(item.FullPath, destinationPath);
            }
            else
            {
                File.Move(item.FullPath, destinationPath);
            }

            SetTransientStatus($"{item.Name} を {newName} に変えました。");
            NoteFutureSharePolicyRepair(destinationPath, sourceParent, SharePolicyRepairReason.Renamed);
            InvalidateSizeCacheUnder(sourceParent);
            _pendingFocusName = newName;
            RefreshShopItems();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetTransientStatus("名前を変えられませんでした。");
        }
    }

    private void MoveToFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ShopItemsListView.SelectedItem is not ShopItem item)
        {
            return;
        }

        MoveDestinationDialog dialog = new(_shopFolder, GetHoldFolderPath(), item.FullPath)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string? destinationFolder = dialog.SelectedFolderPath;
        if (string.IsNullOrWhiteSpace(destinationFolder) || !Directory.Exists(destinationFolder))
        {
            return;
        }

        string sourceParent = Path.GetDirectoryName(item.FullPath) ?? string.Empty;
        if (string.Equals(
                Path.GetFullPath(destinationFolder),
                Path.GetFullPath(sourceParent),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string destinationPath = Path.Combine(destinationFolder, item.Name);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            SetTransientStatus("同じ名前があるので移せません。");
            return;
        }

        try
        {
            SuppressExternalChangeNotifications();
            if (item.IsDirectory)
            {
                Directory.Move(item.FullPath, destinationPath);
            }
            else
            {
                File.Move(item.FullPath, destinationPath);
            }

            SetTransientStatus($"{item.Name} を移しました。");
            NoteFutureSharePolicyRepair(destinationPath, destinationFolder, SharePolicyRepairReason.Moved);
            InvalidateSizeCacheUnder(sourceParent);
            InvalidateSizeCacheUnder(destinationFolder);
            RefreshShopItems();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetTransientStatus("移せませんでした。");
        }
    }

    private void HoldShopItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        List<ShopItem> items = ShopItemsListView.SelectedItems.Cast<ShopItem>()
            .Where(i => !i.IsHoldFolder)
            .ToList();
        if (items.Count == 0) return;

        if (!TryEnsureHoldFolder())
        {
            SetTransientStatus("保留を準備できません。");
            return;
        }

        string holdFolderPath = GetHoldFolderPath();
        int movedCount = 0;
        string? lastName = null;

        foreach (ShopItem item in items)
        {
            string destinationPath = Path.Combine(holdFolderPath, item.Name);
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath)) continue;

            try
            {
                SuppressExternalChangeNotifications();
                if (item.IsDirectory)
                    Directory.Move(item.FullPath, destinationPath);
                else
                    File.Move(item.FullPath, destinationPath);

                movedCount++;
                lastName = item.Name;
                string? sourceParent = Path.GetDirectoryName(item.FullPath);
                if (!string.IsNullOrWhiteSpace(sourceParent))
                    InvalidateSizeCacheUnder(sourceParent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        if (movedCount > 0)
        {
            InvalidateSizeCacheUnder(holdFolderPath);
            SetTransientStatus(movedCount == 1 && lastName is not null
                ? $"{lastName} を保留にしまいました。"
                : $"{movedCount} つ保留にしまいました。");
            RefreshShopItems();
        }
        else
        {
            SetTransientStatus("しまえませんでした。");
        }
    }

    private void DeleteShopItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        List<ShopItem> items = ShopItemsListView.SelectedItems.Cast<ShopItem>()
            .Where(i => !i.IsHoldFolder)
            .ToList();
        if (items.Count == 0) return;

        string confirmMsg = items.Count == 1
            ? $"{items[0].Name} を完全に消します。よろしいですか?"
            : $"{items.Count} つのアイテムを完全に消します。よろしいですか?";
        MessageBoxResult result = System.Windows.MessageBox.Show(
            this, confirmMsg, "削除", MessageBoxButton.OKCancel, MessageBoxImage.None);
        if (result != MessageBoxResult.OK) return;

        int deletedCount = 0;
        string? lastName = null;

        foreach (ShopItem item in items)
        {
            try
            {
                SuppressExternalChangeNotifications();
                if (item.IsDirectory)
                    Directory.Delete(item.FullPath, recursive: true);
                else
                    File.Delete(item.FullPath);

                deletedCount++;
                lastName = item.Name;
                string? sourceParent = Path.GetDirectoryName(item.FullPath);
                if (!string.IsNullOrWhiteSpace(sourceParent))
                    InvalidateSizeCacheUnder(sourceParent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        if (deletedCount > 0)
        {
            SetTransientStatus(deletedCount == 1 && lastName is not null
                ? $"{lastName} を消しました。"
                : $"{deletedCount} つ消しました。");
            RefreshShopItems();
        }
        else
        {
            SetTransientStatus("消せませんでした。");
        }
    }

    private void AddFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentFolder))
        {
            return;
        }

        ShopItem? selected = ShopItemsListView.SelectedItem as ShopItem;
        string targetFolder = selected is { IsDirectory: true } folderItem
            ? folderItem.FullPath
            : _currentFolder;

        if (!Directory.Exists(targetFolder))
        {
            SetTransientStatus("その場所が見つかりません。");
            return;
        }

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

        if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            SetTransientStatus("その名前では作れません。");
            return;
        }

        string destinationPath = Path.Combine(targetFolder, folderName);
        if (Directory.Exists(destinationPath) || File.Exists(destinationPath))
        {
            SetTransientStatus("同じ名前があるので作れません。");
            return;
        }

        try
        {
            SuppressExternalChangeNotifications();
            Directory.CreateDirectory(destinationPath);
            SetTransientStatus($"{folderName} を作りました。");
            NoteFutureSharePolicyRepair(destinationPath, targetFolder, SharePolicyRepairReason.FolderCreated);
            InvalidateSizeCacheUnder(targetFolder);
            if (string.Equals(
                    Path.GetFullPath(targetFolder),
                    Path.GetFullPath(_currentFolder),
                    StringComparison.OrdinalIgnoreCase))
            {
                _pendingFocusName = folderName;
            }
            RefreshShopItems();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetTransientStatus("作れませんでした。");
        }
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
        if (!_exitRequested && _isShopOpen)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _notifyIcon.BalloonTipTitle = "お店は開いたままです";
                _notifyIcon.BalloonTipText = "お店は開いたまま、画面だけ閉じています。";
                _notifyIcon.ShowBalloonTip(3000);
                _trayHintShown = true;
            }
            return;
        }

        CancelFolderSizeCalculation();
        CloseShop(removeSmbShare: false);
        _notificationTimer.Stop();
        _notificationTimer.Tick -= NotificationTimer_Tick;
        _pollingTimer.Stop();
        _pollingTimer.Tick -= PollingTimer_Tick;
        StopFriendShopPolling();
        _transientStatusTimer.Stop();
        _transientStatusTimer.Tick -= TransientStatusTimer_Tick;
        _notifyIcon.BalloonTipClicked -= NotifyIcon_BalloonTipClicked;
        _notifyIcon.MouseDoubleClick -= NotifyIcon_MouseDoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayMenu.Dispose();
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

    private void NotifyIcon_MouseDoubleClick(object? sender, Forms.MouseEventArgs e)
    {
        Dispatcher.Invoke(ShowMainWindowWithPassword);
    }

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

    private void RestoreOpenShopIfNeeded()
    {
        if (!_wasOpenAtLastShutdown || _isShopOpen || string.IsNullOrWhiteSpace(_shopFolder))
        {
            return;
        }

        if (!Directory.Exists(_shopFolder))
        {
            SwkLogger.Warn("Startup restore skipped: shop folder not found");
            return;
        }

        OpenShop();
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

        if (!TryEnsureHoldFolder())
        {
            UpdateShopState(false, "保留を準備できません。");
            return;
        }

        ShopOpenRequest request = new(
            ShareName: shareName,
            ShopRootPath: _shopFolder,
            ProfileLabel: shareName,
            AccessRight: _shareAccessRight);

        ShopOpenResult result;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            result = SmbController.OpenShopSequence(request);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        // 草案6 §A: 所有者書き換えが必要な場合は、利用者の明示同意を挟む。
        // 事前確認できた場合と、フォルダーが完全ロックで確認すらできなかった場合で文言を分ける。
        if (result.OwnershipPrompt != OwnershipChangePrompt.None)
        {
            string consentBody = result.OwnershipPrompt == OwnershipChangePrompt.Unverifiable
                ? "このフォルダーは現在の権限では中身を確認できません。\n所有者を変更してから処理を進めますか?\n※内包されたデータがある場合、すべて公開されます。"
                : "このフォルダーを利用するには、所有者の変更が必要です。\n※内包されたデータがある場合、すべて公開されます。";

            MessageBoxResult consent = System.Windows.MessageBox.Show(
                this,
                consentBody,
                "確認",
                MessageBoxButton.OKCancel,
                MessageBoxImage.None);

            if (consent != MessageBoxResult.OK)
            {
                UpdateShopState(false, "開店を取りやめました。");
                return;
            }

            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                result = SmbController.OpenShopSequence(request, userAuthorizedOwnershipChange: true);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        if (!result.Succeeded)
        {
            string message = string.IsNullOrWhiteSpace(result.FailureMessage)
                ? "お店を開けませんでした。"
                : result.FailureMessage!;
            if (result.BlockedPaths is { Count: > 0 } blocked)
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

        _isShopOpen = true;
        _wasOpenAtLastShutdown = true;
        SaveSettings();

        UpdateShopState(true);
        if (_currentMode == DisplayMode.Shop)
        {
            NavigateTo(_shopFolder, addHistory: false, clearForward: true);
        }
    }

    private void CloseShop(bool removeSmbShare = true)
    {
        DisposeWatcher();
        _pollingTimer.Stop();
        bool wasOpen = _isShopOpen;
        _isShopOpen = false;
        _isPollingMode = false;
        CancelFolderSizeCalculation();

        if (wasOpen && removeSmbShare && !string.IsNullOrWhiteSpace(_shopFolder))
        {
            string shareName = DeriveShareName(_shopFolder);
            if (!string.IsNullOrWhiteSpace(shareName))
            {
                Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                try
                {
                    SmbController.CloseShopSequence(shareName, _shopFolder);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                }
            }
        }

        if (_currentMode == DisplayMode.Shop)
        {
            DisposeContentsWatcher();
            _currentFolder = null;
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

    private void DisposeContentsWatcher()
    {
        if (_contentsSensor is null)
        {
            return;
        }

        _contentsSensor.EnableRaisingEvents = false;
        _contentsSensor.Created -= ContentsSensor_Changed;
        _contentsSensor.Deleted -= ContentsSensor_Changed;
        _contentsSensor.Changed -= ContentsSensor_Changed;
        _contentsSensor.Renamed -= ContentsSensor_Renamed;
        _contentsSensor.Error -= ContentsSensor_Error;
        _contentsSensor.Dispose();
        _contentsSensor = null;
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
            ArrivedItem item = new(
                Path.GetFileName(path),
                Path.GetDirectoryName(path) ?? string.Empty,
                DateTime.Now);
            ArrivedItems.Insert(0, item);
            while (ArrivedItems.Count > MaxArrivedItemCount)
            {
                ArrivedItems.RemoveAt(ArrivedItems.Count - 1);
            }
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
            if (e.ChangeType == WatcherChangeTypes.Created && !Directory.Exists(e.FullPath))
            {
                ArrivedItem item = new(
                    e.Name ?? Path.GetFileName(e.FullPath),
                    Path.GetDirectoryName(e.FullPath) ?? string.Empty,
                    DateTime.Now);
                ArrivedItems.Insert(0, item);

                while (ArrivedItems.Count > MaxArrivedItemCount)
                {
                    ArrivedItems.RemoveAt(ArrivedItems.Count - 1);
                }
            }

            NotifyExternalShopChange();
            NoteFutureSharePolicyRepair(
                e.FullPath,
                Path.GetDirectoryName(e.FullPath) ?? _shopFolder ?? string.Empty,
                SharePolicyRepairReason.ExternalCreated);
            RefreshShopItemsIfCurrentFolder(Path.GetDirectoryName(e.FullPath) ?? string.Empty);
        });
    }

    private void ArrivalSensor_Renamed(object sender, RenamedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            NotifyExternalShopChange();
            NoteFutureSharePolicyRepair(
                e.FullPath,
                Path.GetDirectoryName(e.FullPath) ?? _shopFolder ?? string.Empty,
                SharePolicyRepairReason.ExternalRenamed);
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
            return;
        }

        _pendingNotificationItems.Add(item);
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    private void NotifyShopMaintenance(string statusMessage, string notificationText)
    {
        SetTransientStatus(statusMessage);
        if (!CanShowNotification(externalOnly: true))
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = "ShareWorkin のお知らせ";
        _notifyIcon.BalloonTipText = notificationText;
        _lastNotificationFolder = _shopFolder;
        _notifyIcon.ShowBalloonTip(5000);
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
            return;
        }

        _notifyIcon.BalloonTipTitle = "ShareWorkin のお知らせ";
        _notifyIcon.BalloonTipText = "お店の中身が変更されました。";
        _lastNotificationFolder = _pendingNotificationItems[0].FolderPath;
        _pendingNotificationItems.Clear();
        _notifyIcon.ShowBalloonTip(5000);
    }

    private void NotifyIcon_BalloonTipClicked(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() => VisitShop(_lastNotificationFolder ?? _shopFolder));
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
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            SetTransientStatus("見に行ける場所がありません。");
            return;
        }

        if (!Directory.Exists(folderPath))
        {
            SetTransientStatus("その場所が見つかりません。");
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
        }
    }

    private void SearchFromShopRoot(string searchText)
    {
        string query = searchText.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_shopFolder) || !Directory.Exists(_shopFolder))
        {
            SetTransientStatus("検索できるお店がありません。");
            return;
        }

        string? match = FindFirstShopItem(_shopFolder, query);
        if (string.IsNullOrWhiteSpace(match))
        {
            SetTransientStatus("見つかりませんでした。");
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
        }
        else
        {
            _currentMode = DisplayMode.Shop;
            _activeFriendShop = null;
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

        try
        {
            EnsureHoldFolderForShopChange(notifyWhenRecreated: false);

            List<ShopItem> all = Directory.EnumerateDirectories(_currentFolder)
                .Where(path => !IsHoldFolderPath(path) &&
                               !(_currentMode == DisplayMode.FriendShop &&
                                 string.Equals(Path.GetFileName(path), HoldFolderName, StringComparison.OrdinalIgnoreCase)))
                .Select(path => ShopItem.FromPath(path, isDirectory: true, isHoldFolder: false))
                .Concat(Directory.EnumerateFiles(_currentFolder)
                    .Select(path => ShopItem.FromPath(path, isDirectory: false)))
                .ToList();

            foreach (ShopItem item in all)
            {
                if (item.IsDirectory && _folderSizeCache.TryGetValue(item.FullPath, out long cached))
                {
                    item.SetSize(cached);
                }

                if (_currentMode == DisplayMode.FriendShop)
                {
                    item.IsFromFriendShop = true;
                }
                else if (_permissionMap.TryGetValue(item.FullPath, out var perm))
                {
                    foreach (string user in perm.Users) item.AllowedUsers.Add(user);
                    item.IsReadOnly = perm.IsReadOnly;
                    item.IsSharedOff = perm.IsSharedOff;
                }
                else if (!item.IsHoldFolder && _effectiveParentPerm.HasValue)
                {
                    foreach (string user in _effectiveParentPerm.Value.Users) item.AllowedUsers.Add(user);
                    item.IsReadOnly = _effectiveParentPerm.Value.IsReadOnly;
                    item.IsSharedOff = _effectiveParentPerm.Value.IsSharedOff;
                }
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
    }

    private void StartFriendShopPolling()
    {
        if (_friendShopPollTimer != null) return;
        _friendShopPollTimer = new DispatcherTimer { Interval = PollingInterval };
        _friendShopPollTimer.Tick += FriendShopPollTimer_Tick;
        _friendShopPollTimer.Start();
    }

    private void StopFriendShopPolling()
    {
        if (_friendShopPollTimer == null) return;
        _friendShopPollTimer.Stop();
        _friendShopPollTimer.Tick -= FriendShopPollTimer_Tick;
        _friendShopPollTimer = null;
    }

    private bool _friendShopPollRunning;

    private async void FriendShopPollTimer_Tick(object? sender, EventArgs e)
    {
        if (_currentMode != DisplayMode.FriendShop || string.IsNullOrEmpty(_currentFolder)) return;
        if (_friendShopPollRunning) return;
        _friendShopPollRunning = true;
        try
        {
            RefreshShopItems();
            await ApplyFriendShopReadOnlyAsync(_currentFolder, ShopItems.ToList());
        }
        finally
        {
            _friendShopPollRunning = false;
        }
    }

    private static async Task ApplyFriendShopReadOnlyAsync(string folder, List<ShopItem> items)
    {
        bool folderWritable = await Task.Run(() => ProbeWriteAccess(folder));
        foreach (ShopItem item in items)
        {
            bool isReadOnly = item.IsDirectory
                ? !await Task.Run(() => ProbeWriteAccess(item.FullPath))
                : !folderWritable;
            item.IsReadOnly = isReadOnly;
        }
    }

    private static bool ProbeWriteAccess(string path)
    {
        if (!Directory.Exists(path)) return false;
        try
        {
            string tmp = Path.Combine(path, $".swk_{Path.GetRandomFileName()}");
            using var _ = File.Create(tmp, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private IEnumerable<ShopItem> SortShopItems(IEnumerable<ShopItem> items)
    {
        bool ascending = _sortDirection == ListSortDirection.Ascending;
        return _sortField switch
        {
            ShopSortField.Name => ascending
                ? items.OrderBy(i => i.SortKey).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                : items.OrderBy(i => i.SortKey).ThenByDescending(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
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
            if (!string.IsNullOrWhiteSpace(_currentFolder))
            {
                InvalidateSizeCacheUnder(_currentFolder);
            }
            bool recreatedHoldFolder = EnsureHoldFolderForShopChange(notifyWhenRecreated: true);
            bool shouldNotifyChange =
                e.ChangeType == WatcherChangeTypes.Deleted ||
                e.ChangeType == WatcherChangeTypes.Created;
            if (!recreatedHoldFolder && shouldNotifyChange)
            {
                NotifyExternalShopChange();
            }
            if (e.ChangeType == WatcherChangeTypes.Created)
            {
                NoteFutureSharePolicyRepair(
                    e.FullPath,
                    Path.GetDirectoryName(e.FullPath) ?? _currentFolder ?? string.Empty,
                    SharePolicyRepairReason.ExternalCreated);
            }
            RefreshShopItems();
        });
    }

    private void ContentsSensor_Renamed(object sender, RenamedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrWhiteSpace(_currentFolder))
            {
                InvalidateSizeCacheUnder(_currentFolder);
            }
            EnsureHoldFolderForShopChange(notifyWhenRecreated: true);
            NotifyExternalShopChange();
            NoteFutureSharePolicyRepair(
                e.FullPath,
                Path.GetDirectoryName(e.FullPath) ?? _currentFolder ?? string.Empty,
                SharePolicyRepairReason.ExternalRenamed);
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

        SharePolicyRepair.MarkActionAftercare(_shopFolder, affectedPath, policySourceFolder, reason);
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

        try
        {
            string root = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string current = _currentFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(root, current, StringComparison.OrdinalIgnoreCase)) return string.Empty;

            if (current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                string relative = current[(root.Length + 1)..];
                string[] segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                return string.Join(" / ", segments);
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private string? GetCurrentRootPath()
    {
        if (_currentMode == DisplayMode.FriendShop && _activeFriendShop != null)
            return _activeFriendShop.ConnectUncPath;
        return string.IsNullOrWhiteSpace(_shopFolder) ? null : Path.GetFullPath(_shopFolder);
    }

    private void UpdateBreadcrumbDisplay()
    {
        SyncDropdownToCurrentMode();
        CurrentPathTextBlock.Text = string.IsNullOrEmpty(_breadcrumbFullText)
            ? string.Empty
            : $"›  {_breadcrumbFullText}";
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
            ExplorerTargetComboBox.Items.Add(new ExplorerTarget("わたしのお店", null));

            IReadOnlyList<Friend> friends = FriendsRepository.LoadAll();
            foreach (Friend f in friends
                .OrderBy(f => string.IsNullOrWhiteSpace(f.DisplayName) ? f.HostMachineName : f.DisplayName,
                         StringComparer.CurrentCultureIgnoreCase))
            {
                string label = string.IsNullOrWhiteSpace(f.DisplayName) ? f.HostMachineName : f.DisplayName;
                ExplorerTargetComboBox.Items.Add(new ExplorerTarget(label, f));
            }

            SyncDropdownToCurrentMode();
        }
        finally
        {
            _suppressDropdownChange = false;
        }
    }

    private void ExplorerTargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDropdownChange) return;
        if (ExplorerTargetComboBox.SelectedItem is not ExplorerTarget target) return;

        SwkLogger.Debug($"ExplorerTargetComboBox_SelectionChanged: {target.Label}");

        if (target.Friend == null)
        {
            _activeFriendShop = null;
            EnterShopMode();
        }
        else
        {
            _ = NavigateToFriendShopAsync(target.Friend);
        }
    }

    private async Task NavigateToFriendShopAsync(Friend friend)
    {
        string label = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName;
        SwkLogger.Debug($"NavigateToFriendShopAsync: {label} ({friend.ConnectUncPath})");

        _activeFriendShop = friend;
        _currentMode = DisplayMode.FriendShop;
        CancelFolderSizeCalculation();
        DisposeContentsWatcher();
        _currentFolder = null;
        ShopItems.Clear();
        _backStack.Clear();
        _forwardStack.Clear();
        UpdateBreadcrumb();
        UpdateNavigationState();

        string uncPath = friend.ConnectUncPath;
        if (string.IsNullOrWhiteSpace(uncPath))
        {
            SetTransientStatus("接続できません");
            return;
        }

        // キャッシュに生きた IP があれば優先使用（LastKnownAddress が空や古い場合も対応）
        var liveShop = SwkNetworkCache.ShopInfos.FirstOrDefault(s =>
            string.Equals(s.MachineName, friend.HostMachineName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.ShareName, friend.ShareName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(liveShop?.IpAddress))
            uncPath = $@"\\{liveShop.IpAddress}\{friend.ShareName}";

        SwkLogger.Debug($"NavigateToFriendShopAsync: resolved={uncPath}");

        string password = FriendsRepository.UnprotectPassword(friend.PasswordProtected);
        bool accessible = await Task.Run(() =>
        {
            if (!string.IsNullOrEmpty(password))
                SmbConnectionHelper.EnsureConnection(uncPath, friend.UserName, password, friend.HostMachineName);
            try { return Directory.Exists(uncPath); }
            catch { return false; }
        });

        if (!accessible)
        {
            SwkLogger.Warn($"NavigateToFriendShopAsync: not accessible: {uncPath}");
            SetTransientStatus("接続できません");
            return;
        }

        NavigateTo(uncPath, addHistory: false, clearForward: true);
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
        SetTransientStatus("アクセス履歴は準備中です。");
    }

    private void UpdateHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        SetTransientStatus("更新履歴は準備中です。");
    }

    private void NotificationHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        SetTransientStatus("通知履歴は準備中です。");
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
        if (_currentMode == DisplayMode.FriendShop) return false;
        return !string.IsNullOrWhiteSpace(_currentFolder) &&
               Directory.Exists(_currentFolder) &&
               (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ||
                e.Data.GetDataPresent(InternalDragPathFormat));
    }

    private System.Windows.DragDropEffects GetDropEffect(System.Windows.DragEventArgs e)
    {
        if (!CanAcceptDrop(e))
        {
            return System.Windows.DragDropEffects.None;
        }

        return e.Data.GetDataPresent(InternalDragPathFormat)
            ? System.Windows.DragDropEffects.Move
            : System.Windows.DragDropEffects.Copy;
    }

    private string? GetDropDestinationFolder(System.Windows.DragEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null)
        {
            if (current is System.Windows.Controls.ListViewItem { DataContext: ShopItem { IsDirectory: true } item })
            {
                return item.FullPath;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return _currentFolder;
    }

    private void UpdateDropTargetHighlight(System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(InternalDragPathFormat))
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

    private static ShopItem? GetDropDestinationItem(System.Windows.DragEventArgs e)
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
        try
        {
            var entries = _permissionMap.Select(kv => new PermissionEntry
            {
                Path = kv.Key,
                Users = kv.Value.Users,
                IsReadOnly = kv.Value.IsReadOnly,
                IsSharedOff = kv.Value.IsSharedOff
            }).ToList();
            File.WriteAllText(PermissionsPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }

    private void LoadPermissionMap()
    {
        _permissionMap.Clear();
        if (!File.Exists(PermissionsPath)) return;
        try
        {
            var entries = JsonSerializer.Deserialize<List<PermissionEntry>>(File.ReadAllText(PermissionsPath));
            if (entries is null) return;
            foreach (var e in entries)
            {
                if (!string.IsNullOrEmpty(e.Path))
                    _permissionMap[e.Path] = (e.Users ?? [], e.IsReadOnly, e.IsSharedOff);
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
        ShopDoorButton.Content = isOpen ? "共有を閉じる" : "お店を開く";
        ClosedShopPanel.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
        OpenShopPanel.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;

        string closedText;
        string openText;
        string openTooltip = _shopFolder ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            closedText = statusMessage;
            openText = statusMessage;
            openTooltip = statusMessage;
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

    public string KindText => IsDirectory ? "フォルダー" : "ファイル";

    // Per-item sharing state. Empty list = 全員, non-empty = 指定.
    // The list holds nicknames as shown in 許可指定; ACL wiring is deferred to
    // v2.2 spec freeze, so this is currently in-memory only.
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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WatchFolder { get; set; }

    [JsonPropertyName("_reservedForV22")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ReservedForV22 { get; set; }
}

public sealed record ExplorerTarget(string Label, Friend? Friend);
