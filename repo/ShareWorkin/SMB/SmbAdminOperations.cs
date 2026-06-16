using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ShareWorkin.SMB;

public static class SmbAdminOperations
{
    private static void LogOpenShopDecision(
        AdminCommandRequest request,
        string stage,
        string shopRootPath,
        string shareName,
        bool? needsOwnership = null,
        bool? canRepair = null,
        bool? enumerationBlocked = null,
        int? blockedCount = null,
        bool? ourShareAlreadyExists = null,
        bool? sameNamedShareExists = null,
        bool? applyPermissionsOnOpen = null,
        int? permissionEntryCount = null)
    {
        SwkLogger.Info(
            $"AdminWorker.OpenShop decision corr={request.CorrelationId} stage={stage} root={shopRootPath} share={shareName} " +
            $"needsOwnership={needsOwnership?.ToString() ?? "-"} canRepair={canRepair?.ToString() ?? "-"} " +
            $"enumBlocked={enumerationBlocked?.ToString() ?? "-"} blockedCount={blockedCount?.ToString() ?? "-"} " +
            $"ourShareAlreadyExists={ourShareAlreadyExists?.ToString() ?? "-"} sameNamedShareExists={sameNamedShareExists?.ToString() ?? "-"} " +
            $"applyPermissionsOnOpen={applyPermissionsOnOpen?.ToString() ?? "-"} permissionEntryCount={permissionEntryCount?.ToString() ?? "-"}");
    }

    public static AdminCommandResponse Execute(AdminCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Cmd switch
        {
            AdminProtocol.PingCommand => Ok(request),
            AdminProtocol.OpenShopCommand => OpenShop(request),
            AdminProtocol.CloseShopCommand => CloseShop(request),
            AdminProtocol.SetSubfolderPermissionCommand => SetSubfolderPermission(request),
            AdminProtocol.ResetPathToInheritedCommand => ResetPathToInherited(request),
            AdminProtocol.MarkActionAftercareCommand => MarkActionAftercare(request),
            _ => Fail(request, AdminErrorCode.ValidationFailed, $"Unknown command: {request.Cmd}")
        };
    }

