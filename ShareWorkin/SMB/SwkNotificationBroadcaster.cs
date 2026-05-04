using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShareWorkin.SMB;

/// <summary>
/// 店主側: 開店中、UDP ブロードキャストで「ここにいます」と通知し、
/// TLS/TCP で招待コード要求に応答する
/// </summary>
public sealed class SwkNotificationBroadcaster : IAsyncDisposable
{
    // LAN 内の ShareWorkin 発見に使う固定 UDP ポート（アプリ定数、システムポートではない）
    public const int UdpDiscoveryPort = 7831;

    private readonly string _shareName;
    private TcpListener? _listener;
    private int _listeningPort;
    private CancellationTokenSource? _cancellationSource;
    private Task? _broadcastTask;
    private X509Certificate2? _tlsCertificate;

    public int ListeningPort => _listeningPort;

    public SwkNotificationBroadcaster(string shareName)
    {
        _shareName = shareName ?? throw new ArgumentNullException(nameof(shareName));
    }

    public async Task StartAsync()
    {
        try
        {
            _tlsCertificate = CreateSelfSignedCertificate();

            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
            _listeningPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

            SwkLogger.Info($"SwkNotificationBroadcaster started on TCP port {_listeningPort} for share '{_shareName}'");

            _cancellationSource = new CancellationTokenSource();
            _broadcastTask = Task.WhenAll(
                AcceptConnectionsAsync(_cancellationSource.Token),
                BroadcastPeriodicNotificationsAsync(_cancellationSource.Token),
                ListenUdpProbesAsync(_cancellationSource.Token)
            );

            SwkLogger.Info($"SwkNotificationBroadcaster started for '{_shareName}'");
            await _broadcastTask;
        }
        catch (Exception ex)
        {
            SwkLogger.Error("SwkNotificationBroadcaster.StartAsync failed", ex);
            throw;
        }
    }

