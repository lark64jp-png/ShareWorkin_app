using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
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

        HashSet<string> matchedHosts = new(StringComparer.OrdinalIgnoreCase);
        List<UserListRow> rows = new();

        foreach (Friend f in friends)
        {
            string display = string.IsNullOrWhiteSpace(f.DisplayName) ? f.HostMachineName : f.DisplayName;
            LanCandidate? match = candidates.FirstOrDefault(c => SameHost(f.HostMachineName, c.HostName));
            if (match is not null)
            {
                matchedHosts.Add(NormalizeHostName(match.HostName));
                rows.Add(UserListRow.ActiveFriend(display, f.HostMachineName, match.Address.ToString()));
            }
            else
            {
                rows.Add(UserListRow.UnreachableFriend(display, f.HostMachineName));
            }
        }

        string myHost = NormalizeHostName(Environment.MachineName);
        foreach (LanCandidate c in candidates)
        {
            string host = NormalizeHostName(c.HostName);
            if (string.IsNullOrEmpty(host)) continue;
            if (string.Equals(host, myHost, StringComparison.OrdinalIgnoreCase)) continue;
            if (matchedHosts.Contains(host)) continue;

            rows.Add(UserListRow.Unconfigured(c.HostName ?? host, c.Address.ToString()));
        }

        rows.Sort(static (a, b) =>
        {
            int byKind = ((int)a.Kind).CompareTo((int)b.Kind);
            if (byKind != 0) return byKind;
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        foreach (UserListRow r in rows)
        {
            _rows.Add(r);
        }

        if (_rows.Count == 0)
        {
            StatusTextBlock.Text = "周りには誰もいません。";
        }
        else
        {
            int active = rows.Count(r => r.Kind == UserListRowKind.ActiveFriend);
            int unreach = rows.Count(r => r.Kind == UserListRowKind.UnreachableFriend);
            int unset = rows.Count(r => r.Kind == UserListRowKind.Unconfigured);
            StatusTextBlock.Text = $"認識中 {active} / 不在 {unreach} / 未設定 {unset}";
        }
    }

    private void InviteButton_Click(object sender, RoutedEventArgs e)
    {
        FriendsWindow window = new(_ownerWindow, null) { Owner = this };
        window.ShowDialog();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
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
    ActiveFriend = 0,
    UnreachableFriend = 1,
    Unconfigured = 2,
}

public sealed class UserListRow
{
    public string DisplayName { get; init; } = string.Empty;
    public string DetailLabel { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public UserListRowKind Kind { get; init; }
    public Brush IconBrush { get; init; } = Brushes.LightGray;
    public Brush BandBrush { get; init; } = Brushes.Transparent;
    public Brush StatusForeground { get; init; } = Brushes.Black;

    public static UserListRow ActiveFriend(string displayName, string host, string address)
    {
        return new UserListRow
        {
            DisplayName = displayName,
            DetailLabel = $"{host}  ({address})",
            StatusLabel = "認識中",
            Kind = UserListRowKind.ActiveFriend,
            IconBrush = ColorFromName(displayName),
            BandBrush = new SolidColorBrush(Color.FromRgb(231, 243, 238)),
            StatusForeground = new SolidColorBrush(Color.FromRgb(49, 92, 80)),
        };
    }

    public static UserListRow UnreachableFriend(string displayName, string host)
    {
        return new UserListRow
        {
            DisplayName = displayName,
            DetailLabel = host,
            StatusLabel = "不在",
            Kind = UserListRowKind.UnreachableFriend,
            IconBrush = ColorFromName(displayName),
            BandBrush = new SolidColorBrush(Color.FromRgb(241, 239, 232)),
            StatusForeground = new SolidColorBrush(Color.FromRgb(120, 113, 100)),
        };
    }

    public static UserListRow Unconfigured(string host, string address)
    {
        return new UserListRow
        {
            DisplayName = host,
            DetailLabel = address,
            StatusLabel = "未設定",
            Kind = UserListRowKind.Unconfigured,
            IconBrush = new SolidColorBrush(Color.FromRgb(216, 210, 195)),
            BandBrush = new SolidColorBrush(Color.FromRgb(248, 245, 235)),
            StatusForeground = new SolidColorBrush(Color.FromRgb(155, 130, 92)),
        };
    }

    private static Brush ColorFromName(string name)
    {
        // Stable pseudo-random color from the display name so each friend keeps a
        // recognizable square. Not a real avatar — just a visual hook.
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
