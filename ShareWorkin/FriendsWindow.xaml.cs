using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShareWorkin.SMB;

namespace ShareWorkin;

public partial class FriendsWindow : Window
{
    private readonly Window _ownerWindow;
    private IReadOnlyList<LanCandidate> _candidates;
    private IReadOnlyList<SwkNotificationListener.ShopInfo> _shopInfos;

    // 現在アクティブな操作ターゲット（下部リスト選択で動的に切り替わる）
    private Friend? _activeFriend;
    private SwkNotificationListener.ShopInfo? _activeShopInfo;
    private LanCandidate? _activeNewCandidate;  // Update時の新接続先 or ShopInfo なし候補

    private readonly ObservableCollection<CandidateRow> _candidateRows = new();
    private string _initialName = string.Empty;
    private string _initialMemo = string.Empty;
    private bool _initialApplied;
    private bool _suppressFormEvents;
    private CancellationTokenSource? _cancellationTokenSource;

    // 従来フロー: 既存友達（Update）または候補指定（New）
    public FriendsWindow(Window owner, Friend? existing, LanCandidate? initialCandidate,
        IReadOnlyList<LanCandidate> candidates,
        IReadOnlyList<SwkNotificationListener.ShopInfo>? shopInfos = null)
    {
        InitializeComponent();
        _ownerWindow = owner;
        _candidates = candidates;
        _shopInfos = shopInfos ?? Array.Empty<SwkNotificationListener.ShopInfo>();
        _activeFriend = existing;
        _activeNewCandidate = null;
        _activeShopInfo = null;

        if (existing == null && initialCandidate != null)
        {
            string host = NormalizeHost(initialCandidate.HostName);
            _activeShopInfo = _shopInfos.FirstOrDefault(s =>
                string.Equals(NormalizeHost(s.MachineName), host, StringComparison.OrdinalIgnoreCase));
            if (_activeShopInfo == null)
                _activeNewCandidate = initialCandidate;
        }

        CandidateListView.ItemsSource = _candidateRows;
        SwkLogger.Debug(
            $"FriendsWindow ctor: existing={existing?.DisplayName ?? "null"} " +
            $"candidate={initialCandidate?.HostName ?? "null"}");
        Loaded += (_, _) =>
        {
            ApplyActiveTarget();
            RefreshCandidateRows();
            _initialApplied = true;
        };
    }

    // Phase 4 フロー: ShopInfo から自動登録
    public FriendsWindow(Window owner, SwkNotificationListener.ShopInfo shopInfo,
        IReadOnlyList<LanCandidate>? candidates = null,
        IReadOnlyList<SwkNotificationListener.ShopInfo>? shopInfos = null)
    {
        InitializeComponent();
        _ownerWindow = owner;
        _candidates = candidates ?? Array.Empty<LanCandidate>();
        _shopInfos = shopInfos ?? Array.Empty<SwkNotificationListener.ShopInfo>();
        _activeFriend = null;
        _activeShopInfo = shopInfo;
        _activeNewCandidate = null;

        CandidateListView.ItemsSource = _candidateRows;
        SwkLogger.Debug($"FriendsWindow ctor (ShopInfo): {shopInfo.MachineName}/{shopInfo.ShareName}");
        Loaded += (_, _) =>
        {
            ApplyActiveTarget();
            RefreshCandidateRows();
            _initialApplied = true;
        };
    }

    // ── フォーム適用 ──────────────────────────────────────────────────

    private void ApplyActiveTarget()
    {
        _suppressFormEvents = true;
        try { ApplyActiveTargetInternal(); }
        finally { _suppressFormEvents = false; }
    }

