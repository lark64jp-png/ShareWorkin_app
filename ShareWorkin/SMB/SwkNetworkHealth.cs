using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace ShareWorkin.SMB;

public static class SwkNetworkHealth
{
    private static readonly object _lock = new();
    private static readonly TimeSpan IncomingProbeWarningThreshold = TimeSpan.FromSeconds(45);
    private static DateTime? _lastIncomingProbeAt;
    private static string? _lastIncomingProbeMachineName;
    private static string? _lastIncomingProbeAddress;
    private static DateTime? _lastOutgoingDiscoveryAt;
    private static int _lastOutgoingDiscoveryCount;
    private static int _expectedPeerCount;
    private static DateTime? _lastVisibleRemoteShopAt;
    private static DateTime? _remoteDiscoveryMissingSince;
    private static bool _warningActive;

    public static event Action? Updated;
    public static event Action<SwkNetworkHealthStatus>? StatusChanged;

    public static void RecordIncomingProbe(string? clientMachineName, IPEndPoint remoteEndPoint)
    {
        if (IsSelfProbe(clientMachineName, remoteEndPoint))
        {
            return;
        }

        lock (_lock)
        {
            _lastIncomingProbeAt = DateTime.Now;
            _lastIncomingProbeMachineName = clientMachineName;
            _lastIncomingProbeAddress = remoteEndPoint.Address.ToString();
        }

        EvaluateTransition();
        Updated?.Invoke();
    }

    public static void RecordDiscoverySnapshot(IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos)
    {
        DateTime now = DateTime.Now;
        int visibleRemoteShopCount = shopInfos.Count(static shop =>
            !string.Equals(shop.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase));
        int expectedPeerCount = FriendsRepository.LoadAll().Count;

        lock (_lock)
        {
            _lastOutgoingDiscoveryAt = now;
            _lastOutgoingDiscoveryCount = visibleRemoteShopCount;
            _expectedPeerCount = expectedPeerCount;

            if (visibleRemoteShopCount > 0)
            {
                _lastVisibleRemoteShopAt = now;
                _remoteDiscoveryMissingSince = null;
            }
            else
            {
                bool lostRecently = _lastVisibleRemoteShopAt.HasValue &&
                    now - _lastVisibleRemoteShopAt.Value <= IncomingProbeWarningThreshold;
                _remoteDiscoveryMissingSince = expectedPeerCount > 0 && !lostRecently
                    ? _remoteDiscoveryMissingSince ?? now
                    : null;
            }
        }

        EvaluateTransition();
        Updated?.Invoke();
    }

