using System;

namespace ShareWorkin.SMB;

public static class FriendShareAccessTracker
{
    public static bool IsVerifiedFor(Friend friend, SwkNotificationListener.ShopInfo liveShop)
    {
        if (string.IsNullOrWhiteSpace(friend.LastShareAccessVerifiedAt))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(friend.LastShareAccessSwkInstanceId) &&
            !string.IsNullOrWhiteSpace(liveShop.SwkInstanceId))
        {
            return string.Equals(
                friend.LastShareAccessSwkInstanceId,
                liveShop.SwkInstanceId,
                StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(friend.LastShareAccessHost))
        {
            return string.Equals(
                NormalizeHost(friend.LastShareAccessHost),
                NormalizeHost(liveShop.MachineName),
                StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public static void MarkVerified(Friend friend, SwkNotificationListener.ShopInfo liveShop)
    {
        friend.LastShareAccessVerifiedAt = DateTime.UtcNow.ToString("o");
        friend.LastShareAccessHost = liveShop.MachineName;
        friend.LastShareAccessSwkInstanceId = liveShop.SwkInstanceId;
        friend.LastAccessIssue = null;
    }

    public static void MarkVerified(Friend friend)
    {
        friend.LastShareAccessVerifiedAt = DateTime.UtcNow.ToString("o");
        friend.LastShareAccessHost = friend.HostMachineName;
        friend.LastShareAccessSwkInstanceId = friend.RemoteSwkInstanceId;
        friend.LastAccessIssue = null;
    }

    public static void ClearVerified(Friend friend)
    {
        friend.LastShareAccessVerifiedAt = null;
        friend.LastShareAccessHost = null;
        friend.LastShareAccessSwkInstanceId = null;
    }

    private static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        string trimmed = host.Trim();
        int dot = trimmed.IndexOf('.');
        return dot > 0 ? trimmed[..dot] : trimmed;
    }
}
