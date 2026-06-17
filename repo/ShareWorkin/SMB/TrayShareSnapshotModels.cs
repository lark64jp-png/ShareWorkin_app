using System;
using System.Collections.Generic;

namespace ShareWorkin.SMB;

public enum TrayCommandResultCode
{
    Success,
    FallbackSuccess,
    TimedOut,
    Failed,
    InternalError,
}

public enum TrayCommandErrorReason
{
    None,
    TrayUnavailable,
    Timeout,
    AccessDenied,
    TargetMismatch,
    Cancelled,
    InternalException,
    Unknown,
    ShareNotFound,
    InvalidRequest,
    WindowsQueryFailed,
}

public sealed record TrayCommandResponse<TPayload>(
    string? RequestId,
    TrayCommandResultCode ResultCode,
    TrayCommandErrorReason ErrorReason = TrayCommandErrorReason.None,
    bool Retryable = false,
    bool JoinedInflight = false,
    string? Message = null,
    TPayload? Payload = default);

public sealed record GetShareSnapshotRequest(
    string? RequestId,
    string? ShareName,
    string? ShopRootPath,
    bool ForceRefresh);

public sealed record ShareSnapshotAccessEntry(
    string AccountName,
    string AccessControlType,
    string AccessRight);

public sealed record ShareSnapshotPayload(
    DateTime ObservedAtUtc,
    string Source,
    bool IsStale,
    IReadOnlyList<string> DirtyReasons,
    string? RequestedShareName,
    string? RequestedShopRootPath,
    string? TrayShareName,
    string? TrayShopRootPath,
    string? TrayAccessRight,
    string? WindowsShareName,
    string? WindowsSharePath,
    string? DescriptionLabel,
    string? ShareState,
    uint? CurrentUsers,
    bool HasMatchingShare,
    bool ShareNameMatches,
    bool ShopRootPathMatches,
    string? EffectiveAccessRight,
    IReadOnlyList<ShareSnapshotAccessEntry> AccessEntries);
