using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ShareWorkin.SMB;

public sealed record ShopOpenRequest(
    string ShareName,
    string ShopRootPath,
    string ProfileLabel,
    ShareAccessRight AccessRight);

public enum OwnershipChangePrompt
{
    None,
    Verified,         // 事前確認で全件OK確定。標準ダイアログ。
    Unverifiable,     // 列挙不能で事前確認できず。別文言ダイアログ。
}

public sealed record ShopOpenResult(
    bool Succeeded,
    string? FailureMessage,
    SmbLayerStatus? StatusBefore,
    SmbLayerStatus? StatusAfter,
    OwnershipChangePrompt OwnershipPrompt = OwnershipChangePrompt.None,
    IReadOnlyList<string>? BlockedPaths = null);

public static class SmbController
{
    // 開店中のブロードキャスター（通知を送信）
    private static SwkNotificationBroadcaster? _broadcaster;

    public static SmbLayerStatus GetCurrentState(string? shopRootPath = null)
        => SmbLayerChecker.GetCurrentState(shopRootPath);

    public static bool EnsureInfrastructure()
    {
        bool a = SmbInfrastructureManager.EnsureLanmanServerRunning();
        bool b = SmbInfrastructureManager.EnsureNetworkPrivate();
        bool c = SmbInfrastructureManager.EnsureFirewallSharingEnabled();
        bool d = SmbInfrastructureManager.EnsureSmbServerConfig();
        bool e = SmbInfrastructureManager.EnsureSwkDiscoveryPort();
        string exePath = Environment.ProcessPath ?? string.Empty;
        bool f = string.IsNullOrEmpty(exePath) || SmbInfrastructureManager.EnsureSwkAppTcpRule(exePath);
        return a && b && c && d && e && f;
    }

    public static ShopOpenResult OpenShopSequence(
        ShopOpenRequest request,
        bool userAuthorizedOwnershipChange = false,
        Func<SwkNotificationBroadcaster.InviteApprovalRequest, Task<bool>>? onInviteRequested = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        SwkLogger.Info($"OpenShopSequence start: name='{request.ShareName}', authorizedOwnership={userAuthorizedOwnershipChange}");

        SmbLayerStatus before = SmbLayerChecker.GetCurrentState(request.ShopRootPath);

        if (!EnsureInfrastructure())
        {
            return Fail("お店の準備ができませんでした(共有サービスの設定)。", before, null);
        }

        if (!SmbAccountManager.EnsureAccount())
        {
            return Fail("お店の鍵が用意できませんでした。", before, null);
        }

        // 草案6 §A: 所有権が現ユーザーに無い項目が配下に混ざる場合も、
        // 開店時点でお店の管理下へ揃えるために同意取得を挟む。
        AclRepairPreflight aclRepair = SmbNtfsManager.PreflightAclRepair(request.ShopRootPath);
        if (aclRepair.NeedsOwnershipChange)
        {
            OwnershipChangePrompt prompt = aclRepair.EnumerationBlocked
                ? OwnershipChangePrompt.Unverifiable
                : OwnershipChangePrompt.Verified;

            if (!aclRepair.CanRepairWithOwnershipChange)
            {
                SwkLogger.Warn($"OpenShopSequence aborted: {aclRepair.BlockedPaths.Count} item(s) cannot have ownership changed");
                return new ShopOpenResult(
                    Succeeded: false,
                    FailureMessage: "このフォルダーの一部のファイルはアクセス設定を整えられないため、お店として開けません。",
                    StatusBefore: before,
                    StatusAfter: null,
                    OwnershipPrompt: OwnershipChangePrompt.None,
                    BlockedPaths: aclRepair.BlockedPaths);
            }

            if (!userAuthorizedOwnershipChange)
            {
                SwkLogger.Info($"OpenShopSequence: ownership change required ({prompt}), awaiting user consent");
                return new ShopOpenResult(
                    Succeeded: false,
                    FailureMessage: null,
                    StatusBefore: before,
                    StatusAfter: null,
                    OwnershipPrompt: prompt,
                    BlockedPaths: null);
            }

            SwkLogger.Info("OpenShopSequence: user authorized ownership change, executing takeown");
            if (!SmbNtfsManager.TakeOwnershipRecursive(request.ShopRootPath))
            {
                return Fail("お店のアクセス設定を整えられませんでした。", before, null);
            }
        }

        SwkLogger.Info("OpenShopSequence: aligning ownership to current user");
        if (!SmbNtfsManager.TakeOwnershipRecursive(request.ShopRootPath))
        {
            return Fail("お店のアクセス設定を整えられませんでした。", before, null);
        }

        if (!SmbNtfsManager.IsolateShopRoot(request.ShopRootPath))
        {
            return Fail("お店の場所の準備ができませんでした。", before, null);
        }

        bool ourShareAlreadyExists = SmbShareManager.ListShareWorkinShares()
            .Any(s => string.Equals(s.Name, request.ShareName, StringComparison.OrdinalIgnoreCase));

        if (!ourShareAlreadyExists)
        {
            if (SmbShareManager.ShareExists(request.ShareName))
            {
                SmbNtfsManager.RevokeSwkGuest(request.ShopRootPath);
                return Fail("この名前は別のお店で使われているようです。お店の名前を変えてください。", before, null);
            }

            if (!SmbShareManager.CreateShare(request.ShareName, request.ShopRootPath, request.ProfileLabel))
            {
                SmbNtfsManager.RevokeSwkGuest(request.ShopRootPath);
                return Fail("お店を開くのに失敗しました。", before, null);
            }
        }
        else
        {
            SwkLogger.Info($"OpenShopSequence: reusing existing ShareWorkin share '{request.ShareName}'");
            if (!SmbShareManager.RepairShareDefinition(
                    request.ShareName,
                    request.ShopRootPath,
                    request.ProfileLabel))
            {
                SmbNtfsManager.RevokeSwkGuest(request.ShopRootPath);
                return Fail("お店の入口を整え直せませんでした。", before, null);
            }
        }

        SmbShareManager.RevokeEveryone(request.ShareName);

        if (!SmbShareManager.GrantSwkGuest(request.ShareName, request.AccessRight))
        {
            if (!ourShareAlreadyExists)
            {
                SmbShareManager.RemoveShare(request.ShareName);
            }
            SmbNtfsManager.RevokeSwkGuest(request.ShopRootPath);
            return Fail("お店の入り口の用意に失敗しました。", before, null);
        }

        SmbLayerStatus after = SmbLayerChecker.GetCurrentState(request.ShopRootPath);

        // 通知ブロードキャスターを起動（LAN内の他のPCに「ここで開いてます」と通知）
        try
        {
            _broadcaster = new SwkNotificationBroadcaster(request.ShareName);
            // 招待承認コールバックを購読する(MainWindow から渡される)。
            // 未設定のままだと一律に拒否されるため、店主の意思介在が必須となる。
            _broadcaster.OnInviteRequested = onInviteRequested;
            _ = _broadcaster.StartAsync(); // 非同期で起動（待たない）
            SwkLogger.Info($"SwkNotificationBroadcaster started for '{request.ShareName}'");
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"Failed to start SwkNotificationBroadcaster: {ex.Message}");
            // ブロードキャスター起動失敗は致命的でない。お店は開けたが、他PCからは発見されないだけ
        }

