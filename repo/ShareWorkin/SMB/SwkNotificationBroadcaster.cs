using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

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
    private static readonly string AppHomeDirectory = AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static readonly string CertificatePath = Path.Combine(AppHomeDirectory, "notifycert.dat");
    private static readonly X509KeyStorageFlags CertificateKeyStorageFlags =
        X509KeyStorageFlags.MachineKeySet |
        X509KeyStorageFlags.PersistKeySet |
        X509KeyStorageFlags.Exportable;
    private TcpListener? _listener;
    private int _listeningPort;
    private CancellationTokenSource? _cancellationSource;
    private Task? _broadcastTask;
    private X509Certificate2? _tlsCertificate;

    public int ListeningPort => _listeningPort;

    /// <summary>
    /// 他店から ShopClosing を受信したときのコールバック。
    /// 引数: machineName, shareName
    /// </summary>
    public Action<string, string>? OnShopClosingReceived { get; set; }

    public Action<SwkNotificationProtocol.InteractionEventNotice>? OnInteractionEventReceived { get; set; }

    public SwkNotificationBroadcaster(string shareName)
    {
        _shareName = shareName ?? throw new ArgumentNullException(nameof(shareName));
    }

    public async Task StartAsync()
    {
        try
        {
            _tlsCertificate = LoadOrCreateCertificate();

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
                var notification = CreateShopNotification();
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
                else if (type == "InteractionEventNotice")
                {
                    await HandleInteractionEventNoticeAsync(sslStream, doc, cancellationToken);
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
            string? inviteId = null;
            if (requestDoc.RootElement.TryGetProperty("inviteId", out var idElem))
            {
                inviteId = idElem.GetString();
            }
            string clientLabel = string.IsNullOrWhiteSpace(clientMachine) ? "不明な端末" : clientMachine!;

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

            // 手動招待コード経由の場合は InviteRegistry で照合する。
            InviteRegistry.InviteRecord? matchedInvite = null;
            bool isManual = !string.IsNullOrWhiteSpace(inviteId);
            if (isManual)
            {
                matchedInvite = InviteRegistry.FindUnused(inviteId!, _shareName);
                if (matchedInvite is null)
                {
                    var invalidResponse = new SwkNotificationProtocol.InviteCodeResponse
                    {
                        Result = "InviteIdInvalid",
                        ErrorMessage = "招待コードが見つからないか、既に使用済みです。"
                    };
                    await WriteJsonAsync(stream, invalidResponse, cancellationToken);
                    SwkLogger.Warn($"Invite code request rejected: invalid/used inviteId from {clientLabel}");
                    return;
                }
            }

            // UI ダイアログを挟まず、BK 側ポリシーだけで自動応答する。
            SwkLogger.Info($"Invite request auto-approved for '{clientLabel}' share '{requestedShare}' (manual={isManual})");

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

            // 承認後、手動コード経由なら使用済みにマーク(一回性)。
            if (isManual && matchedInvite is not null)
            {
                if (!InviteRegistry.MarkUsed(matchedInvite.Id, clientLabel))
                {
                    // 競合などで失敗した場合は安全側に倒す。
                    var raceResponse = new SwkNotificationProtocol.InviteCodeResponse
                    {
                        Result = "InviteIdUsed",
                        ErrorMessage = "招待コードは別の端末で使用されました。"
                    };
                    await WriteJsonAsync(stream, raceResponse, cancellationToken);
                    SwkLogger.Warn($"Invite race detected for id={matchedInvite.Id}");
                    return;
                }
            }

            var successResponse = new SwkNotificationProtocol.InviteCodeResponse
            {
                Result = "Ok",
                Password = password
            };
            await WriteJsonAsync(stream, successResponse, cancellationToken);

            SwkLogger.Info($"Sent invite code to {clientLabel} for share '{_shareName}' (manual={isManual})");
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"HandleInviteCodeRequestAsync error: {ex.Message}");
        }
    }

    private async Task HandleInteractionEventNoticeAsync(
        SslStream stream,
        JsonDocument requestDoc,
        CancellationToken cancellationToken)
    {
        try
        {
            string? receiverShareName = requestDoc.RootElement.TryGetProperty("receiverShareName", out JsonElement shareElement)
                ? shareElement.GetString()
                : null;
            if (!string.Equals(receiverShareName, _shareName, StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(stream, new SwkNotificationProtocol.InteractionEventResponse
                {
                    Result = "NotFound",
                    ErrorMessage = "対象共有が一致しません。"
                }, cancellationToken);
                return;
            }

            string? eventId = requestDoc.RootElement.TryGetProperty("eventId", out JsonElement eventElement)
                ? eventElement.GetString()
                : null;
            string? eventType = requestDoc.RootElement.TryGetProperty("eventType", out JsonElement typeElement)
                ? typeElement.GetString()
                : null;
            string? senderMachineName = requestDoc.RootElement.TryGetProperty("senderMachineName", out JsonElement machineElement)
                ? machineElement.GetString()
                : null;
            string? targetName = requestDoc.RootElement.TryGetProperty("targetName", out JsonElement targetElement)
                ? targetElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(eventId) ||
                string.IsNullOrWhiteSpace(eventType) ||
                string.IsNullOrWhiteSpace(senderMachineName) ||
                string.IsNullOrWhiteSpace(targetName))
            {
                await WriteJsonAsync(stream, new SwkNotificationProtocol.InteractionEventResponse
                {
                    Result = "Invalid",
                    ErrorMessage = "交流イベント通知に必要な情報が不足しています。"
                }, cancellationToken);
                return;
            }

            var notice = new SwkNotificationProtocol.InteractionEventNotice
            {
                EventId = eventId,
                EventType = eventType,
                SenderMachineName = senderMachineName,
                SenderDisplayName = requestDoc.RootElement.TryGetProperty("senderDisplayName", out JsonElement displayElement)
                    ? displayElement.GetString()
                    : null,
                SenderSwkInstanceId = requestDoc.RootElement.TryGetProperty("senderSwkInstanceId", out JsonElement instanceElement)
                    ? instanceElement.GetString()
                    : null,
                SenderShareName = requestDoc.RootElement.TryGetProperty("senderShareName", out JsonElement senderShareElement)
                    ? senderShareElement.GetString()
                    : null,
                ReceiverShareName = receiverShareName!,
                TargetName = targetName,
                TargetRelativePath = requestDoc.RootElement.TryGetProperty("targetRelativePath", out JsonElement relativeElement)
                    ? relativeElement.GetString()
                    : null,
                TargetKind = requestDoc.RootElement.TryGetProperty("targetKind", out JsonElement kindElement)
                    ? kindElement.GetString()
                    : null,
                NotificationType = requestDoc.RootElement.TryGetProperty("notificationType", out JsonElement notificationElement)
                    ? notificationElement.GetString()
                    : null,
                Message = requestDoc.RootElement.TryGetProperty("message", out JsonElement messageElement)
                    ? messageElement.GetString()
                    : null,
                IssuedAt = requestDoc.RootElement.TryGetProperty("issuedAt", out JsonElement issuedElement)
                    ? issuedElement.GetString() ?? DateTime.UtcNow.ToString("o")
                    : DateTime.UtcNow.ToString("o")
            };

            OnInteractionEventReceived?.Invoke(notice);
            await WriteJsonAsync(stream, new SwkNotificationProtocol.InteractionEventResponse
            {
                Result = "Ok"
            }, cancellationToken);
            SwkLogger.Info($"InteractionEventNotice accepted: sender={senderMachineName} target={targetName} share={_shareName}");
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(stream, new SwkNotificationProtocol.InteractionEventResponse
            {
                Result = "Error",
                ErrorMessage = ex.Message
            }, cancellationToken);
            SwkLogger.Warn($"HandleInteractionEventNoticeAsync error: {ex.Message}");
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
                        string? clientMachineName = null;
                        try
                        {
                            using JsonDocument probeDoc = JsonDocument.Parse(msg);
                            if (probeDoc.RootElement.TryGetProperty("clientMachineName", out JsonElement clientMachineElement))
                            {
                                clientMachineName = clientMachineElement.GetString();
                            }
                        }
                        catch (Exception ex)
                        {
                            SwkLogger.Debug($"ShopProbe parse warning: {ex.Message}");
                        }

                        var response = CreateShopNotification();
                        string responseJson = JsonSerializer.Serialize(response);
                        byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);
                        await udp.SendAsync(responseBytes, result.RemoteEndPoint, cancellationToken);
                        SwkNetworkHealth.RecordIncomingProbe(clientMachineName, result.RemoteEndPoint);
                        SwkLogger.Debug($"UDP probe response sent to {result.RemoteEndPoint}: port={_listeningPort}");
                    }
                    else if (msg.Contains("\"ShopClosing\""))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(msg);
                            string? closingMachine = doc.RootElement.GetProperty("shopMachineName").GetString();
                            string? closingShare = doc.RootElement.GetProperty("shareName").GetString();
                            if (!string.IsNullOrEmpty(closingMachine) && !string.IsNullOrEmpty(closingShare))
                            {
                                SwkNetworkCache.RemoveShop(closingMachine, closingShare);
                                OnShopClosingReceived?.Invoke(closingMachine, closingShare);
                                SwkLogger.Info($"ShopClosing received: {closingMachine}/{closingShare}");
                            }
                        }
                        catch (Exception ex)
                        {
                            SwkLogger.Debug($"ShopClosing parse error: {ex.Message}");
                        }
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
    /// 起動直後に UDP ブロードキャストを1回送信する
    /// </summary>
    private async Task BroadcastPeriodicNotificationsAsync(CancellationToken cancellationToken)
    {
        await SendUdpBroadcastAsync(cancellationToken);
    }

    private async Task SendUdpBroadcastAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            var notification = CreateShopNotification();
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

    private SwkNotificationProtocol.ShopNotification CreateShopNotification() => new()
    {
        ShopMachineName = Environment.MachineName,
        ShareName = _shareName,
        ListeningPort = _listeningPort,
        SwkInstanceId = SwkInstanceIdentity.GetOrCreateId(),
        IssuedAt = DateTime.UtcNow.ToString("o")
    };

    /// <summary>
    /// お店を閉じる直前に LAN 全体へ通知する。
    /// </summary>
    public async Task BroadcastShopClosingAsync()
    {
        try
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            var msg = new SwkNotificationProtocol.ShopClosing
            {
                ShopMachineName = Environment.MachineName,
                ShareName = _shareName,
                IssuedAt = DateTime.UtcNow.ToString("o")
            };
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
            await udp.SendAsync(bytes, new IPEndPoint(IPAddress.Broadcast, UdpDiscoveryPort));
            SwkLogger.Info($"BroadcastShopClosingAsync sent: {_shareName}");
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"BroadcastShopClosingAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// 共有制限が変わったことを LAN 全体へブロードキャストする（fire-and-forget）。
    /// </summary>
    public async Task BroadcastPermissionChangedAsync()
    {
        try
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            var msg = new SwkNotificationProtocol.SharePermissionChanged
            {
                MachineName = Environment.MachineName,
                ShareName = _shareName
            };
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(msg));
            await udp.SendAsync(bytes, new IPEndPoint(IPAddress.Broadcast, UdpDiscoveryPort));
            SwkLogger.Debug("BroadcastPermissionChangedAsync sent");
        }
        catch (Exception ex)
        {
            SwkLogger.Debug($"BroadcastPermissionChangedAsync error: {ex.Message}");
        }
    }

    private static X509Certificate2 LoadOrCreateCertificate()
    {
        try
        {
            if (File.Exists(CertificatePath))
            {
                byte[] protectedBytes = File.ReadAllBytes(CertificatePath);
                byte[] pfxBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine);
                var loaded = new X509Certificate2(pfxBytes, (string?)null, CertificateKeyStorageFlags);
                if (IsUsableCertificate(loaded))
                {
                    SwkLogger.Info("SwkNotificationBroadcaster loaded persisted TLS certificate");
                    return loaded;
                }

                loaded.Dispose();
                SwkLogger.Warn("Persisted TLS certificate was unusable; regenerating");
            }
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"Failed to load persisted TLS certificate: {ex.Message}");
        }

        X509Certificate2 created = CreateSelfSignedCertificate();
        TryPersistCertificate(created);
        return created;
    }

    private static bool IsUsableCertificate(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey)
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime())
        {
            return false;
        }

        string expectedSubject = $"CN={Environment.MachineName}";
        return string.Equals(certificate.Subject, expectedSubject, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryPersistCertificate(X509Certificate2 certificate)
    {
        try
        {
            Directory.CreateDirectory(AppHomeDirectory);
            byte[] pfxBytes = certificate.Export(X509ContentType.Pfx);
            byte[] protectedBytes = ProtectedData.Protect(pfxBytes, null, DataProtectionScope.LocalMachine);
            File.WriteAllBytes(CertificatePath, protectedBytes);
            SwkLogger.Info("SwkNotificationBroadcaster persisted TLS certificate");
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"Failed to persist TLS certificate: {ex.Message}");
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
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddYears(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, CertificateKeyStorageFlags);
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
