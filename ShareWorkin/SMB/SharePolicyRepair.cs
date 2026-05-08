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

        if (!SmbNtfsManager.TakeOwnershipPath(affectedPath) ||
            !SmbNtfsManager.ResetPathToInherited(affectedPath))
        {
            SwkLogger.Warn($"SharePolicyRepair failed: reason={reason} affected={affectedPath}");
            return;
        }

        SwkLogger.Info($"SharePolicyRepair ok: reason={reason} affected={affectedPath}");
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