        SwkLogger.Info("OpenShopSequence ok");
        return new ShopOpenResult(true, null, before, after);
    }

    public static bool CloseShopSequence(string shareName, string shopRootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(shareName);
        ArgumentException.ThrowIfNullOrEmpty(shopRootPath);

        SwkLogger.Info($"CloseShopSequence start: name='{shareName}'");

        // 通知ブロードキャスターを停止
        if (_broadcaster != null)
        {
            try
            {
                // 非同期で停止（本来は await する。ただし同期メソッドなので、Fire-and-forget）
                _ = _broadcaster.StopAsync();
                _broadcaster = null;
                SwkLogger.Info("SwkNotificationBroadcaster stopped");
            }
            catch (Exception ex)
            {
                SwkLogger.Warn($"Error stopping SwkNotificationBroadcaster: {ex.Message}");
            }
        }

        bool removeOk = SmbShareManager.RemoveShare(shareName);
        bool revokeOk = SmbNtfsManager.RevokeSwkGuest(shopRootPath);
        // 骨格仕様書 v0.1 条文 1.5 (ii) / 条文 3.2: 閉店時に継承を復元する。
        // 開店時に IsolateShopRoot で /inheritance:r したものを対称に戻す。
        bool restoreOk = SmbNtfsManager.RestoreInheritance(shopRootPath);

        bool ok = removeOk && revokeOk && restoreOk;
        SwkLogger.Info($"CloseShopSequence done: ok={ok}");
        return ok;
    }

    public static Task BroadcastPermissionChangedAsync()
        => _broadcaster?.BroadcastPermissionChangedAsync() ?? Task.CompletedTask;

    private static ShopOpenResult Fail(string message, SmbLayerStatus before, SmbLayerStatus? after)
    {
        SwkLogger.Warn($"OpenShopSequence failed: {message}");
        return new ShopOpenResult(false, message, before, after);
    }
}
