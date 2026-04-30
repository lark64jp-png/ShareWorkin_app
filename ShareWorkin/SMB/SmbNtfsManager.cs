using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ShareWorkin.SMB;

public static class SmbNtfsManager
{
    private const string SidSystem = "*S-1-5-18";
    private const string SidAdministrators = "*S-1-5-32-544";
    private const string SwkGuestPrincipal = "swkguest";
    private const string PermFull = "(OI)(CI)(F)";

    public static bool IsolateShopRoot(string shopRootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(shopRootPath);
        if (!Directory.Exists(shopRootPath))
        {
            SwkLogger.Warn($"IsolateShopRoot skipped: path not found ({shopRootPath})");
            return false;
        }

        SwkLogger.Info($"IsolateShopRoot start: {shopRootPath}");

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

        if (!RunIcacls(new[] { shopRootPath, "/grant:r", $"{SwkGuestPrincipal}:{PermFull}" }, "Grant swkguest"))
        {
            return false;
        }

        SwkLogger.Info("IsolateShopRoot ok");
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
        return RunIcacls(new[] { shopRootPath, "/remove:g", SwkGuestPrincipal }, "Revoke swkguest");
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
