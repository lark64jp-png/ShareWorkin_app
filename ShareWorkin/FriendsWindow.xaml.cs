using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ShareWorkin.SMB;

namespace ShareWorkin;

// 草案7 §B: ピックアップ詳細ウィンドウ。「お友達情報の編集」と「未登録 SMB 候補の登録」
// の両方をここでこなす。タイトルが ピックアップ で、クラス名は WPF 互換のため FriendsWindow のまま。
public partial class FriendsWindow : Window
{
    private enum PickupMode { New, Update }

    private readonly Window _ownerWindow;
    private Friend? _existingFriend;
    private LanCandidate? _initialCandidate;
    private IReadOnlyList<LanCandidate> _candidates;
    private readonly ObservableCollection<CandidateRow> _candidateRows = new();
    private InviteTokenPayload? _parsedInvite;
    private readonly PickupMode _mode;
    private string _initialName = string.Empty;
    private string _initialMemo = string.Empty;
    private bool _initialApplied;

    public FriendsWindow(Window owner, Friend? existing, LanCandidate? initialCandidate, IReadOnlyList<LanCandidate> candidates)
    {
        InitializeComponent();
        _ownerWindow = owner;
        _existingFriend = existing;
        _initialCandidate = initialCandidate;
        _candidates = candidates;
        _mode = existing is null ? PickupMode.New : PickupMode.Update;
        CandidateListView.ItemsSource = _candidateRows;
        SwkLogger.Debug(
            $"FriendsWindow ctor: mode={_mode} " +
            $"existing={(existing?.DisplayName ?? "null")} " +
            $"initialCand={(initialCandidate?.HostName ?? initialCandidate?.Address.ToString() ?? "null")} " +
            $"candidates={candidates.Count}");
        Loaded += (_, _) =>
        {
            ApplyMode();
            ApplyExistingData();
            RefreshCandidateRows();
            UpdateOkState();
            _initialApplied = true;
        };
    }

    private void ApplyMode()
    {
        if (_mode == PickupMode.New)
        {
            TitleTextBlock.Text = "ピックアップ — 新規登録";
            SubtitleTextBlock.Text = "お友達名を入れ、招待コードを貼り付けて、候補から相手を選んでください。";
            InviteCodeBorder.Visibility = Visibility.Visible;
            DeleteButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            TitleTextBlock.Text = "ピックアップ — 情報更新";
            SubtitleTextBlock.Text = "お友達の情報を見直します。ホストが変わった場合は候補から選び直してください。";
            InviteCodeBorder.Visibility = Visibility.Collapsed;
            DeleteButton.Visibility = Visibility.Visible;
        }
    }

    private void ApplyExistingData()
    {
        Friend? f = _existingFriend;
        if (f is null)
        {
            NameTextBox.Text = string.Empty;
            MemoTextBox.Text = string.Empty;
            ShareFolderTextBlock.Text = "(招待コードから取得します)";
            AccessTextBlock.Text = "(招待コードから取得します)";
            PresenceTextBlock.Text = _initialCandidate is not null ? "認識中" : "未確認";
        }
        else
        {
            NameTextBox.Text = f.DisplayName;
            MemoTextBox.Text = f.Memo;
            ShareFolderTextBlock.Text = string.IsNullOrEmpty(f.ShareName) ? "(なし)" : f.ShareName;
            AccessTextBlock.Text = string.Equals(f.AccessLevel, "Read", StringComparison.OrdinalIgnoreCase) ? "見るだけ" : "自由に編集";
            PresenceTextBlock.Text = ResolvePresence(f);
        }
        _initialName = NameTextBox.Text;
        _initialMemo = MemoTextBox.Text;
    }

    private static string ResolvePresence(Friend f)
    {
        if (string.IsNullOrWhiteSpace(f.LastCheckedAt)) return "未確認";
        return f.IsCurrentlyFound ? "来店可能" : "不在";
    }

