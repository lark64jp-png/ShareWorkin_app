using System;
using System.Security.Cryptography;
using System.Text;

namespace ShareWorkin.SMB;

public static class SmbAccountManager
{
    public const string AccountName = "swkguest";
    public const string AccountFullName = "ShareWorkin Guest";
    public const string AccountDescription = "ShareWorkin SMB access account";

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
        string? storedPassword = SecureStorage.Get(SecureStorage.KeySwkGuestPassword);

        if (exists && !string.IsNullOrEmpty(storedPassword))
        {
            SwkLogger.Info("SwkGuest account already prepared");
            return true;
        }

        string password = string.IsNullOrEmpty(storedPassword) ? GeneratePassword() : storedPassword;

        if (exists)
        {
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

        SecureStorage.Set(SecureStorage.KeySwkGuestPassword, password);
        SecureStorage.Set(SecureStorage.KeySwkGuestCreatedAt, DateTime.UtcNow.ToString("o"));
        SwkLogger.Info("SwkGuest account ready");
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
