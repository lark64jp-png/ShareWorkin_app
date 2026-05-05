using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShareWorkin.SMB;

public static class SmbConnectionHelper
{
    // WNetAddConnection2 API for mapping network drives
    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetAddConnection2(
        ref NetResource netResource,
        string password,
        string username,
        uint flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetCancelConnection2(
        string name,
        uint flags,
        bool force);

    [StructLayout(LayoutKind.Sequential)]
    private struct NetResource
    {
        public uint dwScope;
        public uint dwType;
        public uint dwDisplayType;
        public uint dwUsage;
        public string lpLocalName;
        public string lpRemoteName;
        public string lpComment;
        public string lpProvider;
    }

    private const uint RESOURCETYPE_DISK = 1;
    private const uint CONNECT_UPDATE_PROFILE = 0x00000001;
    private const uint CONNECT_TEMPORARY = 0x00000004;

    /// <summary>
    /// 認証情報をセッションに登録する（エクスプローラーは開かない）
    /// </summary>
    public static bool EnsureConnection(string uncPath, string userName, string password)
    {
        try
        {
            return AddConnection(uncPath, userName, password);
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
    public static bool ConnectAndOpen(string uncPath, string userName, string password)
    {
        try
        {
            // パスワードをセッションに登録
            if (!AddConnection(uncPath, userName, password))
            {
                SwkLogger.Warn($"Failed to add connection for {uncPath}");
                return false;
            }

            // エクスプローラーで開く
            OpenInExplorer(uncPath);
            return true;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"ConnectAndOpen failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 認証情報をセッションに登録
    /// </summary>
    private static bool AddConnection(string uncPath, string userName, string password)
    {
        var netResource = new NetResource
        {
            dwScope = 2,           // RESOURCE_GLOBALNET
            dwType = RESOURCETYPE_DISK,
            dwDisplayType = 3,     // RESOURCEDISPLAYTYPE_SHARE
            dwUsage = 1,           // RESOURCEUSAGE_CONNECTABLE
            lpRemoteName = uncPath,
        };

        // 一度接続を切る（再接続時のため）
        WNetCancelConnection2(uncPath, 0, false);

        // 接続を追加（セッション内のみ）
        int result = WNetAddConnection2(ref netResource, password, userName, CONNECT_TEMPORARY);

        if (result == 0) // NO_ERROR
        {
            SwkLogger.Debug($"Successfully connected to {uncPath}");
            return true;
        }

        SwkLogger.Warn($"WNetAddConnection2 returned {result} for {uncPath}");
        return false;
    }

    /// <summary>
    /// エクスプローラーで UNC パスを開く
    /// </summary>
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