    private void RefreshCandidateRows()
    {
        IReadOnlyList<Friend> allFriends = FriendsRepository.LoadAll();
        HashSet<string> registeredHosts = allFriends
            .Where(fr => _existingFriend is null || !string.Equals(fr.Id, _existingFriend.Id, StringComparison.Ordinal))
            .Select(fr => NormalizeHost(fr.HostMachineName))
            .Where(h => !string.IsNullOrEmpty(h))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string myHost = NormalizeHost(Environment.MachineName);
        _candidateRows.Clear();
        foreach (LanCandidate c in _candidates)
        {
            string host = NormalizeHost(c.HostName);
            if (string.IsNullOrEmpty(host)) host = c.Address.ToString();
            if (string.Equals(host, myHost, StringComparison.OrdinalIgnoreCase)) continue;
            if (registeredHosts.Contains(host)) continue;
            _candidateRows.Add(new CandidateRow(c));
        }

        // 初期選択は1度だけ反映(ロード直後の自動選択)。
        if (_initialCandidate is not null)
        {
            CandidateRow? match = _candidateRows.FirstOrDefault(r => r.Source.Address.Equals(_initialCandidate.Address));
            if (match is not null)
            {
                CandidateListView.SelectedItem = match;
            }
            _initialCandidate = null;
        }
    }

