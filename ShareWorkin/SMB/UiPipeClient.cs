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

public sealed record TrayStatus(bool IsShopOpen, string? ShopFolder);

public sealed record ShopOpenOutcome(
    bool Ok,
    string? Error,
    bool NeedsOwnership,
    string? OwnershipPrompt,
    IReadOnlyList<string>? BlockedPaths);

public sealed class UiPipeClient : IDisposable
{
    private const string PipeName = "ShareWorkin_TrayPipe";

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _cmdLock = new(1, 1);
    private readonly Channel<string> _responseChannel = Channel.CreateBounded<string>(16);
    private CancellationTokenSource? _receiveCts;

    public event Action? TrayExiting;
    public event Action? ShowRequested;
    public event Action<string, string>? FriendShopClosingReceived;

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

    public ShopOpenOutcome? OpenShop(string shopFolder, string shareName, string profileLabel, int accessRight, bool authorizeOwnership)
    {
        var cmdObj = new
        {
            cmd = "OPEN_SHOP",
            shopFolder,
            shareName,
            profileLabel,
            accessRight,
            authorizeOwnership
        };
        var json = SendCommand(JsonSerializer.Serialize(cmdObj), timeoutMs: 60000);
        if (json == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okP) && okP.GetBoolean();
            string? error = root.TryGetProperty("error", out var e) && e.ValueKind != JsonValueKind.Null
                ? e.GetString() : null;
            bool needsOwnership = root.TryGetProperty("needsOwnership", out var no) && no.GetBoolean();
            string? prompt = root.TryGetProperty("prompt", out var p) ? p.GetString() : null;
            List<string>? blocked = null;
            if (root.TryGetProperty("blockedPaths", out var bp) && bp.ValueKind == JsonValueKind.Array)
            {
                blocked = [];
                foreach (var item in bp.EnumerateArray())
                    if (item.GetString() is string s) blocked.Add(s);
            }
            return new ShopOpenOutcome(ok, error, needsOwnership, prompt, blocked);
        }
        catch { return null; }
    }

    public bool CloseShop()
    {
        var json = SendCommand("{\"cmd\":\"CLOSE_SHOP\"}");
        if (json == null) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch { return false; }
    }

    public void BroadcastClosing() => FireAndForget("{\"cmd\":\"BROADCAST_CLOSING\"}");
    public void BroadcastPermission() => FireAndForget("{\"cmd\":\"BROADCAST_PERMISSION\"}");
    public void ReloadSettings() => FireAndForget("{\"cmd\":\"RELOAD_SETTINGS\"}");

    public void ShowBalloonTip(string title, string text, string? folder)
    {
        FireAndForget(JsonSerializer.Serialize(new { cmd = "SHOW_BALLOON", title, text, folder }));
    }

    public bool SetSubfolderPermission(string path, bool isSharedOff, bool isReadOnly)
    {
        var json = SendCommand(JsonSerializer.Serialize(new
        {
            cmd = "SET_SUBFOLDER_PERMISSION",
            path,
            isSharedOff,
            isReadOnly
        }), timeoutMs: 30000);
        return ReadOk(json);
    }

    public bool ResetPathToInherited(string path)
    {
        var json = SendCommand(JsonSerializer.Serialize(new
        {
            cmd = "RESET_PATH_TO_INHERITED",
            path
        }), timeoutMs: 30000);
        return ReadOk(json);
    }

    public bool MarkActionAftercare(
        string shopRootPath,
        string affectedPath,
        string policySourceFolder,
        SharePolicyRepairReason reason)
    {
        var json = SendCommand(JsonSerializer.Serialize(new
        {
            cmd = "MARK_ACTION_AFTERCARE",
            shopRootPath,
            affectedPath,
            policySourceFolder,
            reason = reason.ToString()
        }), timeoutMs: 30000);
        return ReadOk(json);
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
            return null;
        }
        finally { _cmdLock.Release(); }
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
                    default:
                        await _responseChannel.Writer.WriteAsync(line, ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SwkLogger.Debug($"UiPipeClient.ReceiveLoopAsync ended: {ex.Message}"); }
    }

}
