using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ShareWorkin.SMB;

public enum NotificationCommandResult
{
    Failed,
    Fallback,
    Toast,
}

public sealed record TrayStatus(bool IsShopOpen, string? ShopFolder);
public sealed record IncomingInteractionMessage(SwkIncomingInteractionRecord Entry);

public sealed record ShopOpenOutcome(
    bool Ok,
    string? Error,
    bool NeedsOwnership,
    string? OwnershipPrompt,
    IReadOnlyList<string>? BlockedPaths);

public sealed class UiPipeClient : IDisposable
{
    private const string PipeName = "ShareWorkin_TrayPipe";

    private readonly AdminWorkerProcessClient _adminWorker = new();
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _cmdLock = new(1, 1);
    private readonly Channel<string> _responseChannel = Channel.CreateBounded<string>(16);
    private CancellationTokenSource? _receiveCts;

    public event Action? TrayExiting;
    public event Action? ShowRequested;
    public event Action<string, string>? FriendShopClosingReceived;
    public event Action<SwkIncomingInteractionRecord>? IncomingInteractionReceived;

    public bool IsConnected => _pipe?.IsConnected == true;

    public bool Connect(int timeoutMs = 3000)
    {
        try
        {
            Disconnect();
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(Math.Max(1, timeoutMs));
            _pipe = pipe;
            _reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            _receiveCts = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
            return true;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"UiPipeClient.Connect failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        _receiveCts?.Cancel();
        _receiveCts = null;
        _reader?.Dispose();
        _reader = null;
        _writer?.Dispose();
        _writer = null;
        try
        {
            _pipe?.Dispose();
        }
        catch
        {
        }

        _pipe = null;
    }

    public TrayStatus? GetStatus(int timeoutMs = 15000)
    {
        string? json = SendCommand("{\"cmd\":\"GET_STATUS\"}", timeoutMs);
        if (json == null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            bool isOpen = root.TryGetProperty("isShopOpen", out JsonElement openElement) && openElement.GetBoolean();
            string? folder = root.TryGetProperty("shopFolder", out JsonElement folderElement) &&
                             folderElement.ValueKind != JsonValueKind.Null
                ? folderElement.GetString()
                : null;
            return new TrayStatus(isOpen, folder);
        }
        catch
        {
            return null;
        }
    }

    // Keep a long timeout because opening a share includes infrastructure checks and ACL repair.
    private const int OpenShopTimeoutMs = 120_000;

    public ShopOpenOutcome? OpenShop(
        string shopFolder,
        string shareName,
        string profileLabel,
        int accessRight,
        bool authorizeOwnership,
        List<PermissionRestoreEntry>? restoreEntries = null)
    {
        _ = authorizeOwnership;
        AdminCommandResponse response = _adminWorker.Execute(new AdminCommandRequest
        {
            Cmd = AdminProtocol.OpenShopCommand,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ShopRootPath = shopFolder,
            ShareName = shareName,
            ProfileLabel = profileLabel,
            AccessRight = accessRight,
            ApplyPermissionsOnOpen = true,
            PermissionEntries = restoreEntries
        }, timeoutMs: OpenShopTimeoutMs);

        return new ShopOpenOutcome(
            response.Ok,
            response.ErrorMessage,
            false,
            null,
            response.BlockedPaths);
    }

    public AdminCommandResponse CloseShop(string shopFolder, string shareName)
    {
        if (string.IsNullOrWhiteSpace(shareName))
        {
            return new AdminCommandResponse
            {
                Ok = false,
                ErrorCode = AdminErrorCode.ValidationFailed,
                ErrorMessage = "共有名が不正です。"
            };
        }

        SwkLogger.Info(
            $"UiPipeClient.CloseShop logical-off: share={shareName} shopFolder={shopFolder} action=keep-windows-share");
        return new AdminCommandResponse
        {
            Ok = true,
            ErrorCode = AdminErrorCode.None
        };
    }

    public AdminCommandResponse SetSubfolderPermission(string shopRootPath, string path, bool isSharedOff, bool isReadOnly)
    {
        return new AdminCommandResponse { Ok = true };
    }

    public AdminCommandResponse ResetPathToInherited(string shopRootPath, string path)
    {
        return new AdminCommandResponse { Ok = true };
    }

    public AdminCommandResponse MarkActionAftercare(
        string shopRootPath,
        string affectedPath,
        string policySourceFolder,
        SharePolicyRepairReason reason)
    {
        return new AdminCommandResponse { Ok = true };
    }

    public bool SyncTrayShopOpened(string shopFolder, string shareName, int accessRight)
    {
        string? json = SendCommand(JsonSerializer.Serialize(new
        {
            cmd = "SYNC_SHOP_OPENED",
            shopFolder,
            shareName,
            accessRight
        }), timeoutMs: 5000);
        return ReadOk(json);
    }

    public bool SyncTrayShopClosed()
    {
        string? json = SendCommand("{\"cmd\":\"SYNC_SHOP_CLOSED\"}", timeoutMs: 5000);
        return ReadOk(json);
    }

    public void BroadcastClosing() => FireAndForget("{\"cmd\":\"BROADCAST_CLOSING\"}");
    public void BroadcastPermission() => FireAndForget("{\"cmd\":\"BROADCAST_PERMISSION\"}");
    public void ReloadSettings() => FireAndForget("{\"cmd\":\"RELOAD_SETTINGS\"}");

    public NotificationCommandResult ShowBalloonTip(string title, string text, string? folder)
    {
        return SendNotificationCommand(JsonSerializer.Serialize(new { cmd = "SHOW_BALLOON", title, text, folder }));
    }

    public NotificationCommandResult SendTestNotification(string? folder)
    {
        return SendNotificationCommand(JsonSerializer.Serialize(new { cmd = "TEST_NOTIFICATION", folder }));
    }

    public void SendExitApp() => FireAndForget("{\"cmd\":\"EXIT_APP\"}");

    public void Dispose() => Disconnect();

    private static bool ReadOk(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("ok", out JsonElement okElement) && okElement.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    private string? SendCommand(string json, int timeoutMs = 15000)
    {
        _cmdLock.Wait();
        try
        {
            if (!IsConnected)
            {
                return null;
            }

            _writer!.WriteLine(json);
            using var cts = new CancellationTokenSource(timeoutMs);
            Task<string> task = _responseChannel.Reader.ReadAsync(cts.Token).AsTask();
            task.Wait();
            return task.IsCompletedSuccessfully ? task.Result : null;
        }
        catch (Exception ex)
        {
            SwkLogger.Debug($"UiPipeClient.SendCommand error: {ex.Message}");
            Disconnect();
            return null;
        }
        finally
        {
            _cmdLock.Release();
        }
    }

    private NotificationCommandResult SendNotificationCommand(string json)
    {
        if (!IsConnected)
        {
            SwkLogger.Debug("UiPipeClient.SendNotificationCommand skipped: not connected");
            return NotificationCommandResult.Failed;
        }

        string? responseJson = SendCommand(json);
        NotificationCommandResult result = ReadNotificationCommandResult(responseJson);
        if (result == NotificationCommandResult.Failed)
        {
            SwkLogger.Warn("UiPipeClient.SendNotificationCommand failed: tray did not acknowledge notification command");
        }

        return result;
    }

    private static NotificationCommandResult ReadNotificationCommandResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return NotificationCommandResult.Failed;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out JsonElement okElement) && okElement.GetBoolean();
            if (!ok)
            {
                return NotificationCommandResult.Failed;
            }

            string? delivery = root.TryGetProperty("delivery", out JsonElement deliveryElement)
                ? deliveryElement.GetString()
                : null;

            return delivery switch
            {
                "toast" => NotificationCommandResult.Toast,
                "fallback" => NotificationCommandResult.Fallback,
                _ => NotificationCommandResult.Failed
            };
        }
        catch
        {
            return NotificationCommandResult.Failed;
        }
    }

    private void FireAndForget(string json)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            _writer!.WriteLine(json);
        }
        catch (Exception ex)
        {
            SwkLogger.Debug($"UiPipeClient.FireAndForget error: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                string? line = await _reader!.ReadLineAsync(ct);
                if (line == null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(line);
                string? type = doc.RootElement.TryGetProperty("type", out JsonElement typeElement)
                    ? typeElement.GetString()
                    : null;

                switch (type)
                {
                    case "TRAY_EXITING":
                        TrayExiting?.Invoke();
                        break;
                    case "SHOW":
                        ShowRequested?.Invoke();
                        break;
                    case "FRIEND_SHOP_CLOSING":
                    {
                        string machineName = doc.RootElement.TryGetProperty("machineName", out JsonElement machineNameElement)
                            ? machineNameElement.GetString() ?? string.Empty
                            : string.Empty;
                        string friendShareName = doc.RootElement.TryGetProperty("shareName", out JsonElement shareNameElement)
                            ? shareNameElement.GetString() ?? string.Empty
                            : string.Empty;
                        FriendShopClosingReceived?.Invoke(machineName, friendShareName);
                        break;
                    }
                    case "INCOMING_INTERACTION":
                    {
                        if (doc.RootElement.TryGetProperty("entry", out JsonElement entryElement))
                        {
                            SwkIncomingInteractionRecord? entry = entryElement.Deserialize<SwkIncomingInteractionRecord>();
                            if (entry is not null)
                            {
                                IncomingInteractionReceived?.Invoke(entry);
                            }
                        }

                        break;
                    }
                    default:
                        await _responseChannel.Writer.WriteAsync(line, ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SwkLogger.Debug($"UiPipeClient.ReceiveLoopAsync ended: {ex.Message}");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                Disconnect();
            }
        }
    }
}