    private void OnFormChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialApplied) return;
        UpdateOkState();
    }

    private void CandidateListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialApplied) return;
        UpdateOkState();
    }

    private void UpdateOkState()
    {
        OkButton.IsEnabled = ValidateForOk(out string? error, out _);
        StatusTextBlock.Text = error ?? string.Empty;
    }

    private bool ValidateForOk(out string? error, out CandidateRow? candidate)
    {
        error = null;
        candidate = CandidateListView.SelectedItem as CandidateRow;

        string name = NameTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "お友達名を入れてください。";
            return false;
        }

        if (_mode == PickupMode.New)
        {
            string raw = InviteCodeTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "招待コードを貼り付けてください。";
                return false;
            }
            if (!InviteToken.TryDecode(raw, out InviteTokenPayload? payload, out string? decodeErr) || payload is null)
            {
                error = decodeErr ?? "招待コードを読み取れませんでした。";
                return false;
            }
            _parsedInvite = payload;
            if (candidate is null)
            {
                error = "候補から相手を選んでください。";
                return false;
            }
            string inviteHost = NormalizeHost(payload.HostMachineName);
            string candHost = NormalizeHost(candidate.Source.HostName);
            if (string.IsNullOrEmpty(candHost)) candHost = candidate.Source.Address.ToString();
            if (!string.Equals(inviteHost, candHost, StringComparison.OrdinalIgnoreCase))
            {
                error = $"招待コード ({inviteHost}) と候補 ({candHost}) のホスト名が一致しません。";
                return false;
            }
            return true;
        }

        // Update mode.
        bool nameChanged = !string.Equals(name, _initialName, StringComparison.Ordinal);
        bool memoChanged = !string.Equals(MemoTextBox.Text ?? string.Empty, _initialMemo, StringComparison.Ordinal);
        bool candidateChosen = candidate is not null;
        if (!nameChanged && !memoChanged && !candidateChosen)
        {
            error = "変更がありません。";
            return false;
        }
        return true;
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        SwkLogger.Debug("FriendsWindow.ReloadButton_Click");
        StatusTextBlock.Text = "周りを見ています…";
        try
        {
            _candidates = await LanScanner.ScanAsync();
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"FriendsWindow LAN scan failed: {ex.Message}");
            _candidates = Array.Empty<LanCandidate>();
        }

        if (_existingFriend is not null)
        {
            Friend? fresh = FriendsRepository.LoadAll()
                .FirstOrDefault(f => string.Equals(f.Id, _existingFriend.Id, StringComparison.Ordinal));
            if (fresh is not null)
            {
                // 不通→繋がった の遷移を反映するため、LAN candidates と照らして
                // LastFoundAt / LastCheckedAt を最新化してから表示。
                string nowIso = DateTime.UtcNow.ToString("o");
                fresh.LastCheckedAt = nowIso;
                LanCandidate? match = _candidates.FirstOrDefault(c => string.Equals(NormalizeHost(c.HostName), NormalizeHost(fresh.HostMachineName), StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    fresh.LastFoundAt = nowIso;
                    fresh.LastKnownAddress = match.Address.ToString();
                }
                _existingFriend = fresh;
                ApplyExistingData();
            }
        }

        RefreshCandidateRows();
        UpdateOkState();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateForOk(out string? error, out CandidateRow? candidate))
        {
            StatusTextBlock.Text = error ?? "確認できませんでした。";
            return;
        }

        string name = NameTextBox.Text.Trim();
        string memo = MemoTextBox.Text ?? string.Empty;
        string nowIso = DateTime.UtcNow.ToString("o");

        if (_mode == PickupMode.New)
        {
            if (_parsedInvite is null || candidate is null)
            {
                StatusTextBlock.Text = "招待コードを確認できませんでした。";
                return;
            }
            Friend friend = new()
            {
                DisplayName = name,
                Memo = memo,
                HostMachineName = _parsedInvite.HostMachineName,
                ShareName = _parsedInvite.ShareName,
                UserName = _parsedInvite.UserName,
                PasswordProtected = FriendsRepository.ProtectPassword(_parsedInvite.Password),
                AccessLevel = _parsedInvite.AccessLevel,
                ProfileLabel = _parsedInvite.ProfileLabel,
                AddedAt = nowIso,
                LastKnownAddress = candidate.Source.Address.ToString(),
                LastFoundAt = nowIso,
                LastCheckedAt = nowIso,
            };

            List<Friend> all = FriendsRepository.LoadAll().ToList();
            // 同一 host+share の既存があれば置き換え(重複登録の防止)。
            all.RemoveAll(f =>
                string.Equals(f.HostMachineName, friend.HostMachineName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(f.ShareName, friend.ShareName, StringComparison.OrdinalIgnoreCase));
            all.Add(friend);
            if (!FriendsRepository.SaveAll(all))
            {
                StatusTextBlock.Text = "登録できませんでした。";
                return;
            }
            SwkLogger.Debug($"FriendsWindow.OkButton_Click(New): registered name={name} host={friend.HostMachineName}");
            DialogResult = true;
            Close();
            return;
        }

        // Update mode.
        if (_existingFriend is null) return;
        Friend target = _existingFriend;
        target.DisplayName = name;
        target.Memo = memo;
        if (candidate is not null)
        {
            string newHost = string.IsNullOrWhiteSpace(candidate.Source.HostName)
                ? candidate.Source.Address.ToString()
                : candidate.Source.HostName!;
            int dot = newHost.IndexOf('.');
            if (dot > 0) newHost = newHost[..dot];
            target.HostMachineName = newHost;
            target.LastKnownAddress = candidate.Source.Address.ToString();
            target.LastFoundAt = nowIso;
            target.LastCheckedAt = nowIso;
        }

        List<Friend> allUpd = FriendsRepository.LoadAll().ToList();
        int idx = allUpd.FindIndex(f => string.Equals(f.Id, target.Id, StringComparison.Ordinal));
        if (idx >= 0) allUpd[idx] = target; else allUpd.Add(target);
        if (!FriendsRepository.SaveAll(allUpd))
        {
            StatusTextBlock.Text = "保存できませんでした。";
            return;
        }
        SwkLogger.Debug($"FriendsWindow.OkButton_Click(Update): id={target.Id} swap={candidate is not null}");
        DialogResult = true;
        Close();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_existingFriend is null) return;
        SwkLogger.Debug($"FriendsWindow.DeleteButton_Click: target={_existingFriend.DisplayName}");
        MessageBoxResult result = System.Windows.MessageBox.Show(
            this,
            $"「{_existingFriend.DisplayName}」を削除します。よろしいですか?",
            "ShareWorkin",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        List<Friend> all = FriendsRepository.LoadAll().ToList();
        all.RemoveAll(f => string.Equals(f.Id, _existingFriend.Id, StringComparison.Ordinal));
        FriendsRepository.SaveAll(all);
        SwkLogger.Debug($"FriendsWindow.DeleteButton_Click: deleted, remaining={all.Count}");
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        SwkLogger.Debug("FriendsWindow.CancelButton_Click");
        DialogResult = false;
        Close();
    }

    private static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        string trimmed = host.Trim();
        int dot = trimmed.IndexOf('.');
        return dot > 0 ? trimmed[..dot] : trimmed;
    }

    private sealed class CandidateRow
    {
        public LanCandidate Source { get; }
        public string HostNameLabel => string.IsNullOrWhiteSpace(Source.HostName) ? Source.Address.ToString() : Source.HostName!;
        public string IpLabel => Source.Address.ToString();
        public CandidateRow(LanCandidate source) => Source = source;
    }
}
