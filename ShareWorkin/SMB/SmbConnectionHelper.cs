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
        // 一度接続を切る（再接続時のため）
        NetUseDel(null, uncPath, 0);

        var useInfo = new UseInfo2
        {
            ui2_local = null,
            ui2_remote = uncPath,
            ui2_password = password,
            ui2_asg_type = USE_DISKDEV,
            ui2_username = userName,
            ui2_domainname = string.IsNullOrWhiteSpace(machineName) ? null : machineName,
        };

        int result = NetUseAdd(null, 2, ref useInfo, out int parmError);

        if (result == 0)
        {
            SwkLogger.Debug($"Successfully connected to {uncPath}");
            return true;
        }

        // 1219: SESSION_CREDENTIAL_CONFLICT
        // 同じサーバーに別認証 (anonymous プローブセッション等) が居座っている場合に出る。
        // サーバー全体と IPC$ のセッションを強制削除してリトライする。
        if (result == 1219)
        {
            string server = ExtractServerPart(uncPath);
            if (!string.IsNullOrEmpty(server))
            {
                SwkLogger.Debug($"NetUseAdd returned 1219; clearing existing sessions to {server} and retrying");
                NetUseDel(null, server, 2);              // \\192.168.0.218
                NetUseDel(null, server + @"\IPC$", 2);   // \\192.168.0.218\IPC$

                result = NetUseAdd(null, 2, ref useInfo, out parmError);
                if (result == 0)
                {
                    SwkLogger.Debug($"Successfully connected to {uncPath} after clearing conflicting sessions");
                    return true;
                }
            }
        }

        SwkLogger.Warn($"NetUseAdd returned {result} for {uncPath} (parmError={parmError})");
        return false;
    }

    private static string ExtractServerPart(string uncPath)
    {
        if (string.IsNullOrEmpty(uncPath) || !uncPath.StartsWith(@"\\")) return string.Empty;
        int idx = uncPath.IndexOf('\\', 2);
        return idx < 0 ? uncPath : uncPath.Substring(0, idx);
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
