using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ShareWorkin.SMB;

namespace ShareWorkinTray;

public sealed class TrayPipeServer
{
    private const string PipeName = "ShareWorkin_TrayPipe";
    private readonly TrayApp _tray;
    private CancellationTokenSource? _cts;
    private volatile TrayPipeSession? _activeSession;

    public bool HasConnectedClient => _activeSession?.IsConnected == true;

    public TrayPipeServer(TrayApp tray) => _tray = tray;

    internal static string BuildNotificationCorrelationId(string title, string? text, string? folder)
    {
        string raw = $"{title}\n{text ?? string.Empty}\n{folder ?? string.Empty}";
        unchecked
        {
            uint hash = 2166136261;
            foreach (char ch in raw)
            {
                hash ^= ch;
                hash *= 16777619;
            }

            return hash.ToString("x8");
        }
    }

    public void Start()
    {
        SwkLogger.Info("TrayPipeServer start requested");
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _activeSession?.Dispose();
        _activeSession = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SwkLogger.Info("TrayPipeServer accept waiting");
                var pipe = CreatePipeServer();
                SwkLogger.Info("TrayPipeServer pipe created");
                await pipe.WaitForConnectionAsync(ct);
                SwkLogger.Info("TrayPipeServer accept connected");
                var session = new TrayPipeSession(pipe, _tray);
                var prev = _activeSession;
                _activeSession = session;
                _ = session.RunAsync(ct).ContinueWith(_ =>
                {
                    if (ReferenceEquals(_activeSession, session))
                    {
                        _activeSession = null;
                        _tray.NotifyUiDisconnected();
                    }
                }, TaskScheduler.Default);
                if (prev is not null)
                {
                    try
                    {
                        prev.Dispose();
                    }
                    catch (Exception ex)
                    {
                        SwkLogger.Debug($"TrayPipeServer previous session dispose skipped: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                SwkLogger.Warn($"TrayPipeServer accept error: type={ex.GetType().FullName} message={ex.Message}");
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private static NamedPipeServerStream CreatePipeServer()
    {
        PipeSecurity security = BuildPipeSecurity();
        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security,
            HandleInheritability.None);
    }

    private static PipeSecurity BuildPipeSecurity()
    {
        var security = new PipeSecurity();
        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var authUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        SecurityIdentifier currentUserSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current user SID could not be resolved.");

        security.AddAccessRule(new PipeAccessRule(adminsSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(authUsersSid,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(currentUserSid, PipeAccessRights.FullControl, AccessControlType.Allow));

        return security;
    }

    public Task PushMessageAsync(string json)
    {
        var session = _activeSession;
        return session?.IsConnected == true ? session.SendAsync(json) : Task.CompletedTask;
    }

}

internal sealed class TrayPipeSession : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TrayApp _tray;
    private bool _disposed;

    public bool IsConnected => !_disposed && _pipe.IsConnected;

    public TrayPipeSession(NamedPipeServerStream pipe, TrayApp tray)
    {
        _pipe = pipe;
        _tray = tray;
        _reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _pipe.IsConnected)
            {
                string? line = await _reader.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                string captured = line;
                _ = Task.Run(() => ProcessMessageAsync(captured, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SwkLogger.Debug($"TrayPipeSession.RunAsync ended: {ex.Message}"); }
    }

    private async Task ProcessMessageAsync(string line, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string? cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : null;

            switch (cmd)
            {
                case "GET_STATUS":
                    await SendAsync(JsonSerializer.Serialize(new
                    {
                        type = "STATUS",
                        isShopOpen = _tray.IsShopOpen,
                        shopFolder = _tray.ShopFolder
                    }));
                    break;

                case "GET_SHARE_SNAPSHOT":
                {
                    GetShareSnapshotRequest request = new(
                        root.TryGetProperty("requestId", out var requestIdElement) &&
                        requestIdElement.ValueKind != JsonValueKind.Null
                            ? requestIdElement.GetString()
                            : null,
                        root.TryGetProperty("shareName", out var shareNameElement) &&
                        shareNameElement.ValueKind != JsonValueKind.Null
                            ? shareNameElement.GetString()
                            : null,
                        root.TryGetProperty("shopRootPath", out var shopRootPathElement) &&
                        shopRootPathElement.ValueKind != JsonValueKind.Null
                            ? shopRootPathElement.GetString()
                            : null,
                        root.TryGetProperty("forceRefresh", out var forceRefreshElement) &&
                        forceRefreshElement.ValueKind == JsonValueKind.True);
                    TrayCommandResponse<ShareSnapshotPayload> response = await _tray.GetShareSnapshotAsync(request);
                    await SendAsync(JsonSerializer.Serialize(response));
                    break;
                }

                case "SYNC_SHOP_OPENED":
                {
                    string shopFolder = root.TryGetProperty("shopFolder", out var sf) ? sf.GetString() ?? "" : "";
                    string shareName = root.TryGetProperty("shareName", out var sn) ? sn.GetString() ?? "" : "";
                    int arVal = root.TryGetProperty("accessRight", out var ar) ? ar.GetInt32() : 0;
                    var accessRight = arVal == 1 ? ShareAccessRight.Read : ShareAccessRight.Full;

                    bool ok = _tray.UpdateShopOpenedState(shopFolder, shareName, accessRight);

                    await SendAsync(JsonSerializer.Serialize(new
                    {
                        type = "SYNC_SHOP_OPENED_RESULT",
                        ok
                    }));
                    break;
                }

                case "SYNC_SHOP_CLOSED":
                {
                    bool ok = _tray.UpdateShopClosedState();
                    await SendAsync($"{{\"type\":\"SYNC_SHOP_CLOSED_RESULT\",\"ok\":{(ok ? "true" : "false")}}}");
                    break;
                }

                case "BROADCAST_CLOSING":
                    _tray.BroadcastShopClosing();
                    break;

                case "BROADCAST_PERMISSION":
                    _tray.BroadcastPermissionChanged();
                    break;

                case "SHOW_BALLOON":
                {
                    string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    string? text = root.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                    string safeText = text ?? string.Empty;
                    string? folder = root.TryGetProperty("folder", out var f) && f.ValueKind != JsonValueKind.Null ? f.GetString() : null;
                    string corr = TrayPipeServer.BuildNotificationCorrelationId(title, safeText, folder);
                    string folderText = string.IsNullOrWhiteSpace(folder) ? "-" : folder;
                    int textLen = safeText.Length;
                    SwkLogger.Info(
                        $"NotificationTrace.Received corr={corr} title={title} folder={folderText} textLen={textLen}");
                    NotificationDisplayResult result = _tray.ShowBalloonTip(title, safeText, folder);
                    SwkLogger.Info(
                        $"NotificationTrace.Completed corr={corr} title={title} folder={folderText} textLen={textLen} delivery={result}");
                    await SendAsync(JsonSerializer.Serialize(new
                    {
                        type = "SHOW_BALLOON_RESULT",
                        ok = result != NotificationDisplayResult.Failed,
                        delivery = result switch
                        {
                            NotificationDisplayResult.Toast => "toast",
                            NotificationDisplayResult.Fallback => "fallback",
                            _ => "failed"
                        }
                    }));
                    break;
                }

                case "TEST_NOTIFICATION":
                {
                    string? folder = root.TryGetProperty("folder", out var f) && f.ValueKind != JsonValueKind.Null
                        ? f.GetString()
                        : null;
                    SwkLogger.Info("TrayPipeServer TEST_NOTIFICATION received");
                    NotificationDisplayResult result = _tray.ShowTestNotification(folder);
                    await SendAsync(JsonSerializer.Serialize(new
                    {
                        type = "TEST_NOTIFICATION_RESULT",
                        ok = result != NotificationDisplayResult.Failed,
                        delivery = result switch
                        {
                            NotificationDisplayResult.Toast => "toast",
                            NotificationDisplayResult.Fallback => "fallback",
                            _ => "failed"
                        }
                    }));
                    break;
                }

                case "EXIT_APP":
                    _tray.ExitApp(fromUiRequest: true);
                    break;

                case "RELOAD_SETTINGS":
                    _tray.LoadSettings();
                    break;

                case "PING":
                    await SendAsync("{\"type\":\"PONG\"}");
                    break;
            }
        }
        catch (Exception ex) { SwkLogger.Warn($"TrayPipeSession.ProcessMessageAsync error: {ex.Message}"); }
    }

    public async Task SendAsync(string json)
    {
        if (_disposed) return;
        await _writeLock.WaitAsync();
        try { await _writer.WriteLineAsync(json); }
        catch (Exception ex) { SwkLogger.Debug($"TrayPipeSession.SendAsync error: {ex.Message}"); }
        finally { _writeLock.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _pipe.Disconnect(); } catch { }
        try { _reader.Dispose(); } catch { }
        try { _writer.Dispose(); } catch { }
        try { _pipe.Dispose(); } catch { }
        try { _writeLock.Dispose(); } catch { }
    }
}
