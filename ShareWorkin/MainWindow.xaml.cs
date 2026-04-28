using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ShareWorkin;

public partial class MainWindow : Window
{
    private const int MaxArrivedItemCount = 100;

    private static readonly TimeSpan NotificationQuietTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TransientStatusDuration = TimeSpan.FromSeconds(4);

    private static readonly string SettingsDirectory = AppContext.BaseDirectory;

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly string HoldFolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShareWorkin",
        "hold");

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
    private FileSystemWatcher? _arrivalSensor;
    private FileSystemWatcher? _contentsSensor;
    private string? _shopFolder;
    private string? _currentFolder;
    private string? _lastNotificationFolder;
    private string? _pendingFocusName;
    private JsonElement? _loadedReservedForV22;
    private bool _notificationEnabled = true;
    private bool _isShopOpen;
    private bool _isPollingMode;
    private bool _exitRequested;
    private bool _trayHintShown;
    private DisplayMode _currentMode = DisplayMode.Shop;

    public ObservableCollection<ArrivedItem> ArrivedItems { get; } = [];

    public ObservableCollection<ShopItem> ShopItems { get; } = [];

    public MainWindow()
    {
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
        _trayShowItem = new Forms.ToolStripMenuItem("お店を見る");
        _trayShowItem.Click += (_, _) => Dispatcher.Invoke(ShowMainWindow);
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

        EnsureHoldFolder();
        LoadSettings();
        NotificationCheckBox.Checked += NotificationCheckBox_Changed;
        NotificationCheckBox.Unchecked += NotificationCheckBox_Changed;
        UpdateShopState(false);
    }

    private static void EnsureHoldFolder()
    {
        try
        {
            Directory.CreateDirectory(HoldFolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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

        _shopFolder = dialog.SelectedPath;
        MyShopTextBox.Text = _shopFolder;
        SaveSettings();
        ReopenShopIfNeeded();
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

    private void NotificationCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _notificationEnabled = NotificationCheckBox.IsChecked == true;
        if (!_notificationEnabled)
        {
            _notificationTimer.Stop();
            _pendingNotificationItems.Clear();
        }

        SaveSettings();
    }

    private void ShopItemsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ShopItemsListView.SelectedItem is ShopItem item)
        {
            OpenShopItem(item);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_backStack.Count == 0 || string.IsNullOrWhiteSpace(_currentFolder))
        {
            return;
        }

        _forwardStack.Push(_currentFolder);
        NavigateTo(_backStack.Pop(), addHistory: false);
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_forwardStack.Count == 0 || string.IsNullOrWhiteSpace(_currentFolder))
        {
            return;
        }

        _backStack.Push(_currentFolder);
        NavigateTo(_forwardStack.Pop(), addHistory: false);
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (ArrivedItems.Count == 0)
        {
            StatusTextBlock.Text = "履歴はまだありません。";
            return;
        }

        string historyText = string.Join(
            Environment.NewLine,
            ArrivedItems.Take(20).Select(item => $"{item.ArrivedAtText}  {item.Name}"));
        System.Windows.MessageBox.Show(this, historyText, "履歴", MessageBoxButton.OK, MessageBoxImage.None);
    }

    private void ShopItemsListView_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = CanAcceptDrop(e) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void ShopItemsListView_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!CanAcceptDrop(e) || string.IsNullOrWhiteSpace(_currentFolder))
        {
            e.Handled = true;
            return;
        }

        string destinationFolder = GetDropDestinationFolder(e) ?? _currentFolder;
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
                if (Directory.Exists(sourcePath))
                {
                    CopyDirectory(sourcePath, destinationPath);
                    placedCount++;
                    lastPlacedName = sourceName;
                }
                else if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, destinationPath);
                    placedCount++;
                    lastPlacedName = sourceName;
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

            RefreshShopItems();
        }
        else if (sawSameName)
        {
            SetTransientStatus("同じ名前があるので置けません。");
        }

        e.Handled = true;
    }

    private void ShopItemsContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool hasSelection = ShopItemsListView.SelectedItem is ShopItem;
        HoldShopItemMenuItem.IsEnabled = hasSelection;
        HoldShopItemMenuItem.Visibility = _currentMode == DisplayMode.Hold
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void MoveShopItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ShopItemsListView.SelectedItem is not ShopItem item)
        {
            return;
        }

        MoveDestinationDialog dialog = new(_shopFolder, HoldFolderPath, item.FullPath)
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
            if (item.IsDirectory)
            {
                Directory.Move(item.FullPath, destinationPath);
            }
            else
            {
                File.Move(item.FullPath, destinationPath);
            }

            SetTransientStatus($"{item.Name} を移しました。");
            RefreshShopItems();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetTransientStatus("移せませんでした。");
        }
    }

    private void HoldShopItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ShopItemsListView.SelectedItem is not ShopItem item)
        {
            return;
        }

        EnsureHoldFolder();
        string destinationPath = Path.Combine(HoldFolderPath, item.Name);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            SetTransientStatus("同じ名前が保留にあるので、しまえません。");
            return;
        }

        try
        {
            if (item.IsDirectory)
            {
                Directory.Move(item.FullPath, destinationPath);
            }
            else
            {
                File.Move(item.FullPath, destinationPath);
            }

            SetTransientStatus($"{item.Name} を保留にしまいました。");
            RefreshShopItems();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetTransientStatus("しまえませんでした。");
        }
    }

    private void DeleteShopItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ShopItemsListView.SelectedItem is not ShopItem item)
        {
            return;
        }

        MessageBoxResult result = System.Windows.MessageBox.Show(
            this,
            $"{item.Name} を完全に消します。よろしいですか?",
            "削除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.None);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            if (item.IsDirectory)
            {
                Directory.Delete(item.FullPath, recursive: true);
            }
            else
            {
                File.Delete(item.FullPath);
            }

            SetTransientStatus($"{item.Name} を消しました。");
            RefreshShopItems();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetTransientStatus("消せませんでした。");
        }
    }

    private void HoldViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMode == DisplayMode.Hold)
        {
            EnterShopMode();
        }
        else
        {
            EnterHoldMode();
        }
    }

    private void EnterHoldMode()
    {
        EnsureHoldFolder();
        _currentMode = DisplayMode.Hold;
        _backStack.Clear();
        _forwardStack.Clear();
        SectionTitleTextBlock.Text = "保留";
        HoldViewButton.Content = "お店の中身を見る";
        NavigateTo(HoldFolderPath, addHistory: false, clearForward: true);
    }

    private void EnterShopMode()
    {
        _currentMode = DisplayMode.Shop;
        _backStack.Clear();
        _forwardStack.Clear();
        SectionTitleTextBlock.Text = "お店の中身";
        HoldViewButton.Content = "保留を見る";

        if (_isShopOpen && !string.IsNullOrWhiteSpace(_shopFolder) && Directory.Exists(_shopFolder))
        {
            NavigateTo(_shopFolder, addHistory: false, clearForward: true);
        }
        else
        {
            DisposeContentsWatcher();
            _currentFolder = null;
            ShopItems.Clear();
            CurrentLocationTextBlock.Text = "お店を開くと、ここに中身が並びます。";
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
                _notifyIcon.BalloonTipText = "トレイからお店を見られます。";
                _notifyIcon.ShowBalloonTip(3000);
                _trayHintShown = true;
            }
            return;
        }

        CloseShop();
        _notificationTimer.Stop();
        _notificationTimer.Tick -= NotificationTimer_Tick;
        _pollingTimer.Stop();
        _pollingTimer.Tick -= PollingTimer_Tick;
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

    private void ExitApp()
    {
        _exitRequested = true;
        Close();
    }

    private void NotifyIcon_MouseDoubleClick(object? sender, Forms.MouseEventArgs e)
    {
        Dispatcher.Invoke(ShowMainWindow);
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

        DisposeWatcher();
        _pollingTimer.Stop();

        try
        {
            _arrivalSensor = new FileSystemWatcher(_shopFolder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _arrivalSensor.Created += ArrivalSensor_Created;
            _arrivalSensor.Error += ArrivalSensor_Error;
            _isPollingMode = false;
            _isShopOpen = true;
            UpdateShopState(true);
            if (_currentMode == DisplayMode.Shop)
            {
                NavigateTo(_shopFolder, addHistory: false, clearForward: true);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            DisposeWatcher();
            BeginPolling();
        }
    }

    private void CloseShop()
    {
        DisposeWatcher();
        _pollingTimer.Stop();
        _isShopOpen = false;
        _isPollingMode = false;

        if (_currentMode == DisplayMode.Shop)
        {
            DisposeContentsWatcher();
            _currentFolder = null;
            _backStack.Clear();
            _forwardStack.Clear();
            ShopItems.Clear();
            UpdateNavigationState();
        }

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
            foreach (string file in Directory.EnumerateFiles(_shopFolder))
            {
                _knownFiles.Add(file);
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
            snapshot = Directory.EnumerateFiles(_shopFolder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var current = new HashSet<string>(snapshot, StringComparer.OrdinalIgnoreCase);
        var newcomers = current.Except(_knownFiles).ToList();

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
            QueueNotification(item);
        }

        _knownFiles.Clear();
        foreach (string path in current)
        {
            _knownFiles.Add(path);
        }
    }

    private void ReopenShopIfNeeded()
    {
        if (_isShopOpen)
        {
            OpenShop();
        }
        else
        {
            UpdateShopState(false);
        }
    }

    private void ArrivalSensor_Created(object sender, FileSystemEventArgs e)
    {
        if (Directory.Exists(e.FullPath))
        {
            return;
        }

        Dispatcher.Invoke(() =>
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

            QueueNotification(item);
            RefreshShopItemsIfCurrentFolder(item.FolderPath);
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

    private void QueueNotification(ArrivedItem item)
    {
        if (!_notificationEnabled)
        {
            return;
        }

        _pendingNotificationItems.Add(item);
        _notificationTimer.Stop();
        _notificationTimer.Start();
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

        ArrivedItem latestItem = _pendingNotificationItems[0];
        string notificationText = _pendingNotificationItems.Count == 1
            ? latestItem.Name
            : $"{_pendingNotificationItems.Count} つ届きました";

        _notifyIcon.BalloonTipTitle = "あなたのお店に、何か届きました";
        _notifyIcon.BalloonTipText = notificationText;
        _lastNotificationFolder = latestItem.FolderPath;
        _pendingNotificationItems.Clear();
        _notifyIcon.ShowBalloonTip(5000);
    }

    private void NotifyIcon_BalloonTipClicked(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() => VisitShop(_lastNotificationFolder ?? _shopFolder));
    }

    private void OpenShopItem(ShopItem item)
    {
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
            StatusTextBlock.Text = "見に行ける場所がありません。";
            return;
        }

        if (!Directory.Exists(folderPath))
        {
            StatusTextBlock.Text = "その場所が見つかりません。";
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
            StatusTextBlock.Text = "その場所へ行けませんでした。";
        }
    }

    private void OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            StatusTextBlock.Text = "その場所が見つかりません。";
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
            StatusTextBlock.Text = "開けませんでした。";
        }
    }

    private void NavigateTo(string folderPath, bool addHistory, bool clearForward = false)
    {
        if (!Directory.Exists(folderPath))
        {
            StatusTextBlock.Text = "その場所が見つかりません。";
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

        _currentFolder = folderPath;
        RefreshShopItems();
        StartContentsSensor(folderPath);
        UpdateNavigationState();
    }

    private void RefreshShopItems()
    {
        ShopItems.Clear();

        if (string.IsNullOrWhiteSpace(_currentFolder) || !Directory.Exists(_currentFolder))
        {
            CurrentLocationTextBlock.Text = _currentMode == DisplayMode.Hold
                ? "保留に何もありません。"
                : "お店を開くと、ここに中身が並びます。";
            return;
        }

        try
        {
            IEnumerable<ShopItem> directories = Directory.EnumerateDirectories(_currentFolder)
                .Select(path => ShopItem.FromPath(path, isDirectory: true));
            IEnumerable<ShopItem> files = Directory.EnumerateFiles(_currentFolder)
                .Select(path => ShopItem.FromPath(path, isDirectory: false));

            foreach (ShopItem item in directories.Concat(files).OrderBy(item => item.SortKey).ThenBy(item => item.Name))
            {
                ShopItems.Add(item);
            }

            CurrentLocationTextBlock.Text = $"現在地: {GetDisplayPath(_currentFolder)}";

            ApplyPendingFocus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CurrentLocationTextBlock.Text = "中身を見られませんでした。";
        }
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
        Dispatcher.Invoke(RefreshShopItems);
    }

    private void ContentsSensor_Renamed(object sender, RenamedEventArgs e)
    {
        Dispatcher.Invoke(RefreshShopItems);
    }

    private void ContentsSensor_Error(object sender, ErrorEventArgs e)
    {
        Dispatcher.Invoke(RefreshShopItems);
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

    private string GetDisplayPath(string folderPath)
    {
        string? rootPath;
        string rootLabel;
        if (_currentMode == DisplayMode.Hold)
        {
            rootPath = HoldFolderPath;
            rootLabel = "保留";
        }
        else
        {
            rootPath = _shopFolder;
            rootLabel = string.IsNullOrWhiteSpace(_shopFolder)
                ? "お店"
                : Path.GetFileName(_shopFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return folderPath;
        }

        string root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string current = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(root, current, StringComparison.OrdinalIgnoreCase))
        {
            return rootLabel;
        }

        if (current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return rootLabel + current[root.Length..];
        }

        return folderPath;
    }

    private void SetTransientStatus(string message)
    {
        StatusTextBlock.Text = message;
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
        return !string.IsNullOrWhiteSpace(_currentFolder) &&
               Directory.Exists(_currentFolder) &&
               e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop);
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
        if (!File.Exists(SettingsPath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(SettingsPath);
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json);
            _shopFolder = settings?.ShopFolder ?? settings?.WatchFolder;
            MyShopTextBox.Text = _shopFolder ?? string.Empty;
            _notificationEnabled = settings?.NotificationEnabled ?? true;
            NotificationCheckBox.IsChecked = _notificationEnabled;
            _loadedReservedForV22 = settings?.ReservedForV22;
        }
        catch (IOException)
        {
            StatusTextBlock.Text = "設定を読み込めません。";
        }
        catch (JsonException)
        {
            StatusTextBlock.Text = "設定を読み込めません。";
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            AppSettings settings = new()
            {
                ShopFolder = _shopFolder,
                NotificationEnabled = _notificationEnabled,
                Version = "2.1",
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
        ShopDoorButton.Content = isOpen ? "お店を閉じる" : "お店を開く";

        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            StatusTextBlock.Text = statusMessage;
        }
        else if (isOpen)
        {
            StatusTextBlock.Text = _isPollingMode
                ? $"開店中: {_shopFolder}（気配の届け方を変えました）"
                : $"開店中: {_shopFolder}";
        }
        else if (string.IsNullOrWhiteSpace(_shopFolder))
        {
            StatusTextBlock.Text = "共有する場所を選んでください。";
        }
        else
        {
            StatusTextBlock.Text = $"閉店中: {_shopFolder}";
        }
    }
}

public sealed record ShopItem(string Name, string FullPath, bool IsDirectory, DateTime UpdatedAt)
{
    public string KindText => IsDirectory ? "フォルダー" : "ファイル";

    public string UpdatedAtText => UpdatedAt.ToString("yyyy/MM/dd HH:mm:ss");

    public string SortKey => IsDirectory ? "0" : "1";

    public static ShopItem FromPath(string path, bool isDirectory)
    {
        DateTime updatedAt = isDirectory
            ? Directory.GetLastWriteTime(path)
            : File.GetLastWriteTime(path);
        return new ShopItem(Path.GetFileName(path), path, isDirectory, updatedAt);
    }
}

public sealed record ArrivedItem(string Name, string FolderPath, DateTime ArrivedAt)
{
    public string ArrivedAtText => ArrivedAt.ToString("yyyy/MM/dd HH:mm:ss");
}

public sealed class AppSettings
{
    [JsonPropertyName("_v")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShopFolder { get; set; }

    public bool NotificationEnabled { get; set; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WatchFolder { get; set; }

    [JsonPropertyName("_reservedForV22")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ReservedForV22 { get; set; }
}
