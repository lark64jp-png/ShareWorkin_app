using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
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
    private sealed record CachedShareSnapshot(ShareSnapshotPayload Payload, DateTime CachedAtUtc);

    private static readonly string AppHomeDirectory = AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static readonly string SettingsPath = Path.Combine(AppHomeDirectory, "settings.json");
    private static readonly TimeSpan BalloonTipShownTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShareSnapshotTtl = TimeSpan.FromSeconds(15);

    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly object _balloonTipSync = new();
    private readonly object _shareSnapshotSync = new();
    private readonly Dictionary<string, CachedShareSnapshot> _shareSnapshotCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<TrayCommandResponse<ShareSnapshotPayload>>> _shareSnapshotInflight = new(StringComparer.OrdinalIgnoreCase);
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
        LogStartupShareState("after-load-settings");
        ReconcilePersistedShopStateWithWindowsShare();
        SmbController.OnShopClosingReceived = HandleFriendShopClosingReceived;
        SmbController.OnInteractionEventReceived = HandleIncomingInteractionReceived;
        RestoreShopOpenStateFromSettings();
        LogStartupShareState("after-restore");
        _ = ObserveStartupShareStateAfterStartAsync();
        _notifyIcon.Visible = true;
        PipeServer.Start();
    }

    public void Dispose()
    {
        PipeServer.Stop();
        // Tray終了は共有停止ではない。明示的な共有停止操作以外では、
        // 共有中状態をOFFへ落とす処理をここで行わない。
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
            SwkLogger.Warn(
                $"TrayApp.UpdateShopOpenedState rejected: shopFolder={shopFolder ?? "-"} shareName={shareName ?? "-"}");
            return false;
        }

        SwkLogger.Info(
            $"TrayApp.UpdateShopOpenedState start: previousOpen={_isShopOpen} previousFolder={_shopFolder ?? "-"} " +
            $"newFolder={shopFolder} shareName={shareName} accessRight={accessRight}");
        SmbController.StartShopBroadcaster(shareName);
        _isShopOpen = true;
        _shopFolder = shopFolder;
        _activeShareName = shareName;
        _shareAccessRight = accessRight;
        MarkShareSnapshotDirty("TrayShopOpened");
        PatchSettingsOpenState(true, shopFolder);
        SwkLogger.Info(
            $"TrayApp.UpdateShopOpenedState complete: trayOpen={_isShopOpen} shopFolder={_shopFolder ?? "-"} " +
            $"activeShareName={_activeShareName ?? "-"}");
        return true;
    }

    public bool UpdateShopClosedState()
    {
        if (!_isShopOpen)
        {
            SwkLogger.Info(
                $"TrayApp.UpdateShopClosedState no-op: trayOpen={_isShopOpen} shopFolder={_shopFolder ?? "-"}");
            PatchSettingsOpenState(false, _shopFolder);
            return true;
        }

        SwkLogger.Info(
            $"TrayApp.UpdateShopClosedState start: trayOpen={_isShopOpen} shopFolder={_shopFolder ?? "-"} " +
            $"activeShareName={_activeShareName ?? "-"}");
        SmbController.StopShopBroadcaster();
        _isShopOpen = false;
        _activeShareName = null;
        MarkShareSnapshotDirty("TrayShopClosed");
        PatchSettingsOpenState(false, _shopFolder);
        SwkLogger.Info(
            $"TrayApp.UpdateShopClosedState complete: trayOpen={_isShopOpen} shopFolder={_shopFolder ?? "-"}");
        return true;
    }

    public async Task<TrayCommandResponse<ShareSnapshotPayload>> GetShareSnapshotAsync(GetShareSnapshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        GetShareSnapshotRequest effectiveRequest = request with
        {
            ShareName = string.IsNullOrWhiteSpace(request.ShareName) ? _activeShareName : request.ShareName,
            ShopRootPath = string.IsNullOrWhiteSpace(request.ShopRootPath) ? _shopFolder : request.ShopRootPath
        };
        string cacheKey = BuildShareSnapshotCacheKey(effectiveRequest.ShareName, effectiveRequest.ShopRootPath);
        Task<TrayCommandResponse<ShareSnapshotPayload>> pendingTask;
        bool joinedInflight = false;

        lock (_shareSnapshotSync)
        {
            if (!effectiveRequest.ForceRefresh &&
                _shareSnapshotCache.TryGetValue(cacheKey, out CachedShareSnapshot? cached) &&
                IsFresh(cached.Payload))
            {
                return BuildShareSnapshotResponse(
                    effectiveRequest,
                    cached.Payload with { Source = "TrayCache" },
                    TrayCommandResultCode.Success,
                    TrayCommandErrorReason.None,
                    retryable: false,
                    joinedInflight: false,
                    message: null);
            }

            if (_shareSnapshotInflight.TryGetValue(cacheKey, out pendingTask!))
            {
                joinedInflight = true;
            }
            else
            {
                pendingTask = Task.Run(() => RefreshShareSnapshotCore(effectiveRequest, cacheKey));
                _shareSnapshotInflight[cacheKey] = pendingTask;
            }
        }

        TrayCommandResponse<ShareSnapshotPayload> response = await pendingTask;
        return response with
        {
            RequestId = effectiveRequest.RequestId,
            JoinedInflight = joinedInflight,
            Payload = response.Payload is null
                ? null
                : response.Payload with
                {
                    RequestedShareName = effectiveRequest.ShareName,
                    RequestedShopRootPath = effectiveRequest.ShopRootPath
                }
        };
    }
    private void RestoreShopOpenStateFromSettings()
    {
        if (!_wasOpenAtLastShutdown || string.IsNullOrWhiteSpace(_shopFolder))
        {
            return;
        }

        if (!Directory.Exists(_shopFolder))
        {
            SwkLogger.Warn(
                $"TrayApp.RestoreShopOpenStateFromSettings skipped: shopFolder missing path={_shopFolder}");
            _wasOpenAtLastShutdown = false;
            PatchSettingsOpenState(false, _shopFolder);
            return;
        }

        string shareName = DeriveShareName(_shopFolder);
        if (string.IsNullOrWhiteSpace(shareName))
        {
            SwkLogger.Warn("TrayApp.RestoreShopOpenStateFromSettings skipped: shareName empty");
            return;
        }

        SmbController.StartShopBroadcaster(shareName);
        _isShopOpen = true;
        _activeShareName = shareName;
        SwkLogger.Info($"TrayApp.RestoreShopOpenStateFromSettings: shop state restored shareName={shareName}");
    }

    private void ReconcilePersistedShopStateWithWindowsShare()
    {
        if (string.IsNullOrWhiteSpace(_shopFolder))
        {
            return;
        }

        ShareWorkinShareInfo? windowsShare = SmbShareManager.FindShareWorkinShareByPath(_shopFolder);
        bool windowsShareExists = windowsShare is not null;
        string derivedShareName = DeriveShareName(_shopFolder);
        SwkLogger.Info(
            $"TrayApp.StartupShareState: settingsOpen={_wasOpenAtLastShutdown} shopFolder={_shopFolder} " +
            $"derivedShareName={derivedShareName} windowsShareExists={windowsShareExists} " +
            $"windowsShareName={windowsShare?.Name ?? "-"} windowsSharePath={windowsShare?.Path ?? "-"}");

        if (!_wasOpenAtLastShutdown && windowsShareExists)
        {
            SwkLogger.Info(
                $"TrayApp.StartupShareResidual: settingsOpen=false and Windows share exists " +
                $"shareName={windowsShare!.Name} sharePath={windowsShare.Path}; action=keep-settings-off");
            return;
        }

        if (_wasOpenAtLastShutdown && !windowsShareExists)
        {
            SwkLogger.Warn(
                $"TrayApp.StartupShareMismatch: settingsOpen=true but Windows share missing " +
                $"derivedShareName={derivedShareName} shopFolder={_shopFolder}; action=log-only");
        }
    }

    private void LogStartupShareState(string stage)
    {
        string? derivedShareName = string.IsNullOrWhiteSpace(_shopFolder) ? null : DeriveShareName(_shopFolder);
        ShareWorkinShareInfo? windowsShare = string.IsNullOrWhiteSpace(_shopFolder)
            ? null
            : SmbShareManager.FindShareWorkinShareByPath(_shopFolder);
        SwkLogger.Info(
            $"TrayApp.StartupShareProbe: stage={stage} observedAt={DateTime.Now:O} " +
            $"settingsOpen={_wasOpenAtLastShutdown} trayOpen={_isShopOpen} shopFolder={_shopFolder ?? "-"} " +
            $"activeShareName={_activeShareName ?? "-"} derivedShareName={derivedShareName ?? "-"} " +
            $"windowsShareExists={(windowsShare is not null)} windowsShareName={windowsShare?.Name ?? "-"} " +
            $"windowsSharePath={windowsShare?.Path ?? "-"}");
    }

    private async Task ObserveStartupShareStateAfterStartAsync()
    {
        await Task.Delay(1500);
        LogStartupShareState("late-1500ms");
        await Task.Delay(1500);
        LogStartupShareState("late-3000ms");
    }

    public void BroadcastShopClosing() => _ = SmbController.BroadcastShopClosingAsync();
    public void BroadcastPermissionChanged() => _ = SmbController.BroadcastPermissionChangedAsync();

    private TrayCommandResponse<ShareSnapshotPayload> RefreshShareSnapshotCore(
        GetShareSnapshotRequest request,
        string cacheKey)
    {
        try
        {
            ShareDefinitionQueryResult query = SmbShareManager.QueryShareDefinition(request.ShareName, request.ShopRootPath);
            ShareSnapshotPayload payload = BuildShareSnapshotPayload(
                request,
                query.Share,
                source: "WindowsConfirmed",
                isStale: false,
                dirtyReasons: Array.Empty<string>());

            if (query.Succeeded && !query.NotFound && query.Share is not null)
            {
                lock (_shareSnapshotSync)
                {
                    _shareSnapshotCache[cacheKey] = new CachedShareSnapshot(payload, DateTime.UtcNow);
                }

                return BuildShareSnapshotResponse(
                    request,
                    payload,
                    TrayCommandResultCode.Success,
                    TrayCommandErrorReason.None,
                    retryable: false,
                    joinedInflight: false,
                    message: null);
            }

            if (TryGetCachedFallback(cacheKey, request, out TrayCommandResponse<ShareSnapshotPayload>? fallback))
            {
                return fallback;
            }

            if (query.TimedOut)
            {
                return BuildShareSnapshotResponse(
                    request,
                    payload with { IsStale = true, DirtyReasons = new[] { "TimedOut" } },
                    TrayCommandResultCode.TimedOut,
                    TrayCommandErrorReason.Timeout,
                    retryable: true,
                    joinedInflight: false,
                    message: "共有状態の確認がタイムアウトしました。");
            }

            if (query.NotFound)
            {
                return BuildShareSnapshotResponse(
                    request,
                    payload with { IsStale = true, DirtyReasons = new[] { "ShareNotFound" } },
                    TrayCommandResultCode.Failed,
                    TrayCommandErrorReason.ShareNotFound,
                    retryable: true,
                    joinedInflight: false,
                    message: "Windows の共有定義に対象が見つかりませんでした。");
            }

            return BuildShareSnapshotResponse(
                request,
                payload with { IsStale = true, DirtyReasons = new[] { "WindowsQueryFailed" } },
                TrayCommandResultCode.Failed,
                TrayCommandErrorReason.WindowsQueryFailed,
                retryable: true,
                joinedInflight: false,
                message: string.IsNullOrWhiteSpace(query.ErrorMessage)
                    ? "共有状態を確認できませんでした。"
                    : query.ErrorMessage);
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"TrayApp.RefreshShareSnapshotCore failed: {ex.GetType().Name}: {ex.Message}");
            if (TryGetCachedFallback(cacheKey, request, out TrayCommandResponse<ShareSnapshotPayload>? fallback))
            {
                return fallback;
            }

            return BuildShareSnapshotResponse(
                request,
                BuildShareSnapshotPayload(request, null, "WindowsError", isStale: true, dirtyReasons: new[] { "InternalException" }),
                TrayCommandResultCode.InternalError,
                TrayCommandErrorReason.InternalException,
                retryable: true,
                joinedInflight: false,
                message: "共有状態を取得できませんでした。");
        }
        finally
        {
            lock (_shareSnapshotSync)
            {
                _shareSnapshotInflight.Remove(cacheKey);
            }
        }
    }

    private bool TryGetCachedFallback(
        string cacheKey,
        GetShareSnapshotRequest request,
        out TrayCommandResponse<ShareSnapshotPayload> response)
    {
        lock (_shareSnapshotSync)
        {
            if (_shareSnapshotCache.TryGetValue(cacheKey, out CachedShareSnapshot? cached))
            {
                ShareSnapshotPayload stalePayload = cached.Payload with
                {
                    Source = "TrayCacheFallback",
                    IsStale = true,
                    DirtyReasons = AppendDirtyReason(cached.Payload.DirtyReasons, "RefreshFailed"),
                    RequestedShareName = request.ShareName,
                    RequestedShopRootPath = request.ShopRootPath
                };
                response = BuildShareSnapshotResponse(
                    request,
                    stalePayload,
                    TrayCommandResultCode.FallbackSuccess,
                    TrayCommandErrorReason.None,
                    retryable: true,
                    joinedInflight: false,
                    message: "前回確認できた状態を表示しています。");
                return true;
            }
        }

        response = null!;
        return false;
    }

    private ShareSnapshotPayload BuildShareSnapshotPayload(
        GetShareSnapshotRequest request,
        ShareDefinitionDetails? share,
        string source,
        bool isStale,
        IReadOnlyList<string> dirtyReasons)
    {
        bool shareNameMatches = string.IsNullOrWhiteSpace(request.ShareName) ||
                                string.Equals(share?.Name, request.ShareName, StringComparison.OrdinalIgnoreCase);
        bool shopRootPathMatches = string.IsNullOrWhiteSpace(request.ShopRootPath) ||
                                   string.Equals(
                                       NormalizePath(share?.Path),
                                       NormalizePath(request.ShopRootPath),
                                       StringComparison.OrdinalIgnoreCase);
        bool hasMatchingShare = share is not null && shareNameMatches && shopRootPathMatches;
        string? effectiveAccessRight = ResolveEffectiveAccessRight(share);

        return new ShareSnapshotPayload(
            DateTime.UtcNow,
            source,
            isStale,
            dirtyReasons,
            request.ShareName,
            request.ShopRootPath,
            _activeShareName,
            _shopFolder,
            _shareAccessRight.ToString(),
            share?.Name,
            share?.Path,
            share?.DescriptionLabel,
            share?.ShareState,
            share?.CurrentUsers,
            hasMatchingShare,
            shareNameMatches,
            shopRootPathMatches,
            effectiveAccessRight,
            share?.AccessEntries
                .Select(x => new ShareSnapshotAccessEntry(x.AccountName, x.AccessControlType, x.AccessRight))
                .ToArray()
                ?? Array.Empty<ShareSnapshotAccessEntry>());
    }

    private static TrayCommandResponse<ShareSnapshotPayload> BuildShareSnapshotResponse(
        GetShareSnapshotRequest request,
        ShareSnapshotPayload payload,
        TrayCommandResultCode resultCode,
        TrayCommandErrorReason errorReason,
        bool retryable,
        bool joinedInflight,
        string? message)
    {
        return new TrayCommandResponse<ShareSnapshotPayload>(
            request.RequestId,
            resultCode,
            errorReason,
            retryable,
            joinedInflight,
            message,
            payload);
    }

    private void MarkShareSnapshotDirty(string reason)
    {
        lock (_shareSnapshotSync)
        {
            foreach (string key in _shareSnapshotCache.Keys.ToArray())
            {
                CachedShareSnapshot cached = _shareSnapshotCache[key];
                _shareSnapshotCache[key] = cached with
                {
                    Payload = cached.Payload with
                    {
                        IsStale = true,
                        DirtyReasons = AppendDirtyReason(cached.Payload.DirtyReasons, reason)
                    }
                };
            }
        }
    }

    private static IReadOnlyList<string> AppendDirtyReason(IReadOnlyList<string> existing, string reason)
    {
        if (existing.Any(x => string.Equals(x, reason, StringComparison.OrdinalIgnoreCase)))
        {
            return existing;
        }

        return existing.Concat(new[] { reason }).ToArray();
    }

    private static bool IsFresh(ShareSnapshotPayload payload)
    {
        return !payload.IsStale && DateTime.UtcNow - payload.ObservedAtUtc <= ShareSnapshotTtl;
    }

    private static string BuildShareSnapshotCacheKey(string? shareName, string? shopRootPath)
    {
        string sharePart = string.IsNullOrWhiteSpace(shareName) ? "-" : shareName.Trim();
        string pathPart = string.IsNullOrWhiteSpace(shopRootPath) ? "-" : NormalizePath(shopRootPath) ?? shopRootPath.Trim();
        return $"{sharePart}|{pathPart}";
    }

    private static string? ResolveEffectiveAccessRight(ShareDefinitionDetails? share)
    {
        if (share is null)
        {
            return null;
        }

        ShareDefinitionAccessInfo? swkAccountEntry = share.AccessEntries.FirstOrDefault(
            x => string.Equals(x.AccountName, SmbAccountManager.LocalQualifiedAccountName, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(x.AccessControlType, "Allow", StringComparison.OrdinalIgnoreCase));
        if (swkAccountEntry is not null)
        {
            return swkAccountEntry.AccessRight;
        }

        ShareDefinitionAccessInfo? allowEntry = share.AccessEntries.FirstOrDefault(
            x => string.Equals(x.AccessControlType, "Allow", StringComparison.OrdinalIgnoreCase));
        return allowEntry?.AccessRight;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
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
            SwkLogger.Info($"TrayApp.ExitApp: tray exits without closing UI or stopping SMB share (fromUiRequest={fromUiRequest})");
        }

        // Tray終了は共有停止ではないため、UIへTRAY_EXITINGを送らない。
        // UIを閉じるとCloseShop経路へ入り、共有中表示・監視状態がOFFへ落ちるため。
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown());
    }

    private bool TryCloseShopForExit()
    {
        SwkLogger.Info("TrayApp.TryCloseShopForExit: skip close because tray exit must preserve sharing");
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
