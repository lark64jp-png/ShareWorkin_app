using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

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
    private static DateTime? _remoteVisibilityAvailableSince;
    private static DateTime? _remoteDiscoveryMissingSince;
    private static bool _warningActive;

    public static event Action? Updated;
    public static event Action<SwkNetworkHealthStatus>? StatusChanged;

    public static void RecordIncomingProbe(string? clientMachineName, IPEndPoint remoteEndPoint)
    {
        if (string.Equals(clientMachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
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
                _remoteVisibilityAvailableSince ??= now;
                _remoteDiscoveryMissingSince = null;
            }
            else
            {
                _remoteVisibilityAvailableSince = null;
                _remoteDiscoveryMissingSince = expectedPeerCount > 0
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
            bool canSeeOthers = hasRecentScan && _lastOutgoingDiscoveryCount > 0;
            DateTime? incomingReference = _lastIncomingProbeAt.HasValue && _remoteVisibilityAvailableSince.HasValue
                ? (_lastIncomingProbeAt.Value > _remoteVisibilityAvailableSince.Value
                    ? _lastIncomingProbeAt.Value
                    : _remoteVisibilityAvailableSince.Value)
                : _lastIncomingProbeAt ?? _remoteVisibilityAvailableSince;
            bool inboundIssue = canSeeOthers &&
                                incomingReference.HasValue &&
                                now - incomingReference.Value > IncomingProbeWarningThreshold;
            bool outboundIssue = hasRecentScan &&
                                 _expectedPeerCount > 0 &&
                                 _lastOutgoingDiscoveryCount == 0 &&
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
