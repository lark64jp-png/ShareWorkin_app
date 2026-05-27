using System;

namespace ShareWorkin.SMB;

public enum SharePolicyRepairReason
{
    Placed,
    Moved,
    Renamed,
    FolderCreated,
    ExternalCreated,
    ExternalRenamed,
}

public static class SharePolicyRepair
{
    private static readonly TimeSpan[] RepairRetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(900),
    ];

    public static void MarkActionAftercare(
        string shopRootPath,
        string affectedPath,
        string policySourceFolder,
        SharePolicyRepairReason reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(shopRootPath);
        ArgumentException.ThrowIfNullOrEmpty(affectedPath);
        ArgumentException.ThrowIfNullOrEmpty(policySourceFolder);

        if (!IsUnderFolder(affectedPath, shopRootPath) ||
            !IsUnderFolder(policySourceFolder, shopRootPath))
        {
            SwkLogger.Warn($"SharePolicyRepair skipped outside shop: reason={reason} affected={affectedPath}");
            return;
        }

        if (!TryNormalizeForSharedShop(affectedPath))
        {
            SwkLogger.Warn($"SharePolicyRepair failed: reason={reason} affected={affectedPath}");
            return;
        }

        SwkLogger.Info($"SharePolicyRepair ok: reason={reason} affected={affectedPath}");
    }

    public static bool TryNormalizeForSharedShop(string affectedPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(affectedPath);

        for (int attempt = 0; attempt < RepairRetryDelays.Length; attempt++)
        {
            TimeSpan delay = RepairRetryDelays[attempt];
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    System.Threading.Thread.Sleep(delay);
                }
                catch
                {
                }
            }

            if (!System.IO.Directory.Exists(affectedPath) && !System.IO.File.Exists(affectedPath))
            {
                return true;
            }

            bool takeOwnershipOk = SmbNtfsManager.TakeOwnershipPath(affectedPath);
            bool resetOk = takeOwnershipOk && SmbNtfsManager.ResetPathToInherited(affectedPath);
            if (resetOk)
            {
                return true;
            }

            SwkLogger.Debug(
                $"SharePolicyRepair retry needed: attempt={attempt + 1}/{RepairRetryDelays.Length} affected={affectedPath} takeown={takeOwnershipOk} reset={resetOk}");
        }

        return false;
    }

    private static bool IsUnderFolder(string path, string rootPath)
    {
        try
        {
            string root = System.IO.Path.GetFullPath(rootPath)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            string current = System.IO.Path.GetFullPath(path)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

            return string.Equals(root, current, StringComparison.OrdinalIgnoreCase) ||
                   current.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
