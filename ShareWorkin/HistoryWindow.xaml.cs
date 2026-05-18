using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using ShareWorkin.SMB;

namespace ShareWorkin;

public partial class HistoryWindow : Window
{
    private readonly HistoryChannel _channel;
    private readonly int _maxCount;
    private readonly ObservableCollection<HistoryRow> _rows = [];
    private readonly Dictionary<string, HistoryEntry> _entryMap = [];
    private List<HistoryRow> _allRows = [];
    private const string TimestampFormat = "yyyy/MM/dd HH:mm:ss";

    public HistoryWindow(string title, string subtitle, HistoryChannel channel, int maxCount)
    {
        InitializeComponent();

        _channel = channel;
        _maxCount = maxCount;
        Title = title;
        TitleTextBlock.Text = title;
        SubtitleTextBlock.Text = subtitle;
        HistoryDataGrid.ItemsSource = _rows;

        RefreshRows();
        HistoryRepository.HistoryChanged += HistoryRepository_HistoryChanged;
        SwkHistoryJournal.RecordAppended += SwkHistoryJournal_RecordAppended;
        Closed += HistoryWindow_Closed;
    }

    private void RefreshRows()
    {
        List<HistoryEntry> entries = HistoryRepository.GetEntries(_channel, _maxCount)
            .OrderByDescending(e => e.OccurredAt)
            .ToList();

        _allRows = entries.Select(static e => new HistoryRow
        {
            EntryId = e.Id,
            TimeText = e.OccurredAt.ToString("yyyy/MM/dd HH:mm:ss"),
            UserText = string.IsNullOrWhiteSpace(e.FriendName) ? "自分" : e.FriendName,
            PathText = BuildPathText(e),
            EventTypeText = GetEventTypeText(e.EventType),
            FileNameText = string.IsNullOrWhiteSpace(e.TargetName) ? "-" : e.TargetName,
            ContentText = e.Message,
            NoteText = string.IsNullOrWhiteSpace(e.Note) ? BuildFallbackNote(e) : e.Note,
            OutcomeText = GetOutcomeText(e.Outcome),
        }).ToList();

        _entryMap.Clear();
        foreach (HistoryEntry entry in entries)
        {
            _entryMap[entry.Id] = entry;
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string filter = FilterTextBox?.Text?.Trim() ?? string.Empty;
        List<HistoryRow> filtered = string.IsNullOrEmpty(filter)
            ? _allRows
            : _allRows.Where(r => MatchesFilter(r, filter)).ToList();

        _rows.Clear();
        foreach (HistoryRow row in filtered)
        {
            _rows.Add(row);
        }

        CountTextBlock.Text = $"{filtered.Count} 件";
        if (_rows.Count > 0)
        {
            HistoryDataGrid.SelectedIndex = 0;
        }
        else
        {
            DetailTextBox.Text = string.Empty;
        }
    }

    private static bool MatchesFilter(HistoryRow row, string filter)
    {
        return Contains(row.FileNameText, filter)
            || Contains(row.EventTypeText, filter)
            || Contains(row.ContentText, filter)
            || Contains(row.PathText, filter)
            || Contains(row.UserText, filter);
    }

    private static bool Contains(string? value, string filter)
        => value?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;

    private void HistoryRepository_HistoryChanged(HistoryChannel channel)
    {
        if (channel != _channel)
        {
            return;
        }

        Dispatcher.InvokeAsync(RefreshRows);
    }

    private void SwkHistoryJournal_RecordAppended(string channel)
    {
        if (!string.Equals(channel, _channel.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dispatcher.InvokeAsync(RefreshRows);
    }

    private void HistoryWindow_Closed(object? sender, EventArgs e)
    {
        HistoryRepository.HistoryChanged -= HistoryRepository_HistoryChanged;
        SwkHistoryJournal.RecordAppended -= SwkHistoryJournal_RecordAppended;
        Closed -= HistoryWindow_Closed;
    }

    private static string GetOutcomeText(HistoryOutcome outcome) => outcome switch
    {
        HistoryOutcome.Success => "成功",
        HistoryOutcome.Warning => "警告",
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
        "ExternalRename" => "名前変更（外部）",
        "Delete" => "削除",
        "Hold" => "保留",
        "CreateFolder" => "フォルダー作成",
        "Notify" => "通知",
        "Register" => "登録",
        "RefreshFriend" => "情報更新",
        "UpdateFriend" => "内容更新",
        "Switch" => "接続先変更",
        "Place" => "配置",
        "PermissionChanged" => "共有設定変更",
        "Log" => "記録",
        _ => eventType
    };

    private static string BuildPathText(HistoryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.PathText))
        {
            return entry.PathText!;
        }

        if (!string.IsNullOrWhiteSpace(entry.DestinationFolder))
        {
            return entry.DestinationFolder!;
        }

        if (!string.IsNullOrWhiteSpace(entry.SourcePath))
        {
            return Path.GetDirectoryName(entry.SourcePath) ?? entry.SourcePath!;
        }

        return "-";
    }

    private static string BuildFallbackNote(HistoryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Source))
        {
            return entry.Source!;
        }

