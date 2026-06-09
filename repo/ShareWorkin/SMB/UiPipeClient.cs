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
        _reader?.Dispose(); _reader = null;
        _writer?.Dispose(); _writer = null;
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    public TrayStatus? GetStatus(int timeoutMs = 15000)
    {
        var json = SendCommand("{\"cmd\":\"GET_STATUS\"}", timeoutMs);
        if (json == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool isOpen = root.TryGetProperty("isShopOpen", out var o) && o.GetBoolean();
            string? folder = root.TryGetProperty("shopFolder", out var f) && f.ValueKind != JsonValueKind.Null
                ? f.GetString() : null;
            return new TrayStatus(isOpen, folder);
        }
        catch { return null; }
    }

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
        }, timeoutMs: 60000);

        return new ShopOpenOutcome(
            response.Ok,
            response.ErrorMessage,
            false,
            null,
            response.BlockedPaths);
    }

    public AdminCommandResponse CloseShop(string shopFolder, string shareName)
    {
        return _adminWorker.Execute(new AdminCommandRequest
        {
            Cmd = AdminProtocol.CloseShopCommand,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ShopRootPath = shopFolder,
            ShareName = shareName
        }, timeoutMs: 30000);
    }

    public AdminCommandResponse SetSubfolderPermission(string shopRootPath, string path, bool isSharedOff, bool isReadOnly)
    {
        return _adminWorker.Execute(new AdminCommandRequest
        {
            Cmd = AdminProtocol.SetSubfolderPermissionCommand,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ShopRootPath = shopRootPath,
            TargetPath = path,
            IsSharedOff = isSharedOff,
            IsReadOnly = isReadOnly
        }, timeoutMs: 30000);
    }

    public AdminCommandResponse ResetPathToInherited(string shopRootPath, string path)
    {
        return _adminWorker.Execute(new AdminCommandRequest
        {
            Cmd = AdminProtocol.ResetPathToInheritedCommand,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ShopRootPath = shopRootPath,
            TargetPath = path
        }, timeoutMs: 30000);
    }

    public AdminCommandResponse MarkActionAftercare(
        string shopRootPath,
        string affectedPath,
        string policySourceFolder,
        SharePolicyRepairReason reason)
    {
        return _adminWorker.Execute(new AdminCommandRequest
        {
            Cmd = AdminProtocol.MarkActionAftercareCommand,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ShopRootPath = shopRootPath,
            TargetPath = affectedPath,
            PolicySourceFolder = policySourceFolder,
            Reason = reason.ToString()
        }, timeoutMs: 30000);
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
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
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
            if (!IsConnected) return null;
            _writer!.WriteLine(json);
            using var cts = new CancellationTokenSource(timeoutMs);
            var task = _responseChannel.Reader.ReadAsync(cts.Token).AsTask();
            task.Wait();
            return task.IsCompletedSuccessfully ? task.Result : null;
        }
        catch (Exception ex)
        {
            SwkLogger.Debug($"UiPipeClient.SendCommand error: {ex.Message}");
            Disconnect();
            return null;
        }
        finally { _cmdLock.Release(); }
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
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
            if (!ok)
            {
                return NotificationCommandResult.Failed;
            }

            string? delivery = root.TryGetProperty("delivery", out var deliveryElement)
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
        if (!IsConnected) return;
        try { _writer!.WriteLine(json); }
        catch (Exception ex) { SwkLogger.Debug($"UiPipeClient.FireAndForget error: {ex.Message}"); }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                string? line = await _reader!.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                string? type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

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
                        string machineName = doc.RootElement.TryGetProperty("machineName", out var mn)
                            ? mn.GetString() ?? string.Empty
                            : string.Empty;
                        string shareName = doc.RootElement.TryGetProperty("shareName", out var sn)
                            ? sn.GetString() ?? string.Empty
                            : string.Empty;
                        FriendShopClosingReceived?.Invoke(machineName, shareName);
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
        catch (OperationCanceledException) { }
        catch (Exception ex) { SwkLogger.Debug($"UiPipeClient.ReceiveLoopAsync ended: {ex.Message}"); }
        finally
        {
            if (!ct.IsCancellationRequested)
                Disconnect();
        }
    }

}
