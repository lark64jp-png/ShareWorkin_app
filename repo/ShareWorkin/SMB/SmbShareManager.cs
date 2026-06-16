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