        return "-";
    }

    private static string BuildDetailText(HistoryEntry entry)
    {
        List<string> lines =
        [
            $"日時: {entry.OccurredAt:yyyy/MM/dd HH:mm:ss}",
            $"ユーザー: {GetUserText(entry)}",
            $"アクション: {GetEventTypeText(entry.EventType)}",
            $"結果: {GetOutcomeText(entry.Outcome)}",
            $"パス: {BuildPathText(entry)}",
            $"ファイル名: {entry.TargetName ?? "-"}",
            $"内容: {entry.Message}",
            $"備考: {(string.IsNullOrWhiteSpace(entry.Note) ? "-" : entry.Note)}",
            $"方向: {entry.Direction}",
            $"Source: {entry.Source ?? "-"}",
            $"SourcePath: {entry.SourcePath ?? "-"}",
            $"DestinationPath: {entry.DestinationPath ?? "-"}",
            $"DestinationFolder: {entry.DestinationFolder ?? "-"}",
            $"FriendId: {entry.FriendId ?? "-"}",
        ];

        return string.Join(Environment.NewLine, lines);
    }

    private static string GetUserText(HistoryEntry entry)
        => string.IsNullOrWhiteSpace(entry.FriendName) ? "自分" : entry.FriendName!;

    private void FilterTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ApplyFilter();

    private void ClearFilterButton_Click(object sender, RoutedEventArgs e)
        => FilterTextBox.Clear();

    private void CopyRowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryDataGrid.SelectedItem is not HistoryRow row ||
            !_entryMap.TryGetValue(row.EntryId, out HistoryEntry? entry))
        {
            return;
        }

        System.Windows.Clipboard.SetText(BuildDetailText(entry));
    }

    private void HistoryDataGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (HistoryDataGrid.SelectedItem is not HistoryRow row ||
            string.IsNullOrWhiteSpace(row.EntryId) ||
            !_entryMap.TryGetValue(row.EntryId, out HistoryEntry? entry))
        {
            DetailTextBox.Text = string.Empty;
            return;
        }

        DetailTextBox.Text = BuildDetailText(entry);
    }

    private void DeleteHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        string input = DeleteTimestampTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            MessageBoxResult confirmAll = System.Windows.MessageBox.Show(
                "すべての履歴を削除してよいですか？",
                Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmAll != MessageBoxResult.Yes)
            {
                return;
            }

            int deletedAllCount = HistoryRepository.DeleteEntries(_channel, deleteThrough: null);
            RefreshRows();
            System.Windows.MessageBox.Show(
                deletedAllCount > 0 ? $"{deletedAllCount} 件の履歴を削除しました。" : "削除対象の履歴はありませんでした。",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!DateTime.TryParseExact(
                input,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime deleteThrough))
        {
            System.Windows.MessageBox.Show(
                $"日時は {TimestampFormat} の形式で入力してください。",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            DeleteTimestampTextBox.Focus();
            DeleteTimestampTextBox.SelectAll();
            return;
        }

        int deletedCount = HistoryRepository.DeleteEntries(_channel, deleteThrough);
        RefreshRows();
        string deleteThroughText = deleteThrough.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        System.Windows.MessageBox.Show(
            deletedCount > 0
                ? $"{deleteThroughText} 以前の履歴を {deletedCount} 件削除しました。"
                : $"{deleteThroughText} 以前の削除対象はありませんでした。",
            Title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}

public sealed class HistoryRow
{
    public string EntryId { get; init; } = string.Empty;
    public string TimeText { get; init; } = string.Empty;
    public string UserText { get; init; } = string.Empty;
    public string PathText { get; init; } = string.Empty;
    public string FileNameText { get; init; } = string.Empty;
    public string EventTypeText { get; init; } = string.Empty;
    public string ContentText { get; init; } = string.Empty;
    public string NoteText { get; init; } = string.Empty;
    public string OutcomeText { get; init; } = string.Empty;
}
