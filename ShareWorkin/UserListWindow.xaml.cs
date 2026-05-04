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

        // 友達のホスト名にぶつかった候補を控える(=その友達は接続OK→非表示)。
        HashSet<string> matchedHosts = new(StringComparer.OrdinalIgnoreCase);
        List<UserListRow> rows = new();

        foreach (Friend f in friends)
        {
            LanCandidate? match = candidates.FirstOrDefault(c => SameHost(f.HostMachineName, c.HostName));
            if (match is not null)
            {
                // 接続OK登録済 = 一覧から非表示(草案7 §C: 要対応のものだけ並べる)。
                matchedHosts.Add(NormalizeHostName(match.HostName));
                continue;
            }
            rows.Add(UserListRow.ForUnreachableFriend(f));
        }

        string myHost = NormalizeHostName(Environment.MachineName);
        foreach (LanCandidate c in candidates)
        {
            string host = NormalizeHostName(c.HostName);
            if (string.IsNullOrEmpty(host)) host = c.Address.ToString();
            if (string.Equals(host, myHost, StringComparison.OrdinalIgnoreCase)) continue;
            if (matchedHosts.Contains(host)) continue;

            rows.Add(UserListRow.ForUnregisteredCandidate(c));
        }

        // 並び順: 不通お友達 → 未登録(草案7 §C)。
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

        int unreach = rows.Count(r => r.Kind == UserListRowKind.UnreachableFriend);
        int unset = rows.Count(r => r.Kind == UserListRowKind.UnregisteredCandidate);
        int hiddenOk = friends.Count - unreach;
        if (_rows.Count == 0)
        {
            StatusTextBlock.Text = hiddenOk > 0
                ? $"対応の必要なお友達はいません。({hiddenOk} 名は接続OKで非表示)"
                : "周りには誰もいません。";
        }
        else
        {
            StatusTextBlock.Text = hiddenOk > 0
                ? $"不通 {unreach} / 未登録 {unset} (接続OK {hiddenOk} 名は非表示)"
                : $"不通 {unreach} / 未登録 {unset}";
        }
        SwkLogger.Debug($"UserListWindow.LoadAsync done: unreach={unreach} unset={unset} hiddenOk={hiddenOk}");
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
        FriendsWindow pickup = new(this, row.Friend, row.Candidate, _lastCandidates);
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
    UnreachableFriend = 0,
    UnregisteredCandidate = 1,
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

    public static UserListRow ForUnreachableFriend(Friend friend)
    {
        // 強調表示: 要対応 (不通の登録済お友達)。
        return new UserListRow
        {
            NameLabel = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName,
            ShareFolderName = friend.ShareName,
            Memo = friend.Memo,
            IpLabel = string.IsNullOrWhiteSpace(friend.LastKnownAddress) ? string.Empty : friend.LastKnownAddress!,
            Kind = UserListRowKind.UnreachableFriend,
            IconBrush = ColorFromName(string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.HostMachineName : friend.DisplayName),
            RowBackground = new SolidColorBrush(Color.FromRgb(255, 235, 230)),
            NameForeground = new SolidColorBrush(Color.FromRgb(150, 50, 40)),
            NameWeight = FontWeights.SemiBold,
            NameStyle = FontStyles.Normal,
            Friend = friend,
            Candidate = null,
        };
    }

    public static UserListRow ForUnregisteredCandidate(LanCandidate candidate)
    {
        // 未登録: 通常表示、お友達名は (ホスト名) を仮置き(イタリック・薄色)。
        string host = string.IsNullOrWhiteSpace(candidate.HostName) ? candidate.Address.ToString() : candidate.HostName!;
        return new UserListRow
        {
            NameLabel = host,
            ShareFolderName = string.Empty,
            Memo = string.Empty,
            IpLabel = candidate.Address.ToString(),
            Kind = UserListRowKind.UnregisteredCandidate,
            IconBrush = new SolidColorBrush(Color.FromRgb(216, 210, 195)),
            RowBackground = Brushes.Transparent,
            NameForeground = new SolidColorBrush(Color.FromRgb(120, 110, 95)),
            NameWeight = FontWeights.Normal,
            NameStyle = FontStyles.Italic,
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
