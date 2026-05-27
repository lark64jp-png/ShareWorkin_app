using System;
using System.Diagnostics;
using System.Windows;
using ShareWorkin.SMB;

namespace ShareWorkin;

public partial class NetworkHealthGuideWindow : Window
{
    private readonly SwkNetworkHealthStatus _status;

    public NetworkHealthGuideWindow(SwkNetworkHealthStatus status)
    {
        InitializeComponent();
        _status = status;
        ApplyStatus();
    }

    private void ApplyStatus()
    {
        AlertTitleTextBlock.Text = _status.Title;
        AlertDetailTextBlock.Text = _status.Detail;
        SummaryTextBlock.Text = BuildSummary();

        InboundGuideBorder.Visibility = _status.HasInboundIssue ? Visibility.Visible : Visibility.Collapsed;
        OutboundGuideBorder.Visibility = _status.HasOutboundIssue ? Visibility.Visible : Visibility.Collapsed;

        InboundGuideTextBlock.Text = _status.HasInboundIssue
            ? BuildInboundGuide()
            : string.Empty;
        OutboundGuideTextBlock.Text = _status.HasOutboundIssue
            ? BuildOutboundGuide()
            : string.Empty;
    }

    private string BuildSummary()
    {
        if (_status.HasInboundIssue && _status.HasOutboundIssue)
        {
            return "このPCの送受信の両方向で確認ポイントがあります。下の案内から、このPC側で触れる設定を先に見直してください。";
        }

        if (_status.HasInboundIssue)
        {
            return "相手側の問題と決めつけず、このPCが見つかりにくくなる設定を先に確認するための案内です。";
        }

        if (_status.HasOutboundIssue)
        {
            return "相手が不在とは限らず、このPCの探索条件で見つけにくくなっている可能性を確認するための案内です。";
        }

        return "現時点では強い警告はありません。";
    }

    private string BuildInboundGuide()
    {
        string lastProbe = _status.LastIncomingProbeAt.HasValue
            ? $"最終受信: {_status.LastIncomingProbeAt.Value:yyyy/MM/dd HH:mm:ss}"
            : "最終受信: 起動後まだ受け取れていません。";
        string source = string.IsNullOrWhiteSpace(_status.LastIncomingProbeMachineName)
            ? string.Empty
            : $" 直近の相手: {_status.LastIncomingProbeMachineName} {_status.LastIncomingProbeAddress}";
        return $"このPCは相手の共有を見つけられていますが、相手からの探索を受け取りにくい可能性があります。{lastProbe}{source}".Trim();
    }

    private string BuildOutboundGuide()
    {
        string missingSince = _status.RemoteDiscoveryMissingSince.HasValue
            ? _status.RemoteDiscoveryMissingSince.Value.ToString("yyyy/MM/dd HH:mm:ss")
            : "起動直後";
        return $"登録済みの相手は {_status.ExpectedPeerCount} 件ありますが、直近スキャンでは見つかった共有が {_status.VisibleRemoteShopCount} 件でした。探索が空になった時刻: {missingSince}";
    }

    private void OpenNetworkSettingsButton_Click(object sender, RoutedEventArgs e)
        => OpenSettings("ms-settings:network-status", "ネットワーク設定を開けませんでした。");

    private void OpenFirewallSettingsButton_Click(object sender, RoutedEventArgs e)
        => OpenSettings("ms-settings:windowsdefender-firewall", "ファイアウォール設定を開けませんでした。");

    private void OpenPowerSettingsButton_Click(object sender, RoutedEventArgs e)
        => OpenSettings("ms-settings:powersleep", "電源設定を開けませんでした。");

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenSettings(string uri, string failureMessage)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"NetworkHealthGuideWindow.OpenSettings failed: uri={uri} error={ex.Message}");
            System.Windows.MessageBox.Show(this, failureMessage, "設定確認", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
