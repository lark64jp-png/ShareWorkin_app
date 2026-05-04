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
    private IReadOnlyList<LanCandidate> _lastCandidates = Array.Empty<LanCandidate>();
    private IReadOnlyList<SwkNotificationListener.ShopInfo> _lastShopInfos = Array.Empty<SwkNotificationListener.ShopInfo>();

    public UserListWindow(Window owner)
    {
        InitializeComponent();
        _ownerWindow = owner;
        UserListView.ItemsSource = _rows;
        SwkLogger.Debug($"UserListWindow ctor: owner={owner?.GetType().Name ?? "null"}");
        Loaded += async (_, _) =>
        {
            SwkLogger.Debug("UserListWindow Loaded -> LoadAsync");
            await LoadAsync();
        };
    }

    // 単一の読み込み処理。初期表示・再読み込み・ピックアップ確定後の更新、
    // すべてここを呼び出す(草案7 §C: 「読み込みは1コンポーネントの処理」)。
    private async Task LoadAsync()
    {
        _rows.Clear();
        StatusTextBlock.Text = "周りを見ています…";

        IReadOnlyList<Friend> friends = FriendsRepository.LoadAll();
        IReadOnlyList<LanCandidate> candidates;
        try
        {
            candidates = await LanScanner.ScanAsync();
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"UserListWindow LAN scan failed: {ex.Message}");
            candidates = Array.Empty<LanCandidate>();
            StatusTextBlock.Text = "周りを見られませんでした。";
        }

        _lastCandidates = candidates;
        SwkLogger.Debug($"UserListWindow.LoadAsync: friends={friends.Count} candidates={candidates.Count}");

        // UDP プローブで実際に開店中のお店を発見する
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos;
        try
        {
            shopInfos = await SwkNotificationListener.ProbeHostsAsync(candidates, CancellationToken.None);
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"UserListWindow ProbeHostsAsync failed: {ex.Message}");
            shopInfos = Array.Empty<SwkNotificationListener.ShopInfo>();
        }
        _lastShopInfos = shopInfos;

        // 開店中ホスト名セット（正規化済み）
        HashSet<string> shopOpen = new(StringComparer.OrdinalIgnoreCase);
        foreach (SwkNotificationListener.ShopInfo s in shopInfos)
        {
            shopOpen.Add(NormalizeHostName(s.MachineName));
        }

        HashSet<string> matchedHosts = new(StringComparer.OrdinalIgnoreCase);
        List<UserListRow> rows = new();

        // Friends を処理: Friend登録済みのホストを分類
        foreach (Friend f in friends)
        {
            LanCandidate? match = candidates.FirstOrDefault(c => SameHost(f.HostMachineName, c.HostName));
            string normalizedHost = NormalizeHostName(f.HostMachineName);
            bool isOpen = shopOpen.Contains(normalizedHost);

            if (match is not null)
            {
                matchedHosts.Add(NormalizeHostName(match.HostName));

                // ① 接続可能: 登録済み + 開店中
                if (isOpen)
                {
                    rows.Add(UserListRow.ForConnectedFriend(f));
                }
                else
                {
                    // ② 不通: 登録済み + オフライン
                    rows.Add(UserListRow.ForUnreachableFriend(f));
                }
            }
            else
            {
                // ② 不通: 登録済みだがLANで見つからない
                rows.Add(UserListRow.ForUnreachableFriend(f));
            }
        }

        string myHost = NormalizeHostName(Environment.MachineName);

        // Candidates を処理: Friend未登録のホストを分類
        foreach (LanCandidate c in candidates)
        {
            string host = NormalizeHostName(c.HostName);
            if (string.IsNullOrEmpty(host)) host = c.Address.ToString();
            if (string.Equals(host, myHost, StringComparison.OrdinalIgnoreCase)) continue;
            if (matchedHosts.Contains(host)) continue;

            bool isOpen = shopOpen.Contains(host);

            if (isOpen)
            {
                // ③ 新しいお店: 未登録 + 開店中
                SwkNotificationListener.ShopInfo? shopInfo = _lastShopInfos
                    .FirstOrDefault(s => string.Equals(NormalizeHostName(s.MachineName), host, StringComparison.OrdinalIgnoreCase));
                rows.Add(UserListRow.ForNewShop(c, shopInfo));
            }
            else
            {
                // ④ Windows PCのみ: 未登録 + オフライン/ShareWorkinなし
                rows.Add(UserListRow.ForWindowsPcOnly(c));
            }
        }

        // 並び順: 接続可能 → 新しいお店 → 不通 → Windows PCのみ
        rows.Sort(static (a, b) =>
        {
            int byKind = ((int)a.Kind).CompareTo((int)b.Kind);
            if (byKind != 0) return byKind;
            return string.Compare(a.NameLabel, b.NameLabel, StringComparison.OrdinalIgnoreCase);
        });

        foreach (UserListRow r in rows)
        {
            _rows.Add(r);
        }

        int connected = rows.Count(r => r.Kind == UserListRowKind.ConnectedFriend);
        int newShop = rows.Count(r => r.Kind == UserListRowKind.NewShop);
        int unreach = rows.Count(r => r.Kind == UserListRowKind.UnreachableFriend);
        int windowsPcOnly = rows.Count(r => r.Kind == UserListRowKind.WindowsPcOnly);

        if (_rows.Count == 0)
        {
            StatusTextBlock.Text = "周りには誰もいません。";
        }
        else
        {
            List<string> status = new();
            if (connected > 0) status.Add($"接続可能 {connected}");
            if (newShop > 0) status.Add($"新しいお店 {newShop}");
            if (unreach > 0) status.Add($"不通 {unreach}");
            if (windowsPcOnly > 0) status.Add($"PC {windowsPcOnly}");
            StatusTextBlock.Text = string.Join(" / ", status);
        }

        SwkLogger.Debug($"UserListWindow.LoadAsync done: connected={connected} newShop={newShop} unreach={unreach} windowsPcOnly={windowsPcOnly}");
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        SwkLogger.Debug("UserListWindow.ReloadButton_Click");
        await LoadAsync();
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
        FriendsWindow pickup = (row.Kind == UserListRowKind.NewShop && row.ShopInfo != null)
            ? new FriendsWindow(this, row.ShopInfo)
            : new FriendsWindow(this, row.Friend, row.Candidate, _lastCandidates);
        bool? result = pickup.ShowDialog();
        SwkLogger.Debug($"UserListWindow.UserListView_MouseDoubleClick: pickup result={result}");
        if (result == true)
        {
            await LoadAsync();
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
    // 優先度順: 接続可能 → 新しいお店 → 不通 → Windows PC のみ
    ConnectedFriend = 0,        // Friend登録済み + 開店中
    NewShop = 1,                // Friend未登録 + 開店中
    UnreachableFriend = 2,      // Friend登録済み + オフライン
    WindowsPcOnly = 3,          // Friend未登録 + ShareWorkinなし
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

    /// <summary>
    /// ① 接続可能: Friend登録済み + 開店中（通知受信中）
    /// </summary>
    public static UserListRow ForConnectedFriend(Friend friend)
    {
        return new UserListRow
        {
            NameLabel = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName,
            ShareFolderName = friend.ShareName,
            Memo = friend.Memo,
            IpLabel = string.IsNullOrWhiteSpace(friend.LastKnownAddress) ? string.Empty : friend.LastKnownAddress!,
            Kind = UserListRowKind.ConnectedFriend,
            IconBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80)), // 緑
            RowBackground = Brushes.Transparent,
            NameForeground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
            NameWeight = FontWeights.Normal,
            NameStyle = FontStyles.Normal,
            Friend = friend,
            Candidate = null,
        };
    }

    /// <summary>
    /// ② 不通: Friend登録済み + オフライン（通知なし）
    /// </summary>
    public static UserListRow ForUnreachableFriend(Friend friend)
    {
        return new UserListRow
        {
            NameLabel = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName,
            ShareFolderName = friend.ShareName,
            Memo = friend.Memo,
            IpLabel = string.IsNullOrWhiteSpace(friend.LastKnownAddress) ? string.Empty : friend.LastKnownAddress!,
            Kind = UserListRowKind.UnreachableFriend,
            IconBrush = new SolidColorBrush(Color.FromRgb(244, 67, 54)), // 赤
            RowBackground = new SolidColorBrush(Color.FromRgb(255, 235, 230)),
            NameForeground = new SolidColorBrush(Color.FromRgb(150, 50, 40)),
            NameWeight = FontWeights.SemiBold,
            NameStyle = FontStyles.Normal,
            Friend = friend,
            Candidate = null,
        };
    }

    /// <summary>
    /// ③ 新しいお店: Friend未登録 + 開店中（UDP で発見済み）
    /// </summary>
    public static UserListRow ForNewShop(LanCandidate candidate, SwkNotificationListener.ShopInfo? shopInfo = null)
    {
        string host = string.IsNullOrWhiteSpace(candidate.HostName) ? candidate.Address.ToString() : candidate.HostName!;
        string shareName = shopInfo?.ShareName ?? string.Empty;
        return new UserListRow
        {
            NameLabel = host,
            ShareFolderName = shareName,
            Memo = string.Empty,
            IpLabel = candidate.Address.ToString(),
            Kind = UserListRowKind.NewShop,
            IconBrush = new SolidColorBrush(Color.FromRgb(255, 193, 7)), // 黄
            RowBackground = Brushes.Transparent,
            NameForeground = new SolidColorBrush(Color.FromRgb(255, 152, 0)),
            NameWeight = FontWeights.Normal,
            NameStyle = FontStyles.Normal,
            Friend = null,
            Candidate = candidate,
            ShopInfo = shopInfo,
        };
    }

    /// <summary>
    /// ④ Windows PCのみ: Friend未登録 + ShareWorkinなし
    /// </summary>
    public static UserListRow ForWindowsPcOnly(LanCandidate candidate)
    {
        string host = string.IsNullOrWhiteSpace(candidate.HostName) ? candidate.Address.ToString() : candidate.HostName!;
        return new UserListRow
        {
            NameLabel = host,
            ShareFolderName = string.Empty,
            Memo = string.Empty,
            IpLabel = candidate.Address.ToString(),
            Kind = UserListRowKind.WindowsPcOnly,
            IconBrush = new SolidColorBrush(Color.FromRgb(158, 158, 158)), // グレー
            RowBackground = Brushes.Transparent,
            NameForeground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
            NameWeight = FontWeights.Normal,
            NameStyle = FontStyles.Normal,
            Friend = null,
            Candidate = candidate,
        };
    }


    private static Brush ColorFromName(string name)
    {
        // 呼び名から擬似乱数色 — お友達ごとに見分けやすい色四角を作るための仮アイコン。
        unchecked
        {
            int h = 23;
            foreach (char ch in name) h = h * 31 + ch;
            byte r = (byte)(120 + (h & 0x7F));
            byte g = (byte)(120 + ((h >> 7) & 0x7F));
            byte b = (byte)(120 + ((h >> 14) & 0x7F));
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        }
    }
}
