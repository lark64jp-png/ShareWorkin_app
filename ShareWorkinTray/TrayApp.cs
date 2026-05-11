using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ShareWorkin.SMB;
using Forms = System.Windows.Forms;

namespace ShareWorkinTray;

public sealed class TrayApp : IDisposable
{
    private static readonly string AppHomeDirectory = @"C:\MyApps\ShareWorkin";
    private static readonly string SettingsPath = Path.Combine(AppHomeDirectory, "settings.json");

    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    internal readonly TrayPipeServer PipeServer;

    private bool _isShopOpen;
    private string? _shopFolder;
    private string? _activeShareName;
    private bool _wasOpenAtLastShutdown;
    private ShareAccessRight _shareAccessRight = ShareAccessRight.Full;
    private string? _lastBalloonFolder;

    public bool IsShopOpen => _isShopOpen;
    public string? ShopFolder => _shopFolder;

    public TrayApp()
    {
        PipeServer = new TrayPipeServer(this);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "ShareWorkin",
            Visible = false
        };
        _notifyIcon.MouseDoubleClick += (_, _) => OpenUiProcess();
        _notifyIcon.BalloonTipClicked += (_, _) => OpenLastBalloonFolder();

        _trayMenu = new Forms.ContextMenuStrip();
        var showItem = new Forms.ToolStripMenuItem("画面を開く");
        showItem.Click += (_, _) => OpenUiProcess();
        var exitItem = new Forms.ToolStripMenuItem("アプリを終了");
        exitItem.Click += (_, _) => RequestExitWithAuth();
        _trayMenu.Items.Add(showItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);
        _notifyIcon.ContextMenuStrip = _trayMenu;
    }

    public void Start()
    {
        LoadSettings();
        SmbController.OnShopClosingReceived = HandleFriendShopClosingReceived;
        _notifyIcon.Visible = true;
        RestoreOpenShopIfNeeded();
        PipeServer.Start();
    }

    public void Dispose()
    {
        PipeServer.Stop();
        if (_isShopOpen && !string.IsNullOrWhiteSpace(_activeShareName) && !string.IsNullOrWhiteSpace(_shopFolder))
        {
            try { SmbController.CloseShopSequence(_activeShareName, _shopFolder); }
            catch (Exception ex) { SwkLogger.Warn($"TrayApp.Dispose CloseShop error: {ex.Message}"); }
        }
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayMenu.Dispose();
    }

    public void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            using var fs = File.OpenRead(SettingsPath);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            if (root.TryGetProperty("ShopFolder", out var sf) && sf.ValueKind != JsonValueKind.Null)
                _shopFolder = sf.GetString();
            else if (root.TryGetProperty("WatchFolder", out var wf) && wf.ValueKind != JsonValueKind.Null)
                _shopFolder = wf.GetString();
            if (root.TryGetProperty("IsOpenAtLastShutdown", out var open))
                _wasOpenAtLastShutdown = open.GetBoolean();
            if (root.TryGetProperty("AccessLevel", out var al))
                _shareAccessRight = string.Equals(al.GetString(), "Read", StringComparison.OrdinalIgnoreCase)
                    ? ShareAccessRight.Read : ShareAccessRight.Full;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            SwkLogger.Warn($"TrayApp.LoadSettings error: {ex.Message}");
        }
    }

    private void RestoreOpenShopIfNeeded()
    {
        if (!_wasOpenAtLastShutdown || string.IsNullOrWhiteSpace(_shopFolder)) return;

        if (!Directory.Exists(_shopFolder))
        {
            PatchSettingsOpenState(false, _shopFolder);
            ShowBalloonTip("お店を再開できませんでした",
                "前回のお店のフォルダーが見つかりません。\n画面を開いて設定を確認してください。", null);
            return;
        }

        string shareName = DeriveShareName(_shopFolder);
        if (string.IsNullOrWhiteSpace(shareName)) return;

        var (ok, error, _, _, _) = OpenShop(_shopFolder, shareName, shareName, _shareAccessRight, false);
        if (!ok)
        {
            SwkLogger.Warn($"TrayApp.RestoreOpenShopIfNeeded failed: {error}");
            PatchSettingsOpenState(false, _shopFolder);
            ShowBalloonTip("お店を再開できませんでした",
                "前回の状態と異なる可能性があります。\n画面を開いて確認してください。", _shopFolder);
        }
    }

    public (bool Ok, string? Error, bool NeedsOwnership, OwnershipChangePrompt OwnershipPrompt, IReadOnlyList<string>? BlockedPaths)
        OpenShop(string shopFolder, string shareName, string profileLabel, ShareAccessRight accessRight, bool authorizeOwnership)
    {
        var request = new ShopOpenRequest(shareName, shopFolder, profileLabel, accessRight);
        var result = SmbController.OpenShopSequence(request,
            userAuthorizedOwnershipChange: authorizeOwnership,
            onInviteRequested: PipeServer.RequestInviteApprovalAsync);

        if (!result.Succeeded && result.OwnershipPrompt != OwnershipChangePrompt.None)
            return (false, null, true, result.OwnershipPrompt, result.BlockedPaths);

        if (!result.Succeeded)
            return (false, result.FailureMessage, false, OwnershipChangePrompt.None, result.BlockedPaths);

        _isShopOpen = true;
        _shopFolder = shopFolder;
        _activeShareName = shareName;
        _shareAccessRight = accessRight;
        PatchSettingsOpenState(true, shopFolder);
        return (true, null, false, OwnershipChangePrompt.None, null);
    }

    public bool CloseShop()
    {
        if (!_isShopOpen) return true;
        bool ok = !string.IsNullOrWhiteSpace(_activeShareName) && !string.IsNullOrWhiteSpace(_shopFolder)
            && SmbController.CloseShopSequence(_activeShareName!, _shopFolder!);
        _isShopOpen = false;
        PatchSettingsOpenState(false, _shopFolder);
        return ok;
    }

    public void BroadcastShopClosing() => _ = SmbController.BroadcastShopClosingAsync();
    public void BroadcastPermissionChanged() => _ = SmbController.BroadcastPermissionChangedAsync();

    private void HandleFriendShopClosingReceived(string machineName, string shareName)
    {
        _ = PipeServer.PushMessageAsync(JsonSerializer.Serialize(new
        {
            type = "FRIEND_SHOP_CLOSING",
            machineName,
            shareName
        }));
    }

    public void ShowBalloonTip(string title, string text, string? folder)
    {
        _lastBalloonFolder = folder;
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(5000);
    }

    public void NotifyUiDisconnected()
    {
        if (_isShopOpen)
            ShowBalloonTip("お店は開いたままです", "お店は開いたまま、管理画面は閉じています。", _shopFolder);
    }

    private void OpenLastBalloonFolder()
    {
        string? path = _lastBalloonFolder;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { SwkLogger.Warn($"OpenLastBalloonFolder error: {ex.Message}"); }
    }

    public void OpenUiProcess()
    {
        if (PipeServer.HasConnectedClient)
        {
            _ = PipeServer.PushMessageAsync("{\"type\":\"SHOW\"}");
            return;
        }
        try
        {
            string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (exeDir == null) return;
            string uiExe = Path.Combine(exeDir, "ShareWorkin.exe");
            if (!File.Exists(uiExe)) return;
            Process.Start(new ProcessStartInfo(uiExe) { UseShellExecute = true });
        }
        catch (Exception ex) { SwkLogger.Warn($"OpenUiProcess error: {ex.Message}"); }
    }

    public void ExitApp(bool fromUiRequest = false)
    {
        bool wasOpen = _isShopOpen;
        CloseShop();
        if (wasOpen)
            PatchSettingsOpenState(true, _shopFolder);
        if (!fromUiRequest)
            _ = PipeServer.PushMessageAsync("{\"type\":\"TRAY_EXITING\"}");
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown());
    }

    private void RequestExitWithAuth()
    {
        if (EntryPasswordManager.IsConfigured)
        {
            string? pw = TrayPasswordDialog.Show("終了するとお店も閉まります。\nパスワードを入力してください。");
            if (pw == null) return;
            if (!EntryPasswordManager.Verify(pw))
            {
                Forms.MessageBox.Show("パスワードが違います。", "ShareWorkin",
                    Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
                return;
            }
            ExitApp();
        }
        else
        {
            var r = Forms.MessageBox.Show("ShareWorkin を終了しますか？\nお店は閉まります。",
                "ShareWorkin", Forms.MessageBoxButtons.OKCancel, Forms.MessageBoxIcon.Question);
            if (r == Forms.DialogResult.OK) ExitApp();
        }
    }

    private void PatchSettingsOpenState(bool isOpen, string? shopFolder)
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var node = JsonNode.Parse(File.ReadAllText(SettingsPath));
            if (node is not JsonObject obj) return;
            obj["IsOpenAtLastShutdown"] = isOpen;
            if (shopFolder != null) obj["ShopFolder"] = shopFolder;
            File.WriteAllText(SettingsPath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { SwkLogger.Warn($"TrayApp.PatchSettingsOpenState error: {ex.Message}"); }
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            string? dir = Path.GetDirectoryName(Environment.ProcessPath);
            string path = Path.Combine(dir ?? string.Empty, "app.ico");
            if (File.Exists(path)) return new System.Drawing.Icon(path);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    internal static string DeriveShareName(string shopFolder)
    {
        string trimmed = shopFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        if (!string.IsNullOrEmpty(name)) return name;
        string root = Path.GetPathRoot(shopFolder) ?? string.Empty;
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).TrimEnd(':');
    }
}
