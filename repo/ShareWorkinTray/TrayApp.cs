using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    internal readonly TrayPipeServer PipeServer;
    private FileSystemWatcher? _shopContentsWatcher;

    private bool _isShopOpen;
    private string? _shopFolder;
    private string? _activeShareName;
    private string? _pcOwnerSid;
    private string? _pcOwnerAccount;
    private bool _wasOpenAtLastShutdown;
    private ShareAccessRight _shareAccessRight = ShareAccessRight.Full;
    private string? _lastBalloonFolder;

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
        _notifyIcon.BalloonTipClicked += (_, _) => OpenLastBalloonFolder();

        _trayMenu = new Forms.ContextMenuStrip();
        var exitItem = new Forms.ToolStripMenuItem("アプリを終了");
        exitItem.Click += (_, _) => RequestExitWithAuth();
        _trayMenu.Items.Add(exitItem);
        _notifyIcon.ContextMenuStrip = _trayMenu;
    }

    public void Start()
    {
        WindowsToastNotificationService.Initialize();
        LoadSettings();
        SmbController.OnShopClosingReceived = HandleFriendShopClosingReceived;
        SmbController.OnInteractionEventReceived = HandleIncomingInteractionReceived;
        _notifyIcon.Visible = true;
        PipeServer.Start();
        RestoreOpenShopIfNeeded();
    }

    public void Dispose()
    {
        StopShopContentsWatcher();
        PipeServer.Stop();
        if (_isShopOpen && !string.IsNullOrWhiteSpace(_activeShareName) && !string.IsNullOrWhiteSpace(_shopFolder))
        {
            try { SmbController.CloseShopSequence(_activeShareName, _shopFolder); }
            catch (Exception ex) { SwkLogger.Warn($"TrayApp.Dispose CloseShop error: {ex.Message}"); }
        }
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

    private void RestoreOpenShopIfNeeded()
    {
        if (!_wasOpenAtLastShutdown || string.IsNullOrWhiteSpace(_shopFolder)) return;

        if (!Directory.Exists(_shopFolder))
        {
            PatchSettingsOpenState(false, _shopFolder);
            ShowBalloonTip("共有を再開できませんでした",
                "前回の共有フォルダーが見つかりません。\n画面を開いて設定を確認してください。", null);
            return;
        }

        string shareName = DeriveShareName(_shopFolder);
        if (string.IsNullOrWhiteSpace(shareName)) return;

        var (ok, error, _, _, _) = OpenShop(_shopFolder, shareName, shareName, _shareAccessRight, false);
        if (!ok)
        {
            SwkLogger.Warn($"TrayApp.RestoreOpenShopIfNeeded failed: {error}");
            PatchSettingsOpenState(false, _shopFolder);
            ShowBalloonTip("共有を再開できませんでした",
                "前回の状態と異なる可能性があります。\n画面を開いて確認してください。", _shopFolder);
        }
    }

    public (bool Ok, string? Error, bool NeedsOwnership, OwnershipChangePrompt OwnershipPrompt, IReadOnlyList<string>? BlockedPaths)
        OpenShop(string shopFolder, string shareName, string profileLabel, ShareAccessRight accessRight, bool authorizeOwnership)
    {
        var request = new ShopOpenRequest(shareName, shopFolder, profileLabel, accessRight);
        var result = SmbController.OpenShopSequence(request,
            userAuthorizedOwnershipChange: authorizeOwnership);

        if (!result.Succeeded && result.OwnershipPrompt != OwnershipChangePrompt.None)
            return (false, null, true, result.OwnershipPrompt, result.BlockedPaths);

        if (!result.Succeeded)
            return (false, result.FailureMessage, false, OwnershipChangePrompt.None, result.BlockedPaths);

        _isShopOpen = true;
        _shopFolder = shopFolder;
        _activeShareName = shareName;
        _shareAccessRight = accessRight;
        PatchSettingsOpenState(true, shopFolder);
        StartShopContentsWatcher(shopFolder);
        return (true, null, false, OwnershipChangePrompt.None, null);
    }

    public bool CloseShop()
    {
        if (!_isShopOpen) return true;
        StopShopContentsWatcher();
        bool ok = !string.IsNullOrWhiteSpace(_activeShareName) && !string.IsNullOrWhiteSpace(_shopFolder)
            && SmbController.CloseShopSequence(_activeShareName!, _shopFolder!);
        _isShopOpen = false;
        PatchSettingsOpenState(false, _shopFolder);
        return ok;
    }

    public void BroadcastShopClosing() => _ = SmbController.BroadcastShopClosingAsync();
    public void BroadcastPermissionChanged() => _ = SmbController.BroadcastPermissionChangedAsync();

    public bool SetSubfolderPermission(string path, bool isSharedOff, bool isReadOnly)
        => SmbNtfsManager.SetSubfolderPermission(path, isSharedOff, isReadOnly);

    public bool ResetPathToInherited(string path)
        => SmbNtfsManager.ResetPathToInherited(path);

    public bool MarkActionAftercare(
        string shopRootPath,
        string affectedPath,
        string policySourceFolder,
        SharePolicyRepairReason reason)
    {
        SharePolicyRepair.MarkActionAftercare(shopRootPath, affectedPath, policySourceFolder, reason);
        return true;
    }

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
            SwkLogger.Info($"ShowToastNotification: title={title}");
            return NotificationDisplayResult.Toast;
        }

        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.ShowBalloonTip(5000);
            SwkLogger.Info($"ShowBalloonTip fallback: title={title}");
            return NotificationDisplayResult.Fallback;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"ShowBalloonTip failed: title={title} ({ex.Message})");
            return NotificationDisplayResult.Failed;
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
        bool wasOpen = _isShopOpen;
        CloseShop();
        if (wasOpen)
            PatchSettingsOpenState(true, _shopFolder);
        if (!fromUiRequest)
            _ = PipeServer.PushMessageAsync("{\"type\":\"TRAY_EXITING\"}");
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown());
    }

    private void RequestExitWithAuth()
    {
        if (EntryPasswordManager.IsConfigured)
        {
            string? pw = TrayPasswordDialog.Show("終了すると共有も閉じます。\nパスワードを入力してください。");
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
            var r = Forms.MessageBox.Show("ShareWorkin を終了しますか？\n共有は閉じます。",
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

    private void StartShopContentsWatcher(string shopFolder)
    {
        StopShopContentsWatcher();

        try
        {
            _shopContentsWatcher = new FileSystemWatcher(shopFolder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            _shopContentsWatcher.Created += ShopContentsWatcher_Created;
            _shopContentsWatcher.Renamed += ShopContentsWatcher_Renamed;
            _shopContentsWatcher.Error += ShopContentsWatcher_Error;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            SwkLogger.Warn($"StartShopContentsWatcher failed: {ex.Message}");
            StopShopContentsWatcher();
        }
    }

    private void StopShopContentsWatcher()
    {
        if (_shopContentsWatcher is null)
        {
            return;
        }

        _shopContentsWatcher.EnableRaisingEvents = false;
        _shopContentsWatcher.Created -= ShopContentsWatcher_Created;
        _shopContentsWatcher.Renamed -= ShopContentsWatcher_Renamed;
        _shopContentsWatcher.Error -= ShopContentsWatcher_Error;
        _shopContentsWatcher.Dispose();
        _shopContentsWatcher = null;
    }

    private void ShopContentsWatcher_Created(object sender, FileSystemEventArgs e)
    {
        HandleShopContentArrival(e.FullPath, SharePolicyRepairReason.ExternalCreated);
    }

    private void ShopContentsWatcher_Renamed(object sender, RenamedEventArgs e)
    {
        HandleShopContentArrival(e.FullPath, SharePolicyRepairReason.ExternalRenamed);
    }

    private void ShopContentsWatcher_Error(object sender, ErrorEventArgs e)
    {
        SwkLogger.Warn($"ShopContentsWatcher error: {e.GetException()?.Message ?? "unknown"}");
    }

    private void HandleShopContentArrival(string affectedPath, SharePolicyRepairReason reason)
    {
        string? shopRootPath = _shopFolder;
        if (!_isShopOpen || string.IsNullOrWhiteSpace(shopRootPath) || string.IsNullOrWhiteSpace(affectedPath))
        {
            return;
        }

        if (!IsUnderFolder(affectedPath, shopRootPath) || IsUnderHoldFolder(affectedPath, shopRootPath))
        {
            return;
        }

        string policySourceFolder = Path.GetDirectoryName(affectedPath) ?? shopRootPath;
        _ = Task.Run(() => SharePolicyRepair.MarkActionAftercare(shopRootPath, affectedPath, policySourceFolder, reason));
    }

    private static bool IsUnderHoldFolder(string path, string shopRootPath)
    {
        string holdFolderPath = Path.Combine(shopRootPath, "保留");
        return IsUnderFolder(path, holdFolderPath);
    }

    private static bool IsUnderFolder(string path, string rootPath)
    {
        try
        {
            string root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string current = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(root, current, StringComparison.OrdinalIgnoreCase) ||
                   current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
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
}
