using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ShareWorkin.SMB;

public sealed record TakeOwnershipPreflight(
    bool AllAccessible,
    bool EnumerationBlocked,
    IReadOnlyList<string> BlockedPaths);

public static class SmbNtfsManager
{
    private const string SidSystem = "*S-1-5-18";
    private const string SidAdministrators = "*S-1-5-32-544";
    private const string PermFull = "(OI)(CI)(F)";
    private const string PermReadOnly = "(OI)(CI)(RX)";

    private const uint WRITE_DAC = 0x00040000;
    private const uint WRITE_OWNER = 0x00080000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    // 草案6 §A: 「触らずに確かめる」原則。WRITE_DAC で開けるなら、所有権を変えずに ACL 修正できる。
    public static bool CanModifyAcl(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            using SafeFileHandle handle = CreateFileW(
                path, WRITE_DAC,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            return !handle.IsInvalid;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanTakeOwnership(string path)
    {
        try
        {
            using SafeFileHandle handle = CreateFileW(
                path, WRITE_OWNER,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            return !handle.IsInvalid;
        }
        catch
        {
            return false;
        }
    }

    // 草案6 §A: 半端な状態を作らないため、実行前に内包全件が処理可能であることを確かめる。
    // 列挙そのものが拒否される(完全ロック)場合は EnumerationBlocked=true で別経路に渡す。
    public static TakeOwnershipPreflight PreflightTakeOwnership(string shopRootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(shopRootPath);
        HashSet<string> blocked = new(StringComparer.OrdinalIgnoreCase);
        bool enumerationBlocked = false;

        if (!Directory.Exists(shopRootPath))
        {
            blocked.Add(shopRootPath);
            return new TakeOwnershipPreflight(false, false, blocked.ToList());
        }

        if (!CanTakeOwnership(shopRootPath))
        {
            blocked.Add(shopRootPath);
        }

        EnumerationOptions opts = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(shopRootPath, "*", opts))
            {
                if (!CanTakeOwnership(entry))
                {
                    blocked.Add(entry);
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            SwkLogger.Warn($"PreflightTakeOwnership enumeration denied: {ex.Message}");
            enumerationBlocked = true;
        }
        catch (Exception ex)
        {
            SwkLogger.Error("PreflightTakeOwnership enumeration error", ex);
            enumerationBlocked = true;
        }

        bool ok = blocked.Count == 0 && !enumerationBlocked;
        SwkLogger.Info($"PreflightTakeOwnership: ok={ok}, enumBlocked={enumerationBlocked}, blockedCount={blocked.Count}");
        return new TakeOwnershipPreflight(ok, enumerationBlocked, blocked.ToList());
    }

    public static bool TakeOwnershipRecursive(string shopRootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(shopRootPath);
        if (!Directory.Exists(shopRootPath))
        {
            SwkLogger.Warn($"TakeOwnershipRecursive skipped: path not found ({shopRootPath})");
            return false;
        }

        SwkLogger.Info($"TakeOwnershipRecursive start: {shopRootPath}");

        ProcessStartInfo psi = new()
        {
            FileName = "takeown.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/F");
        psi.ArgumentList.Add(shopRootPath);
        psi.ArgumentList.Add("/R");
        psi.ArgumentList.Add("/D");
        psi.ArgumentList.Add("Y");

        try
        {
            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("takeown could not be started.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(120000))
            {
                try { process.Kill(true); } catch { }
                SwkLogger.Warn("takeown timeout");
                return false;
            }
            if (process.ExitCode == 0)
            {
                SwkLogger.Info("TakeOwnershipRecursive ok");
                return true;
            }
            SwkLogger.Warn($"takeown failed: exit={process.ExitCode}, stderr={stderr.Trim()}, stdout={stdout.Trim()}");
            return false;
        }
        catch (Exception ex)
        {
            SwkLogger.Error("takeown exception", ex);
            return false;
        }
    }


    public static bool IsolateShopRoot(string shopRootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(shopRootPath);
        if (!Directory.Exists(shopRootPath))
        {
            SwkLogger.Warn($"IsolateShopRoot skipped: path not found ({shopRootPath})");
            return false;
        }

        SwkLogger.Info($"IsolateShopRoot start: {shopRootPath}");

        // 骨格仕様書 v0.1 条文 1.5: 店主のローカル権限を奪わない。継承を切る場合は
        // SYSTEM/Administrators/swkguest に加えて、現在ログオン中の店主 SID にも Full を付与する。
        string? ownerSid;
        try
        {
            ownerSid = WindowsIdentity.GetCurrent().User?.Value;
        }
        catch (Exception ex)
        {
            SwkLogger.Error("IsolateShopRoot: failed to read current user SID", ex);
            return false;
        }
        if (string.IsNullOrEmpty(ownerSid))
        {
            SwkLogger.Warn("IsolateShopRoot: current user SID is empty");
            return false;
        }

        if (!RunIcacls(new[] { shopRootPath, "/inheritance:r" }, "Disable inheritance"))
        {
            return false;
        }

        if (!RunIcacls(new[] { shopRootPath, "/grant:r", $"{SidSystem}:{PermFull}" }, "Grant SYSTEM"))
        {
            return false;
        }

        if (!RunIcacls(new[] { shopRootPath, "/grant:r", $"{SidAdministrators}:{PermFull}" }, "Grant Administrators"))
        {
            return false;
        }

        if (!RunIcacls(new[] { shopRootPath, "/grant:r", $"*{ownerSid}:{PermFull}" }, "Grant Owner"))
        {
            return false;
        }

        if (!RunIcacls(new[] { shopRootPath, "/grant:r", $"{SmbAccountManager.LocalQualifiedAccountName}:{PermFull}" }, "Grant swkguest"))
        {
            return false;
        }

        SwkLogger.Info("IsolateShopRoot ok");
        return true;
    }

    public static bool SetSubfolderPermission(string subfolderPath, bool isSharedOff, bool isReadOnly)
    {
        ArgumentException.ThrowIfNullOrEmpty(subfolderPath);
        if (!Directory.Exists(subfolderPath))
        {
            SwkLogger.Warn($"SetSubfolderPermission skipped: path not found ({subfolderPath})");
            return false;
        }

        if (!isSharedOff && !isReadOnly)
        {
            SwkLogger.Info($"SetSubfolderPermission reset (全員): {subfolderPath}");
            return RunIcacls(new[] { subfolderPath, "/reset" }, "Reset to inherited");
        }

        string? ownerSid;
        try
        {
            ownerSid = WindowsIdentity.GetCurrent().User?.Value;
        }
        catch (Exception ex)
        {
            SwkLogger.Error("SetSubfolderPermission: failed to read current user SID", ex);
            return false;
        }
        if (string.IsNullOrEmpty(ownerSid))
        {
            SwkLogger.Warn("SetSubfolderPermission: current user SID is empty");
            return false;
        }

        if (!RunIcacls(new[] { subfolderPath, "/inheritance:r" }, "Disable inheritance")) return false;
        if (!RunIcacls(new[] { subfolderPath, "/grant:r", $"{SidSystem}:{PermFull}" }, "Grant SYSTEM")) return false;
        if (!RunIcacls(new[] { subfolderPath, "/grant:r", $"{SidAdministrators}:{PermFull}" }, "Grant Admins")) return false;
        if (!RunIcacls(new[] { subfolderPath, "/grant:r", $"*{ownerSid}:{PermFull}" }, "Grant Owner")) return false;

        if (isReadOnly && !isSharedOff)
        {
            SwkLogger.Info($"SetSubfolderPermission read-only: {subfolderPath}");
            return RunIcacls(new[] { subfolderPath, "/grant:r", $"{SmbAccountManager.LocalQualifiedAccountName}:{PermReadOnly}" }, "Grant swkguest read-only");
        }

        SwkLogger.Info($"SetSubfolderPermission shared-off: {subfolderPath}");
        return true;
    }

    public static bool RevokeSwkGuest(string shopRootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(shopRootPath);
        if (!Directory.Exists(shopRootPath))
        {
            SwkLogger.Warn($"RevokeSwkGuest skipped: path not found ({shopRootPath})");
            return true;
        }

        SwkLogger.Info($"RevokeSwkGuest start: {shopRootPath}");
        return RunIcacls(new[] { shopRootPath, "/remove:g", SmbAccountManager.LocalQualifiedAccountName }, "Revoke swkguest");
    }

    public static bool RestoreInheritance(string shopRootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(shopRootPath);
        if (!Directory.Exists(shopRootPath))
        {
            return true;
        }

        SwkLogger.Info($"RestoreInheritance start: {shopRootPath}");
        return RunIcacls(new[] { shopRootPath, "/inheritance:e" }, "Restore inheritance");
    }

    private static bool RunIcacls(IReadOnlyList<string> arguments, string label)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "icacls.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("icacls could not be started.");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(true); } catch { }
                SwkLogger.Warn($"icacls timeout: {label}");
                return false;
            }

            if (process.ExitCode == 0)
            {
                SwkLogger.Info($"icacls ok: {label}");
                return true;
            }

            SwkLogger.Warn(
                $"icacls failed: {label} (exit={process.ExitCode}, stderr={stderr.Trim()}, stdout={stdout.Trim()})");
            return false;
        }
        catch (Exception ex)
        {
            SwkLogger.Error($"icacls exception: {label}", ex);
            return false;
        }
    }
}