    private void ApplyActiveTargetInternal()
    {
        DeleteButton.Visibility = Visibility.Collapsed;
        OpenFolderButton.Visibility = Visibility.Collapsed;

        if (_activeFriend != null)
        {
            // Update モード: 既存友達を編集
            string friendName = _activeFriend.DisplayName;
            if (_activeNewCandidate != null)
            {
                string targetHost = NormalizeHost(_activeNewCandidate.HostName);
                if (string.IsNullOrEmpty(targetHost)) targetHost = _activeNewCandidate.Address.ToString();
                TitleTextBlock.Text = "接続先を変更";
                SubtitleTextBlock.Text =
                    $"「{friendName}」の接続先を「{targetHost}」に変更します。お友達名を確認してください。";
            }
            else if (_activeFriend.IsCurrentlyFound)
            {
                TitleTextBlock.Text = "接続を更新";
                SubtitleTextBlock.Text = $"「{friendName}」の編集ができます。";
            }
            else
            {
                TitleTextBlock.Text = "接続を更新";
                SubtitleTextBlock.Text = $"「{friendName}」は現在未接続です。接続未確定リストから別の接続先を指定するか、削除してください。";
            }

            NameTextBox.Text = _activeFriend.DisplayName;
            MemoTextBox.Text = _activeFriend.Memo;
            ShareFolderTextBlock.Text =
                string.IsNullOrEmpty(_activeFriend.ShareName) ? "(なし)" : _activeFriend.ShareName;
            AccessTextBlock.Text =
                string.Equals(_activeFriend.AccessLevel, "Read", StringComparison.OrdinalIgnoreCase)
                    ? "見るだけ" : "自由に編集";
            PresenceTextBlock.Text = ResolvePresence(_activeFriend);
            DeleteButton.Visibility = Visibility.Visible;
            OpenFolderButton.Visibility =
                _activeFriend.IsCurrentlyFound ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (_activeShopInfo != null)
        {
            // New モード: ShopInfo 自動登録
            TitleTextBlock.Text = "新規接続";
            string shopIp = string.IsNullOrEmpty(_activeShopInfo.IpAddress) ? string.Empty : $"({_activeShopInfo.IpAddress}) ";
            SubtitleTextBlock.Text =
                $"「{_activeShopInfo.MachineName}」{shopIp}を新しくお友達として登録します。お友達名を入力してください。";
            NameTextBox.Text = string.Empty;
            MemoTextBox.Text = string.Empty;
            ShareFolderTextBlock.Text = "(招待コードから取得します)";
            AccessTextBlock.Text = "(招待コードから取得します)";
            PresenceTextBlock.Text = "認識中";
        }
        else if (_activeNewCandidate != null)
        {
            // 候補あり・ShopInfo なし: ShareWorkin 未起動
            string host = NormalizeHost(_activeNewCandidate.HostName);
            if (string.IsNullOrEmpty(host)) host = _activeNewCandidate.Address.ToString();
            TitleTextBlock.Text = "新規接続";
            SubtitleTextBlock.Text =
                $"「{host}」では ShareWorkin が起動していないため登録できません。相手側でアプリを起動してから再読み込みしてください。";
            NameTextBox.Text = string.Empty;
            MemoTextBox.Text = string.Empty;
            ShareFolderTextBlock.Text = "-";
            AccessTextBlock.Text = "-";
            PresenceTextBlock.Text = "未起動";
        }
        else
        {
            // 未選択
            TitleTextBlock.Text = "新規接続";
            SubtitleTextBlock.Text = "接続未確定リストから相手を選んでください。";
            NameTextBox.Text = string.Empty;
            MemoTextBox.Text = string.Empty;
            ShareFolderTextBlock.Text = "-";
            AccessTextBlock.Text = "-";
            PresenceTextBlock.Text = "-";
        }

        _initialName = NameTextBox.Text;
        _initialMemo = MemoTextBox.Text;
        UpdateOkState();
    }

    // ── 下部リスト（接続未確定）────────────────────────────────────────

    private void RefreshCandidateRows()
    {
        IReadOnlyList<Friend> allFriends = FriendsRepository.LoadAll();
        string myHost = NormalizeHost(Environment.MachineName);

        // 確立済み = friends.json に登録済み かつ IsCurrentlyFound
        HashSet<string> establishedHosts = allFriends
            .Where(f => f.IsCurrentlyFound)
            .Select(f => NormalizeHost(f.HostMachineName))
            .Where(h => !string.IsNullOrEmpty(h))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> addedHosts = new(StringComparer.OrdinalIgnoreCase);
        _candidateRows.Clear();

        // LAN スキャン結果から未確立のものを追加
        foreach (LanCandidate c in _candidates)
        {
            string host = NormalizeHost(c.HostName);
            if (string.IsNullOrEmpty(host)) host = c.Address.ToString();
            if (string.Equals(host, myHost, StringComparison.OrdinalIgnoreCase)) continue;
            if (establishedHosts.Contains(host)) continue;

            SwkNotificationListener.ShopInfo? shopInfo = _shopInfos.FirstOrDefault(s =>
                string.Equals(NormalizeHost(s.MachineName), host, StringComparison.OrdinalIgnoreCase));
            _candidateRows.Add(new CandidateRow(c, shopInfo));
            addedHosts.Add(host);
        }

        // LAN スキャンに現れない登録済み・未確立友達も追加
        foreach (Friend f in allFriends)
        {
            if (f.IsCurrentlyFound) continue;
            string host = NormalizeHost(f.HostMachineName);
            if (string.IsNullOrEmpty(host)) continue;
            if (string.Equals(host, myHost, StringComparison.OrdinalIgnoreCase)) continue;
            if (addedHosts.Contains(host)) continue;
            _candidateRows.Add(new CandidateRow(f));
        }
    }

    // ── フォーム変更・バリデーション ─────────────────────────────────

    private void OnFormChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialApplied || _suppressFormEvents) return;
        UpdateOkState();
    }

    private void CandidateListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialApplied) return;
        if (CandidateListView.SelectedItem is not CandidateRow selected) return;

        if (selected.ExistingFriend != null)
        {
            // 登録済み・オフライン友達行
            _activeFriend = selected.ExistingFriend;
            _activeShopInfo = null;
            _activeNewCandidate = selected.Source;
        }
        else if (selected.Source != null)
        {
            string host = NormalizeHost(selected.Source.HostName);
            if (string.IsNullOrEmpty(host)) host = selected.Source.Address.ToString();

            IReadOnlyList<Friend> allFriends = FriendsRepository.LoadAll();
            Friend? matchedFriend = allFriends.FirstOrDefault(f =>
                string.Equals(NormalizeHost(f.HostMachineName), host, StringComparison.OrdinalIgnoreCase) &&
                !f.IsCurrentlyFound);

            if (matchedFriend != null)
            {
                // LAN に見えるが未確立の登録済み友達 → Update モード
                _activeFriend = matchedFriend;
                _activeShopInfo = null;
                _activeNewCandidate = selected.Source;
            }
            else
            {
                // 未登録 → New モード
                _activeFriend = null;
                _activeShopInfo = selected.ShopInfo;
                _activeNewCandidate = selected.ShopInfo == null ? selected.Source : null;
            }
        }

        ApplyActiveTarget();
    }

    private void UpdateOkState()
    {
        OkButton.IsEnabled = ValidateForOk(out string? error);
        StatusTextBlock.Text = error ?? string.Empty;
    }

    private bool ValidateForOk(out string? error)
    {
        error = null;
        string name = NameTextBox.Text?.Trim() ?? string.Empty;

        if (_activeFriend != null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "お友達名を入れてください。";
                return false;
            }
            bool nameChanged = !string.Equals(name, _initialName, StringComparison.Ordinal);
            bool memoChanged = !string.Equals(MemoTextBox.Text ?? string.Empty, _initialMemo, StringComparison.Ordinal);
            bool candidateChanged = _activeNewCandidate != null;
            if (!nameChanged && !memoChanged && !candidateChanged)
            {
                error = "変更がありません。";
                return false;
            }
            return true;
        }

        if (_activeShopInfo != null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "お友達名を入れてください。";
                return false;
            }
            return true;
        }

        error = _activeNewCandidate != null
            ? "ShareWorkin が起動していないため登録できません。"
            : "接続未確定リストから相手を選んでください。";
        return false;
    }

    // ── ボタンアクション ─────────────────────────────────────────────

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        SwkLogger.Debug("FriendsWindow.ReloadButton_Click");
        StatusTextBlock.Text = "周りを見ています…";
        try
        {
            _candidates = await LanScanner.ScanAsync();
            _shopInfos = await SwkNotificationListener.ProbeHostsAsync(_candidates, CancellationToken.None);
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"FriendsWindow reload failed: {ex.Message}");
            _candidates = Array.Empty<LanCandidate>();
            _shopInfos = Array.Empty<SwkNotificationListener.ShopInfo>();
        }

        if (_activeFriend != null)
        {
            Friend? fresh = FriendsRepository.LoadAll()
                .FirstOrDefault(f => string.Equals(f.Id, _activeFriend.Id, StringComparison.Ordinal));
            if (fresh != null) _activeFriend = fresh;
        }

        RefreshCandidateRows();
        ApplyActiveTarget();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateForOk(out string? error))
        {
            StatusTextBlock.Text = error ?? "確認できませんでした。";
            return;
        }

        string name = NameTextBox.Text.Trim();
        string memo = MemoTextBox.Text ?? string.Empty;

        if (_activeFriend != null)
        {
            UpdateExistingFriend(name, memo);
            return;
        }

        if (_activeShopInfo != null)
        {
            OkButton.IsEnabled = false;
            StatusTextBlock.Text = "登録中…";
            _ = RegisterFromShopInfoAsync(name, memo);
            return;
        }

        StatusTextBlock.Text = "登録できる相手が選ばれていません。";
    }

    private async Task RegisterFromShopInfoAsync(string name, string memo)
    {
        try
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(10));

            var listener = new SwkNotificationListener();
            var result = await listener.RequestInviteCodeAsync(_activeShopInfo!, _cancellationTokenSource.Token);

            if (result.errorMessage != null)
            {
                StatusTextBlock.Text = $"登録に失敗しました: {result.errorMessage}";
                OkButton.IsEnabled = true;
                return;
            }

            if (string.IsNullOrEmpty(result.inviteCode) || string.IsNullOrEmpty(result.password))
            {
                StatusTextBlock.Text = "招待コードを取得できませんでした。";
                OkButton.IsEnabled = true;
                return;
            }

            string nowIso = DateTime.UtcNow.ToString("o");
            Friend friend = new()
            {
                DisplayName = name,
                Memo = memo,
                HostMachineName = _activeShopInfo!.MachineName,
                ShareName = _activeShopInfo.ShareName,
                UserName = "swkguest",
                PasswordProtected = FriendsRepository.ProtectPassword(result.password),
                AccessLevel = "Full",
                ProfileLabel = string.Empty,
                AddedAt = nowIso,
                LastKnownAddress = string.Empty,
                LastFoundAt = nowIso,
                LastCheckedAt = nowIso,
            };

            List<Friend> all = FriendsRepository.LoadAll().ToList();
            all.RemoveAll(f =>
                string.Equals(f.HostMachineName, friend.HostMachineName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(f.ShareName, friend.ShareName, StringComparison.OrdinalIgnoreCase));
            all.Add(friend);

            if (!FriendsRepository.SaveAll(all))
            {
                StatusTextBlock.Text = "登録できませんでした。";
                OkButton.IsEnabled = true;
                return;
            }

            SwkLogger.Debug(
                $"FriendsWindow.RegisterFromShopInfoAsync: registered name={name} host={friend.HostMachineName}");
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "接続タイムアウト";
            OkButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"FriendsWindow.RegisterFromShopInfoAsync failed: {ex.Message}");
            StatusTextBlock.Text = $"エラー: {ex.Message}";
            OkButton.IsEnabled = true;
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
        }
    }

    private void UpdateExistingFriend(string name, string memo)
    {
        if (_activeFriend is null) return;

        string nowIso = DateTime.UtcNow.ToString("o");
        Friend target = _activeFriend;
        target.DisplayName = name;
        target.Memo = memo;

        if (_activeNewCandidate != null)
        {
            string newHost = NormalizeHost(_activeNewCandidate.HostName);
            if (string.IsNullOrEmpty(newHost)) newHost = _activeNewCandidate.Address.ToString();
            target.HostMachineName = newHost;
            target.LastKnownAddress = _activeNewCandidate.Address.ToString();
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
        SwkLogger.Debug(
            $"FriendsWindow.UpdateExistingFriend: id={target.Id} newHost={_activeNewCandidate?.Address}");
        DialogResult = true;
        Close();
    }

    private static string ResolvePresence(Friend f)
    {
        if (string.IsNullOrWhiteSpace(f.LastCheckedAt)) return "未確認";
        return f.IsCurrentlyFound ? "来店可能" : "不在";
    }

    public static bool OpenFriendFolder(Friend friend)
    {
        if (friend is null || string.IsNullOrEmpty(friend.ConnectUncPath)) return false;
        try
        {
            string password = FriendsRepository.UnprotectPassword(friend.PasswordProtected);
            if (string.IsNullOrEmpty(password))
            {
                SwkLogger.Warn("Failed to decrypt password for opening folder");
                return false;
            }
            bool ok = SmbConnectionHelper.ConnectAndOpen(friend.ConnectUncPath, friend.UserName, password);
            SwkLogger.Info($"OpenFriendFolder: {friend.DisplayName} - {(ok ? "success" : "failed")}");
            return ok;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"OpenFriendFolder failed: {ex.Message}");
            return false;
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeFriend is null) return;
        SwkLogger.Debug($"FriendsWindow.OpenFolderButton_Click: target={_activeFriend.DisplayName}");
        OpenFolderButton.IsEnabled = false;
        StatusTextBlock.Text = "フォルダーを開いています…";
        StatusTextBlock.Text = OpenFriendFolder(_activeFriend)
            ? "フォルダーを開きました。"
            : "フォルダーを開けませんでした。";
        OpenFolderButton.IsEnabled = true;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeFriend is null) return;
        SwkLogger.Debug($"FriendsWindow.DeleteButton_Click: target={_activeFriend.DisplayName}");
        MessageBoxResult result = System.Windows.MessageBox.Show(
            this,
            $"「{_activeFriend.DisplayName}」を削除します。よろしいですか?",
            "ShareWorkin",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        List<Friend> all = FriendsRepository.LoadAll().ToList();
        all.RemoveAll(f => string.Equals(f.Id, _activeFriend.Id, StringComparison.Ordinal));
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

    // ── CandidateRow ──────────────────────────────────────────────────

    private sealed class CandidateRow
    {
        public LanCandidate? Source { get; }
        public Friend? ExistingFriend { get; }
        public SwkNotificationListener.ShopInfo? ShopInfo { get; }

        public string HostNameLabel
        {
            get
            {
                if (Source != null)
                    return string.IsNullOrWhiteSpace(Source.HostName)
                        ? Source.Address.ToString()
                        : Source.HostName!;
                if (ExistingFriend != null)
                    return $"{ExistingFriend.DisplayName} ({ExistingFriend.HostMachineName})";
                return "?";
            }
        }

        public string IpLabel
        {
            get
            {
                if (Source != null) return Source.Address.ToString();
                return ExistingFriend?.LastKnownAddress ?? string.Empty;
            }
        }

        public string StatusLabel
        {
            get
            {
                if (ShopInfo != null) return "ShareWorkin 起動中";
                if (ExistingFriend != null) return "登録済・不通";
                return string.Empty;
            }
        }

        public CandidateRow(LanCandidate source, SwkNotificationListener.ShopInfo? shopInfo = null)
        {
            Source = source;
            ShopInfo = shopInfo;
        }

        public CandidateRow(Friend friend)
        {
            ExistingFriend = friend;
        }
    }
}
