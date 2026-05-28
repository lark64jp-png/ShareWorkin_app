using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShareWorkin.SMB;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace ShareWorkin;

public partial class UserListWindow : Window
{
    private readonly Window _ownerWindow;
    private readonly ObservableCollection<UserListRow> _rows = new();
    private readonly DispatcherTimer _autoRefreshTimer;
    private bool _isRefreshing;
    private bool _hasFriendUpdates;
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(8);
    private static readonly string UserListStatePath = Path.Combine(
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        "userlist-state.json");

    private const int MaxAutoRecoveryAttempts = 3;
    private static readonly TimeSpan UnstableWindow = TimeSpan.FromMinutes(5);
    private static readonly Dictionary<string, FriendConnectionTracker> _connectionTrackers =
        new(StringComparer.Ordinal);

    public UserListWindow(Window owner)
    {
        InitializeComponent();
        _ownerWindow = owner;
        UserListView.ItemsSource = _rows;
        _autoRefreshTimer = new DispatcherTimer { Interval = AutoRefreshInterval };
        _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
        SwkLogger.Debug($"UserListWindow ctor: owner={owner?.GetType().Name ?? "null"}");
        Loaded += async (_, _) =>
        {
            SwkLogger.Debug("UserListWindow Loaded -> BuildFromCacheAsync");
            SwkNetworkHealth.Updated += OnNetworkHealthUpdated;
            UpdateNetworkHealthBanner();
            await BuildFromCacheAsync();
            _autoRefreshTimer.Start();
        };
        Closed += (_, _) =>
        {
            SwkNetworkHealth.Updated -= OnNetworkHealthUpdated;
            _autoRefreshTimer.Stop();
            _autoRefreshTimer.Tick -= AutoRefreshTimer_Tick;
        };
    }

    // 一覧を開くたびに必ず再スキャンする。キャッシュがあれば暫定表示してから更新。
    private async Task BuildFromCacheAsync()
    {
        if (SwkNetworkCache.IsReady && SwkNetworkCache.ShopInfos.Count > 0)
        {
            BuildUiFromCache();
        }
        else
        {
            TryLoadUserListState();
        }
        await RunScanAndBuildAsync();
    }

    // スキャンを実行してキャッシュを更新し UI を再構築する（再スキャンボタン用）。
    private async Task RunScanAndBuildAsync()
    {
        if (_isRefreshing) return;

        ReloadButton.IsEnabled = false;
        LoadingBar.Visibility = Visibility.Visible;
        ScanStateTextBlock.Text = "接続可能スキャン";
        StatusTextBlock.Text = "スキャン中…";

        try
        {
            _isRefreshing = true;
            await SwkNetworkCache.RefreshAsync(ScanMode.Quick);
        }
        finally
        {
            _isRefreshing = false;
            LoadingBar.Visibility = Visibility.Collapsed;
            ReloadButton.IsEnabled = true;
        }

        BuildUiFromCache();
    }

