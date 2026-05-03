using System;
using System.Collections.Generic;
using System.Linq;

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
    public static SmbLayerStatus GetCurrentState(string? shopRootPath = null)
        => SmbLayerChecker.GetCurrentState(shopRootPath);

    public static bool EnsureInfrastructure()
    {
        bool a = SmbInfrastructureManager.EnsureLanmanServerRunning();
        bool b = SmbInfrastructureManager.EnsureNetworkPrivate();
        bool c = SmbInfrastructureManager.EnsureFirewallSharingEnabled();
        bool d = SmbInfrastructureManager.EnsureSmbServerConfig();
        return a && b && c && d;
    }

    public static ShopOpenResult OpenShopSequence(
        ShopOpenRequest request,
        bool userAuthorizedOwnershipChange = false)
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

        // 草案6 §A: 所有権が現ユーザーに無い場合、破壊的操作の前に「触らずに確かめる」。
        // CanModifyAcl が false なら IsolateShopRoot は必ず失敗するため、事前検査と同意取得を挟む。
        if (!SmbNtfsManager.CanModifyAcl(request.ShopRootPath))
        {
            TakeOwnershipPreflight preflight = SmbNtfsManager.PreflightTakeOwnership(request.ShopRootPath);

            // 内包全件OK: 標準同意ダイアログを出す経路
            // 列挙不能: 事前確認できないが所有権書き換えで救える可能性がある経路(別文言)
            // それ以外(個別不能項目あり): 救済不可として停止
            OwnershipChangePrompt prompt = preflight switch
            {
                { AllAccessible: true } => OwnershipChangePrompt.Verified,
                { EnumerationBlocked: true } => OwnershipChangePrompt.Unverifiable,
                _ => OwnershipChangePrompt.None,
            };

            if (prompt == OwnershipChangePrompt.None)
            {
                SwkLogger.Warn($"OpenShopSequence aborted: {preflight.BlockedPaths.Count} item(s) cannot have ownership changed");
                return new ShopOpenResult(
                    Succeeded: false,
                    FailureMessage: "このフォルダーの一部のファイルは所有者を変更できないため、お店として開けません。",
                    StatusBefore: before,
                    StatusAfter: null,
                    OwnershipPrompt: OwnershipChangePrompt.None,
                    BlockedPaths: preflight.BlockedPaths);
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
                return Fail("所有者の変更に失敗しました。", before, null);
            }
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
        SwkLogger.Info("OpenShopSequence ok");
        return new ShopOpenResult(true, null, before, after);
    }

    public static bool CloseShopSequence(string shareName, string shopRootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(shareName);
        ArgumentException.ThrowIfNullOrEmpty(shopRootPath);

        SwkLogger.Info($"CloseShopSequence start: name='{shareName}'");

        bool removeOk = SmbShareManager.RemoveShare(shareName);
        bool revokeOk = SmbNtfsManager.RevokeSwkGuest(shopRootPath);
        // 骨格仕様書 v0.1 条文 1.5 (ii) / 条文 3.2: 閉店時に継承を復元する。
        // 開店時に IsolateShopRoot で /inheritance:r したものを対称に戻す。
        bool restoreOk = SmbNtfsManager.RestoreInheritance(shopRootPath);

        bool ok = removeOk && revokeOk && restoreOk;
        SwkLogger.Info($"CloseShopSequence done: ok={ok}");
        return ok;
    }

    private static ShopOpenResult Fail(string message, SmbLayerStatus before, SmbLayerStatus? after)
    {
        SwkLogger.Warn($"OpenShopSequence failed: {message}");
        return new ShopOpenResult(false, message, before, after);
    }
}
