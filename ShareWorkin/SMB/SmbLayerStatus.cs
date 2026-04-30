using System.Collections.Generic;

namespace ShareWorkin.SMB;

public enum LayerHealth
{
    Ok,
    NeedsRepair,
    Unknown,
    Failed,
}

public sealed record SmbLayerStatus(
    LayerHealth NetworkProfile,
    LayerHealth FirewallSharing,
    LayerHealth SmbServerConfig,
    LayerHealth LocalAccount,
    LayerHealth ShareDefinition,
    LayerHealth NtfsAccess,
    string? PrimaryDiagnosticMessage,
    IReadOnlyList<string> RawDetails)
{
    public bool AllOk =>
        NetworkProfile == LayerHealth.Ok &&
        FirewallSharing == LayerHealth.Ok &&
        SmbServerConfig == LayerHealth.Ok &&
        LocalAccount == LayerHealth.Ok &&
        ShareDefinition == LayerHealth.Ok &&
        NtfsAccess == LayerHealth.Ok;

    public bool HasFailure =>
        NetworkProfile == LayerHealth.Failed ||
        FirewallSharing == LayerHealth.Failed ||
        SmbServerConfig == LayerHealth.Failed ||
        LocalAccount == LayerHealth.Failed ||
        ShareDefinition == LayerHealth.Failed ||
        NtfsAccess == LayerHealth.Failed;
}
