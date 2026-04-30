using System;
using System.Security.Cryptography;
using System.Text;

namespace ShareWorkin.SMB;

public static class SmbAccountManager
{
    public const string AccountName = "swkguest";
    public const string AccountFullName = "ShareWorkin Guest";
    public const string AccountDescription = "ShareWorkin SMB access account";
    public static string LocalQualifiedAccountName => $@"{Environment.MachineName}\{AccountName}";

    private const int PasswordLength = 24;

    private const string PasswordCharset =
        "ABCDEFGHJKLMNPQRSTUVWXYZ" +
        "abcdefghijkmnpqrstuvwxyz" +
        "23456789" +
        "!@#$%^&*-_=+";

    public static bool AccountExists()
    {
        const string script = @"
$u = Get-LocalUser -Name 'swkguest' -ErrorAction SilentlyContinue;
if ($u) { Write-Output 'EXISTS' } else { Write-Output 'MISSING' }
";
        PowerShellResult result = PowerShellRunner.Run(script, timeoutMs: 10000);
        if (!result.Succeeded)
        {
            SwkLogger.Warn($"AccountExists check failed: exit={result.ExitCode}, stderr={result.StdErr.Trim()}");
            return false;
        }
        return result.StdOut.Contains("EXISTS", StringComparison.Ordinal);
    }

    public static bool EnsureAccount()
    {
        SwkLogger.Info("EnsureSwkGuestAccount start");

        bool exists = AccountExists();
        if (!TryEnsureStoredShopKey(exists, out string password))
        {
            return false;
        }

        if (exists)
        {
            // Opening the shop is also the recovery point. Even if the account
            // already exists, re-enable it and re-apply the stored key so stale
            // Windows account state cannot leave the shop half-open.
            if (!ApplyPasswordToExistingAccount(password))
            {
                SwkLogger.Error("Failed to align password on existing swkguest account");
                return false;
            }
        }
        else
        {
            if (!CreateAccount(password))
            {
                SwkLogger.Error("Failed to create swkguest account");
                return false;
            }
        }

        if (!VerifyAccountCanBeResolved())
        {
            SwkLogger.Error("swkguest account exists but Windows could not resolve it");
            return false;
        }

        SecureStorage.Set(SecureStorage.KeySwkGuestPassword, password);
        SecureStorage.Set(SecureStorage.KeySwkGuestCreatedAt, DateTime.UtcNow.ToString("o"));
        SwkLogger.Info("SwkGuest account ready");
        return true;
    }

    public static bool EnsureStoredShopKey()
    {
        bool exists = AccountExists();
        return TryEnsureStoredShopKey(exists, out _);
    }

    private static bool TryEnsureStoredShopKey(bool accountExists, out string password)
    {
        password = SecureStorage.Get(SecureStorage.KeySwkGuestPassword) ?? string.Empty;
        if (!string.IsNullOrEmpty(password))
        {
            return true;
        }

        if (accountExists)
        {
            // Do not silently rotate the shared shop key. Recreating it here would
            // lock out every already-invited visitor at once.
            SwkLogger.Error("swkguest exists but the stored shop key is missing");
            return false;
        }

        password = GeneratePassword();
        SecureStorage.Set(SecureStorage.KeySwkGuestPassword, password);
        SecureStorage.Set(SecureStorage.KeySwkGuestCreatedAt, DateTime.UtcNow.ToString("o"));
        SwkLogger.Info("Stored shop key prepared before opening the shop");
        return true;
    }

    public static bool RemoveAccount()
    {
        SwkLogger.Info("Remove swkguest account");
        const string script = @"
$u = Get-LocalUser -Name 'swkguest' -ErrorAction SilentlyContinue;
if ($u) { Remove-LocalUser -Name 'swkguest' }
";
        PowerShellResult result = PowerShellRunner.Run(script, timeoutMs: 15000);
        SecureStorage.Remove(SecureStorage.KeySwkGuestPassword);
        SecureStorage.Remove(SecureStorage.KeySwkGuestCreatedAt);
        return result.Succeeded;
    }

    private static bool CreateAccount(string password)
    {
        string script = $@"
$pw = ConvertTo-SecureString -String $env:SWK_PW -AsPlainText -Force;
New-LocalUser -Name 'swkguest' -Password $pw `
              -FullName 'ShareWorkin Guest' `
              -Description 'ShareWorkin SMB access account' `
              -PasswordNeverExpires `
              -UserMayNotChangePassword;
";
        return RunWithSecret(script, password, "CreateSwkGuest", timeoutMs: 20000);
    }

    private static bool ApplyPasswordToExistingAccount(string password)
    {
        string script = @"
$pw = ConvertTo-SecureString -String $env:SWK_PW -AsPlainText -Force;
Set-LocalUser -Name 'swkguest' -Password $pw;
Enable-LocalUser -Name 'swkguest';
";
        return RunWithSecret(script, password, "AlignSwkGuestPassword", timeoutMs: 15000);
    }

    private static bool RunWithSecret(string script, string secret, string label, int timeoutMs)
    {
        PowerShellResult result = PowerShellRunner.RunWithEnvironment(
            script,
            envName: "SWK_PW",
            envValue: secret,
            timeoutMs: timeoutMs);

        if (result.Succeeded)
        {
            SwkLogger.Info($"{label} ok");
            return true;
        }

        SwkLogger.Warn(
            $"{label} failed (exit={result.ExitCode}, timedOut={result.TimedOut}, stderr={result.StdErr.Trim()})");
        return false;
    }

    private static bool VerifyAccountCanBeResolved()
    {
        const string script = @"
$acct = New-Object System.Security.Principal.NTAccount($env:COMPUTERNAME, 'swkguest');
$sid = $acct.Translate([System.Security.Principal.SecurityIdentifier]);
if ($sid.Value) { Write-Output $sid.Value } else { exit 1 }
";
        PowerShellResult result = PowerShellRunner.Run(script, timeoutMs: 10000);
        if (result.Succeeded)
        {
            SwkLogger.Info("swkguest account resolved");
            return true;
        }

        SwkLogger.Warn($"swkguest resolve failed: exit={result.ExitCode}, stderr={result.StdErr.Trim()}");
        return false;
    }

    private static string GeneratePassword()
    {
        StringBuilder sb = new(PasswordLength);
        byte[] buffer = new byte[PasswordLength];
        RandomNumberGenerator.Fill(buffer);
        for (int i = 0; i < PasswordLength; i++)
        {
            sb.Append(PasswordCharset[buffer[i] % PasswordCharset.Length]);
        }
        return sb.ToString();
    }
}
