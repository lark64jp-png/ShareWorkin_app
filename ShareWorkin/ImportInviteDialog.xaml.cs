using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ShareWorkin.SMB;

namespace ShareWorkin;

public partial class ImportInviteDialog : Window
{
    public Friend? Imported { get; private set; }

    public ImportInviteDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => TokenTextBox.Focus();
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        StatusTextBlock.Text = string.Empty;
        string raw = TokenTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            StatusTextBlock.Text = "招待コードを貼り付けてください。";
            return;
        }

        if (!InviteToken.TryDecode(raw, out InviteTokenPayload? payload, out string? error) || payload is null)
        {
            StatusTextBlock.Text = error ?? "招待コードを読み取れませんでした。";
            return;
        }

        if (string.IsNullOrWhiteSpace(payload.InviteId))
        {
            StatusTextBlock.Text = "招待コードに必要な情報が含まれていません。新しいコードを発行してもらってください。";
            return;
        }

        ImportButton.IsEnabled = false;
        StatusTextBlock.Foreground = System.Windows.Media.Brushes.Gray;
        StatusTextBlock.Text = "お店を探しています…";

        try
        {
            Friend? friend = await PerformImportAsync(payload);
            if (friend is null) return;

            Imported = friend;
            DialogResult = true;
            Close();
        }
        finally
        {
            ImportButton.IsEnabled = true;
            StatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
        }
    }

    private async Task<Friend?> PerformImportAsync(InviteTokenPayload payload)
    {
        // 1) LAN 上で対象ホストの ShopInfo を取得する。
        SwkNotificationListener.ShopInfo? shop = await ResolveShopAsync(payload.HostMachineName, payload.ShareName);
        if (shop is null)
        {
            StatusTextBlock.Text = "お店が見つかりませんでした。お友達がお店を開いている状態でもう一度お試しください。";
            return null;
        }

        // 2) TLS 交換 → 接続情報 + 証明書サムプリント取得。
        StatusTextBlock.Text = "接続情報を取得しています…";
        var listener = new SwkNotificationListener();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        SwkNotificationListener.InviteCodeResult result = await listener.RequestInviteCodeAsync(
            shop,
            inviteId: payload.InviteId,
            expectedThumbprint: null,
            cts.Token);

        if (!result.Success)
        {
            StatusTextBlock.Text = $"取り込みに失敗しました: {result.ErrorMessage ?? "不明なエラー"}";
            return null;
        }

        // 3) Friend 登録(同一 host+share の重複は置き換え)。
        string display = string.IsNullOrWhiteSpace(DisplayNameTextBox.Text)
            ? payload.HostMachineName
            : DisplayNameTextBox.Text.Trim();

        string nowIso = DateTime.UtcNow.ToString("o");
        Friend friend = new()
        {
            DisplayName = display,
            HostMachineName = payload.HostMachineName,
            ShareName = payload.ShareName,
            UserName = string.IsNullOrWhiteSpace(payload.UserName) ? "swkguest" : payload.UserName,
            PasswordProtected = FriendsRepository.ProtectPassword(result.Password!),
            OwnerCertThumbprint = result.CertThumbprint ?? string.Empty,
            AccessLevel = string.IsNullOrWhiteSpace(payload.AccessLevel) ? "Full" : payload.AccessLevel,
            ProfileLabel = payload.ProfileLabel ?? string.Empty,
            AddedAt = nowIso,
            LastKnownAddress = shop.IpAddress ?? string.Empty,
            LastFoundAt = nowIso,
            LastCheckedAt = nowIso,
        };

        var existing = FriendsRepository.LoadAll().ToList();
        existing.RemoveAll(f =>
            string.Equals(f.HostMachineName, payload.HostMachineName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(f.ShareName, payload.ShareName, StringComparison.OrdinalIgnoreCase));
        existing.Add(friend);

        if (!FriendsRepository.SaveAll(existing))
        {
            StatusTextBlock.Text = "お友達リストに保存できませんでした。";
            return null;
        }

        return friend;
    }

    private static async Task<SwkNotificationListener.ShopInfo?> ResolveShopAsync(string hostMachineName, string shareName)
    {
        // まずキャッシュから探す(直近の LAN スキャン結果)。
        SwkNotificationListener.ShopInfo? hit = SwkNetworkCache.ShopInfos.FirstOrDefault(s =>
            string.Equals(s.MachineName, hostMachineName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.ShareName, shareName, StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit;

        // キャッシュに無ければ Quick スキャンを回す。
        try
        {
            using var scanCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await SwkNetworkCache.RefreshAsync(ScanMode.Quick, scanCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SwkLogger.Warn($"ImportInviteDialog: scan failed: {ex.Message}");
        }

        return SwkNetworkCache.ShopInfos.FirstOrDefault(s =>
            string.Equals(s.MachineName, hostMachineName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.ShareName, shareName, StringComparison.OrdinalIgnoreCase));
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
