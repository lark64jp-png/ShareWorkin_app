using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ShareWorkin.SMB;
using Forms = System.Windows.Forms;

namespace ShareWorkinTray;

public enum NotificationDisplayResult
{
    Failed,
    Fallback,
    Toast,
}

public sealed class TrayApp : IDisposable
{
    private static readonly string AppHomeDirectory = AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static readonly string SettingsPath = Path.Combine(AppHomeDirectory, "settings.json");
    private static readonly TimeSpan BalloonTipShownTimeout = TimeSpan.FromSeconds(2);

    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly object _balloonTipSync = new();
    internal readonly TrayPipeServer PipeServer;

    private bool _isShopOpen;
    private string? _shopFolder;
    private string? _activeShareName;
    private string? _pcOwnerSid;
    private string? _pcOwnerAccount;
    private bool _wasOpenAtLastShutdown;
    private ShareAccessRight _shareAccessRight = ShareAccessRight.Full;
    private string? _lastBalloonFolder;
    private TaskCompletionSource<bool>? _pendingBalloonTipShown;

    public bool IsShopOpen => _isShopOpen;
    public string? ShopFolder => _shopFolder;

    public TrayApp()
    {
        PipeServer = new TrayPipeServer(this);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "ShareWorkin",
            Visible = false
        };
        _notifyIcon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenUiProcess(); };
        _notifyIcon.BalloonTipShown += (_, _) =>
        {
            SwkLogger.Info("NotifyIcon.BalloonTipShown");
            TaskCompletionSource<bool>? pending;
            lock (_balloonTipSync)
            {
                pending = _pendingBalloonTipShown;
            }

            pending?.TrySetResult(true);
        };
        _notifyIcon.BalloonTipClosed += (_, _) => SwkLogger.Info("NotifyIcon.BalloonTipClosed");
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            SwkLogger.Info("NotifyIcon.BalloonTipClicked");
            OpenLastBalloonFolder();
        };

        _trayMenu = new Forms.ContextMenuStrip();
        var exitItem = new Forms.ToolStripMenuItem("アプリを終了");
        exitItem.Click += (_, _) => RequestExitWithAuth();
        _trayMenu.Items.Add(exitItem);
        _notifyIcon.ContextMenuStrip = _trayMenu;
    }

    public void Start()
    {
        SwkLogger.Info($"TrayApp.Start: elevated={IsRunningAsAdmin()} processPath={Environment.ProcessPath ?? "null"}");
        WindowsToastNotificationService.Initialize();
        LoadSettings();
        SmbController.OnShopClosingReceived = HandleFriendShopClosingReceived;
        SmbController.OnInteractionEventReceived = HandleIncomingInteractionReceived;
        _notifyIcon.Visible = true;
        PipeServer.Start();
    }

    public void Dispose()
    {
        PipeServer.Stop();
        if (_isShopOpen)
            SmbController.StopShopBroadcaster();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayMenu.Dispose();
    }

    public void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var fs = File.OpenRead(SettingsPath);
                using var doc = JsonDocument.Parse(fs);
                var root = doc.RootElement;
                if (root.TryGetProperty("ShopFolder", out var sf) && sf.ValueKind != JsonValueKind.Null)
                    _shopFolder = sf.GetString();
                else if (root.TryGetProperty("WatchFolder", out var wf) && wf.ValueKind != JsonValueKind.Null)
                    _shopFolder = wf.GetString();
                if (root.TryGetProperty("isOpenAtLastShutdown", out var open) ||
                    root.TryGetProperty("IsOpenAtLastShutdown", out open))
                    _wasOpenAtLastShutdown = open.GetBoolean();
                if (root.TryGetProperty("accessLevel", out var al) ||
                    root.TryGetProperty("AccessLevel", out al))
                    _shareAccessRight = string.Equals(al.GetString(), "Read", StringComparison.OrdinalIgnoreCase)
                        ? ShareAccessRight.Read : ShareAccessRight.Full;
                if (root.TryGetProperty("pcOwnerSid", out var ownerSid) && ownerSid.ValueKind != JsonValueKind.Null)
                    _pcOwnerSid = ownerSid.GetString();
                if (root.TryGetProperty("pcOwnerAccount", out var ownerAccount) && ownerAccount.ValueKind != JsonValueKind.Null)
                    _pcOwnerAccount = ownerAccount.GetString();
            }

            bool needsOwnerPersistence = string.IsNullOrWhiteSpace(_pcOwnerSid) ||
                                         string.IsNullOrWhiteSpace(_pcOwnerAccount);
            _pcOwnerSid ??= PcOwnerIdentity.TryGetCurrentUserSid();
            _pcOwnerAccount ??= PcOwnerIdentity.TryGetCurrentUserAccount();
            PcOwnerIdentity.Configure(_pcOwnerSid, _pcOwnerAccount);
            if (needsOwnerPersistence && !string.IsNullOrWhiteSpace(_pcOwnerSid))
            {
                PersistPcOwnerIdentity();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            SwkLogger.Warn($"TrayApp.LoadSettings error: {ex.Message}");
            _pcOwnerSid ??= PcOwnerIdentity.TryGetCurrentUserSid();
            _pcOwnerAccount ??= PcOwnerIdentity.TryGetCurrentUserAccount();
            PcOwnerIdentity.Configure(_pcOwnerSid, _pcOwnerAccount);
        }
    }

    public bool UpdateShopOpenedState(string shopFolder, string shareName, ShareAccessRight accessRight)
    {
        if (string.IsNullOrWhiteSpace(shopFolder) || string.IsNullOrWhiteSpace(shareName))
        {
            return false;
        }

        SmbController.StartShopBroadcaster(shareName);
        _isShopOpen = true;
        _shopFolder = shopFolder;
        _activeShareName = shareName;
        _shareAccessRight = accessRight;
        PatchSettingsOpenState(true, shopFolder);
        return true;
    }

    public bool UpdateShopClosedState()
    {
        if (!_isShopOpen)
        {
            PatchSettingsOpenState(false, _shopFolder);
            return true;
        }

        SmbController.StopShopBroadcaster();
        _isShopOpen = false;
        _activeShareName = null;
        PatchSettingsOpenState(false, _shopFolder);
        return true;
    }

    public void BroadcastShopClosing() => _ = SmbController.BroadcastShopClosingAsync();
    public void BroadcastPermissionChanged() => _ = SmbController.BroadcastPermissionChangedAsync();

    private void HandleFriendShopClosingReceived(string machineName, string shareName)
    {
        _ = PipeServer.PushMessageAsync(JsonSerializer.Serialize(new
        {
            type = "FRIEND_SHOP_CLOSING",
            machineName,
            shareName
        }));
    }

    private void HandleIncomingInteractionReceived(SwkNotificationProtocol.InteractionEventNotice notice)
    {
        SwkIncomingInteractionRecord entry = BuildIncomingInteractionRecord(notice);
        if (!entry.IsSenderVerified)
        {
            SwkLogger.Warn(
                $"Trace.TrayIncoming.Unverified: eventId={entry.EventId} senderMachine={entry.SenderMachineName ?? "-"} " +
                $"senderShare={entry.SenderShareName ?? "-"} senderId={entry.SenderSwkInstanceId ?? "-"} target={entry.TargetName ?? "-"}");
        }
        SwkIncomingInteractionInbox.Append(entry);

        if (PipeServer.HasConnectedClient)
        {
            _ = PipeServer.PushMessageAsync(JsonSerializer.Serialize(new
            {
                type = "INCOMING_INTERACTION",
                entry
            }));
            return;
        }

        string targetName = string.IsNullOrWhiteSpace(entry.TargetName) ? "項目" : entry.TargetName;
        string balloonText = entry.IsSenderVerified
            ? $"{ResolveVerifiedSenderLabel(entry)} から {targetName} が届きました。"
            : $"未照合の送信元から {targetName} が届きました。";
        if (!string.IsNullOrWhiteSpace(entry.Message))
        {
            balloonText += $"\r\nメッセージ: {entry.Message}";
        }
        ShowBalloonTip(GetIncomingNotificationTitle(entry.IsSenderVerified), balloonText, entry.TargetFolder ?? _shopFolder);
        SwkIncomingInteractionInbox.MarkDisplayed(entry.EventId, DateTime.UtcNow);
    }

    private SwkIncomingInteractionRecord BuildIncomingInteractionRecord(SwkNotificationProtocol.InteractionEventNotice notice)
    {
        Friend? verifiedFriend = ResolveVerifiedIncomingInteractionFriend(notice);
        string? relativePath = NormalizeRelativePath(notice.TargetRelativePath);
        string? fullPath = BuildFullPath(_shopFolder, relativePath);
        string? folder = !string.IsNullOrWhiteSpace(fullPath)
            ? (string.Equals(notice.TargetKind, "Folder", StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : Path.GetDirectoryName(fullPath))
            : _shopFolder;

        return new SwkIncomingInteractionRecord
        {
            EventId = notice.EventId,
            OccurredAt = notice.IssuedAt,
            EventType = notice.EventType,
            SenderMachineName = notice.SenderMachineName,
            SenderDisplayName = notice.SenderDisplayName,
            SenderSwkInstanceId = notice.SenderSwkInstanceId,
            SenderShareName = notice.SenderShareName,
            ReceiverShareName = notice.ReceiverShareName,
            TargetName = notice.TargetName,
            TargetRelativePath = relativePath,
            TargetFullPath = fullPath,
            TargetFolder = folder,
            TargetKind = notice.TargetKind,
            NotificationType = notice.NotificationType,
            Message = notice.Message,
            SourceRoute = "Tray.IncomingInteraction",
            ReceivedAt = DateTime.UtcNow.ToString("o"),
            IsSenderVerified = verifiedFriend is not null,
            VerifiedFriendId = verifiedFriend?.Id,
            VerifiedFriendName = ResolveFriendLabel(verifiedFriend)
        };
    }

    private static Friend? ResolveVerifiedIncomingInteractionFriend(SwkNotificationProtocol.InteractionEventNotice notice)
    {
        IReadOnlyList<Friend> friends = FriendsRepository.LoadAll();
        return FriendRecognitionService.ResolveIncomingInteractionFriend(
            friends,
            notice.SenderSwkInstanceId,
            notice.SenderMachineName,
            notice.SenderShareName);
    }

    private static string ResolveVerifiedSenderLabel(SwkIncomingInteractionRecord entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.VerifiedFriendName))
        {
            return entry.VerifiedFriendName;
        }

        return "相手";
    }

    private static string GetIncomingNotificationTitle(bool isVerified) =>
        isVerified
            ? "ShareWorkin の受信(確認済み)"
            : "ShareWorkin の受信(未照合通知)";

    private static string? ResolveFriendLabel(Friend? friend)
    {
        if (friend is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(friend.DisplayName))
        {
            return friend.DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(friend.ProfileLabel))
        {
            return friend.ProfileLabel;
        }

        if (!string.IsNullOrWhiteSpace(friend.HostMachineName))
        {
            return friend.HostMachineName;
        }

        return friend.ShareName;
    }

    private static string? NormalizeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        return relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? BuildFullPath(string? shopFolder, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(shopFolder))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(relativePath)
            ? shopFolder
            : Path.Combine(shopFolder, relativePath);
    }

    public NotificationDisplayResult ShowBalloonTip(string title, string text, string? folder)
    {
        _lastBalloonFolder = folder;
        if (WindowsToastNotificationService.TryShow(title, text))
        {
            SwkLogger.Info($"NotificationDelivery.ToastRequestNoFailureDetected: title={title}");
            return NotificationDisplayResult.Toast;
        }

        try
        {
            var shownTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_balloonTipSync)
            {
                _pendingBalloonTipShown = shownTcs;
            }

            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.ShowBalloonTip(5000);
            SwkLogger.Info($"NotificationDelivery.FallbackRequested: title={title}");

            bool shown = shownTcs.Task.Wait(BalloonTipShownTimeout);
            if (shown)
            {
                SwkLogger.Info($"NotificationDelivery.FallbackSuccess: title={title} signal=BalloonTipShown");
                return NotificationDisplayResult.Fallback;
            }

            SwkLogger.Warn($"NotificationDelivery.FallbackFailed: title={title} signal=BalloonTipShownTimeout");
            return NotificationDisplayResult.Failed;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"NotificationDelivery.FallbackFailed: title={title} signal=Exception message={ex.Message}");
            return NotificationDisplayResult.Failed;
        }
        finally
        {
            lock (_balloonTipSync)
            {
                _pendingBalloonTipShown = null;
            }
        }
    }

    public NotificationDisplayResult ShowTestNotification(string? folder)
    {
        return ShowBalloonTip(
            "ShareWorkin 通知テスト",
            "通知は正常に表示されています。",
            folder);
    }

    public void NotifyUiDisconnected()
    {
        if (_isShopOpen)
            ShowBalloonTip("共有は続いています", "共有は続いています。管理画面は閉じています。", _shopFolder);
    }

    private void OpenLastBalloonFolder()
    {
        string? path = _lastBalloonFolder;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { SwkLogger.Warn($"OpenLastBalloonFolder error: {ex.Message}"); }
    }

    public void OpenUiProcess()
    {
        if (PipeServer.HasConnectedClient)
        {
            _ = PipeServer.PushMessageAsync("{\"type\":\"SHOW\"}");
            return;
        }
        try
        {
            string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (exeDir == null) return;
            string uiExe = Path.Combine(exeDir, "ShareWorkin.exe");
            if (!File.Exists(uiExe)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{uiExe}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { SwkLogger.Warn($"OpenUiProcess error: {ex.Message}"); }
    }

    public void ExitApp(bool fromUiRequest = false)
    {
        if (_isShopOpen)
        {
            if (fromUiRequest)
            {
                // 正常フローではUIが先に閉店するためここには来ない。安全のため終了しない。
                SwkLogger.Warn("TrayApp.ExitApp: fromUiRequest=true but shop is open, exit blocked");
                return;
            }
            if (!TryCloseShopForExit())
                return;
        }

        if (!fromUiRequest)
            _ = PipeServer.PushMessageAsync("{\"type\":\"TRAY_EXITING\"}");
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown());
    }

    // [例外規定] Tray終了時の共有停止に限り CLOSE_SHOP_ADMIN を呼ぶ。
    // 適用条件: ユーザーが Tray メニューから終了を選択・共有中・確認ダイアログで明示的に承認。
    // 共有開始・通常操作での AdminWorker 呼び出しは引き続き禁止。
    // 失敗・UAC キャンセル・必要情報不足の場合は終了しない。
    private bool TryCloseShopForExit()
    {
        string? shareName = _activeShareName;
        string? shopFolder = _shopFolder;
        if (string.IsNullOrWhiteSpace(shareName) || string.IsNullOrWhiteSpace(shopFolder))
        {
            ShowCloseShopIncompleteMessage();
            return false;
        }

        var confirm = Forms.MessageBox.Show(
            $"共有「{shareName}」を停止してから終了します。\n管理者権限の確認が求められます。\nよろしいですか？",
            "ShareWorkin",
            Forms.MessageBoxButtons.OKCancel,
            Forms.MessageBoxIcon.Question);
        if (confirm != Forms.DialogResult.OK)
            return false;

        var client = new AdminWorkerProcessClient();
        AdminCommandResponse response = client.Execute(new AdminCommandRequest
        {
            Cmd = AdminProtocol.CloseShopCommand,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ShareName = shareName,
            ShopRootPath = shopFolder,
        });

        if (!response.Ok)
        {
            ShowCloseShopIncompleteMessage();
            return false;
        }

        SmbController.StopShopBroadcaster();
        _isShopOpen = false;
        _activeShareName = null;
        PatchSettingsOpenState(false, _shopFolder);
        return true;
    }

    private static void ShowCloseShopIncompleteMessage()
    {
        Forms.MessageBox.Show(
            "共有停止が完了しなかったため、Trayを終了できませんでした。\nShareWorkin本体を開いて共有停止を確認してください。",
            "ShareWorkin",
            Forms.MessageBoxButtons.OK,
            Forms.MessageBoxIcon.Warning);
    }

    private void RequestExitWithAuth()
    {
        if (EntryPasswordManager.IsConfigured)
        {
            string? pw = TrayPasswordDialog.Show("Tray を終了します。\nパスワードを入力してください。");
            if (pw == null) return;
            if (!EntryPasswordManager.Verify(pw))
            {
                Forms.MessageBox.Show("パスワードが違います。", "ShareWorkin",
                    Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
                return;
            }
            ExitApp();
        }
        else
        {
            var r = Forms.MessageBox.Show("Tray を終了しますか？",
                "ShareWorkin", Forms.MessageBoxButtons.OKCancel, Forms.MessageBoxIcon.Question);
            if (r == Forms.DialogResult.OK) ExitApp();
        }
    }

    private void PatchSettingsOpenState(bool isOpen, string? shopFolder)
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var node = JsonNode.Parse(File.ReadAllText(SettingsPath));
            if (node is not JsonObject obj) return;
            obj["isOpenAtLastShutdown"] = isOpen;
            obj.Remove("IsOpenAtLastShutdown");
            if (shopFolder != null) obj["ShopFolder"] = shopFolder;
            if (!string.IsNullOrWhiteSpace(_pcOwnerSid)) obj["pcOwnerSid"] = _pcOwnerSid;
            if (!string.IsNullOrWhiteSpace(_pcOwnerAccount)) obj["pcOwnerAccount"] = _pcOwnerAccount;
            File.WriteAllText(SettingsPath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { SwkLogger.Warn($"TrayApp.PatchSettingsOpenState error: {ex.Message}"); }
    }

    private void PersistPcOwnerIdentity()
    {
        try
        {
            JsonObject obj;
            if (File.Exists(SettingsPath))
            {
                obj = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new JsonObject();
            }
            else
            {
                obj = new JsonObject();
            }

            if (!string.IsNullOrWhiteSpace(_pcOwnerSid))
            {
                obj["pcOwnerSid"] = _pcOwnerSid;
            }

            if (!string.IsNullOrWhiteSpace(_pcOwnerAccount))
            {
                obj["pcOwnerAccount"] = _pcOwnerAccount;
            }

            File.WriteAllText(SettingsPath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"TrayApp.PersistPcOwnerIdentity error: {ex.Message}");
        }
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            string? dir = Path.GetDirectoryName(Environment.ProcessPath);
            string path = Path.Combine(dir ?? string.Empty, "app.ico");
            if (File.Exists(path)) return new System.Drawing.Icon(path);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    internal static string DeriveShareName(string shopFolder)
    {
        string trimmed = shopFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        if (!string.IsNullOrEmpty(name)) return name;
        string root = Path.GetPathRoot(shopFolder) ?? string.Empty;
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).TrimEnd(':');
    }

    private static bool IsRunningAsAdmin()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