    private static AdminCommandResponse OpenShop(AdminCommandRequest request)
    {
        if (!SharePathPolicy.TryNormalizeLocalPath(request.ShopRootPath, out string shopRootPath))
        {
            return Fail(request, AdminErrorCode.ValidationFailed, "Shop root path is invalid.");
        }

        if (!Directory.Exists(shopRootPath))
        {
            return Fail(request, AdminErrorCode.ValidationFailed, "Shop root path was not found.");
        }

        string shareName = request.ShareName?.Trim() ?? string.Empty;
        if (!SharePathPolicy.ValidateShareName(shareName, out string shareNameError))
        {
            return Fail(request, AdminErrorCode.ValidationFailed, shareNameError);
        }

        string trayExecutablePath = Path.Combine(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            $"{AdminProtocol.TrayProcessName}.exe");

        SwkLogger.Info(
            $"AdminWorker.OpenShop start corr={request.CorrelationId} root={shopRootPath} share={shareName}");

        if (!SmbInfrastructureManager.EnsureLanmanServerRunning() ||
            !SmbInfrastructureManager.EnsureNetworkPrivate() ||
            !SmbInfrastructureManager.EnsureFirewallSharingEnabled() ||
            !SmbInfrastructureManager.EnsureSmbServerConfig() ||
            !SmbInfrastructureManager.EnsureSwkDiscoveryPort() ||
            !SmbInfrastructureManager.EnsureSwkAppTcpRule(trayExecutablePath))
        {
            return Fail(request, AdminErrorCode.InfrastructureFailed, "お店の準備ができませんでした(共有サービスの設定)。");
        }

        if (!SmbAccountManager.EnsureAccount())
        {
            return Fail(request, AdminErrorCode.AccountFailed, "お店の鍵が用意できませんでした。");
        }

        AclRepairPreflight aclRepair = SmbNtfsManager.PreflightAclRepair(shopRootPath);
        LogOpenShopDecision(
            request,
            stage: "acl-preflight",
            shopRootPath,
            shareName,
            needsOwnership: aclRepair.NeedsOwnershipChange,
            canRepair: aclRepair.CanRepairWithOwnershipChange,
            enumerationBlocked: aclRepair.EnumerationBlocked,
            blockedCount: aclRepair.BlockedPaths.Count,
            applyPermissionsOnOpen: request.ApplyPermissionsOnOpen,
            permissionEntryCount: request.PermissionEntries?.Count ?? 0);
        if (aclRepair.NeedsOwnershipChange)
        {
            LogOpenShopDecision(
                request,
                stage: "ownership-required",
                shopRootPath,
                shareName,
                needsOwnership: true,
                canRepair: aclRepair.CanRepairWithOwnershipChange,
                enumerationBlocked: aclRepair.EnumerationBlocked,
                blockedCount: aclRepair.BlockedPaths.Count);
            if (!SmbNtfsManager.TakeOwnershipRecursive(shopRootPath))
            {
                string message = aclRepair.BlockedPaths.Count > 0
                    ? "このフォルダーの一部のファイルはアクセス設定を整えられないため、お店として開けません。"
                    : "お店のアクセス設定を整えられませんでした。";
                return Fail(request, AdminErrorCode.OwnershipRepairFailed, message, aclRepair.BlockedPaths);
            }
        }
        else if (!SmbNtfsManager.TakeOwnershipRecursive(shopRootPath))
        {
            LogOpenShopDecision(
                request,
                stage: "ownership-align-failed",
                shopRootPath,
                shareName,
                needsOwnership: false,
                canRepair: aclRepair.CanRepairWithOwnershipChange,
                enumerationBlocked: aclRepair.EnumerationBlocked,
                blockedCount: aclRepair.BlockedPaths.Count);
            return Fail(request, AdminErrorCode.OwnershipRepairFailed, "お店のアクセス設定を整えられませんでした。");
        }

        if (!SmbNtfsManager.IsolateShopRoot(shopRootPath))
        {
            return Fail(request, AdminErrorCode.OwnershipRepairFailed, "お店の場所の準備ができませんでした。");
        }

        bool ourShareAlreadyExists = SmbShareManager.ListShareWorkinShares()
            .Any(s => string.Equals(s.Name, shareName, StringComparison.OrdinalIgnoreCase));
        LogOpenShopDecision(
            request,
            stage: "shareworkin-share-scan",
            shopRootPath,
            shareName,
            ourShareAlreadyExists: ourShareAlreadyExists,
            applyPermissionsOnOpen: request.ApplyPermissionsOnOpen,
            permissionEntryCount: request.PermissionEntries?.Count ?? 0);

        if (!ourShareAlreadyExists)
        {
            bool sameNamedShareExists = SmbShareManager.ShareExists(shareName);
            LogOpenShopDecision(
                request,
                stage: "share-name-scan",
                shopRootPath,
                shareName,
                ourShareAlreadyExists: false,
                sameNamedShareExists: sameNamedShareExists);
            if (sameNamedShareExists)
            {
                SmbNtfsManager.RevokeSwkGuest(shopRootPath);
                return Fail(request, AdminErrorCode.ShareNameConflict, "この名前は別のお店で使われているようです。お店の名前を変えてください。");
            }

            LogOpenShopDecision(
                request,
                stage: "create-share",
                shopRootPath,
                shareName,
                ourShareAlreadyExists: false,
                sameNamedShareExists: false);
            if (!SmbShareManager.CreateShare(shareName, shopRootPath, request.ProfileLabel ?? shareName))
            {
                SmbNtfsManager.RevokeSwkGuest(shopRootPath);
                return Fail(request, AdminErrorCode.ShareCreateFailed, "お店を開くのに失敗しました。");
            }
        }
        else
        {
            LogOpenShopDecision(
                request,
                stage: "repair-share",
                shopRootPath,
                shareName,
                ourShareAlreadyExists: true);
            if (!SmbShareManager.RepairShareDefinition(shareName, shopRootPath, request.ProfileLabel ?? shareName))
            {
                SmbNtfsManager.RevokeSwkGuest(shopRootPath);
                return Fail(request, AdminErrorCode.ShareRepairFailed, "お店の入口を整え直せませんでした。");
            }
        }

        SmbShareManager.RevokeEveryone(shareName);
        ShareAccessRight accessRight = request.AccessRight == 1 ? ShareAccessRight.Read : ShareAccessRight.Full;
        LogOpenShopDecision(
            request,
            stage: "grant-swkguest",
            shopRootPath,
            shareName,
            ourShareAlreadyExists: ourShareAlreadyExists);
        if (!SmbShareManager.GrantSwkGuest(shareName, accessRight))
        {
            if (!ourShareAlreadyExists)
            {
                SmbShareManager.RemoveShare(shareName);
            }

            SmbNtfsManager.RevokeSwkGuest(shopRootPath);
            return Fail(request, AdminErrorCode.ShareGrantFailed, "お店の入り口の用意に失敗しました。");
        }

        if (request.ApplyPermissionsOnOpen)
        {
            LogOpenShopDecision(
                request,
                stage: "apply-permissions-on-open",
                shopRootPath,
                shareName,
                applyPermissionsOnOpen: true,
                permissionEntryCount: request.PermissionEntries?.Count ?? 0);
            AdminCommandResponse? permResult = ApplyPermissionsOnOpen(request, shopRootPath, request.PermissionEntries);
            if (permResult != null) return permResult;
        }

        SwkLogger.Info(
            $"AdminWorker.OpenShop ok corr={request.CorrelationId} root={shopRootPath} share={shareName}");
        return Ok(request);
    }