    public async ValueTask StopAsync()
    {
        try
        {
            _cancellationSource?.Cancel();
            if (_broadcastTask != null)
            {
                try { await _broadcastTask; }
                catch (OperationCanceledException) { }
            }
            _listener?.Stop();
            SwkLogger.Info("SwkNotificationBroadcaster stopped");
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"SwkNotificationBroadcaster.StopAsync error: {ex.Message}");
        }
    }

    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener != null)
        {
            try
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                SwkLogger.Warn($"AcceptConnectionsAsync error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// TLS 接続を受け付け、ShopNotification を送信後、InviteCodeRequest を待つ
    /// </summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using var sslStream = new SslStream(client.GetStream(), false);
                var sslOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = _tlsCertificate,
                };
                await sslStream.AuthenticateAsServerAsync(sslOptions, cancellationToken);

                // 接続直後に ShopNotification を送信（相手が「開店中」であることを確認できる）
                var notification = new SwkNotificationProtocol.ShopNotification
                {
                    ShopMachineName = Environment.MachineName,
                    ShareName = _shareName,
                    ListeningPort = _listeningPort,
                    IssuedAt = DateTime.UtcNow.ToString("o")
                };
                await WriteJsonAsync(sslStream, notification, cancellationToken);

                // InviteCodeRequest を待つ（タイムアウト 10 秒）
                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                reqCts.CancelAfter(TimeSpan.FromSeconds(10));

                string? requestJson;
                try
                {
                    requestJson = await ReadJsonAsync(sslStream, reqCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // タイムアウト = 発見プローブとして正常終了（InviteCodeRequest は不要）
                    return;
                }

                if (string.IsNullOrEmpty(requestJson)) return;

                using JsonDocument doc = JsonDocument.Parse(requestJson);
                string? type = doc.RootElement.GetProperty("type").GetString();
                if (type == "InviteCodeRequest")
                {
                    await HandleInviteCodeRequestAsync(sslStream, doc, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                SwkLogger.Debug($"HandleClientAsync error: {ex.Message}");
            }
        }
    }

    private async Task HandleInviteCodeRequestAsync(SslStream stream, JsonDocument requestDoc, CancellationToken cancellationToken)
    {
        try
        {
            string? requestedShare = requestDoc.RootElement.GetProperty("shareName").GetString();
            string? clientMachine = requestDoc.RootElement.GetProperty("clientMachineName").GetString();

            if (requestedShare != _shareName)
            {
                var errorResponse = new SwkNotificationProtocol.InviteCodeResponse
                {
                    Result = "NotFound",
                    ErrorMessage = $"Share '{requestedShare}' not found"
                };
                await WriteJsonAsync(stream, errorResponse, cancellationToken);
                return;
            }

            string? password = SecureStorage.Get(SecureStorage.KeySwkGuestPassword);
            if (string.IsNullOrEmpty(password))
            {
                var errorResponse = new SwkNotificationProtocol.InviteCodeResponse
                {
                    Result = "Denied",
                    ErrorMessage = "Shop key not ready"
                };
                await WriteJsonAsync(stream, errorResponse, cancellationToken);
                return;
            }

            var successResponse = new SwkNotificationProtocol.InviteCodeResponse
            {
                Result = "Ok",
                InviteCode = "SWK1.auto",
                Password = password
            };
            await WriteJsonAsync(stream, successResponse, cancellationToken);

            SwkLogger.Info($"Sent invite code to {clientMachine} for share '{_shareName}'");
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"HandleInviteCodeRequestAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// UDP ポート 7831 でプローブを待ち受け、ShopNotification を返す
    /// </summary>
    private async Task ListenUdpProbesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, UdpDiscoveryPort));

            SwkLogger.Info($"SwkNotificationBroadcaster listening UDP probes on port {UdpDiscoveryPort}");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    UdpReceiveResult result = await udp.ReceiveAsync(cancellationToken);
                    string msg = Encoding.UTF8.GetString(result.Buffer);

                    if (msg.Contains("\"ShopProbe\""))
                    {
                        var response = new SwkNotificationProtocol.ShopNotification
                        {
                            ShopMachineName = Environment.MachineName,
                            ShareName = _shareName,
                            ListeningPort = _listeningPort,
                            IssuedAt = DateTime.UtcNow.ToString("o")
                        };
                        string responseJson = JsonSerializer.Serialize(response);
                        byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);
                        await udp.SendAsync(responseBytes, result.RemoteEndPoint, cancellationToken);
                        SwkLogger.Debug($"UDP probe response sent to {result.RemoteEndPoint}: port={_listeningPort}");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    SwkLogger.Debug($"ListenUdpProbesAsync recv error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"ListenUdpProbesAsync failed to bind UDP {UdpDiscoveryPort}: {ex.Message}");
        }
    }

    /// <summary>
    /// 起動直後と 30 秒ごとに UDP ブロードキャストを送信する
    /// </summary>
    private async Task BroadcastPeriodicNotificationsAsync(CancellationToken cancellationToken)
    {
        await SendUdpBroadcastAsync(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                await SendUdpBroadcastAsync(cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                SwkLogger.Warn($"BroadcastPeriodicNotificationsAsync error: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
    }

    private async Task SendUdpBroadcastAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            var notification = new SwkNotificationProtocol.ShopNotification
            {
                ShopMachineName = Environment.MachineName,
                ShareName = _shareName,
                ListeningPort = _listeningPort,
                IssuedAt = DateTime.UtcNow.ToString("o")
            };
            string json = JsonSerializer.Serialize(notification);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await udp.SendAsync(bytes, new IPEndPoint(IPAddress.Broadcast, UdpDiscoveryPort), cancellationToken);
            SwkLogger.Debug($"UDP broadcast sent: tcpPort={_listeningPort}");
        }
        catch (Exception ex)
        {
            SwkLogger.Debug($"SendUdpBroadcastAsync error: {ex.Message}");
        }
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={Environment.MachineName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(Environment.MachineName);
        sanBuilder.AddDnsName("127.0.0.1");
        request.CertificateExtensions.Add(sanBuilder.Build());

        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddYears(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.EphemeralKeySet);
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
        _tlsCertificate?.Dispose();
        _cancellationSource?.Dispose();
        _listener?.Stop();
    }
}
