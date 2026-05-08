using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShareWorkin.SMB;

/// <summary>
/// お友達側: UDP プローブで LAN 内の開店中お店を発見し、TLS/TCP で招待コードを取得する
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
        public string? IpAddress { get; set; }
    }

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

    public async ValueTask StopAsync()
    {
        try
        {
            _cancellationSource?.Cancel();
            if (_listenTask != null)
            {
                try { await _listenTask; }
                catch (OperationCanceledException) { }
            }
            SwkLogger.Info("SwkNotificationListener stopped");
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"SwkNotificationListener.StopAsync error: {ex.Message}");
        }
    }

    public IReadOnlyList<ShopInfo> GetDiscoveredShops()
    {
        lock (_shopsLock)
        {
            return _discoveredShops.Values.ToList();
        }
    }

    private async Task DiscoverShopsPeriodicAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<LanCandidate> candidates = await LanScanner.ScanAsync();
                IReadOnlyList<ShopInfo> found = await ProbeHostsAsync(candidates, cancellationToken);

                lock (_shopsLock)
                {
                    _discoveredShops.Clear();
                    foreach (ShopInfo s in found)
                    {
                        string key = $"{s.MachineName}:{s.ShareName}";
                        _discoveredShops[key] = s;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                SwkLogger.Warn($"DiscoverShopsPeriodicAsync error: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
    }

    /// <summary>
    /// 各 LAN 候補に UDP プローブを送信し、ShopNotification が返ってきたものを開店中のお店として返す
    /// UserListWindow から直接呼び出す（静的メソッド）
    /// </summary>
    public static async Task<IReadOnlyList<ShopInfo>> ProbeHostsAsync(
        IReadOnlyList<LanCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var result = new List<ShopInfo>();
        if (candidates.Count == 0) return result;

        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));

            var probe = new { type = "ShopProbe", clientMachineName = Environment.MachineName };
            byte[] probeBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(probe));

            int sentCount = 0;
            foreach (LanCandidate c in candidates)
            {
                try
                {
                    await udp.SendAsync(probeBytes, new IPEndPoint(c.Address, SwkNotificationBroadcaster.UdpDiscoveryPort), cancellationToken);
                    sentCount++;
                }
                catch (Exception ex)
                {
                    SwkLogger.Debug($"ProbeHostsAsync send failed to {c.Address}: {ex.Message}");
                }
            }

            SwkLogger.Debug($"ProbeHostsAsync: sent {sentCount} probes");

            // 最大 2 秒待機してレスポンスを収集
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

            var respondedIps = new HashSet<string>();

            while (!timeoutCts.IsCancellationRequested)
            {
                try
                {
                    UdpReceiveResult recv = await udp.ReceiveAsync(timeoutCts.Token);
                    string senderIp = recv.RemoteEndPoint.Address.ToString();

                    if (respondedIps.Contains(senderIp)) continue;

                    string json = Encoding.UTF8.GetString(recv.Buffer);
                    using JsonDocument doc = JsonDocument.Parse(json);

                    if (!doc.RootElement.TryGetProperty("type", out var typeProp)) continue;
                    if (typeProp.GetString() != "ShopNotification") continue;

                    string? machineName = doc.RootElement.GetProperty("shopMachineName").GetString();
                    string? shareName = doc.RootElement.GetProperty("shareName").GetString();
                    int tcpPort = doc.RootElement.GetProperty("listeningPort").GetInt32();
                    string? issuedAtStr = doc.RootElement.GetProperty("issuedAt").GetString();

                    if (string.IsNullOrEmpty(machineName) || string.IsNullOrEmpty(shareName) || tcpPort <= 0) continue;

                    respondedIps.Add(senderIp);
                    result.Add(new ShopInfo
                    {
                        MachineName = machineName,
                        ShareName = shareName,
                        Port = tcpPort,
                        IssuedAt = DateTime.Parse(issuedAtStr ?? DateTime.UtcNow.ToString("o")),
                        LastCheckedAt = DateTime.UtcNow,
                        IpAddress = senderIp
                    });

                    SwkLogger.Debug($"ProbeHostsAsync: discovered {machineName}/{shareName} tcpPort={tcpPort}");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    SwkLogger.Debug($"ProbeHostsAsync recv error: {ex.Message}");
                }
            }

            SwkLogger.Info($"ProbeHostsAsync: {result.Count} shop(s) found from {sentCount} probes");
            return result;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"ProbeHostsAsync failed: {ex.Message}");
            return result;
        }
    }

    public sealed class InviteCodeResult
    {
        public string? Password { get; init; }
        public string? CertThumbprint { get; init; }
        public string? ErrorMessage { get; init; }
        public bool Success => string.IsNullOrEmpty(ErrorMessage) && !string.IsNullOrEmpty(Password);
    }

    /// <summary>
    /// 発見済みのお店に対して招待コードを要求する。
    /// inviteId が非 null = 手動招待コード経由(店主側で InviteRegistry 照合 + 一回性)。
    /// expectedThumbprint が非 null = 既存 Friend の TLS ピン留め検証(不一致なら失敗)。
    /// expectedThumbprint が null = 初回 TOFU(成功した接続の証明書サムプリントを返す)。
    /// </summary>
    public async Task<InviteCodeResult> RequestInviteCodeAsync(
        ShopInfo shop,
        string? inviteId,
        string? expectedThumbprint,
        CancellationToken cancellationToken)
    {
        string? capturedThumbprint = null;

        try
        {
            string connectTarget = shop.IpAddress ?? shop.MachineName;
            SwkLogger.Info($"RequestInviteCodeAsync: connecting to {shop.MachineName}({connectTarget}):{shop.Port} (manual={inviteId != null}, pinned={expectedThumbprint != null})");

            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            try
            {
                await client.ConnectAsync(connectTarget, shop.Port, cts.Token);
            }
            catch (OperationCanceledException)
            {
                SwkLogger.Warn($"RequestInviteCodeAsync: TCP connect timed out to {shop.MachineName}({connectTarget}):{shop.Port}");
                return new InviteCodeResult { ErrorMessage = "接続タイムアウト" };
            }

            using var sslStream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            var sslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = shop.MachineName,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (s, cert, ch, errors) =>
                {
                    if (cert is null) return false;

                    // サムプリントを取り出す(self-signed なので chain/errors は無視)。
                    string thumb;
                    try
                    {
                        thumb = cert is X509Certificate2 c2
                            ? c2.Thumbprint
                            : new X509Certificate2(cert).Thumbprint;
                    }
                    catch
                    {
                        return false;
                    }

                    capturedThumbprint = thumb;

                    if (string.IsNullOrEmpty(expectedThumbprint))
                    {
                        // 初回 TOFU: 任意の自己署名を受け入れて、後で Friend に保存する。
                        return true;
                    }

                    bool match = string.Equals(thumb, expectedThumbprint, StringComparison.OrdinalIgnoreCase);
                    if (!match)
                    {
                        SwkLogger.Warn($"TLS pinning mismatch for {shop.MachineName}: expected={expectedThumbprint} got={thumb}");
                    }
                    return match;
                },
            };

            try
            {
                await sslStream.AuthenticateAsClientAsync(sslOptions, cancellationToken);
            }
            catch (System.Security.Authentication.AuthenticationException)
            {
                return new InviteCodeResult
                {
                    CertThumbprint = capturedThumbprint,
                    ErrorMessage = "店主の証明書が以前と違います。乗っ取りの可能性があるため接続を中止しました。"
                };
            }

            // Broadcaster は接続直後に ShopNotification を送るので、先に受信する
            string? notificationJson = await ReadJsonAsync(sslStream, cancellationToken);
            if (string.IsNullOrEmpty(notificationJson))
                return new InviteCodeResult { CertThumbprint = capturedThumbprint, ErrorMessage = "応答なし" };

            SwkLogger.Debug($"RequestInviteCodeAsync: received ShopNotification from {shop.MachineName}/{shop.ShareName}");

            // InviteCodeRequest を送信
            var request = new SwkNotificationProtocol.InviteCodeRequest
            {
                ShareName = shop.ShareName,
                ClientMachineName = Environment.MachineName,
                InviteId = inviteId,
            };
            await WriteJsonAsync(sslStream, request, cancellationToken);

            // InviteCodeResponse を受信
            string? responseJson = await ReadJsonAsync(sslStream, cancellationToken);
            if (string.IsNullOrEmpty(responseJson))
                return new InviteCodeResult { CertThumbprint = capturedThumbprint, ErrorMessage = "応答なし" };

            using JsonDocument doc = JsonDocument.Parse(responseJson);
            string? result = doc.RootElement.GetProperty("result").GetString();

            if (result != "Ok")
            {
                string? errorMsg = null;
                if (doc.RootElement.TryGetProperty("errorMessage", out var errorElement))
                    errorMsg = errorElement.GetString();
                return new InviteCodeResult
                {
                    CertThumbprint = capturedThumbprint,
                    ErrorMessage = errorMsg ?? result
                };
            }

            string? password = doc.RootElement.GetProperty("password").GetString();

            SwkLogger.Info($"Received invite code from {shop.MachineName}/{shop.ShareName}");
            return new InviteCodeResult
            {
                Password = password,
                CertThumbprint = capturedThumbprint,
            };
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"RequestInviteCodeAsync failed: {ex.Message}");
            return new InviteCodeResult
            {
                CertThumbprint = capturedThumbprint,
                ErrorMessage = ex.Message
            };
        }
    }

    private static async Task<string?> ReadJsonAsync(SslStream stream, CancellationToken cancellationToken)
    {
        byte[] lengthBuffer = new byte[4];
        int read = await stream.ReadAsync(lengthBuffer, 0, 4, cancellationToken);
        if (read < 4) return null;

        int length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length <= 0 || length > 1024 * 1024) return null;

        byte[] jsonBuffer = new byte[length];
        read = await stream.ReadAsync(jsonBuffer, 0, length, cancellationToken);
        if (read < length) return null;

        return Encoding.UTF8.GetString(jsonBuffer);
    }

    private static async Task WriteJsonAsync(SslStream stream, object obj, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(obj);
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        byte[] lengthBytes = BitConverter.GetBytes(jsonBytes.Length);
        await stream.WriteAsync(lengthBytes, 0, 4, cancellationToken);
        await stream.WriteAsync(jsonBytes, 0, jsonBytes.Length, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cancellationSource?.Dispose();
    }
}
