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

        // Future hook: when folder-level shop rules exist, this is where the affected
        // item/subtree is aligned to the destination folder's rule without widening it.
    }
}