    private async void AutoRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await AutoRefreshSilentAsync();
    }

    // 定期チェック用：UI表示なしでスキャンする。変化がなくても 8 秒固定で回し続ける。
    private async Task AutoRefreshSilentAsync()
    {
        if (_isRefreshing) return;

        var snapshot = _rows.Select(r => (r.Kind, r.NameLabel)).ToList();

        LoadingBar.Visibility = Visibility.Visible;
        _isRefreshing = true;
        try
        {
            await SwkNetworkCache.RefreshAsync(ScanMode.Quick);
        }
        finally
        {
            _isRefreshing = false;
            LoadingBar.Visibility = Visibility.Collapsed;
        }

        BuildUiFromCache();

        bool changed = !snapshot.SequenceEqual(_rows.Select(r => (r.Kind, r.NameLabel)));
        if (changed)
        {
            SwkLogger.Debug("UserListWindow.AutoRefreshSilentAsync: change detected");
        }
        else
        {
            SwkLogger.Debug("UserListWindow.AutoRefreshSilentAsync: no change -> interval=8s");
        }

        _autoRefreshTimer.Interval = AutoRefreshInterval;
    }

    private void BuildUiFromCache()
    {
        IReadOnlyList<LanCandidate> candidates = SwkNetworkCache.Candidates;
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos = SwkNetworkCache.ShopInfos;
        List<Friend> friends = FriendsRepository.LoadAll().ToList();

        SwkLogger.Debug($"UserListWindow.BuildUiFromCache: friends={friends.Count} candidates={candidates.Count} shopInfos={shopInfos.Count}");

        List<UserListRow> rows = BuildRowsFromCache(candidates, shopInfos, friends);

        _rows.Clear();
        foreach (UserListRow r in rows)
            _rows.Add(r);

        SaveUserListState(rows);

        SwkLogger.Debug("UserListWindow.BuildUiFromCache rows: " +
            string.Join(" | ", rows.Select(r => $"{r.Kind}:{r.StatusLabel}:{r.NameLabel}:{r.IpLabel}")));

        int connected = rows.Count(r => r.Kind == UserListRowKind.ConnectedFriend);
        int resumeRequired = rows.Count(r => r.Kind == UserListRowKind.ResumeRequiredFriend);
        int autoRecovering = rows.Count(r => r.Kind == UserListRowKind.AutoRecovering);
        int unstable = rows.Count(r => r.Kind == UserListRowKind.Unstable);
        int relinkRequired = rows.Count(r => r.Kind == UserListRowKind.RelinkCandidateFriend);
        int manualRequired = rows.Count(r => r.Kind == UserListRowKind.ManualRequired);
        int newShop = rows.Count(r => r.Kind == UserListRowKind.NewShop);
        int unreach = rows.Count(r => r.Kind == UserListRowKind.UnreachableFriend);
        int windowsPcOnly = rows.Count(r => r.Kind == UserListRowKind.WindowsPcOnly);
        int installCandidate = rows.Count(r => r.Kind == UserListRowKind.InstallCandidate);

        ScanStateTextBlock.Text = "接続可能スキャン";
        if (_rows.Count == 0)
            StatusTextBlock.Text = "周りには誰もいません。";
        else
            SetStatusCountsText(connected, resumeRequired, autoRecovering, unstable, relinkRequired, manualRequired, unreach, newShop, windowsPcOnly, installCandidate);
        UpdateNetworkHealthBanner();

        SwkLogger.Debug(
            $"UserListWindow.BuildUiFromCache done: connected={connected} resumeRequired={resumeRequired} " +
            $"autoRecovering={autoRecovering} unstable={unstable} relinkRequired={relinkRequired} " +
            $"manualRequired={manualRequired} newShop={newShop} unreach={unreach} " +
            $"windowsPcOnly={windowsPcOnly} installCandidate={installCandidate}");
    }

    private void OnNetworkHealthUpdated()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdateNetworkHealthBanner);
            return;
        }

        UpdateNetworkHealthBanner();
    }

    private void UpdateNetworkHealthBanner()
    {
        SwkNetworkHealthStatus status = SwkNetworkHealth.GetStatus();
        NetworkWarningBorder.Visibility = status.HasWarning ? Visibility.Visible : Visibility.Collapsed;
        NetworkWarningTitleTextBlock.Text = status.Title;
        NetworkWarningDetailTextBlock.Text = status.Detail;
    }

    private void NetworkGuideButton_Click(object sender, RoutedEventArgs e)
    {
        SwkNetworkHealthStatus status = SwkNetworkHealth.GetStatus();
        if (!status.HasWarning)
        {
            return;
        }

        var window = new NetworkHealthGuideWindow(status) { Owner = this };
        window.ShowDialog();
    }

    private static List<UserListRow> BuildRowsFromCache(
        IReadOnlyList<LanCandidate> candidates,
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos,
        List<Friend> friends)
    {
        HashSet<string> representedCandidateKeys = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> representedLiveShopKeys = new(StringComparer.OrdinalIgnoreCase);
        List<UserListRow> rows = new();

        foreach (Friend f in friends)
        {
            SwkNotificationListener.ShopInfo? liveShop = FindLiveShopForFriend(f, shopInfos);
            SwkNotificationListener.ShopInfo? relinkShop = liveShop is null && !f.HasCertificateMismatch
                ? FindRelinkCandidateForFriend(f, shopInfos)
                : null;

            if (liveShop is not null && !f.HasCertificateMismatch)
            {
                // 接続確認済み or 確認待ち → trackerリセット（復旧ログ）
                ResetTracker(f.Id);
                rows.Add(IsConnectedFriend(f, liveShop)
                    ? UserListRow.ForConnectedFriend(f, liveShop)
                    : UserListRow.ForResumeRequiredFriend(f, liveShop));
            }
            else if (relinkShop is not null)
            {
                // 同ホストに別SwkInstanceId → 接続先更新あり（自動回復不可）
                ResetTracker(f.Id);
                rows.Add(UserListRow.ForRelinkCandidateFriend(f, relinkShop));
            }
            else
            {
                // cert-mismatch かつ liveShop が見えている → 即 ManualRequired（AutoRecovering/Unstable 不要）
                if (f.HasCertificateMismatch && liveShop is not null)
                {
                    ResetTracker(f.Id);
                    SwkLogger.Info($"[UserList] CertMismatch: FriendId={f.Id} → ManualRequired immediately");
                    rows.Add(UserListRow.ForManualRequired(f, null, liveShop));
                    continue;
                }

                // liveShop なし → 猶予状態を経由して最終判定
                FriendConnectionTracker tracker = GetOrCreateTracker(f.Id);
                bool hasHistory = !string.IsNullOrWhiteSpace(f.LastFoundAt);
                tracker.RecordMiss(hasHistory);

                UserListRow row;
                if (hasHistory && tracker.MissCount <= MaxAutoRecoveryAttempts)
                {
                    // 猶予1：自動復旧中（最大 MaxAutoRecoveryAttempts 回）
                    row = UserListRow.ForAutoRecovering(f, tracker.MissCount, MaxAutoRecoveryAttempts);
                }
                else if (hasHistory && tracker.FirstMissAt.HasValue &&
                         DateTime.Now - tracker.FirstMissAt.Value < UnstableWindow)
                {
                    // 猶予2：一時不安定（UnstableWindow 以内）
                    row = UserListRow.ForUnstable(f);
                }
                else
                {
                    // 時間窓超過 or 接続実績なし → 最終判定
                    LanCandidate? candidate = FindCandidateForFriend(f, candidates);
                    SwkNotificationListener.ShopInfo? visibleShop = FindVisibleShopForFriend(f, shopInfos);
                    bool needsManual = f.HasCertificateMismatch || visibleShop is not null || candidate is not null;
                    row = needsManual
                        ? UserListRow.ForManualRequired(f, candidate, visibleShop)
                        : UserListRow.ForUnreachableFriend(f);
                }

                // 状態遷移ログ（Kindが変わった時のみ）
                if (tracker.LastKind != row.Kind)
                {
                    SwkLogger.Info(
                        $"[UserList] StateTransition: FriendId={f.Id} " +
                        $"{tracker.LastKind?.ToString() ?? "none"} → {row.Kind} missCount={tracker.MissCount}");
                    tracker.LastKind = row.Kind;
                }

                rows.Add(row);
            }
        }

        string myHost = NormalizeHostName(Environment.MachineName);

        foreach (LanCandidate c in candidates)
        {
            string host = NormalizeHostName(c.HostName);
            if (string.IsNullOrEmpty(host)) host = c.Address.ToString();
            if (string.Equals(host, myHost, StringComparison.OrdinalIgnoreCase)) continue;
            if (representedCandidateKeys.Contains(c.Address.ToString())) continue;
            if (representedCandidateKeys.Contains(host)) continue;

            SwkNotificationListener.ShopInfo? shopInfo = shopInfos
                .FirstOrDefault(s => string.Equals(NormalizeHostName(s.MachineName), host, StringComparison.OrdinalIgnoreCase));
            // hostname 一致失敗時は IP で再試行（mDNS suffix など正規化差異を吸収）
            shopInfo ??= shopInfos
                .FirstOrDefault(s => string.Equals(s.IpAddress, c.Address.ToString(), StringComparison.OrdinalIgnoreCase));
            if (IsCandidateCoveredByRegisteredFriend(c, shopInfo, friends))
                continue;
            bool isOpen = shopInfo is not null;

            if (isOpen)
            {
                string liveShopKey = $"{NormalizeHostName(shopInfo!.MachineName)}|{shopInfo.ShareName}";
                if (!representedLiveShopKeys.Add(liveShopKey))
                {
                    continue;
                }
                rows.Add(UserListRow.ForNewShop(c, shopInfo));
            }
            else if (c.IsInstallCandidate)
                rows.Add(UserListRow.ForInstallCandidate(c));
            else
                rows.Add(UserListRow.ForWindowsPcOnly(c));
        }

        foreach (SwkNotificationListener.ShopInfo shopInfo in shopInfos)
        {
            string host = NormalizeHostName(shopInfo.MachineName);
            if (string.IsNullOrEmpty(host))
            {
                host = shopInfo.IpAddress ?? string.Empty;
            }
            if (string.IsNullOrEmpty(host)) continue;
            if (string.Equals(host, myHost, StringComparison.OrdinalIgnoreCase)) continue;

            string liveShopKey = $"{NormalizeHostName(shopInfo.MachineName)}|{shopInfo.ShareName}";
            if (!representedLiveShopKeys.Add(liveShopKey))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(shopInfo.IpAddress) ||
                !System.Net.IPAddress.TryParse(shopInfo.IpAddress, out System.Net.IPAddress? ipAddress))
            {
                continue;
            }

            LanCandidate candidate = new(ipAddress, shopInfo.MachineName);
            if (IsCandidateCoveredByRegisteredFriend(candidate, shopInfo, friends))
            {
                continue;
            }

            rows.Add(UserListRow.ForNewShop(candidate, shopInfo));
        }

        rows.Sort(static (a, b) =>
        {
            int byKind = ((int)a.Kind).CompareTo((int)b.Kind);
            if (byKind != 0) return byKind;
            return string.Compare(a.NameLabel, b.NameLabel, StringComparison.OrdinalIgnoreCase);
        });

        return rows;
    }

    public static void TrySaveSnapshot()
    {
        try
        {
            IReadOnlyList<LanCandidate> candidates = SwkNetworkCache.Candidates;
            IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos = SwkNetworkCache.ShopInfos;
            List<Friend> friends = FriendsRepository.LoadAll().ToList();
            SaveUserListState(BuildRowsFromCache(candidates, shopInfos, friends));
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"UserListWindow.TrySaveSnapshot failed: {ex.Message}");
        }
    }

    private bool TryLoadUserListState()
    {
        try
        {
            if (!File.Exists(UserListStatePath))
            {
                return false;
            }

            UserListSnapshotState? state = JsonSerializer.Deserialize<UserListSnapshotState>(
                File.ReadAllText(UserListStatePath, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (state?.Rows is not { Count: > 0 })
            {
                return false;
            }

            _rows.Clear();
            foreach (UserListSnapshotRow row in state.Rows)
            {
                if (!Enum.TryParse(row.Kind, out UserListRowKind kind))
                {
                    continue;
                }

                _rows.Add(new UserListRow
                {
                    Kind = kind,
                    StatusLabel = row.StatusLabel ?? string.Empty,
                    NameLabel = row.NameLabel ?? string.Empty,
                    IpLabel = row.IpLabel ?? string.Empty,
                    ShareFolderName = row.ShareFolderName ?? string.Empty,
                    ResumeButtonVisibility = Visibility.Collapsed,
                });
            }

            if (_rows.Count == 0)
            {
                return false;
            }

            ScanStateTextBlock.Text = "保存状態";
            StatusTextBlock.Text = "前回の一覧を表示中…";
            SwkLogger.Debug($"UserListWindow.TryLoadUserListState ok: rows={_rows.Count}");
            return true;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"UserListWindow.TryLoadUserListState failed: {ex.Message}");
            return false;
        }
    }

    private void SetStatusCountsText(
        int connected,
        int resumeRequired,
        int autoRecovering,
        int unstable,
        int relinkRequired,
        int manualRequired,
        int unreach,
        int newShop,
        int windowsPcOnly,
        int installCandidate)
    {
        StatusTextBlock.Inlines.Clear();
        AddStatusRun("接続中", connected, Color.FromRgb(76, 175, 80));
        AddSeparatorRun();
        AddStatusRun("接続確認中", resumeRequired, Color.FromRgb(191, 87, 0));
        AddSeparatorRun();
        AddStatusRun("復旧中", autoRecovering, Color.FromRgb(191, 87, 0));
        AddSeparatorRun();
        AddStatusRun("一時不安定", unstable, Color.FromRgb(191, 87, 0));
        AddSeparatorRun();
        AddStatusRun("接続先更新あり", relinkRequired, Color.FromRgb(191, 87, 0));
        AddSeparatorRun();
        AddStatusRun("手動確認待ち", manualRequired, Color.FromRgb(150, 50, 40));
        AddSeparatorRun();
        AddStatusRun("接続不明", unreach, Color.FromRgb(150, 50, 40));
        AddSeparatorRun();
        AddStatusRun("登録可能", newShop, Color.FromRgb(255, 152, 0));
        AddSeparatorRun();
        AddStatusRun("登録不可", windowsPcOnly, Color.FromRgb(120, 120, 120));
        if (installCandidate > 0)
        {
            AddSeparatorRun();
            AddStatusRun("Windowsのみ", installCandidate, Color.FromRgb(80, 110, 170));
        }
    }

    private void AddStatusRun(string label, int count, Color color)
    {
        StatusTextBlock.Inlines.Add(new Run(label)
        {
            Foreground = new SolidColorBrush(color),
            FontWeight = FontWeights.SemiBold,
        });
        StatusTextBlock.Inlines.Add(new Run($" {count}"));
    }

    private void AddSeparatorRun()
    {
        StatusTextBlock.Inlines.Add(new Run("　"));
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        SwkLogger.Debug("UserListWindow.ReloadButton_Click");
        _autoRefreshTimer.Interval = AutoRefreshInterval;
        await RunScanAndBuildAsync();
    }

    private static FriendConnectionTracker GetOrCreateTracker(string friendId)
    {
        if (!_connectionTrackers.TryGetValue(friendId, out FriendConnectionTracker? tracker))
        {
            tracker = new FriendConnectionTracker();
            _connectionTrackers[friendId] = tracker;
        }
        return tracker;
    }

    private static void ResetTracker(string friendId)
    {
        if (!_connectionTrackers.TryGetValue(friendId, out FriendConnectionTracker? tracker) ||
            tracker.MissCount == 0)
        {
            return;
        }

        UserListRowKind? prevKind = tracker.LastKind;
        tracker.Reset();
        SwkLogger.Info(
            $"[UserList] StateTransition: FriendId={friendId} {prevKind?.ToString() ?? "none"} → recovered");
    }

    private async void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: UserListRow row } || row.Friend is null)
        {
            return;
        }

        // 手動確認待ち：FriendsWindow を直接開いてユーザー判断を促す
        if (row.Kind == UserListRowKind.ManualRequired)
        {
            _autoRefreshTimer.Stop();
            try
            {
                IReadOnlyList<LanCandidate> candidates = SwkNetworkCache.Candidates;
                IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfosForManual = SwkNetworkCache.ShopInfos;
                FriendsWindow pickup = new(this, row.Friend, row.Candidate, candidates, shopInfosForManual);
                bool? result = pickup.ShowDialog();
                SwkLogger.Info($"[UserList] ManualRequired FriendsWindow closed: FriendId={row.Friend.Id} result={result}");
                if (result == true)
                {
                    _hasFriendUpdates = true;
                    await RunScanAndBuildAsync();
                }
            }
            finally
            {
                _autoRefreshTimer.Start();
            }
            return;
        }

        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos = SwkNetworkCache.ShopInfos;
        SwkNotificationListener.ShopInfo? liveShop = row.ShopInfo ?? FindLiveShopForFriend(row.Friend, shopInfos);
        if (liveShop is null)
        {
            StatusTextBlock.Text = "再開できる接続先が見つかりません。先に再スキャンしてください。";
            return;
        }

        List<Friend> friends = FriendsRepository.LoadAll().ToList();
        Friend? target = friends.FirstOrDefault(f => string.Equals(f.Id, row.Friend.Id, StringComparison.Ordinal));
        if (target is null)
        {
            StatusTextBlock.Text = "対象ユーザーを読み直してください。";
            return;
        }

        SwkLogger.Info(
            $"Investigation.ResumeButton_Click: friend={target.DisplayName} host={liveShop.MachineName} share={liveShop.ShareName}");

        ReloadButton.IsEnabled = false;
        LoadingBar.Visibility = Visibility.Visible;
        StatusTextBlock.Text = "接続情報を再取得しています…";
        try
        {
            bool refreshed = await TryRefreshFriendFromBkAsync(target, liveShop);
            if (!refreshed)
            {
                HistoryRepository.Append(new HistoryEntry
                {
                    Channel = HistoryChannel.Access,
                    FriendId = target.Id,
                    FriendName = string.IsNullOrWhiteSpace(target.DisplayName) ? target.HostMachineName : target.DisplayName,
                    Direction = HistoryDirection.Outgoing,
                    EventType = "Resume",
                    Message = $"{(string.IsNullOrWhiteSpace(target.DisplayName) ? target.HostMachineName : target.DisplayName)} の接続情報を再取得できませんでした。",
                    Outcome = HistoryOutcome.Failure,
                    TargetName = liveShop.ShareName,
                    PathText = $@"\\{target.HostMachineName}\{liveShop.ShareName}",
                    Source = "UserListWindow",
                });
                BuildUiFromCache();
                StatusTextBlock.Text = "接続情報を再取得できませんでした。";
                return;
            }

            bool verified = await Task.Run(() => TryVerifyFriendShareOnDemand(target, liveShop, allowReconnect: true));
            if (verified)
            {
                FriendShareAccessTracker.MarkVerified(target, liveShop);
            }
            else
            {
                FriendShareAccessTracker.ClearVerified(target);
            }

            if (!FriendsRepository.SaveAll(friends))
            {
                BuildUiFromCache();
                StatusTextBlock.Text = "保存できませんでした。";
                return;
            }

            _hasFriendUpdates = true;
            HistoryRepository.Append(new HistoryEntry
            {
                Channel = HistoryChannel.Access,
                FriendId = target.Id,
                FriendName = string.IsNullOrWhiteSpace(target.DisplayName) ? target.HostMachineName : target.DisplayName,
                Direction = HistoryDirection.Outgoing,
                EventType = "Resume",
                Message = verified
                    ? $"{(string.IsNullOrWhiteSpace(target.DisplayName) ? target.HostMachineName : target.DisplayName)} の接続を再開しました。"
                    : $"{(string.IsNullOrWhiteSpace(target.DisplayName) ? target.HostMachineName : target.DisplayName)} の接続情報を再取得しました。",
                Outcome = verified ? HistoryOutcome.Success : HistoryOutcome.Info,
                TargetName = liveShop.ShareName,
                PathText = $@"\\{target.HostMachineName}\{liveShop.ShareName}",
                Source = "UserListWindow",
            });
            BuildUiFromCache();
            StatusTextBlock.Text = verified
                ? "接続情報を再取得し、共有接続も確認しました。"
                : "接続情報を再取得しました。共有接続は未確認です。";
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            ReloadButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SwkLogger.Debug("UserListWindow.CloseButton_Click");
        if (_hasFriendUpdates)
        {
            DialogResult = true;
            return;
        }

        Close();
    }

    private async void UserListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (UserListView.SelectedItem is not UserListRow row)
        {
            return;
        }

        SwkLogger.Debug($"UserListWindow.UserListView_MouseDoubleClick: row={row.Kind}/{row.NameLabel}");

        // ダイアログ表示中は自動リフレッシュを止める。確定で戻った時に必ず可視スキャンを
        // 走らせて緑色のバーで進行中を伝えるため、在進行のスキャンが残っていれば終わるまで待つ。
        _autoRefreshTimer.Stop();
        try
        {
            IReadOnlyList<LanCandidate> candidates = SwkNetworkCache.Candidates;
            IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos = SwkNetworkCache.ShopInfos;

            FriendsWindow pickup = (row.Kind == UserListRowKind.NewShop && row.ShopInfo != null)
                ? new FriendsWindow(this, row.ShopInfo, candidates, shopInfos)
                : new FriendsWindow(this, row.Friend, row.Candidate, candidates, shopInfos);
            bool? result = pickup.ShowDialog();
            SwkLogger.Debug($"UserListWindow.UserListView_MouseDoubleClick: pickup result={result}");

            while (_isRefreshing)
            {
                await Task.Delay(50);
            }

            if (result == true)
            {
                _autoRefreshTimer.Interval = AutoRefreshInterval;
                await RunScanAndBuildAsync();
            }
        }
        finally
        {
            _autoRefreshTimer.Start();
        }
    }

    private static bool SameHost(string expected, string? found)
    {
        string e1 = NormalizeHostName(expected);
        string f1 = NormalizeHostName(found);
        return !string.IsNullOrWhiteSpace(e1) && !string.IsNullOrWhiteSpace(f1)
            && string.Equals(e1, f1, StringComparison.OrdinalIgnoreCase);
    }

    private static Friend? FindSwitchTargetForCandidate(
        LanCandidate candidate,
        SwkNotificationListener.ShopInfo shopInfo,
        IReadOnlyList<Friend> friends)
    {
        List<Friend> matches = friends
            .Where(f => !f.IsCurrentlyFound)
            .Where(f => IsLikelySwitchMatch(f, candidate, shopInfo))
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool IsLikelySwitchMatch(
        Friend friend,
        LanCandidate candidate,
        SwkNotificationListener.ShopInfo shopInfo)
    {
        if (HasBothInstanceIds(friend, shopInfo))
        {
            return SameSwkInstance(friend, shopInfo);
        }

        bool sameShare = !string.IsNullOrWhiteSpace(friend.ShareName) &&
            string.Equals(friend.ShareName, shopInfo.ShareName, StringComparison.OrdinalIgnoreCase);

        return sameShare;
    }

    private static LanCandidate? FindCandidateForFriend(Friend friend, IReadOnlyList<LanCandidate> candidates)
    {
        return candidates.FirstOrDefault(c =>
            SameHost(friend.HostMachineName, c.HostName));
    }

    private static SwkNotificationListener.ShopInfo? FindLiveShopForFriend(
        Friend friend,
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos)
        => FriendRecognitionService.FindLiveShopForFriend(friend, shopInfos);

    private static SwkNotificationListener.ShopInfo? FindVisibleShopForFriend(
        Friend friend,
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos)
        => FriendRecognitionService.FindVisibleShopForFriend(friend, shopInfos);

    private static SwkNotificationListener.ShopInfo? FindRelinkCandidateForFriend(
        Friend friend,
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos)
        => FriendRecognitionService.FindRelinkCandidateForFriend(friend, shopInfos);

    private static SwkNotificationListener.ShopInfo? FindLiveShopByHostAndShare(
        Friend friend,
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos)
        => FriendRecognitionService.FindLiveShopForFriend(friend, shopInfos);

    private static async Task<bool> TryRefreshFriendFromBkAsync(
        Friend friend,
        SwkNotificationListener.ShopInfo liveShop)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        RefreshExistingFriendResult result = await FriendRecognitionService.RefreshExistingFriendAsync(
            friend,
            liveShop,
            cts.Token);

        if (!result.Success)
        {
            SwkLogger.Warn(
                $"UserListWindow.TryRefreshFriendFromBkAsync failed: id={friend.Id} {result.ErrorMessage ?? "empty password"}");
            return false;
        }
        return true;
    }

    private static bool IsCandidateCoveredByRegisteredFriend(
        LanCandidate candidate,
        SwkNotificationListener.ShopInfo? shopInfo,
        IReadOnlyList<Friend> friends)
    {
        foreach (Friend friend in friends)
        {
            bool sameHost = SameHost(friend.HostMachineName, candidate.HostName);
            if (shopInfo is null)
            {
                // hostname 不一致でも LastKnownAddress と candidate IP が一致すれば既登録扱い
                bool sameIp = !string.IsNullOrWhiteSpace(friend.LastKnownAddress) &&
                    string.Equals(friend.LastKnownAddress, candidate.Address.ToString(), StringComparison.OrdinalIgnoreCase);
                if (sameHost || sameIp)
                {
                    return true;
                }
                continue;
            }

            bool sameShare = string.Equals(friend.ShareName, shopInfo.ShareName, StringComparison.OrdinalIgnoreCase);
            if (HasBothInstanceIds(friend, shopInfo) &&
                SameSwkInstance(friend, shopInfo) &&
                sameShare)
            {
                return true;
            }

            if (sameHost && sameShare)
            {
                return true;
            }

            if (!sameHost && sameShare)
            {
                List<Friend> sameShareFriends = friends
                    .Where(f => string.Equals(f.ShareName, shopInfo.ShareName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (sameShareFriends.Count == 1)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AddCandidateKeys(HashSet<string> matchedHosts, LanCandidate candidate)
    {
        string host = NormalizeHostName(candidate.HostName);
        if (!string.IsNullOrWhiteSpace(host))
        {
            matchedHosts.Add(host);
        }
        matchedHosts.Add(candidate.Address.ToString());
    }

    private static string NormalizeHostName(string? host)
        => FriendRecognitionService.NormalizeHost(host);

    private static bool IsConnectedFriend(Friend friend, SwkNotificationListener.ShopInfo liveShop)
    {
        return FriendShareAccessTracker.IsVerifiedFor(friend, liveShop);
    }

    // 共有確認は明示操作からのみ呼ぶ。ユーザー一覧の観測経路から呼んではいけない。
    private static bool TryVerifyFriendShareOnDemand(
        Friend friend,
        SwkNotificationListener.ShopInfo liveShop,
        bool allowReconnect)
    {
        try
        {
            List<string> candidates = BuildFriendUncCandidates(friend, liveShop);
            string password = FriendsRepository.UnprotectPassword(friend.PasswordProtected);
            string friendLabel = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName;
            SwkLogger.Info(
                $"Investigation.TryVerifyFriendShareOnDemand.Start: friend={friendLabel} host={liveShop.MachineName} " +
                $"share={liveShop.ShareName} allowReconnect={allowReconnect} candidates={string.Join(" | ", candidates)}");

            foreach (string path in candidates)
            {
                if (CanEnumerateShare(path))
                {
                    SwkLogger.Info(
                        $"Investigation.TryVerifyFriendShareOnDemand.SuccessExisting: friend={friendLabel} path={path}");
                    return true;
                }

                if (allowReconnect && !string.IsNullOrEmpty(password))
                {
                    SmbConnectionHelper.EnsureConnection(path, friend.UserName, password, liveShop.MachineName);
                    if (CanEnumerateShare(path))
                    {
                        SwkLogger.Info(
                            $"Investigation.TryVerifyFriendShareOnDemand.SuccessReconnect: friend={friendLabel} path={path}");
                        return true;
                    }
                }
            }

            SwkLogger.Debug($"UserListWindow.TryVerifyFriendShareOnDemand failed: friend={friend.Id} candidates={string.Join(", ", candidates)}");
            SwkLogger.Info(
                $"Investigation.TryVerifyFriendShareOnDemand.Fail: friend={friendLabel} candidates={string.Join(" | ", candidates)}");
            return false;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"UserListWindow.TryVerifyFriendShareOnDemand failed: friend={friend.Id} {ex.Message}");
            return false;
        }
    }

    private static bool HasBothInstanceIds(Friend friend, SwkNotificationListener.ShopInfo shopInfo) =>
        !string.IsNullOrWhiteSpace(friend.RemoteSwkInstanceId) &&
        !string.IsNullOrWhiteSpace(shopInfo.SwkInstanceId);

    private static bool SameSwkInstance(Friend friend, SwkNotificationListener.ShopInfo shopInfo) =>
        HasBothInstanceIds(friend, shopInfo) &&
        FriendRecognitionService.SameSwkInstance(friend, shopInfo);

    private static bool CanEnumerateShare(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            using IEnumerator<string> enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            _ = enumerator.MoveNext();
            return true;
        }
        catch (Exception ex)
        {
            SwkLogger.Debug($"UserListWindow.CanEnumerateShare failed: {path}: {ex.Message}");
            return false;
        }
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

    private static void SaveUserListState(IReadOnlyList<UserListRow> rows)
    {
        try
        {
            var state = new
            {
                savedAt = DateTime.UtcNow.ToString("o"),
                rows = rows.Select(r => new
                {
                    kind = r.Kind.ToString(),
                    statusLabel = r.StatusLabel,
                    nameLabel = r.NameLabel,
                    ipLabel = r.IpLabel,
                    shareFolderName = r.ShareFolderName,
                    friendId = r.Friend?.Id,
                    friendDisplayName = r.Friend?.DisplayName,
                    friendHostMachineName = r.Friend?.HostMachineName,
                    friendShareName = r.Friend?.ShareName,
                    friendRemoteSwkInstanceId = r.Friend?.RemoteSwkInstanceId,
                    friendLastAccessIssue = r.Friend?.LastAccessIssue,
                    friendOwnerCertThumbprint = r.Friend?.OwnerCertThumbprint,
                    friendLastKnownAddress = r.Friend?.LastKnownAddress,
                    friendLastFoundAt = r.Friend?.LastFoundAt,
                    friendLastSeenAt = r.Friend?.LastSeenAt,
                    shopSwkInstanceId = r.ShopInfo?.SwkInstanceId,
                    shopMachineName = r.ShopInfo?.MachineName,
                    shopIpAddress = r.ShopInfo?.IpAddress,
                    shopShareName = r.ShopInfo?.ShareName,
                    shopPort = r.ShopInfo?.Port,
                    friendLastShareAccessVerifiedAt = r.Friend?.LastShareAccessVerifiedAt,
                    friendLastShareAccessHost = r.Friend?.LastShareAccessHost,
                    friendLastShareAccessSwkInstanceId = r.Friend?.LastShareAccessSwkInstanceId,
                    candidateHostName = r.Candidate?.HostName,
                    candidateAddress = r.Candidate?.Address.ToString(),
                }).ToList()
            };
            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(UserListStatePath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"UserListWindow.SaveUserListState failed: {ex.Message}");
        }
    }

    private sealed class FriendConnectionTracker
    {
        public int MissCount { get; private set; }
        public DateTime? FirstMissAt { get; private set; }
        public UserListRowKind? LastKind { get; set; }

        public void RecordMiss(bool hasHistory)
        {
            if (MissCount == 0 && hasHistory)
            {
                FirstMissAt = DateTime.Now;
            }
            MissCount++;
        }

        public void Reset()
        {
            MissCount = 0;
            FirstMissAt = null;
            LastKind = null;
        }
    }
}

file sealed class UserListSnapshotState
{
    [JsonPropertyName("savedAt")]
    public string? SavedAt { get; set; }

    [JsonPropertyName("rows")]
    public List<UserListSnapshotRow> Rows { get; set; } = [];
}

file sealed class UserListSnapshotRow
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("statusLabel")]
    public string? StatusLabel { get; set; }

    [JsonPropertyName("nameLabel")]
    public string? NameLabel { get; set; }

    [JsonPropertyName("ipLabel")]
    public string? IpLabel { get; set; }

    [JsonPropertyName("shareFolderName")]
    public string? ShareFolderName { get; set; }
}

public enum UserListRowKind
{
    // 値の小さい順に表示される(0 が先頭)。
    ConnectedFriend = 0,        // 接続中：Friend登録済み + 開店中 + 明示接続成功確認あり
    ResumeRequiredFriend = 1,   // 接続確認中：Friend登録済み + 開店中 + 接続成功確認待ち
    AutoRecovering = 2,         // 自動復旧中：liveShop消失後 MaxAutoRecoveryAttempts 回以内
    Unstable = 3,               // 一時不安定：リトライ上限超過後 UnstableWindow 以内
    RelinkCandidateFriend = 4,  // 接続先更新あり：同一ホストに別SwkInstanceIdが出現
    ManualRequired = 5,         // 手動確認待ち：候補あり or cert-mismatch（元SwitchCandidate/UnreachableFriend統合）
    NewShop = 6,                // 未登録：Friend未登録 + 開店中
    UnreachableFriend = 7,      // 接続不明：候補なし・時間窓超過
    WindowsPcOnly = 8,          // Friend未登録 + ShareWorkinなし（445+135応答）
    InstallCandidate = 9,       // Friend未登録 + ポート21/22応答（インストール候補）
}

public sealed class UserListRow
{
    public string NameLabel { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public string ShareFolderName { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
    public string IpLabel { get; init; } = string.Empty;
    public UserListRowKind Kind { get; init; }
    public Brush IconBrush { get; init; } = Brushes.LightGray;
    public ImageSource? IconImage { get; init; }
    public Brush RowBackground { get; init; } = Brushes.Transparent;
    public Brush NameForeground { get; init; } = Brushes.Black;
    public FontWeight NameWeight { get; init; } = FontWeights.Medium;
    public System.Windows.FontStyle NameStyle { get; init; } = FontStyles.Normal;
    public Visibility ResumeButtonVisibility { get; init; } = Visibility.Collapsed;
    public string ResumeButtonLabel { get; init; } = "再開";

    public Friend? Friend { get; init; }
    public LanCandidate? Candidate { get; init; }
    public SwkNotificationListener.ShopInfo? ShopInfo { get; init; }

    private static ImageSource? LoadIconImage(string? iconKey)
    {
        string? path = IconService.ResolvePath(iconKey);
        if (path == null) return null;
        try
        {
            BitmapImage img = new();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }

    public static UserListRow ForConnectedFriend(
        Friend friend,
        SwkNotificationListener.ShopInfo? liveShop = null) => new()
    {
        NameLabel = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName,
        StatusLabel = "登録済み / 接続中",
        ShareFolderName = liveShop?.ShareName ?? friend.ShareName,
        Memo = friend.Memo,
        IpLabel = liveShop?.IpAddress ?? friend.LastKnownAddress ?? string.Empty,
        Kind = UserListRowKind.ConnectedFriend,
        IconBrush = Brushes.White,
        IconImage = LoadIconImage(friend.IconKey),
        RowBackground = new SolidColorBrush(Color.FromRgb(232, 245, 233)),
        NameForeground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
        NameWeight = FontWeights.Normal,
        Friend = friend,
        ShopInfo = liveShop,
    };

    public static UserListRow ForResumeRequiredFriend(
        Friend friend,
        SwkNotificationListener.ShopInfo liveShop) => new()
    {
        NameLabel = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName,
        StatusLabel = "登録済み / 再開待ち",
        ShareFolderName = liveShop.ShareName,
        Memo = "接続先は見えています。再開して共有接続を確認してください。",
        IpLabel = liveShop.IpAddress ?? friend.LastKnownAddress ?? string.Empty,
        Kind = UserListRowKind.ResumeRequiredFriend,
        IconBrush = Brushes.White,
        IconImage = LoadIconImage(friend.IconKey),
        RowBackground = new SolidColorBrush(Color.FromRgb(255, 243, 224)),
        NameForeground = new SolidColorBrush(Color.FromRgb(191, 87, 0)),
        NameWeight = FontWeights.SemiBold,
        ResumeButtonLabel = "更新",
        ResumeButtonVisibility = Visibility.Visible,
        Friend = friend,
        ShopInfo = liveShop,
    };

    public static UserListRow ForRelinkCandidateFriend(
        Friend friend,
        SwkNotificationListener.ShopInfo liveShop) => new()
    {
        NameLabel = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName,
        StatusLabel = "登録済み / 接続更新候補",
        ShareFolderName = liveShop.ShareName,
        Memo = "同じ相手の可能性があります。このPCの接続情報を更新して再接続を試してください。",
        IpLabel = liveShop.IpAddress ?? friend.LastKnownAddress ?? string.Empty,
        Kind = UserListRowKind.RelinkCandidateFriend,
        IconBrush = Brushes.White,
        IconImage = LoadIconImage(friend.IconKey),
        RowBackground = new SolidColorBrush(Color.FromRgb(255, 243, 224)),
        NameForeground = new SolidColorBrush(Color.FromRgb(191, 87, 0)),
        NameWeight = FontWeights.SemiBold,
        ResumeButtonLabel = "再開",
        ResumeButtonVisibility = Visibility.Visible,
        Friend = friend,
        ShopInfo = liveShop,
    };

    public static UserListRow ForAutoRecovering(Friend friend, int missCount, int maxAttempts) => new()
    {
        NameLabel = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName,
        StatusLabel = "接続復旧中…",
        ShareFolderName = friend.ShareName,
        Memo = $"再試行 {missCount}/{maxAttempts}",
        IpLabel = friend.LastKnownAddress ?? string.Empty,
        Kind = UserListRowKind.AutoRecovering,
        IconBrush = Brushes.White,
        IconImage = LoadIconImage(friend.IconKey),
        RowBackground = new SolidColorBrush(Color.FromRgb(255, 243, 224)),
        NameForeground = new SolidColorBrush(Color.FromRgb(191, 87, 0)),
        NameWeight = FontWeights.Normal,
        Friend = friend,
    };

    public static UserListRow ForUnstable(Friend friend) => new()
    {
        NameLabel = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName,
        StatusLabel = "一時不安定",
        ShareFolderName = friend.ShareName,
        Memo = "接続先が一時的に見えません。自動復帰を試みています。",
        IpLabel = friend.LastKnownAddress ?? string.Empty,
        Kind = UserListRowKind.Unstable,
        IconBrush = Brushes.White,
        IconImage = LoadIconImage(friend.IconKey),
        RowBackground = new SolidColorBrush(Color.FromRgb(255, 243, 224)),
        NameForeground = new SolidColorBrush(Color.FromRgb(191, 87, 0)),
        NameWeight = FontWeights.Normal,
        Friend = friend,
    };

    public static UserListRow ForManualRequired(
        Friend friend,
        LanCandidate? candidate = null,
        SwkNotificationListener.ShopInfo? visibleShop = null) => new()
    {
        NameLabel = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName,
        StatusLabel = "手動確認待ち",
        ShareFolderName = friend.ShareName,
        Memo = friend.HasCertificateMismatch
            ? "通知経路に問題があります。接続情報を再確認してください。"
            : visibleShop is not null
                ? $"候補：{visibleShop.MachineName}/{visibleShop.ShareName}。同じ相手か確認してください。"
                : candidate is not null
                    ? $"接続先候補（{(string.IsNullOrWhiteSpace(candidate.HostName) ? candidate.Address.ToString() : candidate.HostName)}）があります。"
                    : "接続先を確認してください。",
        IpLabel = visibleShop?.IpAddress ?? candidate?.Address.ToString() ?? friend.LastKnownAddress ?? string.Empty,
        Kind = UserListRowKind.ManualRequired,
        IconBrush = Brushes.White,
        IconImage = LoadIconImage(friend.IconKey),
        RowBackground = new SolidColorBrush(Color.FromRgb(255, 235, 230)),
        NameForeground = new SolidColorBrush(Color.FromRgb(150, 50, 40)),
        NameWeight = FontWeights.SemiBold,
        ResumeButtonLabel = "確認",
        ResumeButtonVisibility = Visibility.Visible,
        Friend = friend,
        Candidate = candidate,
        ShopInfo = visibleShop,
    };

    public static UserListRow ForUnreachableFriend(Friend friend) => new()
    {
        NameLabel = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName,
        StatusLabel = "接続不明",
        ShareFolderName = friend.ShareName,
        Memo = "相手のPCが見つかりません。ネットワーク接続を確認してください。",
        IpLabel = friend.LastKnownAddress ?? string.Empty,
        Kind = UserListRowKind.UnreachableFriend,
        IconBrush = Brushes.White,
        IconImage = LoadIconImage(friend.IconKey),
        RowBackground = new SolidColorBrush(Color.FromRgb(255, 235, 230)),
        NameForeground = new SolidColorBrush(Color.FromRgb(150, 50, 40)),
        NameWeight = FontWeights.Normal,
        Friend = friend,
    };

    public static UserListRow ForNewShop(LanCandidate candidate, SwkNotificationListener.ShopInfo? shopInfo = null)
    {
        string host = string.IsNullOrWhiteSpace(candidate.HostName) ? candidate.Address.ToString() : candidate.HostName!;
        return new UserListRow
        {
            NameLabel = host,
            StatusLabel = "未登録 / 開店中",
            ShareFolderName = shopInfo?.ShareName ?? string.Empty,
            IpLabel = candidate.Address.ToString(),
            Kind = UserListRowKind.NewShop,
            IconBrush = Brushes.White,
            RowBackground = new SolidColorBrush(Color.FromRgb(255, 248, 225)),
            NameForeground = new SolidColorBrush(Color.FromRgb(255, 152, 0)),
            NameWeight = FontWeights.Normal,
            Candidate = candidate,
            ShopInfo = shopInfo,
        };
    }

    public static UserListRow ForWindowsPcOnly(LanCandidate candidate)
    {
        string host = string.IsNullOrWhiteSpace(candidate.HostName) ? candidate.Address.ToString() : candidate.HostName!;
        return new UserListRow
        {
            NameLabel = host,
            StatusLabel = "未登録 / 共有のみ",
            IpLabel = candidate.Address.ToString(),
            Kind = UserListRowKind.WindowsPcOnly,
            IconBrush = Brushes.White,
            RowBackground = new SolidColorBrush(Color.FromRgb(245, 245, 245)),
            NameForeground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
            NameWeight = FontWeights.Normal,
            Candidate = candidate,
        };
    }

    public static UserListRow ForInstallCandidate(LanCandidate candidate)
    {
        string host = string.IsNullOrWhiteSpace(candidate.HostName) ? candidate.Address.ToString() : candidate.HostName!;
        return new UserListRow
        {
            NameLabel = host,
            StatusLabel = "未登録 / 要導入",
            IpLabel = candidate.Address.ToString(),
            Kind = UserListRowKind.InstallCandidate,
            IconBrush = Brushes.White,
            RowBackground = new SolidColorBrush(Color.FromRgb(235, 240, 255)),
            NameForeground = new SolidColorBrush(Color.FromRgb(80, 110, 170)),
            NameWeight = FontWeights.Normal,
            Candidate = candidate,
        };
    }
}
