using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ShareWorkin.SMB;

public enum ShareAccessRight
{
    Full,
    Read,
}

public sealed record ShareWorkinShareInfo(string Name, string Path, string DescriptionLabel);
public sealed record ShareDefinitionAccessInfo(string AccountName, string AccessControlType, string AccessRight);
public sealed record ShareDefinitionDetails(
    string Name,
    string Path,
    string DescriptionLabel,
    string? ShareState,
    uint? CurrentUsers,
    IReadOnlyList<ShareDefinitionAccessInfo> AccessEntries);
public sealed record ShareDefinitionQueryResult(
    bool Succeeded,
    bool TimedOut,
    bool NotFound,
    string? ErrorMessage,
    ShareDefinitionDetails? Share);

public static class SmbShareManager
{
    public const string DescriptionPrefix = "ShareWorkin:";

    public static bool ShareExists(string shareName)
    {
        ArgumentException.ThrowIfNullOrEmpty(shareName);
        string escaped = EscapeSingleQuotes(shareName);
        string script = $@"
$s = Get-SmbShare -Name '{escaped}' -ErrorAction SilentlyContinue;
if ($s) {{ Write-Output 'EXISTS' }} else {{ Write-Output 'MISSING' }}
";
        PowerShellResult result = PowerShellRunner.Run(script, timeoutMs: 10000);
        return result.Succeeded && result.StdOut.Contains("EXISTS", StringComparison.Ordinal);
    }

    public static bool CreateShare(string shareName, string folderPath, string profileLabel)
    {
        ArgumentException.ThrowIfNullOrEmpty(shareName);
        ArgumentException.ThrowIfNullOrEmpty(folderPath);
        ArgumentNullException.ThrowIfNull(profileLabel);

        string description = string.IsNullOrWhiteSpace(profileLabel)
            ? $"{DescriptionPrefix} {shareName}"
            : $"{DescriptionPrefix} {profileLabel}";

        string esName = EscapeSingleQuotes(shareName);
        string esPath = EscapeSingleQuotes(folderPath);
        string esDesc = EscapeSingleQuotes(description);

        string account = EscapeSingleQuotes(SmbAccountManager.LocalQualifiedAccountName);
        string script = $@"
New-SmbShare -Name '{esName}' -Path '{esPath}' -Description '{esDesc}' -FullAccess '{account}';
";
        PowerShellResult result = PowerShellRunner.Run(script, timeoutMs: 15000);
        if (result.Succeeded)
        {
            SwkLogger.Info($"CreateShare ok: {shareName}");
            return true;
        }
        SwkLogger.Warn($"CreateShare failed: {shareName}, stderr={result.StdErr.Trim()}");
        return false;
    }

    public static bool RepairShareDefinition(string shareName, string folderPath, string profileLabel)
    {
        ArgumentException.ThrowIfNullOrEmpty(shareName);
        ArgumentException.ThrowIfNullOrEmpty(folderPath);
        ArgumentNullException.ThrowIfNull(profileLabel);

        string description = string.IsNullOrWhiteSpace(profileLabel)
            ? $"{DescriptionPrefix} {shareName}"
            : $"{DescriptionPrefix} {profileLabel}";

        string esName = EscapeSingleQuotes(shareName);
        string esPath = EscapeSingleQuotes(folderPath);
        string esDesc = EscapeSingleQuotes(description);
        string account = EscapeSingleQuotes(SmbAccountManager.LocalQualifiedAccountName);

        string script = $@"
$s = Get-SmbShare -Name '{esName}' -ErrorAction SilentlyContinue;
if ($s) {{
    Remove-SmbShare -Name '{esName}' -Force | Out-Null;
    New-SmbShare -Name '{esName}' -Path '{esPath}' -Description '{esDesc}' -FullAccess '{account}' | Out-Null;
}}
";
        PowerShellResult result = PowerShellRunner.Run(script, timeoutMs: 15000);
        if (result.Succeeded)
        {
            SwkLogger.Info($"RepairShareDefinition ok: {shareName}");
            return true;
        }

        SwkLogger.Warn($"RepairShareDefinition failed: {shareName}, stderr={result.StdErr.Trim()}");
        return false;
    }

    public static bool RemoveShare(string shareName)
    {
        ArgumentException.ThrowIfNullOrEmpty(shareName);
        string esName = EscapeSingleQuotes(shareName);
        string script = $@"
$s = Get-SmbShare -Name '{esName}' -ErrorAction SilentlyContinue;
if ($s) {{ Remove-SmbShare -Name '{esName}' -Force }}
";
        PowerShellResult result = PowerShellRunner.Run(script, timeoutMs: 15000);
        if (result.Succeeded)
        {
            SwkLogger.Info($"RemoveShare ok: {shareName}");
            return true;
        }
        SwkLogger.Warn($"RemoveShare failed: {shareName}, stderr={result.StdErr.Trim()}");
        return false;
    }

