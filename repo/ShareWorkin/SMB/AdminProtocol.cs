using System.Collections.Generic;

namespace ShareWorkin.SMB;

public static class AdminProtocol
{
    public const string HelperProcessName = "ShareWorkinAdminWorker";
    public const string TrayProcessName = "ShareWorkinTray";

    public const string PingCommand = "PING";
    public const string OpenShopCommand = "OPEN_SHOP_ADMIN";
    public const string CloseShopCommand = "CLOSE_SHOP_ADMIN";
    public const string SetSubfolderPermissionCommand = "SET_SUBFOLDER_PERMISSION_ADMIN";
    public const string ResetPathToInheritedCommand = "RESET_PATH_TO_INHERITED_ADMIN";
    public const string MarkActionAftercareCommand = "MARK_ACTION_AFTERCARE_ADMIN";
}

public enum AdminErrorCode
{
    None,
    ValidationFailed,
    UnauthorizedClient,
    PathNotAllowed,
    HelperUnavailable,
    InfrastructureFailed,
    AccountFailed,
    OwnershipRepairFailed,
    ShareNameConflict,
    ShareCreateFailed,
    ShareRepairFailed,
    ShareGrantFailed,
    CloseShopFailed,
    PermissionApplyFailed,
    ResetInheritanceFailed,
    AftercareFailed,
    InternalError,
}

public sealed class PermissionRestoreEntry
{
    public string Path { get; set; } = string.Empty;
    public bool IsSharedOff { get; set; }
    public bool IsReadOnly { get; set; }
}

public sealed class AdminCommandRequest
{
    public string Cmd { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string? ShopRootPath { get; set; }
    public string? ShareName { get; set; }
    public string? ProfileLabel { get; set; }
    public int AccessRight { get; set; }
    public string? TargetPath { get; set; }
    public bool IsSharedOff { get; set; }
    public bool IsReadOnly { get; set; }
    public string? PolicySourceFolder { get; set; }
    public string? Reason { get; set; }
    public bool ApplyPermissionsOnOpen { get; set; }
    // null = normalize all top-level entries; non-null = apply listed entries + reset unmapped top-level dirs
    public List<PermissionRestoreEntry>? PermissionEntries { get; set; }
}

public sealed class AdminCommandResponse
{
    public bool Ok { get; set; }
    public AdminErrorCode ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public List<string>? BlockedPaths { get; set; }
}