    private static AdminCommandResponse? ApplyPermissionsOnOpen(
        AdminCommandRequest request,
        string shopRootPath,
        List<PermissionRestoreEntry>? entries)
    {
        const string holdFolderName = "保留";
        var permFailed = new List<string>();
        var resetFailed = new List<string>();

        if (entries != null)
        {
            var entryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PermissionRestoreEntry entry in entries)
            {
                if (!SharePathPolicy.TryNormalizeLocalPath(entry.Path, out string entryPath)) continue;
                if (!SharePathPolicy.IsUnderRoot(entryPath, shopRootPath))
                {
                    SwkLogger.Warn($"ApplyPermissionsOnOpen: entry not under shop root, skipped: {entryPath}");
                    continue;
                }
                if (!Directory.Exists(entryPath)) continue;
                entryPaths.Add(entryPath);
                if (!SmbNtfsManager.SetSubfolderPermission(entryPath, entry.IsSharedOff, entry.IsReadOnly))
                    permFailed.Add(entryPath);
            }
            foreach (string dir in Directory.EnumerateDirectories(shopRootPath))
            {
                if (entryPaths.Contains(dir)) continue;
                if (string.Equals(Path.GetFileName(dir), holdFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!SmbNtfsManager.ResetPathToInherited(dir))
                    resetFailed.Add(dir);
            }
        }
        else
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(shopRootPath))
            {
                if (string.Equals(Path.GetFileName(entry), holdFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!SmbNtfsManager.ResetPathToInherited(entry))
                    resetFailed.Add(entry);
            }
        }

