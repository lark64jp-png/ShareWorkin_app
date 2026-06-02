using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ShareWorkin.SMB;

public enum ScanMode { Quick, Full }

public static class SwkNetworkCache
{
    private static readonly object _lock = new();
    private static readonly SemaphoreSlim _scanLock = new(1, 1);
    private static IReadOnlyList<LanCandidate> _candidates = Array.Empty<LanCandidate>();
    private static IReadOnlyList<SwkNotificationListener.ShopInfo> _shopInfos = Array.Empty<SwkNotificationListener.ShopInfo>();
    private static DateTime? _lastScanAt;
    private static ScanMode _lastScanMode = ScanMode.Quick;

    public static IReadOnlyList<LanCandidate> Candidates { get { lock (_lock) return _candidates; } }
    public static IReadOnlyList<SwkNotificationListener.ShopInfo> ShopInfos { get { lock (_lock) return _shopInfos; } }
    public static DateTime? LastScanAt { get { lock (_lock) return _lastScanAt; } }
    public static ScanMode LastScanMode { get { lock (_lock) return _lastScanMode; } }
    public static bool IsReady => LastScanAt.HasValue;
    public static bool IsScanning => _scanLock.CurrentCount == 0;

    public static event Action? Updated;

    public static void Update(
        IReadOnlyList<LanCandidate> candidates,
        IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos,
        ScanMode mode)
    {
        lock (_lock)
        {
            _candidates = candidates;
            _shopInfos = shopInfos;
            _lastScanAt = DateTime.Now;
            _lastScanMode = mode;
        }
        SwkLogger.Info($"SwkNetworkCache updated: candidates={candidates.Count} shopInfos={shopInfos.Count} mode={mode}");
        Updated?.Invoke();
    }

    public static void RemoveShop(string machineName, string shareName)
    {
        lock (_lock)
        {
            var updated = _shopInfos
                .Where(s => !(string.Equals(s.MachineName, machineName, StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(s.ShareName, shareName, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            _shopInfos = updated;
        }
        SwkLogger.Info($"SwkNetworkCache.RemoveShop: {machineName}/{shareName}");
        Updated?.Invoke();
    }

    public static void UpsertShop(SwkNotificationListener.ShopInfo shop)
    {
        lock (_lock)
        {
            var updated = _shopInfos
                .Where(s => !string.Equals(s.MachineName, shop.MachineName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            updated.Add(shop);
            _shopInfos = updated;
            _lastScanAt = DateTime.Now;
        }
        SwkLogger.Debug($"SwkNetworkCache.UpsertShop: {shop.MachineName}/{shop.ShareName}");
        Updated?.Invoke();
    }

    public static async Task RefreshAsync(ScanMode mode, CancellationToken ct = default)
    {
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SwkLogger.Info($"SwkNetworkCache.RefreshAsync start: mode={mode}");

            IReadOnlyList<LanCandidate> candidates;
            try
            {
                candidates = await LanScanner.ScanAsync(fullScan: mode == ScanMode.Full, ct).ConfigureAwait(false);
                candidates = await MergeSavedFriendCandidatesAsync(candidates, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SwkLogger.Warn($"SwkNetworkCache: LAN scan failed: {ex.Message}");
                candidates = await MergeSavedFriendCandidatesAsync(Array.Empty<LanCandidate>(), ct).ConfigureAwait(false);
            }

            IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos;
            try
            {
                shopInfos = await SwkNotificationListener.ProbeHostsAsync(candidates, ct).ConfigureAwait(false);
                SwkNetworkHealth.RecordDiscoverySnapshot(shopInfos);
            }
            catch (Exception ex)
            {
                SwkLogger.Warn($"SwkNetworkCache: ProbeHostsAsync failed: {ex.Message}");
                shopInfos = Array.Empty<SwkNotificationListener.ShopInfo>();
                SwkNetworkHealth.RecordDiscoverySnapshot(shopInfos);
            }

            Update(candidates, shopInfos, mode);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private static async Task<IReadOnlyList<LanCandidate>> MergeSavedFriendCandidatesAsync(
        IReadOnlyList<LanCandidate> scanned,
        CancellationToken ct)
    {
        Dictionary<string, LanCandidate> merged = new(StringComparer.OrdinalIgnoreCase);

        foreach (LanCandidate c in scanned)
        {
            merged[c.Address.ToString()] = c;
        }

        foreach (Friend friend in FriendsRepository.LoadAll())
        {
            AddFriendAddress(friend.LastKnownAddress, friend.HostMachineName);

            if (!string.IsNullOrWhiteSpace(friend.HostMachineName))
            {
                try
                {
                    IPAddress[] addresses = await Dns.GetHostAddressesAsync(friend.HostMachineName, ct).ConfigureAwait(false);
                    foreach (IPAddress address in addresses)
                    {
                        AddFriendAddress(address.ToString(), friend.HostMachineName);
                    }
                }
                catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException)
                {
                    SwkLogger.Debug($"MergeSavedFriendCandidatesAsync DNS failed for {friend.HostMachineName}: {ex.Message}");
                }
            }
        }

        return merged.Values.ToArray();

        void AddFriendAddress(string? value, string? hostName)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!IPAddress.TryParse(value, out IPAddress? address)) return;
            if (address.AddressFamily != AddressFamily.InterNetwork) return;
            if (IsLinkLocal(address)) return;
            string key = address.ToString();
            if (!merged.ContainsKey(key))
            {
                merged[key] = new LanCandidate(address, hostName);
            }
        }

        static bool IsLinkLocal(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
        }
    }
}
