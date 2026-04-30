using System;
using System.Linq;

namespace ShareWorkin.SMB;

public sealed record ShopOpenRequest(
    string ShareName,
    string ShopRootPath,
    string ProfileLabel,
    ShareAccessRight AccessRight);

public sealed record ShopOpenResult(
    bool Succeeded,
    string? FailureMessage,
    SmbLayerStatus? StatusBefore,
    SmbLayerStatus? StatusAfter);

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

    public static ShopOpenResult OpenShopSequence(ShopOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SwkLogger.Info($"OpenShopSequence start: name='{request.ShareName}'");

        SmbLayerStatus before = SmbLayerChecker.GetCurrentState(request.ShopRootPath);

        if (!EnsureInfrastructure())
        {
            return Fail("お店の準備ができませんでした(共有サービスの設定)。", before, null);
        }

        if (!SmbAccountManager.EnsureAccount())
        {
            return Fail("お店の鍵が用意できませんでした。", before, null);
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

        bool ok = removeOk && revokeOk;
        SwkLogger.Info($"CloseShopSequence done: ok={ok}");
        return ok;
    }

    private static ShopOpenResult Fail(string message, SmbLayerStatus before, SmbLayerStatus? after)
    {
        SwkLogger.Warn($"OpenShopSequence failed: {message}");
        return new ShopOpenResult(false, message, before, after);
    }
}
