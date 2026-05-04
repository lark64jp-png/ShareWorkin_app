using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShareWorkin.SMB;

/// <summary>
/// お友達側: 定期的に LAN 内のホストに接続して「ここに誰がいますか？」と問い合わせ
/// 店主側の通知を受け取り、開店中のお店リストを保持
/// </summary>
public sealed class SwkNotificationListener : IAsyncDisposable
{
    private readonly Dictionary<string, ShopInfo> _discoveredShops = new();
    private readonly object _shopsLock = new();
    private CancellationTokenSource? _cancellationSource;
    private Task? _listenTask;

    public sealed class ShopInfo
    {
        public required string MachineName { get; set; }
        public required string ShareName { get; set; }
        public required int Port { get; set; }
        public required DateTime IssuedAt { get; set; }
        public DateTime LastCheckedAt { get; set; }
    }

    /// <summary>
    /// リスナーを起動。定期的に LAN を探索する
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            _cancellationSource = new CancellationTokenSource();
            _listenTask = DiscoverShopsPeriodicAsync(_cancellationSource.Token);
            await _listenTask;
        }
        catch (Exception ex)
        {
            SwkLogger.Error("SwkNotificationListener.StartAsync failed", ex);
            throw;
        }
    }

    /// <summary>
    /// リスナーを停止
    /// </summary>
    public async ValueTask StopAsync()
    {
        try
        {
            _cancellationSource?.Cancel();
            if (_listenTask != null)
            {
                try
                {
                    await _listenTask;
                }
                catch (OperationCanceledException)
                {
                    // 予想内の例外
                }
            }

            SwkLogger.Info("SwkNotificationListener stopped");
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"SwkNotificationListener.StopAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// 現在発見されているお店リストを取得
    /// </summary>
    public IReadOnlyList<ShopInfo> GetDiscoveredShops()
    {
        lock (_shopsLock)
        {
            return _discoveredShops.Values.ToList();
        }
    }

    /// <summary>
    /// 定期的に LAN を探索してお店を発見
    /// </summary>
    private async Task DiscoverShopsPeriodicAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // LAN スキャン結果を利用して各ホストに接続を試みる
                // 実装予定: LanScanner の結果を活用し、各ホストに SwkNotificationBroadcaster ポートを探索
                await DiscoverFromLanCandidatesAsync(cancellationToken);

                // 1 分ごとに再探索
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                SwkLogger.Warn($"DiscoverShopsPeriodicAsync error: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
    }

    /// <summary>
    /// LAN スキャン結果から各ホストに接続を試みる
    /// </summary>
    private async Task DiscoverFromLanCandidatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<LanCandidate> candidates = await LanScanner.ScanAsync();
            if (candidates.Count == 0)
            {
                return;
            }

            // 各候補ホストに接続試行（複数同時接続）
            var tasks = new List<Task>();
            foreach (LanCandidate c in candidates)
            {
                string host = c.HostName ?? c.Address.ToString();
                tasks.Add(QueryHostAsync(host, 0, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            SwkLogger.Debug($"DiscoverFromLanCandidatesAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// 特定のホストに接続して通知を受け取る
    /// </summary>
    public async Task<ShopInfo?> QueryHostAsync(string hostName, int port, CancellationToken cancellationToken)
    {
        try
        {
            using (var client = new TcpClient())
            {
                // 接続タイムアウト: 5秒
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(5));

                    try
                    {
                        await client.ConnectAsync(hostName, port, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        SwkLogger.Debug($"QueryHostAsync timeout connecting to {hostName}:{port}");
                        return null;
                    }
                }

                using (var sslStream = new SslStream(client.GetStream(), true, (s, c, ch, p) => true)) // 証明書検証をスキップ（自己署名）
                {
                    // TLS ハンドシェイク
                    var sslOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = hostName,
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                        RemoteCertificateValidationCallback = (s, c, ch, p) => true, // 証明書検証をスキップ（自己署名）
                    };
                    await sslStream.AuthenticateAsClientAsync(sslOptions, cancellationToken);

                    // ShopNotification を受信
                    string? notificationJson = await ReadJsonAsync(sslStream, cancellationToken);
                    if (string.IsNullOrEmpty(notificationJson))
                        return null;

                    using (JsonDocument doc = JsonDocument.Parse(notificationJson))
                    {
                        string? type = doc.RootElement.GetProperty("type").GetString();
                        if (type != "ShopNotification")
                            return null;

                        string? machineName = doc.RootElement.GetProperty("shopMachineName").GetString();
                        string? shareName = doc.RootElement.GetProperty("shareName").GetString();
                        int listeningPort = doc.RootElement.GetProperty("listeningPort").GetInt32();
                        string? issuedAtStr = doc.RootElement.GetProperty("issuedAt").GetString();

                        if (string.IsNullOrEmpty(machineName) || string.IsNullOrEmpty(shareName))
                            return null;

                        var shopInfo = new ShopInfo
                        {
                            MachineName = machineName,
                            ShareName = shareName,
                            Port = listeningPort,
                            IssuedAt = DateTime.Parse(issuedAtStr ?? DateTime.UtcNow.ToString("o")),
                            LastCheckedAt = DateTime.UtcNow
                        };

                        // 発見リストに追加/更新
                        lock (_shopsLock)
                        {
                            string key = $"{machineName}:{shareName}";
                            _discoveredShops[key] = shopInfo;
                        }

                        SwkLogger.Debug($"Discovered shop: {machineName}/{shareName} on port {listeningPort}");
                        return shopInfo;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SwkLogger.Debug($"QueryHostAsync failed for {hostName}:{port}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 発見済みのお店に対して招待コードを要求
    /// </summary>
    public async Task<(string? inviteCode, string? password, string? errorMessage)> RequestInviteCodeAsync(
        ShopInfo shop,
        CancellationToken cancellationToken)
    {
        try
        {
            using (var client = new TcpClient())
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(5));

                    try
                    {
                        await client.ConnectAsync(shop.MachineName, shop.Port, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return (null, null, "Connection timeout");
                    }
                }

                using (var sslStream = new SslStream(client.GetStream(), true, (s, c, ch, p) => true))
                {
                    // TLS ハンドシェイク
                    var sslOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = shop.MachineName,
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                        RemoteCertificateValidationCallback = (s, c, ch, p) => true, // 証明書検証をスキップ（自己署名）
                    };
                    await sslStream.AuthenticateAsClientAsync(sslOptions, cancellationToken);

                    // InviteCodeRequest を送信
                    var request = new SwkNotificationProtocol.InviteCodeRequest
                    {
                        ShareName = shop.ShareName,
                        ClientMachineName = Environment.MachineName
                    };
                    await WriteJsonAsync(sslStream, request, cancellationToken);

                    // InviteCodeResponse を受信
                    string? responseJson = await ReadJsonAsync(sslStream, cancellationToken);
                    if (string.IsNullOrEmpty(responseJson))
                        return (null, null, "No response from server");

                    using (JsonDocument doc = JsonDocument.Parse(responseJson))
                    {
                        string? result = doc.RootElement.GetProperty("result").GetString();

                        if (result != "Ok")
                        {
                            string? errorMsg = null;
                            if (doc.RootElement.TryGetProperty("errorMessage", out var errorElement))
                            {
                                errorMsg = errorElement.GetString();
                            }
                            return (null, null, errorMsg ?? result);
                        }

                        string? inviteCode = doc.RootElement.GetProperty("inviteCode").GetString();
                        string? password = doc.RootElement.GetProperty("password").GetString();

                        SwkLogger.Info($"Received invite code from {shop.MachineName}/{shop.ShareName}");
                        return (inviteCode, password, null);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"RequestInviteCodeAsync failed: {ex.Message}");
            return (null, null, ex.Message);
        }
    }

    /// <summary>
    /// JSON を SslStream から読み込む
    /// </summary>
    private static async Task<string?> ReadJsonAsync(SslStream stream, CancellationToken cancellationToken)
    {
        byte[] lengthBuffer = new byte[4];
        int read = await stream.ReadAsync(lengthBuffer, 0, 4, cancellationToken);
        if (read < 4)
            return null;

        int length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length <= 0 || length > 1024 * 1024)
            return null;

        byte[] jsonBuffer = new byte[length];
        read = await stream.ReadAsync(jsonBuffer, 0, length, cancellationToken);
        if (read < length)
            return null;

        return Encoding.UTF8.GetString(jsonBuffer);
    }

    /// <summary>
    /// JSON を SslStream に書き込む
    /// </summary>
    private static async Task WriteJsonAsync(SslStream stream, object obj, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(obj);
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        byte[] lengthBytes = BitConverter.GetBytes(jsonBytes.Length);

        await stream.WriteAsync(lengthBytes, 0, 4, cancellationToken);
        await stream.WriteAsync(jsonBytes, 0, jsonBytes.Length, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// リソース解放
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cancellationSource?.Dispose();
    }
}
