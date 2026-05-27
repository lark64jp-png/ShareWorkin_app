namespace ShareWorkin.SMB;

public static class SmbInfrastructureManager
{
    public static bool EnsureNetworkPrivate()
    {
        const string script = @"
$p = Get-NetConnectionProfile | Select-Object -First 1;
if ($p -and $p.NetworkCategory -ne 'Private' -and $p.NetworkCategory -ne 'DomainAuthenticated') {
    Set-NetConnectionProfile -InterfaceIndex $p.InterfaceIndex -NetworkCategory Private;
}
";
        return RunStep(script, "EnsureNetworkPrivate", timeoutMs: 15000);
    }

    public static bool EnsureFirewallSharingEnabled()
    {
        const string script = @"
Set-NetFirewallRule -DisplayGroup 'ファイルとプリンターの共有' -Profile Private -Enabled True;
";
        return RunStep(script, "EnsureFirewallSharingEnabled", timeoutMs: 15000);
    }

    public static bool EnsureSwkDiscoveryPort()
    {
        const string script = @"
$ruleName = 'ShareWorkin Discovery (UDP 7831)';
$existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue;
if ($existing) {
    Set-NetFirewallRule -DisplayName $ruleName -Profile Private,Domain -Enabled True | Out-Null;
} else {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol UDP -LocalPort 7831 -Profile Private,Domain -Action Allow | Out-Null;
}
";
        return RunStep(script, "EnsureSwkDiscoveryPort", timeoutMs: 15000);
    }

    public static bool EnsureSwkAppTcpRule(string exePath)
    {
        string escaped = exePath.Replace("'", "''");
        string script = $@"
$ruleName = 'ShareWorkin App (TCP Inbound)';
$exePath = '{escaped}';
$existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue;
if ($existing) {{
    Set-NetFirewallRule -DisplayName $ruleName -Program $exePath -Profile Private,Domain -Enabled True | Out-Null;
}} else {{
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -Program $exePath -Profile Private,Domain -Action Allow | Out-Null;
}}
";
        return RunStep(script, "EnsureSwkAppTcpRule", timeoutMs: 15000);
    }

    public static bool EnsureSmbServerConfig()
    {
        const string script = @"
Set-SmbServerConfiguration -RejectUnencryptedAccess $false -Force;
Set-SmbServerConfiguration -ServerHidden $false -Force;
";
        return RunStep(script, "EnsureSmbServerConfig", timeoutMs: 30000);
    }

    public static bool EnsureLanmanServerRunning()
    {
        const string script = @"
$svc = Get-Service -Name LanmanServer;
if ($svc.Status -ne 'Running') {
    Start-Service LanmanServer;
}
";
        return RunStep(script, "EnsureLanmanServerRunning", timeoutMs: 15000);
    }

    public static bool StopLanmanServer()
    {
        const string script = @"
Stop-Service LanmanServer -Force;
";
        return RunStep(script, "StopLanmanServer", timeoutMs: 15000);
    }

    private static bool RunStep(string script, string label, int timeoutMs)
    {
        SwkLogger.Info($"Infra step: {label} start");
        PowerShellResult result = PowerShellRunner.Run(script, timeoutMs);
        if (result.Succeeded)
        {
            SwkLogger.Info($"Infra step: {label} ok");
            return true;
        }

        string trimmed = result.StdErr.Trim();
        SwkLogger.Warn(
            $"Infra step: {label} failed (exit={result.ExitCode}, timedOut={result.TimedOut}, stderr={trimmed})");
        return false;
    }
}
