using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
    private int _intervalStepIndex = 0;
    private static readonly int[] IntervalSteps = { 8, 16, 30, 60 };
    private static readonly TimeSpan TrustedRefreshReloadWindow = TimeSpan.FromSeconds(8);
    private DateTime _lastManualReloadAtUtc = DateTime.MinValue;

    public UserListWindow(Window owner)
    {
        InitializeComponent();
        _ownerWindow = owner;
        UserListView.ItemsSource = _rows;
        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(IntervalSteps[0]) };
        _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
        UserListView.SelectionChanged += UserListView_SelectionChanged;
        SwkLogger.Debug($"UserListWindow ctor: owner={owner?.GetType().Name ?? "null"}");
        Loaded += async (_, _) =>
        {
            SwkLogger.Debug("UserListWindow Loaded -> BuildFromCacheAsync");
            await BuildFromCacheAsync();
            _autoRefreshTimer.Start();
        };
        Closed += (_, _) =>
        {
            _autoRefreshTimer.Stop();
            _autoRefreshTimer.Tick -= AutoRefreshTimer_Tick;
        };
    }

    // 一覧を開くたびに必ず再スキャンする。キャッシュがあれば暫定表示してから更新。
    private async Task BuildFromCacheAsync()
    {
        if (SwkNetworkCache.IsReady && SwkNetworkCache.ShopInfos.Count > 0)
            BuildUiFromCache();
        await RunScanAndBuildAsync();
    }

    // スキャンを実行してキャッシュを更新し UI を再構築する（再スキャンボタン用）。
    private async Task RunScanAndBuildAsync()
    {
        if (_isRefreshing) return;

        ScanMode mode = ScanModeComboBox.SelectedIndex == 1 ? ScanMode.Full : ScanMode.Quick;

        ReloadButton.IsEnabled = false;
        LoadingBar.Visibility = Visibility.Visible;
        ScanStateTextBlock.Text = mode == ScanMode.Full ? "全PCスキャン" : "接続可能スキャン";
        StatusTextBlock.Text = "スキャン中…";

        try
        {
            _isRefreshing = true;
            await SwkNetworkCache.RefreshAsync(mode);
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

    // 定期チェック用：UI表示なしでスキャンし、変化があればインターバルをリセット。
    private async Task AutoRefreshSilentAsync()
    {
        if (_isRefreshing) return;

        var snapshot = _rows.Select(r => (r.Kind, r.NameLabel)).ToList();

        ScanMode mode = ScanModeComboBox.SelectedIndex == 1 ? ScanMode.Full : ScanMode.Quick;
        _isRefreshing = true;
        try
        {
            await SwkNetworkCache.RefreshAsync(mode);
        }
        finally
        {
            _isRefreshing = false;
        }

        BuildUiFromCache();

        bool changed = !snapshot.SequenceEqual(_rows.Select(r => (r.Kind, r.NameLabel)));
        if (changed)
        {
            _intervalStepIndex = 0;
            SwkLogger.Debug("UserListWindow.AutoRefreshSilentAsync: change detected -> interval reset to 8s");
        }
        else if (_intervalStepIndex < IntervalSteps.Length - 1)
        {
            _intervalStepIndex++;
            SwkLogger.Debug($"UserListWindow.AutoRefreshSilentAsync: no change -> interval={IntervalSteps[_intervalStepIndex]}s");
        }

        _autoRefreshTimer.Interval = TimeSpan.FromSeconds(IntervalSteps[_intervalStepIndex]);
    }

    private void BuildUiFromCache()
    {
        IReadOnlyList<LanCandidate> candidates = SwkNetworkCache.Candidates;
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos = SwkNetworkCache.ShopInfos;
        List<Friend> friends = FriendsRepository.LoadAll().ToList();
        bool friendsChanged = false;

        SwkLogger.Debug($"UserListWindow.BuildUiFromCache: friends={friends.Count} candidates={candidates.Count} shopInfos={shopInfos.Count}");

        HashSet<string> matchedHosts = new(StringComparer.OrdinalIgnoreCase);
        List<UserListRow> rows = new();

        foreach (Friend f in friends)
        {
            LanCandidate? match = FindCandidateForFriend(f, candidates);
            SwkNotificationListener.ShopInfo? liveShop = FindLiveShopForFriend(f, shopInfos);
            if (liveShop is not null && UpdateFriendFromLiveShop(f, liveShop))
            {
                friendsChanged = true;
            }

            if (match is not null)
            {
                AddMatchedCandidateKeys(matchedHosts, match);
                rows.Add(liveShop is not null && !f.HasCertificateMismatch
                    ? UserListRow.ForConnectedFriend(f, liveShop)
                    : UserListRow.ForUnreachableFriend(f));
            }
            else
            {
                rows.Add(liveShop is not null && !f.HasCertificateMismatch
                    ? UserListRow.ForConnectedFriend(f, liveShop)
                    : UserListRow.ForUnreachableFriend(f));
            }
        }

        if (friendsChanged && FriendsRepository.SaveAll(friends))
        {
            _hasFriendUpdates = true;
        }

        string myHost = NormalizeHostName(Environment.MachineName);

        foreach (LanCandidate c in candidates)
        {
            string host = NormalizeHostName(c.HostName);
            if (string.IsNullOrEmpty(host)) host = c.Address.ToString();
            if (string.Equals(host, myHost, StringComparison.OrdinalIgnoreCase)) continue;
            if (matchedHosts.Contains(c.Address.ToString())) continue;
            if (matchedHosts.Contains(host)) continue;

            SwkNotificationListener.ShopInfo? shopInfo = shopInfos
                .FirstOrDefault(s => string.Equals(NormalizeHostName(s.MachineName), host, StringComparison.OrdinalIgnoreCase));
            bool isOpen = shopInfo is not null;

            if (isOpen)
            {
                Friend? switchTarget = FindSwitchTargetForCandidate(c, shopInfo!, friends);
                rows.Add(switchTarget is not null
                    ? UserListRow.ForSwitchCandidate(switchTarget, c, shopInfo!)
                    : UserListRow.ForNewShop(c, shopInfo));
            }
            else if (c.IsInstallCandidate)
            {
                rows.Add(UserListRow.ForInstallCandidate(c));
            }
            else
            {
                rows.Add(UserListRow.ForWindowsPcOnly(c));
            }
        }

        rows.Sort(static (a, b) =>
        {
            int byKind = ((int)a.Kind).CompareTo((int)b.Kind);
            if (byKind != 0) return byKind;
            return string.Compare(a.NameLabel, b.NameLabel, StringComparison.OrdinalIgnoreCase);
        });

        _rows.Clear();
        foreach (UserListRow r in rows)
        {
            _rows.Add(r);
        }

        UpdateRefreshCertButtonState();

        int connected = rows.Count(r => r.Kind == UserListRowKind.ConnectedFriend);
        int switchable = rows.Count(r => r.Kind == UserListRowKind.SwitchCandidate);
        int newShop = rows.Count(r => r.Kind == UserListRowKind.NewShop);
        int unreach = rows.Count(r => r.Kind == UserListRowKind.UnreachableFriend);
        int windowsPcOnly = rows.Count(r => r.Kind == UserListRowKind.WindowsPcOnly);
        int installCandidate = rows.Count(r => r.Kind == UserListRowKind.InstallCandidate);

        string modeLabel = SwkNetworkCache.LastScanMode == ScanMode.Full ? "全PCスキャン" : "接続可能スキャン";
        ScanStateTextBlock.Text = modeLabel;
        StatusTextBlock.Text = _rows.Count == 0
            ? "周りには誰もいません。"
            : $"登録済接続中 {connected}　切替候補 {switchable}　登録済不在 {unreach}　登録可能 {newShop}　登録不可 {windowsPcOnly}　全PC分 {installCandidate}";

        SwkLogger.Debug($"UserListWindow.BuildUiFromCache done: connected={connected} switchable={switchable} newShop={newShop} unreach={unreach} windowsPcOnly={windowsPcOnly} installCandidate={installCandidate}");
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        DateTime nowUtc = DateTime.UtcNow;
        bool trustedRefreshRequested = nowUtc - _lastManualReloadAtUtc <= TrustedRefreshReloadWindow;
        _lastManualReloadAtUtc = nowUtc;
        SwkLogger.Debug($"UserListWindow.ReloadButton_Click: trustedRefresh={trustedRefreshRequested}");
        _intervalStepIndex = 0;
        _autoRefreshTimer.Interval = TimeSpan.FromSeconds(IntervalSteps[0]);
        await RunScanAndBuildAsync();

        if (trustedRefreshRequested)
        {
            await RefreshRegisteredFriendsFromBkAsync();
        }
    }

    private async void RefreshCertButton_Click(object sender, RoutedEventArgs e)
    {
        if (UserListView.SelectedItem is not UserListRow row || row.Friend is null || !row.Friend.HasCertificateMismatch)
        {
            UpdateRefreshCertButtonState();
            return;
        }

        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos = SwkNetworkCache.ShopInfos;
        SwkNotificationListener.ShopInfo? liveShop = FindLiveShopForFriend(row.Friend, shopInfos);
        if (liveShop is null)
        {
            StatusTextBlock.Text = "再確認できる接続先が見つかりません。先に再スキャンしてください。";
            UpdateRefreshCertButtonState();
            return;
        }

        List<Friend> friends = FriendsRepository.LoadAll().ToList();
        Friend? target = friends.FirstOrDefault(f => string.Equals(f.Id, row.Friend.Id, StringComparison.Ordinal));
        if (target is null)
        {
            StatusTextBlock.Text = "対象ユーザーを読み直してください。";
            UpdateRefreshCertButtonState();
            return;
        }

        ReloadButton.IsEnabled = false;
        RefreshCertButton.IsEnabled = false;
        LoadingBar.Visibility = Visibility.Visible;
        StatusTextBlock.Text = "証明書を再確認中…";
        try
        {
            bool refreshed = await TryRefreshFriendFromBkAsync(target, liveShop);
            if (!refreshed)
            {
                BuildUiFromCache();
                StatusTextBlock.Text = "証明書を更新できませんでした。";
                return;
            }

            if (!FriendsRepository.SaveAll(friends))
            {
                BuildUiFromCache();
                StatusTextBlock.Text = "保存できませんでした。";
                return;
            }

            _hasFriendUpdates = true;
            BuildUiFromCache();
            StatusTextBlock.Text = "証明書を更新しました。";
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            ReloadButton.IsEnabled = true;
            UpdateRefreshCertButtonState();
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
                _intervalStepIndex = 0;
                _autoRefreshTimer.Interval = TimeSpan.FromSeconds(IntervalSteps[0]);
                await RunScanAndBuildAsync();
            }
        }
        finally
        {
            _autoRefreshTimer.Start();
        }
    }

    private void UserListView_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateRefreshCertButtonState();
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
        bool sameShare = !string.IsNullOrWhiteSpace(friend.ShareName) &&
            string.Equals(friend.ShareName, shopInfo.ShareName, StringComparison.OrdinalIgnoreCase);

        bool sameLastIp = !string.IsNullOrWhiteSpace(friend.LastKnownAddress) &&
            string.Equals(friend.LastKnownAddress, candidate.Address.ToString(), StringComparison.OrdinalIgnoreCase);

        return sameShare || sameLastIp;
    }

    private static LanCandidate? FindCandidateForFriend(Friend friend, IReadOnlyList<LanCandidate> candidates)
    {
        return candidates.FirstOrDefault(c =>
            SameHost(friend.HostMachineName, c.HostName) ||
            (!string.IsNullOrWhiteSpace(friend.LastKnownAddress) &&
             string.Equals(c.Address.ToString(), friend.LastKnownAddress, StringComparison.OrdinalIgnoreCase)));
    }

    private static SwkNotificationListener.ShopInfo? FindLiveShopForFriend(
        Friend friend,
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos)
    {
        string normalizedHost = NormalizeHostName(friend.HostMachineName);

        SwkNotificationListener.ShopInfo? exact = shopInfos.FirstOrDefault(s =>
            string.Equals(NormalizeHostName(s.MachineName), normalizedHost, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.ShareName, friend.ShareName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        SwkNotificationListener.ShopInfo? sameMachine = shopInfos.FirstOrDefault(s =>
            string.Equals(NormalizeHostName(s.MachineName), normalizedHost, StringComparison.OrdinalIgnoreCase));
        if (sameMachine is not null) return sameMachine;

        if (!string.IsNullOrWhiteSpace(friend.LastKnownAddress))
        {
            return shopInfos.FirstOrDefault(s =>
                string.Equals(s.IpAddress, friend.LastKnownAddress, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(friend.ShareName))
        {
            List<SwkNotificationListener.ShopInfo> sameShare = shopInfos
                .Where(s => string.Equals(s.ShareName, friend.ShareName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sameShare.Count == 1)
            {
                SwkLogger.Debug($"FindLiveShopForFriend: recovered by unique share {friend.HostMachineName}/{friend.ShareName} -> {sameShare[0].MachineName}/{sameShare[0].ShareName}");
                return sameShare[0];
            }
        }

        return null;
    }

    private async Task RefreshRegisteredFriendsFromBkAsync()
    {
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos = SwkNetworkCache.ShopInfos;
        List<Friend> friends = FriendsRepository.LoadAll().ToList();
        List<(Friend Friend, SwkNotificationListener.ShopInfo LiveShop)> targets = friends
            .Select(friend => (Friend: friend, LiveShop: FindLiveShopForFriend(friend, shopInfos)))
            .Where(x => x.LiveShop is not null && x.Friend.HasCertificateMismatch)
            .Select(x => (x.Friend, x.LiveShop!))
            .ToList();

        if (targets.Count == 0)
        {
            SwkLogger.Debug("UserListWindow.RefreshRegisteredFriendsFromBkAsync: no cert mismatch targets");
            BuildUiFromCache();
            return;
        }

        ReloadButton.IsEnabled = false;
        LoadingBar.Visibility = Visibility.Visible;
        StatusTextBlock.Text = "BKへ再確認中…";

        int refreshed = 0;
        try
        {
            foreach ((Friend friend, SwkNotificationListener.ShopInfo liveShop) in targets)
            {
                if (await TryRefreshFriendFromBkAsync(friend, liveShop))
                {
                    refreshed++;
                }
            }

            if (refreshed > 0 && FriendsRepository.SaveAll(friends))
            {
                _hasFriendUpdates = true;
            }

            BuildUiFromCache();
            StatusTextBlock.Text = refreshed > 0
                ? $"BK再確認で {refreshed} 件更新しました。"
                : "BK再確認では更新できませんでした。";
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            ReloadButton.IsEnabled = true;
        }
    }

    private void UpdateRefreshCertButtonState()
    {
        if (RefreshCertButton == null)
        {
            return;
        }

        bool canRefresh = false;
        if (UserListView.SelectedItem is UserListRow row && row.Friend?.HasCertificateMismatch == true)
        {
            canRefresh = FindLiveShopForFriend(row.Friend, SwkNetworkCache.ShopInfos) is not null;
        }

        RefreshCertButton.IsEnabled = canRefresh && !_isRefreshing;
    }

    private static async Task<bool> TryRefreshFriendFromBkAsync(
        Friend friend,
        SwkNotificationListener.ShopInfo liveShop)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var listener = new SwkNotificationListener();
        SwkNotificationListener.InviteCodeResult result = await listener.RequestInviteCodeAsync(
            liveShop,
            inviteId: null,
            expectedThumbprint: null,
            cts.Token);

        if (!result.Success || string.IsNullOrEmpty(result.Password))
        {
            SwkLogger.Warn(
                $"UserListWindow.TryRefreshFriendFromBkAsync failed: id={friend.Id} {result.ErrorMessage ?? "empty password"}");
            return false;
        }

        string nowIso = DateTime.UtcNow.ToString("o");
        friend.HostMachineName = liveShop.MachineName;
        friend.ShareName = liveShop.ShareName;
        friend.PasswordProtected = FriendsRepository.ProtectPassword(result.Password);
        friend.OwnerCertThumbprint = result.CertThumbprint ?? string.Empty;
        friend.LastKnownAddress = liveShop.IpAddress ?? string.Empty;
        friend.LastFoundAt = nowIso;
        friend.LastCheckedAt = nowIso;
        friend.LastSeenAt = nowIso;
        friend.LastAccessIssue = null;
        return true;
    }

    private static bool UpdateFriendFromLiveShop(Friend friend, SwkNotificationListener.ShopInfo liveShop)
    {
        bool changed = false;
        string? previousLastSeen = friend.LastSeenAt;
        string nowIso = DateTime.UtcNow.ToString("o");

        if (!string.Equals(friend.HostMachineName, liveShop.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            friend.HostMachineName = liveShop.MachineName;
            changed = true;
        }

        if (!string.Equals(friend.ShareName, liveShop.ShareName, StringComparison.OrdinalIgnoreCase))
        {
            friend.ShareName = liveShop.ShareName;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(liveShop.IpAddress) &&
            !string.Equals(friend.LastKnownAddress, liveShop.IpAddress, StringComparison.OrdinalIgnoreCase))
        {
            friend.LastKnownAddress = liveShop.IpAddress;
            changed = true;
        }

        friend.LastFoundAt = nowIso;
        friend.LastCheckedAt = nowIso;
        friend.LastSeenAt = nowIso;
        return changed || !string.Equals(previousLastSeen, nowIso, StringComparison.Ordinal);
    }

    private static void AddMatchedCandidateKeys(HashSet<string> matchedHosts, LanCandidate candidate)
    {
        string host = NormalizeHostName(candidate.HostName);
        if (!string.IsNullOrWhiteSpace(host))
        {
            matchedHosts.Add(host);
        }
        matchedHosts.Add(candidate.Address.ToString());
    }

    private static string NormalizeHostName(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        string trimmed = host.Trim();
        int dot = trimmed.IndexOf('.');
        return dot > 0 ? trimmed[..dot] : trimmed;
    }
}

public enum UserListRowKind
{
    // 値の小さい順に表示される(0 が先頭)。
    ConnectedFriend = 0,        // Friend登録済み + 開店中
    SwitchCandidate = 1,        // Friend登録済み + 接続先切替候補
    NewShop = 2,                // Friend未登録 + 開店中
    UnreachableFriend = 3,      // Friend登録済み + オフライン
    WindowsPcOnly = 4,          // Friend未登録 + ShareWorkinなし（445+135応答）
    InstallCandidate = 5,       // Friend未登録 + ポート21/22応答（インストール候補）
}

public sealed class UserListRow
{
    public string NameLabel { get; init; } = string.Empty;
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
    };

    public static UserListRow ForUnreachableFriend(Friend friend) => new()
    {
        NameLabel = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName,
        ShareFolderName = friend.ShareName,
        Memo = friend.HasCertificateMismatch
            ? "証明書が登録時と違うため接続を停止中"
            : friend.Memo,
        IpLabel = friend.LastKnownAddress ?? string.Empty,
        Kind = UserListRowKind.UnreachableFriend,
        IconBrush = Brushes.White,
        IconImage = LoadIconImage(friend.IconKey),
        RowBackground = new SolidColorBrush(Color.FromRgb(255, 235, 230)),
        NameForeground = new SolidColorBrush(Color.FromRgb(150, 50, 40)),
        NameWeight = FontWeights.SemiBold,
        Friend = friend,
    };

    public static UserListRow ForNewShop(LanCandidate candidate, SwkNotificationListener.ShopInfo? shopInfo = null)
    {
        string host = string.IsNullOrWhiteSpace(candidate.HostName) ? candidate.Address.ToString() : candidate.HostName!;
        return new UserListRow
        {
            NameLabel = host,
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

    public static UserListRow ForSwitchCandidate(
        Friend friend,
        LanCandidate candidate,
        SwkNotificationListener.ShopInfo shopInfo)
    {
        string host = string.IsNullOrWhiteSpace(candidate.HostName) ? candidate.Address.ToString() : candidate.HostName!;
        string friendName = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName;
        return new UserListRow
        {
            NameLabel = friendName,
            ShareFolderName = shopInfo.ShareName,
            Memo = $"切替候補: {host}",
            IpLabel = candidate.Address.ToString(),
            Kind = UserListRowKind.SwitchCandidate,
            IconBrush = Brushes.White,
            IconImage = LoadIconImage(friend.IconKey),
            RowBackground = new SolidColorBrush(Color.FromRgb(255, 243, 224)),
            NameForeground = new SolidColorBrush(Color.FromRgb(191, 87, 0)),
            NameWeight = FontWeights.SemiBold,
            Friend = friend,
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