    public static bool RevokeEveryone(string shareName)
    {
        ArgumentException.ThrowIfNullOrEmpty(shareName);
        string esName = EscapeSingleQuotes(shareName);
        string script = $@"
Revoke-SmbShareAccess -Name '{esName}' -AccountName 'Everyone' -Force -ErrorAction SilentlyContinue | Out-Null;
";
        PowerShellResult result = PowerShellRunner.Run(script, timeoutMs: 10000);
        return result.Succeeded;
    }

    public static bool GrantSwkGuest(string shareName, ShareAccessRight right)
    {
        ArgumentException.ThrowIfNullOrEmpty(shareName);
        string esName = EscapeSingleQuotes(shareName);
        string accessRight = right switch
        {
            ShareAccessRight.Full => "Full",
            ShareAccessRight.Read => "Read",
            _ => "Full",
        };
        string account = EscapeSingleQuotes(SmbAccountManager.LocalQualifiedAccountName);
        string script = $@"
Revoke-SmbShareAccess -Name '{esName}' -AccountName '{account}' -Force -ErrorAction SilentlyContinue | Out-Null;
Grant-SmbShareAccess -Name '{esName}' -AccountName '{account}' -AccessRight {accessRight} -Force | Out-Null;
";
        PowerShellResult result = PowerShellRunner.Run(script, timeoutMs: 15000);
        if (result.Succeeded)
        {
            SwkLogger.Info($"GrantSwkGuest ok: {shareName} ({accessRight})");
            return true;
        }
        SwkLogger.Warn($"GrantSwkGuest failed: {shareName}, stderr={result.StdErr.Trim()}");
        return false;
    }

    public static IReadOnlyList<ShareWorkinShareInfo> ListShareWorkinShares()
    {
        const string script = @"
$shares = Get-SmbShare | Where-Object { $_.Description -like 'ShareWorkin:*' };
$out = @();
foreach ($s in $shares) {
    $out += [ordered]@{
        Name = $s.Name;
        Path = $s.Path;
        Description = $s.Description;
    };
}
$out | ConvertTo-Json -Compress -Depth 4
";
        PowerShellResult result = PowerShellRunner.Run(script, timeoutMs: 10000);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return Array.Empty<ShareWorkinShareInfo>();
        }