    public static SwkNetworkHealthStatus GetStatus()
    {
        lock (_lock)
        {
            DateTime now = DateTime.Now;
            bool hasRecentScan = _lastOutgoingDiscoveryAt.HasValue &&
                                 now - _lastOutgoingDiscoveryAt.Value <= IncomingProbeWarningThreshold;
            bool hasRecentVisibleRemote = _lastVisibleRemoteShopAt.HasValue &&
                                          now - _lastVisibleRemoteShopAt.Value <= IncomingProbeWarningThreshold;
            bool canSeeOthers = hasRecentScan && hasRecentVisibleRemote;
            bool inboundIssue = canSeeOthers &&
                                _lastIncomingProbeAt.HasValue &&
                                now - _lastIncomingProbeAt.Value > IncomingProbeWarningThreshold;
            bool outboundIssue = hasRecentScan &&
                                 _expectedPeerCount > 0 &&
                                 _remoteDiscoveryMissingSince.HasValue &&
                                 now - _remoteDiscoveryMissingSince.Value > IncomingProbeWarningThreshold;

            if (!inboundIssue && !outboundIssue)
            {
                return SwkNetworkHealthStatus.None;
            }

            string incomingAgeText = _lastIncomingProbeAt.HasValue
                ? $"{Math.Max(1, (int)Math.Round((now - _lastIncomingProbeAt.Value).TotalMinutes))}分"
                : "起動後まだ";
            string incomingTail = string.IsNullOrWhiteSpace(_lastIncomingProbeMachineName)
                ? string.Empty
                : $" 直近受信: {_lastIncomingProbeMachineName} {_lastIncomingProbeAddress}";
            string outboundAgeText = _remoteDiscoveryMissingSince.HasValue
                ? $"{Math.Max(1, (int)Math.Round((now - _remoteDiscoveryMissingSince.Value).TotalMinutes))}分"
                : "起動後まだ";
            string title = inboundIssue && outboundIssue
                ? "このPCの接続状態を確認してください"
                : inboundIssue
                    ? "このPCは他のPCから見つかりにくい可能性があります"
                    : "このPCから他のPCを見つけにくい可能性があります";
            string detail = inboundIssue && outboundIssue
                ? "相手を見つけられない状態と、相手から探されにくい状態が続いています。省電力 / Wi-Fi / ファイアウォールを確認してください。"
                : inboundIssue
                    ? $"自分は相手を見つけていますが、相手からの探査受信が {incomingAgeText} ありません。省電力 / Wi-Fi / ファイアウォールを確認してください。{incomingTail}".Trim()
                    : $"登録済みの相手がいるのに、このPCから見つけられる共有が {outboundAgeText} ありません。Wi-Fi / ネットワークプロファイル / ファイアウォールを確認してください。";

            return new SwkNetworkHealthStatus(true, title, detail)
            {
                HasInboundIssue = inboundIssue,
                HasOutboundIssue = outboundIssue,
                ExpectedPeerCount = _expectedPeerCount,
                VisibleRemoteShopCount = _lastOutgoingDiscoveryCount,
                LastIncomingProbeMachineName = _lastIncomingProbeMachineName,
                LastIncomingProbeAddress = _lastIncomingProbeAddress,
                LastIncomingProbeAt = _lastIncomingProbeAt,
                RemoteDiscoveryMissingSince = _remoteDiscoveryMissingSince,
            };
        }
    }

    private static void EvaluateTransition()
    {
        SwkNetworkHealthStatus status = GetStatus();
        bool shouldWarn = status.HasWarning;
        bool changed;

        lock (_lock)
        {
            changed = shouldWarn != _warningActive;
            _warningActive = shouldWarn;
        }

        if (!changed)
        {
            return;
        }

        if (shouldWarn)
        {
            SwkLogger.Warn($"NetworkHealthWarning: {status.Detail}");
            StatusChanged?.Invoke(status);
            return;
        }

        SwkLogger.Info("NetworkHealthRecovered: incoming probe visibility restored");
        StatusChanged?.Invoke(new SwkNetworkHealthStatus(
            false,
            "このPCの見え方が回復しました。",
            "他のPCからの探査受信が再開しました。"));
    }

    private static bool IsSelfProbe(string? clientMachineName, IPEndPoint remoteEndPoint)
    {
        IPAddress address = remoteEndPoint.Address;
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (!IsLocalAddress(address))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(clientMachineName) ||
               string.Equals(clientMachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalAddress(IPAddress address)
    {
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Any(local => NormalizeAddress(local).Equals(NormalizeAddress(address)));
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

public sealed record SwkNetworkHealthStatus(
    bool HasWarning,
    string Title,
    string Detail)
{
    public static SwkNetworkHealthStatus None { get; } = new(false, string.Empty, string.Empty);

    public bool HasInboundIssue { get; init; }
    public bool HasOutboundIssue { get; init; }
    public int ExpectedPeerCount { get; init; }
    public int VisibleRemoteShopCount { get; init; }
    public string? LastIncomingProbeMachineName { get; init; }
    public string? LastIncomingProbeAddress { get; init; }
    public DateTime? LastIncomingProbeAt { get; init; }
    public DateTime? RemoteDiscoveryMissingSince { get; init; }
}