        if (permFailed.Count > 0)
            return Fail(request, AdminErrorCode.PermissionApplyFailed, "権限の設定に失敗したフォルダーがあります。", permFailed);
        if (resetFailed.Count > 0)
            return Fail(request, AdminErrorCode.ResetInheritanceFailed, "継承設定の復元に失敗したフォルダーがあります。", resetFailed);
        return null;
    }

    private static AdminCommandResponse CloseShop(AdminCommandRequest request)
    {
        string shareName = request.ShareName?.Trim() ?? string.Empty;
        if (!SharePathPolicy.ValidateShareName(shareName, out string shareNameError))
        {
            return Fail(request, AdminErrorCode.ValidationFailed, shareNameError);
        }

        if (!SharePathPolicy.TryNormalizeLocalPath(request.ShopRootPath, out string shopRootPath))
        {
            return Fail(request, AdminErrorCode.ValidationFailed, "Shop root path is invalid.");
        }

        SwkLogger.Info(
            $"AdminWorker.CloseShop start corr={request.CorrelationId} root={shopRootPath} share={shareName}");

        bool removeOk = SmbShareManager.RemoveShare(shareName);
        bool revokeOk = SmbNtfsManager.RevokeSwkGuest(shopRootPath);
        bool restoreOk = SmbNtfsManager.RestoreInheritance(shopRootPath);
        if (!removeOk || !revokeOk || !restoreOk)
        {
            string detail = $"remove={removeOk}, revoke={revokeOk}, restore={restoreOk}";
            return Fail(request, AdminErrorCode.CloseShopFailed, $"共有終了処理に失敗しました。({detail})");
        }

        SwkLogger.Info(
            $"AdminWorker.CloseShop ok corr={request.CorrelationId} root={shopRootPath} share={shareName}");
        return Ok(request);
    }

    private static AdminCommandResponse SetSubfolderPermission(AdminCommandRequest request)
    {
        if (!TryValidateManagedPath(request, out string shopRootPath, out string targetPath))
        {
            return Fail(request, AdminErrorCode.PathNotAllowed, "Target path is not managed by the current shop.");
        }

        SwkLogger.Info(
            $"AdminWorker.SetSubfolderPermission start corr={request.CorrelationId} root={shopRootPath} target={targetPath} off={request.IsSharedOff} ro={request.IsReadOnly}");

        if (!SmbNtfsManager.SetSubfolderPermission(targetPath, request.IsSharedOff, request.IsReadOnly))
        {
            return Fail(request, AdminErrorCode.PermissionApplyFailed, "権限の設定に失敗しました。");
        }

        SwkLogger.Info(
            $"AdminWorker.SetSubfolderPermission ok corr={request.CorrelationId} root={shopRootPath} target={targetPath}");
        return Ok(request);
    }

    private static AdminCommandResponse ResetPathToInherited(AdminCommandRequest request)
    {
        if (!TryValidateManagedPath(request, out string shopRootPath, out string targetPath))
        {
            return Fail(request, AdminErrorCode.PathNotAllowed, "Target path is not managed by the current shop.");
        }

        SwkLogger.Info(
            $"AdminWorker.ResetPathToInherited start corr={request.CorrelationId} root={shopRootPath} target={targetPath}");

        if (!SmbNtfsManager.ResetPathToInherited(targetPath))
        {
            return Fail(request, AdminErrorCode.ResetInheritanceFailed, "継承設定の復元に失敗しました。");
        }

        SwkLogger.Info(
            $"AdminWorker.ResetPathToInherited ok corr={request.CorrelationId} root={shopRootPath} target={targetPath}");
        return Ok(request);
    }

    private static AdminCommandResponse MarkActionAftercare(AdminCommandRequest request)
    {
        if (!TryValidateManagedPath(request, out string shopRootPath, out string affectedPath))
        {
            return Fail(request, AdminErrorCode.PathNotAllowed, "Aftercare target path is not managed by the current shop.");
        }

        if (!SharePathPolicy.TryNormalizeLocalPath(request.PolicySourceFolder, out string policySourceFolder) ||
            !SharePathPolicy.IsUnderRoot(policySourceFolder, shopRootPath))
        {
            return Fail(request, AdminErrorCode.PathNotAllowed, "Policy source path is not managed by the current shop.");
        }

        if (!Enum.TryParse(request.Reason, ignoreCase: true, out SharePolicyRepairReason reason))
        {
            return Fail(request, AdminErrorCode.ValidationFailed, "Aftercare reason is invalid.");
        }

        SwkLogger.Info(
            $"AdminWorker.MarkActionAftercare start corr={request.CorrelationId} root={shopRootPath} affected={affectedPath} source={policySourceFolder} reason={reason}");

        SharePolicyRepair.MarkActionAftercare(shopRootPath, affectedPath, policySourceFolder, reason);
        SwkLogger.Info(
            $"AdminWorker.MarkActionAftercare ok corr={request.CorrelationId} root={shopRootPath} affected={affectedPath}");
        return Ok(request);
    }

    private static bool TryValidateManagedPath(
        AdminCommandRequest request,
        out string shopRootPath,
        out string targetPath)
    {
        shopRootPath = string.Empty;
        targetPath = string.Empty;

        if (!SharePathPolicy.TryNormalizeLocalPath(request.ShopRootPath, out shopRootPath) ||
            !SharePathPolicy.TryNormalizeLocalPath(request.TargetPath, out targetPath))
        {
            return false;
        }

        return SharePathPolicy.IsUnderRoot(targetPath, shopRootPath);
    }

    private static AdminCommandResponse Ok(AdminCommandRequest request) => new()
    {
        Ok = true,
        CorrelationId = request.CorrelationId,
        ErrorCode = AdminErrorCode.None
    };

    private static AdminCommandResponse Fail(
        AdminCommandRequest request,
        AdminErrorCode errorCode,
        string message,
        IReadOnlyList<string>? blockedPaths = null)
    {
        LogFailure(request, errorCode, message, blockedPaths);
        return new AdminCommandResponse
        {
            Ok = false,
            CorrelationId = request.CorrelationId,
            ErrorCode = errorCode,
            ErrorMessage = message,
            BlockedPaths = blockedPaths?.ToList()
        };
    }

    private static void LogFailure(
        AdminCommandRequest request,
        AdminErrorCode errorCode,
        string message,
        IReadOnlyList<string>? blockedPaths)
    {
        int blockedCount = blockedPaths?.Count ?? 0;
        string sample = blockedPaths == null
            ? string.Empty
            : string.Join(" | ", blockedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Take(5));
        SwkLogger.Warn(
            $"AdminWorker.Fail corr={request.CorrelationId} cmd={request.Cmd} code={errorCode} blockedCount={blockedCount} message={message} sample={sample}");
    }
}
