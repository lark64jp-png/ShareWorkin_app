using System;
using System.Collections.Generic;
using System.Linq;
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
            }
            catch (Exception ex)
            {
                SwkLogger.Warn($"SwkNetworkCache: LAN scan failed: {ex.Message}");
                candidates = Array.Empty<LanCandidate>();
            }

            IReadOnlyList<SwkNotificationListener.ShopInfo> shopInfos;
            try
            {
                shopInfos = await SwkNotificationListener.ProbeHostsAsync(candidates, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SwkLogger.Warn($"SwkNetworkCache: ProbeHostsAsync failed: {ex.Message}");
                shopInfos = Array.Empty<SwkNotificationListener.ShopInfo>();
            }

            Update(candidates, shopInfos, mode);
        }
        finally
        {
            _scanLock.Release();
        }
    }
}
