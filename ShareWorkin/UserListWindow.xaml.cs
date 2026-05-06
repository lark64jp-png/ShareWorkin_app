using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ShareWorkin.SMB;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace ShareWorkin;

public partial class UserListWindow : Window
{
    private readonly Window _ownerWindow;
    private readonly ObservableCollection<UserListRow> _rows = new();

    public UserListWindow(Window owner)
    {
        InitializeComponent();
        _ownerWindow = owner;
        UserListView.ItemsSource = _rows;
        SwkLogger.Debug($"UserListWindow ctor: owner={owner?.GetType().Name ?? "null"}");
        Loaded += async (_, _) =>
        {
            SwkLogger.Debug("UserListWindow Loaded -> BuildFromCacheAsync");
            await BuildFromCacheAsync();
        };
    }

    // 一覧を開くたびに必ず再スキャンする。キャッシュがあれば暫定表示してから更新。
    private async Task BuildFromCacheAsync()
    {
        if (SwkNetworkCache.IsReady)
            BuildUiFromCache();
        await RunScanAndBuildAsync();
    }

    // スキャンを実行してキャッシュを更新し UI を再構築する（再スキャンボタン用）。
    private async Task RunScanAndBuildAsync()
    {
        ScanMode mode = ScanModeComboBox.SelectedIndex == 1 ? ScanMode.Full : ScanMode.Quick;

        ReloadButton.IsEnabled = false;
        LoadingBar.Visibility = Visibility.Visible;
        ScanStateTextBlock.Text = mode == ScanMode.Full ? "全PCスキャン" : "接続可能スキャン";
        StatusTextBlock.Text = "スキャン中…";

        try
        {
            await SwkNetworkCache.RefreshAsync(mode);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            ReloadButton.IsEnabled = true;
        }

        BuildUiFromCache();
    }

