using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ShareWorkin;

public partial class HistoryWindow : Window
{
    public HistoryWindow(string title, string subtitle, IReadOnlyList<HistoryEntry> entries)
    {
        InitializeComponent();

        Title = title;
        TitleTextBlock.Text = title;
        SubtitleTextBlock.Text = subtitle;

        List<HistoryRow> rows = entries
            .OrderByDescending(e => e.OccurredAt)
            .Select(static e => new HistoryRow
            {
                TimeText = e.OccurredAt.ToString("yyyy/MM/dd HH:mm:ss"),
                FriendName = string.IsNullOrWhiteSpace(e.FriendName) ? "-" : e.FriendName,
                DirectionText = GetDirectionText(e.Direction),
                EventTypeText = GetEventTypeText(e.EventType),
                TargetName = string.IsNullOrWhiteSpace(e.TargetName) ? "-" : e.TargetName,
                OutcomeText = GetOutcomeText(e.Outcome),
                DetailText = e.Message,
            })
            .ToList();

        HistoryDataGrid.ItemsSource = rows;
        CountTextBlock.Text = $"{rows.Count} 件";
    }

    private static string GetDirectionText(HistoryDirection direction) => direction switch
    {
        HistoryDirection.Incoming => "もらう",
        HistoryDirection.Outgoing => "渡す",
        _ => "-"
    };

    private static string GetOutcomeText(HistoryOutcome outcome) => outcome switch
    {
        HistoryOutcome.Success => "成功",
        HistoryOutcome.Failure => "失敗",
        _ => "情報"
    };

    private static string GetEventTypeText(string eventType) => eventType switch
    {
        "Receive" => "受取",
        "Send" => "送る",
        "Connect" => "接続",
        "Resume" => "再開",
        "Move" => "移動",
        "Rename" => "名前変更",
        "Delete" => "削除",
        "Hold" => "保留",
        "CreateFolder" => "フォルダー作成",
        "Notify" => "通知",
        "Register" => "登録",
        "RefreshFriend" => "情報更新",
        "UpdateFriend" => "内容更新",
        "Switch" => "接続先変更",
        "Place" => "配置",
        _ => eventType
    };
}

public sealed class HistoryRow
{
    public string TimeText { get; init; } = string.Empty;
    public string FriendName { get; init; } = string.Empty;
    public string DirectionText { get; init; } = string.Empty;
    public string EventTypeText { get; init; } = string.Empty;
    public string TargetName { get; init; } = string.Empty;
    public string OutcomeText { get; init; } = string.Empty;
    public string DetailText { get; init; } = string.Empty;
}
