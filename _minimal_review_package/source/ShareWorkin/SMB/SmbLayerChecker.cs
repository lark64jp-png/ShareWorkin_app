using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ShareWorkin.SMB;

public static class SmbLayerChecker
{
    private const string SwkGuestAccountName = "swkguest";
    private const string ShareWorkinDescriptionPrefix = "ShareWorkin:";

    private const string CheckScript = @"
$ErrorActionPreference = 'SilentlyContinue';
$result = [ordered]@{
    NetworkProfile  = $null;
    FirewallSharing = $null;
    SmbServerConfig = $null;
    LocalAccount    = $null;
    ShareDefinition = $null;
    Details         = @();
};

try {
    $p = Get-NetConnectionProfile | Select-Object -First 1;
    if ($p) { $result.NetworkProfile = $p.NetworkCategory.ToString(); }
} catch { $result.Details += 'L1: ' + $_.Exception.Message; }

try {
    $rules = Get-NetFirewallRule -DisplayGroup 'ファイルとプリンターの共有';
    if ($rules) {
        $enabled = @($rules | Where-Object { $_.Profile -match 'Private' -and $_.Enabled -eq 'True' });
        if ($enabled.Count -gt 0) { $result.FirewallSharing = 'Enabled'; }
        else { $result.FirewallSharing = 'Disabled'; }
    } else { $result.FirewallSharing = 'NotFound'; }
} catch { $result.Details += 'L2: ' + $_.Exception.Message; }

try {
    $cfg = Get-SmbServerConfiguration;
    $result.SmbServerConfig = [ordered]@{
        RejectUnencryptedAccess = [bool]$cfg.RejectUnencryptedAccess;
        ServerHidden            = [bool]$cfg.ServerHidden;
        ServiceRunning          = (Get-Service -Name LanmanServer).Status.ToString();
    };
} catch { $result.Details += 'L3: ' + $_.Exception.Message; }

try {
    $u = Get-LocalUser -Name 'swkguest';
    if ($u) {
        if ($u.Enabled) { $result.LocalAccount = 'Enabled'; }
        else            { $result.LocalAccount = 'Disabled'; }
    } else { $result.LocalAccount = 'NotFound'; }
} catch { $result.Details += 'L4: ' + $_.Exception.Message; }

try {
    $marked = @(Get-SmbShare | Where-Object { $_.Description -like 'ShareWorkin:*' });
    $result.ShareDefinition = [ordered]@{
        Count = $marked.Count;
        Names = @($marked | Select-Object -ExpandProperty Name);
    };
} catch { $result.Details += 'L5: ' + $_.Exception.Message; }

$result | ConvertTo-Json -Compress -Depth 5
";

    public static SmbLayerStatus GetCurrentState(string? shopRootPath = null)
    {
        SwkLogger.Info("SmbLayerChecker.GetCurrentState start");

        PowerShellResult psResult = PowerShellRunner.Run(CheckScript, timeoutMs: 20000);

        List<string> details = new();
        if (psResult.TimedOut)
        {
            SwkLogger.Error("SmbLayerChecker timed out");
            details.Add("Layer check timed out.");
            return new SmbLayerStatus(
                LayerHealth.Unknown, LayerHealth.Unknown, LayerHealth.Unknown,
                LayerHealth.Unknown, LayerHealth.Unknown, LayerHealth.Unknown,
                "確認に時間がかかっています。", details);
        }

        if (!string.IsNullOrWhiteSpace(psResult.StdErr))
        {
            details.Add("stderr: " + psResult.StdErr.Trim());
        }

        LayerCheckPayload? payload = ParsePayload(psResult.StdOut, details);
        if (payload is null)
        {
            return new SmbLayerStatus(
                LayerHealth.Unknown, LayerHealth.Unknown, LayerHealth.Unknown,
                LayerHealth.Unknown, LayerHealth.Unknown, LayerHealth.Unknown,
                "確認結果を読み取れませんでした。", details);
        }

        if (payload.Details is { Count: > 0 })
        {
            details.AddRange(payload.Details);
        }

        LayerHealth networkProfile = EvaluateNetworkProfile(payload.NetworkProfile);
        LayerHealth firewallSharing = EvaluateFirewall(payload.FirewallSharing);
        LayerHealth smbServerConfig = EvaluateSmbServer(payload.SmbServerConfig);
        LayerHealth localAccount = EvaluateLocalAccount(payload.LocalAccount);
        LayerHealth shareDefinition = LayerHealth.Ok;

        LayerHealth ntfsAccess = string.IsNullOrWhiteSpace(shopRootPath)
            ? LayerHealth.Unknown
            : EvaluateNtfs(shopRootPath, details);

        string? primary = null;
        if (networkProfile != LayerHealth.Ok) primary = "ネットワーク設定の確認が必要です。";
        else if (firewallSharing != LayerHealth.Ok) primary = "ファイル共有のファイアウォール設定の確認が必要です。";
        else if (smbServerConfig != LayerHealth.Ok) primary = "共有サービスの設定の確認が必要です。";
        else if (localAccount != LayerHealth.Ok) primary = "お店の鍵の用意が必要です。";

        SmbLayerStatus status = new(
            networkProfile, firewallSharing, smbServerConfig,
            localAccount, shareDefinition, ntfsAccess,
            primary, details);

        SwkLogger.Info(
            $"SmbLayerChecker result: profile={networkProfile} fw={firewallSharing} smb={smbServerConfig} acct={localAccount} share={shareDefinition} ntfs={ntfsAccess}");

        return status;
    }

