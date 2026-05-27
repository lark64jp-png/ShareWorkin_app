using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShareWorkin.SMB;

public static class FriendRecognitionService
{
    public const string OwnerCertificateMismatchMessage =
        "店主の証明書が以前と違います。乗っ取りの可能性があるため接続を中止しました。";

    public static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return string.Empty;
        }

        string trimmed = host.Trim();
        int dot = trimmed.IndexOf('.');
        return dot > 0 ? trimmed[..dot] : trimmed;
    }

    public static bool SameSwkInstance(Friend friend, SwkNotificationListener.ShopInfo shopInfo) =>
        !string.IsNullOrWhiteSpace(friend.RemoteSwkInstanceId) &&
        !string.IsNullOrWhiteSpace(shopInfo.SwkInstanceId) &&
        string.Equals(friend.RemoteSwkInstanceId, shopInfo.SwkInstanceId, StringComparison.OrdinalIgnoreCase);

    public static bool IsCompatibleLiveShopForFriend(Friend friend, SwkNotificationListener.ShopInfo liveShop)
    {
        if (!string.IsNullOrWhiteSpace(friend.RemoteSwkInstanceId) &&
            !string.IsNullOrWhiteSpace(liveShop.SwkInstanceId))
        {
            bool sameShare = string.Equals(friend.ShareName, liveShop.ShareName, StringComparison.OrdinalIgnoreCase);
            return (SameSwkInstance(friend, liveShop) && sameShare) ||
                   IsRelinkCandidateLiveShopForFriend(friend, liveShop);
        }

        return MatchesHostAndShare(friend, liveShop) || MatchesIpAndShare(friend, liveShop);
    }

    public static SwkNotificationListener.ShopInfo? FindLiveShopForFriend(
        Friend friend,
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos)
    {
        if (!string.IsNullOrWhiteSpace(friend.RemoteSwkInstanceId))
        {
            SwkNotificationListener.ShopInfo? byId = shopInfos.FirstOrDefault(s =>
                SameSwkInstance(friend, s) &&
                string.Equals(s.ShareName, friend.ShareName, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }

            SwkNotificationListener.ShopInfo? fallback = FindByHostOrAddressAndShare(friend, shopInfos);
            return string.IsNullOrWhiteSpace(fallback?.SwkInstanceId) ? fallback : null;
        }

        return FindByHostOrAddressAndShare(friend, shopInfos);
    }

    public static SwkNotificationListener.ShopInfo? FindVisibleShopForFriend(
        Friend friend,
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos)
    {
        SwkNotificationListener.ShopInfo? live = FindLiveShopForFriend(friend, shopInfos);
        if (live is not null)
        {
            return live;
        }

        if (string.IsNullOrWhiteSpace(friend.ShareName))
        {
            return null;
        }

        List<SwkNotificationListener.ShopInfo> sameShare = shopInfos
            .Where(s => string.Equals(s.ShareName, friend.ShareName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return sameShare.Count == 1 ? sameShare[0] : null;
    }

    public static SwkNotificationListener.ShopInfo? FindRelinkCandidateForFriend(
        Friend friend,
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos)
    {
        if (string.IsNullOrWhiteSpace(friend.RemoteSwkInstanceId))
        {
            return null;
        }

        SwkNotificationListener.ShopInfo? fallback = FindByHostOrAddressAndShare(friend, shopInfos);
        if (fallback is null || string.IsNullOrWhiteSpace(fallback.SwkInstanceId))
        {
            return null;
        }

        return SameSwkInstance(friend, fallback) ? null : fallback;
    }

    public static bool IsRelinkCandidateLiveShopForFriend(
        Friend friend,
        SwkNotificationListener.ShopInfo liveShop)
    {
        if (string.IsNullOrWhiteSpace(friend.RemoteSwkInstanceId) ||
            string.IsNullOrWhiteSpace(liveShop.SwkInstanceId))
        {
            return false;
        }

        return !SameSwkInstance(friend, liveShop) &&
               (MatchesHostAndShare(friend, liveShop) || MatchesIpAndShare(friend, liveShop));
    }

    public static bool ShouldReplaceExistingRegistration(Friend existing, Friend incoming)
    {
        if (!string.IsNullOrWhiteSpace(incoming.RemoteSwkInstanceId))
        {
            return string.Equals(existing.RemoteSwkInstanceId, incoming.RemoteSwkInstanceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.ShareName, incoming.ShareName, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(NormalizeHost(existing.HostMachineName), NormalizeHost(incoming.HostMachineName), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.ShareName, incoming.ShareName, StringComparison.OrdinalIgnoreCase);
    }

    public static Friend? ResolveIncomingInteractionFriend(
        IReadOnlyList<Friend> friends,
        string? senderSwkInstanceId,
        string? senderMachineName,
        string? senderShareName)
    {
        if (!string.IsNullOrWhiteSpace(senderSwkInstanceId))
        {
            List<Friend> byInstance = friends
                .Where(friend => string.Equals(friend.RemoteSwkInstanceId, senderSwkInstanceId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (!string.IsNullOrWhiteSpace(senderShareName))
            {
                byInstance = byInstance
                    .Where(friend => string.Equals(friend.ShareName, senderShareName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (byInstance.Count == 1)
            {
                return byInstance[0];
            }
        }

        if (!string.IsNullOrWhiteSpace(senderMachineName) &&
            !string.IsNullOrWhiteSpace(senderShareName))
        {
            List<Friend> byHostAndShare = friends
                .Where(friend =>
                    string.Equals(NormalizeHost(friend.HostMachineName), NormalizeHost(senderMachineName), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(friend.ShareName, senderShareName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byHostAndShare.Count == 1)
            {
                return byHostAndShare[0];
            }
        }

        return null;
    }

    public static async Task<RefreshExistingFriendResult> RefreshExistingFriendAsync(
        Friend friend,
        SwkNotificationListener.ShopInfo liveShop,
        CancellationToken cancellationToken)
    {
        if (!IsCompatibleLiveShopForFriend(friend, liveShop))
        {
            return new RefreshExistingFriendResult
            {
                ErrorMessage = "接続先の照合に失敗しました。"
            };
        }

        var listener = new SwkNotificationListener();
        string? expectedThumbprint = string.IsNullOrWhiteSpace(friend.OwnerCertThumbprint)
            ? null
            : friend.OwnerCertThumbprint;

        SwkNotificationListener.InviteCodeResult result = await listener.RequestInviteCodeAsync(
            liveShop,
            inviteId: null,
            expectedThumbprint: expectedThumbprint,
            cancellationToken);

        if ((!result.Success || string.IsNullOrWhiteSpace(result.Password)) &&
            string.Equals(result.ErrorMessage, OwnerCertificateMismatchMessage, StringComparison.Ordinal))
        {
            friend.LastAccessIssue = Friend.AccessIssueCertMismatch;
            if (!FriendShareAccessTracker.IsVerifiedFor(friend, liveShop))
            {
                return new RefreshExistingFriendResult
                {
                    ErrorMessage = result.ErrorMessage
                };
            }

            result = await listener.RequestInviteCodeAsync(
                liveShop,
                inviteId: null,
                expectedThumbprint: null,
                cancellationToken);
            if (!result.Success ||
                string.IsNullOrWhiteSpace(result.Password) ||
                string.IsNullOrWhiteSpace(result.CertThumbprint))
            {
                return new RefreshExistingFriendResult
                {
                    ErrorMessage = result.ErrorMessage ?? "接続情報を更新できませんでした。"
                };
            }
        }
        else if (!result.Success || string.IsNullOrWhiteSpace(result.Password))
        {
            return new RefreshExistingFriendResult
            {
                ErrorMessage = result.ErrorMessage ?? "接続情報を更新できませんでした。"
            };
        }

        string nowIso = DateTime.UtcNow.ToString("o");
        friend.HostMachineName = liveShop.MachineName;
        friend.ShareName = liveShop.ShareName;
        friend.PasswordProtected = FriendsRepository.ProtectPassword(result.Password);
        if (!string.IsNullOrWhiteSpace(result.CertThumbprint))
        {
            friend.OwnerCertThumbprint = result.CertThumbprint;
        }

        if (!string.IsNullOrWhiteSpace(result.SwkInstanceId))
        {
            friend.RemoteSwkInstanceId = result.SwkInstanceId;
        }
        else if (!string.IsNullOrWhiteSpace(liveShop.SwkInstanceId))
        {
            friend.RemoteSwkInstanceId = liveShop.SwkInstanceId;
        }

        friend.LastKnownAddress = liveShop.IpAddress ?? string.Empty;
        friend.LastFoundAt = nowIso;
        friend.LastCheckedAt = nowIso;
        friend.LastSeenAt = nowIso;
        friend.LastAccessIssue = null;
        FriendShareAccessTracker.ClearVerified(friend);
        return new RefreshExistingFriendResult();
    }

    private static SwkNotificationListener.ShopInfo? FindByHostOrAddressAndShare(
        Friend friend,
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos)
    {
        SwkNotificationListener.ShopInfo? byHostAndShare = shopInfos.FirstOrDefault(s => MatchesHostAndShare(friend, s));
        if (byHostAndShare is not null)
        {
            return byHostAndShare;
        }

        return shopInfos.FirstOrDefault(s => MatchesIpAndShare(friend, s));
    }

    private static bool MatchesHostAndShare(Friend friend, SwkNotificationListener.ShopInfo shopInfo) =>
        !string.IsNullOrWhiteSpace(friend.ShareName) &&
        string.Equals(NormalizeHost(friend.HostMachineName), NormalizeHost(shopInfo.MachineName), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(friend.ShareName, shopInfo.ShareName, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesIpAndShare(Friend friend, SwkNotificationListener.ShopInfo shopInfo) =>
        !string.IsNullOrWhiteSpace(friend.ShareName) &&
        !string.IsNullOrWhiteSpace(friend.LastKnownAddress) &&
        string.Equals(friend.LastKnownAddress, shopInfo.IpAddress, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(friend.ShareName, shopInfo.ShareName, StringComparison.OrdinalIgnoreCase);
}

public sealed class RefreshExistingFriendResult
{
    public string? ErrorMessage { get; init; }
    public bool Success => string.IsNullOrWhiteSpace(ErrorMessage);
}
