using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShareWorkin.SMB;

public static class SmbConnectionHelper
{
    // WNetAddConnection2 (mpr.dll) は P9NP (WSL Plan 9) が MPR チェーンに入っている環境で
    // error 67 を返すことが確認されたため、NetUseAdd (netapi32.dll) に置き換える。
    // NetUseAdd は LanmanWorkstation サービスに直接つながり、MPR を経由しない。
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUseAdd(
        string? serverName,
        int level,
        ref UseInfo2 buf,
        out int parmError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUseDel(
        string? serverName,
        string useName,
        int forceLevel);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(
        string name,
        int flags,
        bool force);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UseInfo2
    {
        public string? ui2_local;
        public string ui2_remote;
        public string ui2_password;
        public uint ui2_status;
        public uint ui2_asg_type;
        public uint ui2_refcount;
        public uint ui2_usecount;
        public string ui2_username;
        public string? ui2_domainname;
    }

    private const int USE_DISKDEV = 0;
    private const int USE_IPC = 3;
    private const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;

    /// <summary>
    /// 認証情報をセッションに登録する（エクスプローラーは開かない）
    /// </summary>
    public static bool EnsureConnection(string uncPath, string userName, string password, string? machineName = null)
    {
        try
        {
            return AddConnection(uncPath, userName, password, machineName);
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"EnsureConnection failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 認証情報をセッションに登録して、UNC パスを開く
    /// </summary>
    public static bool ConnectAndOpen(string uncPath, string userName, string password, string? machineName = null)
    {
        try
        {
            if (!AddConnection(uncPath, userName, password, machineName))
            {
                SwkLogger.Warn($"Failed to add connection for {uncPath}");
                return false;
            }

            OpenInExplorer(uncPath);
            return true;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"ConnectAndOpen failed: {ex.Message}");
            return false;
        }
    }

    private static bool AddConnection(string uncPath, string userName, string password, string? machineName)
    {
        string server = ExtractServerPart(uncPath);
        if (string.IsNullOrWhiteSpace(server))
        {
            SwkLogger.Warn($"Invalid UNC path: {uncPath}");
            return false;
        }

        // 一度接続を切る（再接続時のため）。戻り値もログへ残す。
        ClearConnection(uncPath, force: false);

        // Windows は同一サーバー単位で資格情報を共有するため、共有名へ触る前に
        // IPC$ を同じ資格情報で握っておく。既存の anonymous/null セッションがある
        // 環境では、共有への NetUseAdd よりこちらを先に処理した方が 1219 を避けやすい。
        string ipcPath = server + @"\IPC$";
        int ipcResult = AddConnectionCore(ipcPath, userName, password, machineName, USE_IPC, out int ipcParmError);
        if (ipcResult == ERROR_SESSION_CREDENTIAL_CONFLICT)
        {
            SwkLogger.Debug($"IPC$ NetUseAdd returned 1219; clearing existing sessions to {server} and retrying");
            ClearServerConnections(server, uncPath, machineName);
            ipcResult = AddConnectionCore(ipcPath, userName, password, machineName, USE_IPC, out ipcParmError);
        }

        if (ipcResult != 0 && ipcResult != ERROR_SESSION_CREDENTIAL_CONFLICT)
        {
            SwkLogger.Debug($"IPC$ NetUseAdd returned {ipcResult} for {ipcPath} (parmError={ipcParmError}); continuing with share connection");
        }

        int result = AddConnectionCore(uncPath, userName, password, machineName, USE_DISKDEV, out int parmError);

        if (result == 0)
        {
            SwkLogger.Debug($"Successfully connected to {uncPath}");
            return true;
        }

        // 1219: SESSION_CREDENTIAL_CONFLICT
        // 同じサーバーに別認証 (anonymous プローブセッション等) が居座っている場合に出る。
        // サーバー全体と IPC$ のセッションを強制削除し、IPC$ から認証を作り直してリトライする。
        if (result == ERROR_SESSION_CREDENTIAL_CONFLICT)
        {
            SwkLogger.Debug($"NetUseAdd returned 1219; clearing existing sessions to {server} and retrying");
            ClearServerConnections(server, uncPath, machineName);

            ipcResult = AddConnectionCore(ipcPath, userName, password, machineName, USE_IPC, out ipcParmError);
            if (ipcResult != 0)
            {
                SwkLogger.Warn($"IPC$ NetUseAdd returned {ipcResult} for {ipcPath} after clearing (parmError={ipcParmError})");
            }

            result = AddConnectionCore(uncPath, userName, password, machineName, USE_DISKDEV, out parmError);
            if (result == 0)
            {
                SwkLogger.Debug($"Successfully connected to {uncPath} after clearing conflicting sessions");
                return true;
            }
        }

        SwkLogger.Warn($"NetUseAdd returned {result} for {uncPath} (parmError={parmError})");
        return false;
    }

    private static int AddConnectionCore(
        string uncPath,
        string userName,
        string password,
        string? machineName,
        uint asgType,
        out int parmError)
    {
        var useInfo = new UseInfo2
        {
            ui2_local = null,
            ui2_remote = uncPath,
            ui2_password = password,
            ui2_asg_type = asgType,
            ui2_username = userName,
            ui2_domainname = string.IsNullOrWhiteSpace(machineName) ? null : machineName,
        };

        return NetUseAdd(null, 2, ref useInfo, out parmError);
    }

    private static void ClearServerConnections(string server, string sharePath, string? machineName)
    {
        ClearConnection(sharePath, force: true);
        ClearConnection(server + @"\IPC$", force: true);
        ClearConnection(server, force: true);

        string shareName = ExtractShareName(sharePath);
        if (!string.IsNullOrWhiteSpace(machineName) &&
            !string.Equals(NormalizeServerName(server), NormalizeServerName(machineName), StringComparison.OrdinalIgnoreCase))
        {
            string machineServer = machineName.StartsWith(@"\\", StringComparison.Ordinal)
                ? machineName
                : $@"\\{machineName}";

            if (!string.IsNullOrWhiteSpace(shareName))
            {
                ClearConnection(machineServer + @"\" + shareName, force: true);
            }

            ClearConnection(machineServer + @"\IPC$", force: true);
            ClearConnection(machineServer, force: true);
        }
    }

    private static void ClearConnection(string path, bool force)
    {
        int forceLevel = force ? 2 : 0;
        int netUseResult = NetUseDel(null, path, forceLevel);
        int wnetResult = WNetCancelConnection2(path, 0, force);
        SwkLogger.Debug($"ClearConnection {path}: NetUseDel={netUseResult}, WNetCancelConnection2={wnetResult}, force={force}");
    }

    private static string ExtractServerPart(string uncPath)
    {
        if (string.IsNullOrEmpty(uncPath) || !uncPath.StartsWith(@"\\")) return string.Empty;
        int idx = uncPath.IndexOf('\\', 2);
        return idx < 0 ? uncPath : uncPath.Substring(0, idx);
    }

    private static string ExtractShareName(string uncPath)
    {
        if (string.IsNullOrEmpty(uncPath) || !uncPath.StartsWith(@"\\")) return string.Empty;
        int serverEnd = uncPath.IndexOf('\\', 2);
        if (serverEnd < 0 || serverEnd + 1 >= uncPath.Length) return string.Empty;
        int shareEnd = uncPath.IndexOf('\\', serverEnd + 1);
        return shareEnd < 0
            ? uncPath[(serverEnd + 1)..]
            : uncPath[(serverEnd + 1)..shareEnd];
    }

    private static string NormalizeServerName(string server)
    {
        return server.Trim().TrimStart('\\');
    }

    private static void OpenInExplorer(string uncPath)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "explorer.exe",
                Arguments = uncPath,
                UseShellExecute = true,
            };
            using (Process p = Process.Start(psi)!)
            {
                SwkLogger.Debug($"Opened {uncPath} in Explorer");
            }
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"Failed to open Explorer: {ex.Message}");
        }
    }
}