    private static LayerCheckPayload? ParsePayload(string stdout, List<string> details)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            details.Add("Layer check returned empty output.");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LayerCheckPayload>(stdout.Trim(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            SwkLogger.Error("LayerCheck JSON parse failed", ex);
            details.Add("結果のJSON解釈に失敗しました。");
            return null;
        }
    }

    private static LayerHealth EvaluateNetworkProfile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return LayerHealth.Unknown;
        return value.Equals("Private", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("DomainAuthenticated", StringComparison.OrdinalIgnoreCase)
            ? LayerHealth.Ok
            : LayerHealth.NeedsRepair;
    }

    private static LayerHealth EvaluateFirewall(string? value)
    {
        return value switch
        {
            "Enabled" => LayerHealth.Ok,
            "Disabled" => LayerHealth.NeedsRepair,
            "NotFound" => LayerHealth.NeedsRepair,
            _ => LayerHealth.Unknown,
        };
    }

    private static LayerHealth EvaluateSmbServer(SmbServerConfigPayload? cfg)
    {
        if (cfg is null) return LayerHealth.Unknown;
        if (!string.Equals(cfg.ServiceRunning, "Running", StringComparison.OrdinalIgnoreCase))
        {
            return LayerHealth.NeedsRepair;
        }
        if (cfg.RejectUnencryptedAccess) return LayerHealth.NeedsRepair;
        return LayerHealth.Ok;
    }

    private static LayerHealth EvaluateLocalAccount(string? value)
    {
        return value switch
        {
            "Enabled" => LayerHealth.Ok,
            "Disabled" => LayerHealth.NeedsRepair,
            "NotFound" => LayerHealth.NeedsRepair,
            _ => LayerHealth.Unknown,
        };
    }

    private static LayerHealth EvaluateNtfs(string shopRootPath, List<string> details)
    {
        try
        {
            if (!Directory.Exists(shopRootPath))
            {
                details.Add($"NTFS check: shop root not found ({shopRootPath}).");
                return LayerHealth.NeedsRepair;
            }
            return LayerHealth.Unknown;
        }
        catch (Exception ex)
        {
            details.Add("NTFS check error: " + ex.Message);
            return LayerHealth.Unknown;
        }
    }

    private sealed class LayerCheckPayload
    {
        public string? NetworkProfile { get; set; }
        public string? FirewallSharing { get; set; }
        public SmbServerConfigPayload? SmbServerConfig { get; set; }
        public string? LocalAccount { get; set; }
        public ShareDefinitionPayload? ShareDefinition { get; set; }
        public List<string>? Details { get; set; }
    }

    private sealed class SmbServerConfigPayload
    {
        public bool RejectUnencryptedAccess { get; set; }
        public bool ServerHidden { get; set; }
        public string? ServiceRunning { get; set; }
    }

    private sealed class ShareDefinitionPayload
    {
        public int Count { get; set; }
        public List<string>? Names { get; set; }
    }
}