    private void BuildUiFromCache()
    {
        IReadOnlyList<LanCandidate> candidates = SwkNetworkCache.Candidates;
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos = SwkNetworkCache.ShopInfos;
        IReadOnlyList<Friend> friends = FriendsRepository.LoadAll();

        SwkLogger.Debug($"UserListWindow.BuildUiFromCache: friends={friends.Count} candidates={candidates.Count} shopInfos={shopInfos.Count}");

        HashSet<string> shopOpen = new(StringComparer.OrdinalIgnoreCase);
        foreach (SwkNotificationListener.ShopInfo s in shopInfos)
        {
            shopOpen.Add(NormalizeHostName(s.MachineName));
        }

        HashSet<string> matchedHosts = new(StringComparer.OrdinalIgnoreCase);
        List<UserListRow> rows = new();

        foreach (Friend f in friends)
        {
            LanCandidate? match = candidates.FirstOrDefault(c => SameHost(f.HostMachineName, c.HostName));
            string normalizedHost = NormalizeHostName(f.HostMachineName);
            bool isOpen = shopOpen.Contains(normalizedHost);

            if (match is not null)
            {
                matchedHosts.Add(NormalizeHostName(match.HostName));
                rows.Add(isOpen
                    ? UserListRow.ForConnectedFriend(f)
                    : UserListRow.ForUnreachableFriend(f));
            }
            else
            {
                rows.Add(UserListRow.ForUnreachableFriend(f));
            }
        }

        string myHost = NormalizeHostName(Environment.MachineName);

        foreach (LanCandidate c in candidates)
        {
            string host = NormalizeHostName(c.HostName);
            if (string.IsNullOrEmpty(host)) host = c.Address.ToString();
            if (string.Equals(host, myHost, StringComparison.OrdinalIgnoreCase)) continue;
            if (matchedHosts.Contains(host)) continue;

            bool isOpen = shopOpen.Contains(host);

            if (isOpen)
            {
                SwkNotificationListener.ShopInfo? shopInfo = shopInfos
                    .FirstOrDefault(s => string.Equals(NormalizeHostName(s.MachineName), host, StringComparison.OrdinalIgnoreCase));
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

        int connected = rows.Count(r => r.Kind == UserListRowKind.ConnectedFriend);
        int newShop = rows.Count(r => r.Kind == UserListRowKind.NewShop);
        int unreach = rows.Count(r => r.Kind == UserListRowKind.UnreachableFriend);
        int windowsPcOnly = rows.Count(r => r.Kind == UserListRowKind.WindowsPcOnly);
        int installCandidate = rows.Count(r => r.Kind == UserListRowKind.InstallCandidate);

        string modeLabel = SwkNetworkCache.LastScanMode == ScanMode.Full ? "全PCスキャン" : "接続可能スキャン";
        ScanStateTextBlock.Text = modeLabel;
        StatusTextBlock.Text = _rows.Count == 0
            ? "周りには誰もいません。"
            : $"登録済接続中 {connected}　登録済不在 {unreach}　登録可能 {newShop}　登録不可 {windowsPcOnly}　全PC分 {installCandidate}";

        SwkLogger.Debug($"UserListWindow.BuildUiFromCache done: connected={connected} newShop={newShop} unreach={unreach} windowsPcOnly={windowsPcOnly} installCandidate={installCandidate}");
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        SwkLogger.Debug("UserListWindow.ReloadButton_Click");
        await RunScanAndBuildAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SwkLogger.Debug("UserListWindow.CloseButton_Click");
        DialogResult = true;
        Close();
    }

    private async void UserListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (UserListView.SelectedItem is not UserListRow row)
        {
            return;
        }

        SwkLogger.Debug($"UserListWindow.UserListView_MouseDoubleClick: row={row.Kind}/{row.NameLabel}");

        IReadOnlyList<LanCandidate> candidates = SwkNetworkCache.Candidates;
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos = SwkNetworkCache.ShopInfos;

        FriendsWindow pickup = (row.Kind == UserListRowKind.NewShop && row.ShopInfo != null)
            ? new FriendsWindow(this, row.ShopInfo, candidates, shopInfos)
            : new FriendsWindow(this, row.Friend, row.Candidate, candidates, shopInfos);
        bool? result = pickup.ShowDialog();
        SwkLogger.Debug($"UserListWindow.UserListView_MouseDoubleClick: pickup result={result}");
        if (result == true)
        {
            await RunScanAndBuildAsync();
        }
    }

    private static bool SameHost(string expected, string? found)
    {
        string e1 = NormalizeHostName(expected);
        string f1 = NormalizeHostName(found);
        return !string.IsNullOrWhiteSpace(e1) && !string.IsNullOrWhiteSpace(f1)
            && string.Equals(e1, f1, StringComparison.OrdinalIgnoreCase);
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
    NewShop = 1,                // Friend未登録 + 開店中
    UnreachableFriend = 2,      // Friend登録済み + オフライン
    WindowsPcOnly = 3,          // Friend未登録 + ShareWorkinなし（445+135応答）
    InstallCandidate = 4,       // Friend未登録 + ポート21/22応答（インストール候補）
}

public sealed class UserListRow
{
    public string NameLabel { get; init; } = string.Empty;
    public string ShareFolderName { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
    public string IpLabel { get; init; } = string.Empty;
    public UserListRowKind Kind { get; init; }
    public Brush IconBrush { get; init; } = Brushes.LightGray;
    public Brush RowBackground { get; init; } = Brushes.Transparent;
    public Brush NameForeground { get; init; } = Brushes.Black;
    public FontWeight NameWeight { get; init; } = FontWeights.Medium;
    public System.Windows.FontStyle NameStyle { get; init; } = FontStyles.Normal;

    public Friend? Friend { get; init; }
    public LanCandidate? Candidate { get; init; }
    public SwkNotificationListener.ShopInfo? ShopInfo { get; init; }

    public static UserListRow ForConnectedFriend(Friend friend) => new()
    {
        NameLabel = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName,
        ShareFolderName = friend.ShareName,
        Memo = friend.Memo,
        IpLabel = friend.LastKnownAddress ?? string.Empty,
        Kind = UserListRowKind.ConnectedFriend,
        IconBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
        RowBackground = Brushes.Transparent,
        NameForeground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
        NameWeight = FontWeights.Normal,
        Friend = friend,
    };

    public static UserListRow ForUnreachableFriend(Friend friend) => new()
    {
        NameLabel = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName,
        ShareFolderName = friend.ShareName,
        Memo = friend.Memo,
        IpLabel = friend.LastKnownAddress ?? string.Empty,
        Kind = UserListRowKind.UnreachableFriend,
        IconBrush = new SolidColorBrush(Color.FromRgb(244, 67, 54)),
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
            IconBrush = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
            RowBackground = Brushes.Transparent,
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
            IpLabel = candidate.Address.ToString(),
            Kind = UserListRowKind.WindowsPcOnly,
            IconBrush = new SolidColorBrush(Color.FromRgb(158, 158, 158)),
            RowBackground = Brushes.Transparent,
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
            IconBrush = new SolidColorBrush(Color.FromRgb(100, 140, 210)),
            RowBackground = Brushes.Transparent,
            NameForeground = new SolidColorBrush(Color.FromRgb(80, 110, 170)),
            NameWeight = FontWeights.Normal,
            Candidate = candidate,
        };
    }
}
