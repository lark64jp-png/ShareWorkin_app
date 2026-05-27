using System;
using System.Security.Principal;

namespace ShareWorkin.SMB;

public static class PcOwnerIdentity
{
    private static readonly object SyncRoot = new();
    private static string? _configuredOwnerSid;
    private static string? _configuredOwnerAccount;

    public static void Configure(string? ownerSid, string? ownerAccount)
    {
        lock (SyncRoot)
        {
            if (!string.IsNullOrWhiteSpace(ownerSid))
            {
                _configuredOwnerSid = ownerSid;
            }

            if (!string.IsNullOrWhiteSpace(ownerAccount))
            {
                _configuredOwnerAccount = ownerAccount;
            }
        }
    }

    public static string? GetConfiguredOwnerSid()
    {
        lock (SyncRoot)
        {
            return _configuredOwnerSid;
        }
    }

    public static string? GetConfiguredOwnerAccount()
    {
        lock (SyncRoot)
        {
            return _configuredOwnerAccount;
        }
    }

    public static string? GetEffectiveOwnerSid()
    {
        string? configured = GetConfiguredOwnerSid();
        return !string.IsNullOrWhiteSpace(configured) ? configured : TryGetCurrentUserSid();
    }

    public static string? GetEffectiveOwnerAccount()
    {
        string? configured = GetConfiguredOwnerAccount();
        return !string.IsNullOrWhiteSpace(configured) ? configured : TryGetCurrentUserAccount();
    }

    public static string? TryGetCurrentUserSid()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"PcOwnerIdentity.TryGetCurrentUserSid failed: {ex.Message}");
            return null;
        }
    }

    public static string? TryGetCurrentUserAccount()
    {
        try
        {
            return WindowsIdentity.GetCurrent().Name;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"PcOwnerIdentity.TryGetCurrentUserAccount failed: {ex.Message}");
            return null;
        }
    }
}
