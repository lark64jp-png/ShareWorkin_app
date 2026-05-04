using System;
using System.Collections.Generic;
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

namespace ShareWorkin.SMB;

/// <summary>
/// 店主側: 開店中、定期的に C クラス範囲に「ここにいます」と通知を送信
/// TLS/TCP でセキュアに通信
/// </summary>
public sealed class SwkNotificationBroadcaster : IAsyncDisposable
{
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
        _listeningPort = 0;
    }

    /// <summary>
    /// リスナーを起動。動的ポートで TcpListener を開始し、定期的に通知を送信する
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            // TLS 証明書を生成（自己署名、LAN 内限定）
            _tlsCertificate = CreateSelfSignedCertificate();

            // 動的ポートでリッスン開始
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
            _listeningPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

            SwkLogger.Info($"SwkNotificationBroadcaster started on port {_listeningPort} for share '{_shareName}'");

            // ポート情報をファイルに記録（Listener側がこれを読み取ってポート番号を知る）
            WritePortInfoFile();

            // キャンセルトークンを準備
            _cancellationSource = new CancellationTokenSource();

            // 接続受け付けタスクと定期送信タスクを並行実行
            _broadcastTask = Task.WhenAll(
                AcceptConnectionsAsync(_cancellationSource.Token),
                BroadcastPeriodicNotificationsAsync(_cancellationSource.Token)
            );

            await _broadcastTask;
        }
        catch (Exception ex)
        {
            SwkLogger.Error("SwkNotificationBroadcaster.StartAsync failed", ex);
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
            if (_broadcastTask != null)
            {
                try
                {
                    await _broadcastTask;
                }
                catch (OperationCanceledException)
                {
                    // 予想内の例外
                }
            }

            _listener?.Stop();
            SwkLogger.Info("SwkNotificationBroadcaster stopped");
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"SwkNotificationBroadcaster.StopAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// 接続受け付けループ
    /// </summary>
    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener != null)
        {
            try
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                SwkLogger.Warn($"AcceptConnectionsAsync error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 単一クライアントの処理
    /// </summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using (var sslStream = new SslStream(client.GetStream(), false))
                {
                    // TLS ハンドシェイク
                    var sslOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _tlsCertificate,
                    };
                    await sslStream.AuthenticateAsServerAsync(sslOptions, cancellationToken);

                    // クライアントからリクエストを受信
                    string? requestJson = await ReadJsonAsync(sslStream, cancellationToken);
                    if (string.IsNullOrEmpty(requestJson))
                    {
                        return;
                    }

                    // リクエストをパース
                    using (JsonDocument doc = JsonDocument.Parse(requestJson))
                    {
                        string? type = doc.RootElement.GetProperty("type").GetString();

                        if (type == "InviteCodeRequest")
                        {
                            await HandleInviteCodeRequestAsync(sslStream, doc, cancellationToken);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SwkLogger.Debug($"HandleClientAsync error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// InviteCodeRequest に応答
    /// </summary>
    private async Task HandleInviteCodeRequestAsync(SslStream stream, JsonDocument requestDoc, CancellationToken cancellationToken)
    {
        try
        {
            string? requestedShare = requestDoc.RootElement.GetProperty("shareName").GetString();
            string? clientMachine = requestDoc.RootElement.GetProperty("clientMachineName").GetString();

            // 要求されたシェアが自分のものかチェック
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

            // 招待コードを生成
            string? inviteCode = GenerateInviteCode();
            if (string.IsNullOrEmpty(inviteCode))
            {
                var errorResponse = new SwkNotificationProtocol.InviteCodeResponse
                {
                    Result = "Denied",
                    ErrorMessage = "Failed to generate invite code"
                };
                await WriteJsonAsync(stream, errorResponse, cancellationToken);
                return;
            }

            // パスワードを取得
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

            // 成功応答
            var successResponse = new SwkNotificationProtocol.InviteCodeResponse
            {
                Result = "Ok",
                InviteCode = inviteCode,
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
    /// ポート情報をローカルファイルに記録（Listener側でポート番号を取得するため）
    /// </summary>
    private void WritePortInfoFile()
    {
        try
        {
            string appFolder = AppContext.BaseDirectory;
            string portInfoPath = Path.Combine(appFolder, "broadcaster_port.txt");

            string content = _listeningPort.ToString();
            File.WriteAllText(portInfoPath, content);

            SwkLogger.Debug($"Port info written to {portInfoPath}: {_listeningPort}");
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"WritePortInfoFile failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 定期的に通知を送信
    /// 当面はポート情報ファイルの定期更新のみ（Listener側がSMB経由で読み取る）
    /// </summary>
    private async Task BroadcastPeriodicNotificationsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // ポート情報ファイルを定期的に更新（タイムスタンプ更新で「生きている」ことを示す）
                WritePortInfoFile();

                // 30秒ごとに更新
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                SwkLogger.Warn($"BroadcastPeriodicNotificationsAsync error: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
    }

    /// <summary>
    /// 招待コードを生成（InviteDialog と同じロジック）
    /// </summary>
    private string? GenerateInviteCode()
    {
        try
        {
            var payload = new SwkNotificationProtocol.InviteCodeRequest
            {
                ShareName = _shareName,
                ClientMachineName = Environment.MachineName
            };

            // 実装予定: InviteToken.Encode と同様のロジックで招待コードを生成
            // ただし Password は含めない（通知で別途渡す）
            return "SWK1.placeholder"; // TODO: 実装
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"GenerateInviteCode failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// TLS ハンドシェイク用の自己署名証明書を生成
    /// </summary>
    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using (var rsa = RSA.Create(2048))
        {
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

            using (var cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddYears(1)))
            {
                return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.EphemeralKeySet);
            }
        }
    }

    /// <summary>
    /// SslStream から JSON を読み込む
    /// </summary>
    private static async Task<string?> ReadJsonAsync(SslStream stream, CancellationToken cancellationToken)
    {
        byte[] lengthBuffer = new byte[4];
        int read = await stream.ReadAsync(lengthBuffer, 0, 4, cancellationToken);
        if (read < 4)
            return null;

        int length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length <= 0 || length > 1024 * 1024) // 1MB 上限
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
        _tlsCertificate?.Dispose();
        _cancellationSource?.Dispose();
        _listener?.Stop();
    }
}