        try
        {
            string trimmed = result.StdOut.Trim();
            using JsonDocument doc = JsonDocument.Parse(trimmed.StartsWith("[") ? trimmed : $"[{trimmed}]");
            List<ShareWorkinShareInfo> list = new();
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                string name = el.GetProperty("Name").GetString() ?? string.Empty;
                string path = el.GetProperty("Path").GetString() ?? string.Empty;
                string desc = el.GetProperty("Description").GetString() ?? string.Empty;
                string label = ExtractLabel(desc);
                list.Add(new ShareWorkinShareInfo(name, path, label));
            }
            return list;
        }
        catch (JsonException ex)
        {
            SwkLogger.Error("ListShareWorkinShares parse failed", ex);
            return Array.Empty<ShareWorkinShareInfo>();
        }
    }

    public static ShareWorkinShareInfo? FindShareWorkinShareByPath(string folderPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(folderPath);
        string normalizedTarget = NormalizeSharePath(folderPath);

        foreach (ShareWorkinShareInfo share in ListShareWorkinShares())
        {
            if (string.Equals(NormalizeSharePath(share.Path), normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return share;
            }
        }

        return null;
    }

    public static ShareDefinitionDetails? FindShareDefinition(string? shareName, string? folderPath)
        => QueryShareDefinition(shareName, folderPath).Share;

    public static ShareDefinitionQueryResult QueryShareDefinition(string? shareName, string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(shareName) && string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("shareName or folderPath is required.");
        }

        string? escapedName = string.IsNullOrWhiteSpace(shareName) ? null : EscapeSingleQuotes(shareName);
        string? escapedPath = string.IsNullOrWhiteSpace(folderPath) ? null : EscapeSingleQuotes(folderPath);
        string script = $@"
function Normalize-SharePath([string]$value) {{
    if ([string]::IsNullOrWhiteSpace($value)) {{ return $null }}
    try {{
        return [System.IO.Path]::GetFullPath($value).TrimEnd('\','/')
    }}
    catch {{
        return $value.TrimEnd('\','/')
    }}
}}

$requestedName = {(escapedName is null ? "$null" : $"'{escapedName}'")};
$requestedPath = {(escapedPath is null ? "$null" : $"'{escapedPath}'")};
$normalizedRequestedPath = Normalize-SharePath $requestedPath;
$shares = Get-SmbShare | Where-Object {{ $_.Description -like 'ShareWorkin:*' }};

if ($requestedName) {{
    $shares = $shares | Where-Object {{ $_.Name -eq $requestedName }};
}}

if ($normalizedRequestedPath) {{
    $shares = $shares | Where-Object {{ (Normalize-SharePath $_.Path) -eq $normalizedRequestedPath }};
}}

$share = $shares | Select-Object -First 1;
if (-not $share) {{
    Write-Output '';
    exit 0;
}}

$accessEntries = @();
try {{
    $accessEntries = @(Get-SmbShareAccess -Name $share.Name -ErrorAction Stop | ForEach-Object {{
        [ordered]@{{
            AccountName = $_.AccountName;
            AccessControlType = [string]$_.AccessControlType;
            AccessRight = [string]$_.AccessRight;
        }}
    }});
}}
catch {{
    $accessEntries = @();
}}

[ordered]@{{
    Name = $share.Name;
    Path = $share.Path;
    Description = $share.Description;
    ShareState = if ($share.PSObject.Properties['ShareState']) {{ [string]$share.ShareState }} else {{ $null }};
    CurrentUsers = if ($share.PSObject.Properties['CurrentUsers']) {{ [uint32]$share.CurrentUsers }} else {{ $null }};
    AccessEntries = $accessEntries;
}} | ConvertTo-Json -Compress -Depth 6
";

        PowerShellResult result = PowerShellRunner.Run(script, timeoutMs: 10000);
        if (result.TimedOut)
        {
            return new ShareDefinitionQueryResult(
                Succeeded: false,
                TimedOut: true,
                NotFound: false,
                ErrorMessage: "PowerShell timed out.",
                Share: null);
        }

        if (!result.Succeeded)
        {
            return new ShareDefinitionQueryResult(
                Succeeded: false,
                TimedOut: false,
                NotFound: false,
                ErrorMessage: string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut.Trim() : result.StdErr.Trim(),
                Share: null);
        }

        if (string.IsNullOrWhiteSpace(result.StdOut))
        {
            return new ShareDefinitionQueryResult(
                Succeeded: true,
                TimedOut: false,
                NotFound: true,
                ErrorMessage: null,
                Share: null);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(result.StdOut.Trim());
            JsonElement root = doc.RootElement;
            string name = root.GetProperty("Name").GetString() ?? string.Empty;
            string path = root.GetProperty("Path").GetString() ?? string.Empty;
            string description = root.GetProperty("Description").GetString() ?? string.Empty;
            string label = ExtractLabel(description);
            string? shareState = root.TryGetProperty("ShareState", out JsonElement stateElement) &&
                                 stateElement.ValueKind != JsonValueKind.Null
                ? stateElement.GetString()
                : null;
            uint? currentUsers = root.TryGetProperty("CurrentUsers", out JsonElement currentUsersElement) &&
                                 currentUsersElement.ValueKind != JsonValueKind.Null
                ? currentUsersElement.GetUInt32()
                : null;

            List<ShareDefinitionAccessInfo> accessEntries = new();
            if (root.TryGetProperty("AccessEntries", out JsonElement accessEntriesElement) &&
                accessEntriesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in accessEntriesElement.EnumerateArray())
                {
                    accessEntries.Add(new ShareDefinitionAccessInfo(
                        entry.TryGetProperty("AccountName", out JsonElement accountElement)
                            ? accountElement.GetString() ?? string.Empty
                            : string.Empty,
                        entry.TryGetProperty("AccessControlType", out JsonElement controlElement)
                            ? controlElement.GetString() ?? string.Empty
                            : string.Empty,
                        entry.TryGetProperty("AccessRight", out JsonElement rightElement)
                            ? rightElement.GetString() ?? string.Empty
                            : string.Empty));
                }
            }

            return new ShareDefinitionQueryResult(
                Succeeded: true,
                TimedOut: false,
                NotFound: false,
                ErrorMessage: null,
                Share: new ShareDefinitionDetails(name, path, label, shareState, currentUsers, accessEntries));
        }
        catch (JsonException ex)
        {
            SwkLogger.Error("FindShareDefinition parse failed", ex);
            return new ShareDefinitionQueryResult(
                Succeeded: false,
                TimedOut: false,
                NotFound: false,
                ErrorMessage: ex.Message,
                Share: null);
        }
    }

    public static bool RemoveAllShareWorkinShares()
    {
        IReadOnlyList<ShareWorkinShareInfo> shares = ListShareWorkinShares();
        bool allOk = true;
        foreach (ShareWorkinShareInfo info in shares)
        {
            if (!RemoveShare(info.Name))
            {
                allOk = false;
            }
        }
        return allOk;
    }

    private static string ExtractLabel(string description)
    {
        if (description.StartsWith(DescriptionPrefix, StringComparison.Ordinal))
        {
            return description[DescriptionPrefix.Length..].TrimStart();
        }
        return string.Empty;
    }

    private static string NormalizeSharePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static string EscapeSingleQuotes(string value) => value.Replace("'", "''");
}
