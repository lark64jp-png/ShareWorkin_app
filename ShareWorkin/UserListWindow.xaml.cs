using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

    // 定期チェック用：UI表示なしでスキャンする。変化がなくても 8 秒固定で回し続ける。
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
        bool friendsChanged = false;

        SwkLogger.Debug($"UserListWindow.BuildUiFromCache: friends={friends.Count} candidates={candidates.Count} shopInfos={shopInfos.Count}");

        HashSet<string> representedCandidateKeys = new(StringComparer.OrdinalIgnoreCase);
        List<UserListRow> rows = new();

        foreach (Friend f in friends)
        {
            SwkNotificationListener.ShopInfo? liveShop = FindLiveShopForFriend(f, shopInfos);
            if (liveShop is not null && UpdateFriendFromLiveShop(f, liveShop))
            {
                friendsChanged = true;
            }

            if (liveShop is not null && !f.HasCertificateMismatch)
            {
                rows.Add(CanOpenFriendShare(f, liveShop)
                    ? UserListRow.ForConnectedFriend(f, liveShop)
                    : UserListRow.ForResumeRequiredFriend(f, liveShop));
            }
            else
            {
                rows.Add(UserListRow.ForUnreachableFriend(f, FindCandidateForFriend(f, candidates), liveShop));
            }

            if (TryFindRecoveryCandidateForFriend(f, candidates, shopInfos, out LanCandidate? recoveryCandidate, out SwkNotificationListener.ShopInfo? recoveryShop))
            {
                rows.Add(UserListRow.ForSwitchCandidate(f, recoveryCandidate!, recoveryShop!));
                AddCandidateKeys(representedCandidateKeys, recoveryCandidate!);
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
            if (representedCandidateKeys.Contains(c.Address.ToString())) continue;
            if (representedCandidateKeys.Contains(host)) continue;

            SwkNotificationListener.ShopInfo? shopInfo = shopInfos
                .FirstOrDefault(s => string.Equals(NormalizeHostName(s.MachineName), host, StringComparison.OrdinalIgnoreCase));
            if (IsCandidateCoveredByRegisteredFriend(c, shopInfo, friends))
            {
                continue;
            }
            bool isOpen = shopInfo is not null;

            if (isOpen)
            {
                rows.Add(UserListRow.ForNewShop(c, shopInfo));
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

        SwkLogger.Debug("UserListWindow.BuildUiFromCache rows: " +
            string.Join(" | ", rows.Select(r => $"{r.Kind}:{r.StatusLabel}:{r.NameLabel}:{r.IpLabel}")));

        int connected = rows.Count(r => r.Kind == UserListRowKind.ConnectedFriend);
        int resumeRequired = rows.Count(r => r.Kind == UserListRowKind.ResumeRequiredFriend);
        int switchable = rows.Count(r => r.Kind == UserListRowKind.SwitchCandidate);
        int newShop = rows.Count(r => r.Kind == UserListRowKind.NewShop);
        int unreach = rows.Count(r => r.Kind == UserListRowKind.UnreachableFriend);
        int windowsPcOnly = rows.Count(r => r.Kind == UserListRowKind.WindowsPcOnly);
        int installCandidate = rows.Count(r => r.Kind == UserListRowKind.InstallCandidate);

        string modeLabel = SwkNetworkCache.LastScanMode == ScanMode.Full ? "全PCスキャン" : "接続可能スキャン";
        ScanStateTextBlock.Text = modeLabel;
        if (_rows.Count == 0)
        {
            StatusTextBlock.Text = "周りには誰もいません。";
        }
        else
        {
            SetStatusCountsText(
                connected,
                resumeRequired,
                switchable,
                unreach,
                newShop,
                windowsPcOnly,
                installCandidate);
        }

        SwkLogger.Debug($"UserListWindow.BuildUiFromCache done: connected={connected} resumeRequired={resumeRequired} switchable={switchable} newShop={newShop} unreach={unreach} windowsPcOnly={windowsPcOnly} installCandidate={installCandidate}");
    }

    private void SetStatusCountsText(
        int connected,
        int resumeRequired,
        int switchable,
        int unreach,
        int newShop,
        int windowsPcOnly,
        int installCandidate)
    {
        StatusTextBlock.Inlines.Clear();
        AddStatusRun("登録済接続可能", connected, Color.FromRgb(76, 175, 80));
        AddSeparatorRun();
        AddStatusRun("再開待ち", resumeRequired, Color.FromRgb(191, 87, 0));
        AddSeparatorRun();
        AddStatusRun("切替候補", switchable, Color.FromRgb(191, 87, 0));
        AddSeparatorRun();
        AddStatusRun("登録済不在", unreach, Color.FromRgb(150, 50, 40));
        AddSeparatorRun();
        AddStatusRun("登録可能", newShop, Color.FromRgb(255, 152, 0));
        AddSeparatorRun();
        AddStatusRun("登録不可", windowsPcOnly, Color.FromRgb(120, 120, 120));
        AddSeparatorRun();
        AddStatusRun("全PC分", installCandidate, Color.FromRgb(80, 110, 170));
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

    private static bool TryFindRecoveryCandidateForFriend(
        Friend friend,
        IReadOnlyList<LanCandidate> candidates,
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos,
        out LanCandidate? candidate,
        out SwkNotificationListener.ShopInfo? shopInfo)
    {
        candidate = FindCandidateForFriend(friend, candidates);
        shopInfo = null;
        if (candidate is null)
        {
            return false;
        }

        string candidateHost = NormalizeHostName(candidate.HostName);
        if (string.IsNullOrEmpty(candidateHost))
        {
            candidateHost = candidate.Address.ToString();
        }
        string candidateIp = candidate.Address.ToString();

        shopInfo = shopInfos.FirstOrDefault(s =>
            string.Equals(NormalizeHostName(s.MachineName), candidateHost, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.IpAddress, candidateIp, StringComparison.OrdinalIgnoreCase));
        if (shopInfo is null)
        {
            return false;
        }

        bool sameShare = string.Equals(friend.ShareName, shopInfo.ShareName, StringComparison.OrdinalIgnoreCase);
        bool sameHost = SameHost(friend.HostMachineName, candidate.HostName) ||
            (!string.IsNullOrWhiteSpace(friend.LastKnownAddress) &&
             string.Equals(friend.LastKnownAddress, candidateIp, StringComparison.OrdinalIgnoreCase));

        if (sameHost && sameShare && !friend.HasCertificateMismatch)
        {
            return false;
        }

        return friend.HasCertificateMismatch || IsLikelySwitchMatch(friend, candidate, shopInfo);
    }

    private async void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: UserListRow row } || row.Friend is null)
        {
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

        ReloadButton.IsEnabled = false;
        LoadingBar.Visibility = Visibility.Visible;
        StatusTextBlock.Text = "接続情報を再取得しています…";
        try
        {
            bool refreshed = await TryRefreshFriendFromBkAsync(target, liveShop);
            if (!refreshed)
            {
                BuildUiFromCache();
                StatusTextBlock.Text = "接続情報を再取得できませんでした。";
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
            StatusTextBlock.Text = "接続情報を再取得しました。";
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
    {
        string normalizedHost = NormalizeHostName(friend.HostMachineName);

        SwkNotificationListener.ShopInfo? exact = shopInfos.FirstOrDefault(s =>
            string.Equals(NormalizeHostName(s.MachineName), normalizedHost, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.ShareName, friend.ShareName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        return null;
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

    private static bool IsCandidateCoveredByRegisteredFriend(
        LanCandidate candidate,
        SwkNotificationListener.ShopInfo? shopInfo,
        IReadOnlyList<Friend> friends)
    {
        foreach (Friend friend in friends)
        {
            bool sameHost = SameHost(friend.HostMachineName, candidate.HostName);
            if (!sameHost)
            {
                continue;
            }

            if (shopInfo is null)
            {
                return true;
            }

            if (string.Equals(friend.ShareName, shopInfo.ShareName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
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
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        string trimmed = host.Trim();
        int dot = trimmed.IndexOf('.');
        return dot > 0 ? trimmed[..dot] : trimmed;
    }

    private static bool CanOpenFriendShare(Friend friend, SwkNotificationListener.ShopInfo liveShop)
    {
        // 相手PCの終了直後は SMB/UNC の確認が OS 側で長く待つことがある。
        // ユーザー一覧の再描画で UI を止めないよう、共有確認は短時間で打ち切る。
        const int timeoutMs = 1500;
        try
        {
            Task<bool> probeTask = Task.Run(() => ProbeFriendShare(friend, liveShop));
            if (probeTask.Wait(timeoutMs))
            {
                return probeTask.Result;
            }

            SwkLogger.Warn($"UserListWindow.CanOpenFriendShare timed out after {timeoutMs}ms: friend={friend.Id}");
            return false;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"UserListWindow.CanOpenFriendShare failed: friend={friend.Id} {ex.Message}");
            return false;
        }
    }

    private static bool ProbeFriendShare(Friend friend, SwkNotificationListener.ShopInfo liveShop)
    {
        List<string> candidates = BuildFriendUncCandidates(friend, liveShop);
        string password = FriendsRepository.UnprotectPassword(friend.PasswordProtected);

        foreach (string path in candidates)
        {
            if (CanEnumerateShare(path))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(password))
            {
                SmbConnectionHelper.EnsureConnection(path, friend.UserName, password, liveShop.MachineName);
                if (CanEnumerateShare(path))
                {
                    return true;
                }
            }
        }

        SwkLogger.Debug($"UserListWindow.ProbeFriendShare failed: friend={friend.Id} candidates={string.Join(", ", candidates)}");
        return false;
    }

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
}

public enum UserListRowKind
{
    // 値の小さい順に表示される(0 が先頭)。
    ConnectedFriend = 0,        // Friend登録済み + 開店中
    ResumeRequiredFriend = 1,   // Friend登録済み + 開店中 + SMB共有列挙NG
    SwitchCandidate = 2,        // Friend登録済み + 接続先切替候補
    NewShop = 3,                // Friend未登録 + 開店中
    UnreachableFriend = 4,      // Friend登録済み + オフライン
    WindowsPcOnly = 5,          // Friend未登録 + ShareWorkinなし（445+135応答）
    InstallCandidate = 6,       // Friend未登録 + ポート21/22応答（インストール候補）
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
        Memo = "共有フォルダを表示できません。再開を試してください。",
        IpLabel = liveShop.IpAddress ?? friend.LastKnownAddress ?? string.Empty,
        Kind = UserListRowKind.ResumeRequiredFriend,
        IconBrush = Brushes.White,
        IconImage = LoadIconImage(friend.IconKey),
        RowBackground = new SolidColorBrush(Color.FromRgb(255, 243, 224)),
        NameForeground = new SolidColorBrush(Color.FromRgb(191, 87, 0)),
        NameWeight = FontWeights.SemiBold,
        ResumeButtonVisibility = Visibility.Visible,
        Friend = friend,
        ShopInfo = liveShop,
    };

    public static UserListRow ForUnreachableFriend(
        Friend friend,
        LanCandidate? candidate = null,
        SwkNotificationListener.ShopInfo? liveShop = null) => new()
    {
        NameLabel = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName,
        ShareFolderName = friend.ShareName,
        StatusLabel = liveShop is not null
            ? "登録済み / 要再確認"
            : candidate is not null
                ? "登録済み / 候補あり"
                : "登録済み / 候補不明",
        Memo = friend.HasCertificateMismatch
            ? "証明書が登録時と違うため接続を停止中"
            : string.IsNullOrWhiteSpace(friend.Memo)
                ? candidate is not null ? "接続先の見直し候補があります。" : "接続先を確認できていません。"
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
            StatusLabel = "登録済み / 切替候補",
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
