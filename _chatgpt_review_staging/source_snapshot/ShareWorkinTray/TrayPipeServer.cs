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

    public void Start()
    {
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
                var pipe = CreatePipeServer();
                await pipe.WaitForConnectionAsync(ct);
                var session = new TrayPipeSession(pipe, _tray);
                var prev = _activeSession;
                _activeSession = session;
                prev?.Dispose();
                _ = session.RunAsync(ct).ContinueWith(_ =>
                {
                    if (ReferenceEquals(_activeSession, session))
                    {
                        _activeSession = null;
                        _tray.NotifyUiDisconnected();
                    }
                }, TaskScheduler.Default);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                SwkLogger.Warn($"TrayPipeServer accept error: {ex.Message}");
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

        security.AddAccessRule(new PipeAccessRule(adminsSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(authUsersSid,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

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

                case "OPEN_SHOP":
                {
                    string shopFolder = root.TryGetProperty("shopFolder", out var sf) ? sf.GetString() ?? "" : "";
                    string shareName = root.TryGetProperty("shareName", out var sn) ? sn.GetString() ?? "" : "";
                    string profileLabel = root.TryGetProperty("profileLabel", out var pl) ? pl.GetString() ?? shareName : shareName;
                    int arVal = root.TryGetProperty("accessRight", out var ar) ? ar.GetInt32() : 0;
                    bool authorize = root.TryGetProperty("authorizeOwnership", out var ao) && ao.GetBoolean();
                    var accessRight = arVal == 1 ? ShareAccessRight.Read : ShareAccessRight.Full;

                    var (ok, error, needsOwnership, prompt, blocked) =
                        _tray.OpenShop(shopFolder, shareName, profileLabel, accessRight, authorize);

                    await SendAsync(JsonSerializer.Serialize(new
                    {
                        type = "OPEN_RESULT",
                        ok,
                        error,
                        needsOwnership,
                        prompt = prompt.ToString(),
                        blockedPaths = blocked
                    }));
                    break;
                }

                case "CLOSE_SHOP":
                {
                    bool ok = _tray.CloseShop();
                    await SendAsync($"{{\"type\":\"CLOSE_RESULT\",\"ok\":{(ok ? "true" : "false")}}}");
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
                    string text = root.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                    string? folder = root.TryGetProperty("folder", out var f) && f.ValueKind != JsonValueKind.Null ? f.GetString() : null;
                    _tray.ShowBalloonTip(title, text, folder);
                    break;
                }

                case "EXIT_APP":
                    _tray.ExitApp(fromUiRequest: true);
                    break;

                case "RELOAD_SETTINGS":
                    _tray.LoadSettings();
                    break;

                case "SET_SUBFOLDER_PERMISSION":
                {
                    string path = root.TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty;
                    bool isSharedOff = root.TryGetProperty("isSharedOff", out var off) && off.GetBoolean();
                    bool isReadOnly = root.TryGetProperty("isReadOnly", out var ro) && ro.GetBoolean();
                    bool ok = _tray.SetSubfolderPermission(path, isSharedOff, isReadOnly);
                    await SendAsync($"{{\"type\":\"SET_SUBFOLDER_PERMISSION_RESULT\",\"ok\":{(ok ? "true" : "false")}}}");
                    break;
                }

                case "RESET_PATH_TO_INHERITED":
                {
                    string path = root.TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty;
                    bool ok = _tray.ResetPathToInherited(path);
                    await SendAsync($"{{\"type\":\"RESET_PATH_TO_INHERITED_RESULT\",\"ok\":{(ok ? "true" : "false")}}}");
                    break;
                }

                case "MARK_ACTION_AFTERCARE":
                {
                    string shopRootPath = root.TryGetProperty("shopRootPath", out var sr) ? sr.GetString() ?? string.Empty : string.Empty;
                    string affectedPath = root.TryGetProperty("affectedPath", out var ap) ? ap.GetString() ?? string.Empty : string.Empty;
                    string policySourceFolder = root.TryGetProperty("policySourceFolder", out var ps) ? ps.GetString() ?? string.Empty : string.Empty;
                    string reasonText = root.TryGetProperty("reason", out var rs) ? rs.GetString() ?? string.Empty : string.Empty;
                    bool parsed = Enum.TryParse(reasonText, ignoreCase: true, out SharePolicyRepairReason reason);
                    bool ok = parsed && _tray.MarkActionAftercare(shopRootPath, affectedPath, policySourceFolder, reason);
                    await SendAsync($"{{\"type\":\"MARK_ACTION_AFTERCARE_RESULT\",\"ok\":{(ok ? "true" : "false")}}}");
                    break;
                }

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
        _reader.Dispose();
        _writer.Dispose();
        _pipe.Dispose();
        _writeLock.Dispose();
    }
}
