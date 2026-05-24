using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;
using ShareWorkin.SMB;

namespace ShareWorkin;

public partial class HistoryWindow : Window
{
    private readonly HistoryChannel _channel;
    private readonly int _maxCount;
    private readonly ObservableCollection<HistoryRow> _rows = [];
    private readonly Dictionary<string, HistoryEntry> _entryMap = [];
    private List<HistoryRow> _allRows = [];
    private readonly System.Windows.Threading.DispatcherTimer _refreshDebounceTimer;
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
        _refreshDebounceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _refreshDebounceTimer.Tick += RefreshDebounceTimer_Tick;

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
            UserText = BuildUserText(e),
            PathText = BuildPathText(e),
            EventTypeText = GetEventTypeText(e.EventType),
            FileNameText = string.IsNullOrWhiteSpace(e.TargetName) ? "-" : e.TargetName,
            IsFolder = !string.IsNullOrWhiteSpace(e.TargetName)
                    && (string.Equals(e.EventType, "CreateFolder", StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrEmpty(Path.GetExtension(e.TargetName))),
            ContentText = e.Message,
            NoteText = string.IsNullOrWhiteSpace(e.Note) ? BuildFallbackNote(e) : e.Note,
            OutcomeText = GetOutcomeText(e.Outcome),
            AttentionText = BuildAttentionText(e),
            RowBackground = GetRowBackground(e),
            RowForeground = MediaBrushes.Black,
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
        string fFileName = FilterFileName?.Text?.Trim() ?? string.Empty;
        string fUser     = FilterUser?.Text?.Trim()     ?? string.Empty;
        string fPath     = FilterPath?.Text?.Trim()     ?? string.Empty;
        string fAction   = FilterAction?.Text?.Trim()   ?? string.Empty;
        string fContent  = FilterContent?.Text?.Trim()  ?? string.Empty;
        string fNote     = FilterNote?.Text?.Trim()     ?? string.Empty;

        List<HistoryRow> filtered = _allRows.Where(r =>
            Contains(r.FileNameText,  fFileName) &&
            Contains(r.UserText,      fUser)     &&
            Contains(r.PathText,      fPath)     &&
            Contains(r.EventTypeText, fAction)   &&
            Contains(r.ContentText,   fContent)  &&
            Contains(r.NoteText,      fNote)
        ).ToList();

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
            DetailLeft.Text = string.Empty;
            DetailRight.Text = string.Empty;
        }
    }

    private static bool Contains(string? value, string filter)
        => string.IsNullOrEmpty(filter) || value?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;

    private void HistoryRepository_HistoryChanged(HistoryChannel channel)
    {
        if (channel != _channel)
        {
            return;
        }

        ScheduleRefresh();
    }

    private void SwkHistoryJournal_RecordAppended(string channel)
    {
        if (!string.Equals(channel, _channel.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        Dispatcher.InvokeAsync(() =>
        {
            _refreshDebounceTimer.Stop();
            _refreshDebounceTimer.Start();
        });
    }

    private void RefreshDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _refreshDebounceTimer.Stop();
        RefreshRows();
    }

    private void HistoryWindow_Closed(object? sender, EventArgs e)
    {
        _refreshDebounceTimer.Stop();
        _refreshDebounceTimer.Tick -= RefreshDebounceTimer_Tick;
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
        "PermissionCascade" => "共有設定変更（連動）",
        "Copy" => "コピー",
        "Log" => "記録",
        _ => eventType
    };

    private static string BuildUserText(HistoryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.FriendName))
        {
            return entry.FriendName!;
        }

        return entry.Direction == HistoryDirection.Incoming ? "不明" : "自分";
    }

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

    private static string BuildFallbackNote(HistoryEntry entry) => "-";

    private static string BuildAttentionText(HistoryEntry entry)
    {
        if (entry.Outcome == HistoryOutcome.Failure)
        {
            return "変更できなかったため、先に確認してほしい行です。";
        }

        if (entry.Outcome == HistoryOutcome.Warning)
        {
            return "危険回避や条件不一致で止めた行です。理由を確認してほしい行です。";
        }

        if (string.Equals(entry.EventType, "PermissionCascade", StringComparison.OrdinalIgnoreCase))
        {
            return "配下の共有設定見直しが発生した行です。大量変更の確認起点になる行です。";
        }

        if (string.Equals(entry.EventType, "PermissionChanged", StringComparison.OrdinalIgnoreCase))
        {
            return "共有設定の変化を示す行です。移動や配置の結果確認に向いています。";
        }

        if (IsExternalObservation(entry))
        {
            return "外部変化または観測由来の行です。利用者操作と区別して確認できます。";
        }

        return "変更結果の確認用行です。";
    }

    private static MediaBrush GetRowBackground(HistoryEntry entry)
    {
        if (entry.Outcome == HistoryOutcome.Failure)
        {
            return new MediaSolidColorBrush(MediaColor.FromRgb(255, 228, 228));
        }

        if (entry.Outcome == HistoryOutcome.Warning)
        {
            return new MediaSolidColorBrush(MediaColor.FromRgb(255, 242, 236));
        }

        if (string.Equals(entry.EventType, "Delete", StringComparison.OrdinalIgnoreCase))
        {
            return new MediaSolidColorBrush(MediaColor.FromRgb(255, 228, 228));
        }

        if (string.Equals(entry.EventType, "Hold", StringComparison.OrdinalIgnoreCase))
        {
            return new MediaSolidColorBrush(MediaColor.FromRgb(255, 242, 236));
        }

        if (string.Equals(entry.EventType, "PermissionCascade", StringComparison.OrdinalIgnoreCase))
        {
            return new MediaSolidColorBrush(MediaColor.FromRgb(255, 248, 222));
        }

        if (string.Equals(entry.EventType, "PermissionChanged", StringComparison.OrdinalIgnoreCase))
        {
            return new MediaSolidColorBrush(MediaColor.FromRgb(255, 250, 232));
        }

        if (IsExternalObservation(entry))
        {
            return new MediaSolidColorBrush(MediaColor.FromRgb(238, 245, 252));
        }

        if (entry.Outcome == HistoryOutcome.Success)
        {
            return new MediaSolidColorBrush(MediaColor.FromRgb(243, 249, 240));
        }

        return MediaBrushes.White;
    }

    private static bool IsExternalObservation(HistoryEntry entry)
    {
        if (string.Equals(entry.EventType, "ExternalRename", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(entry.Source, "Watcher", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.Source, "Polling", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string BuildDetailLeft(HistoryEntry entry) =>
        string.Join(Environment.NewLine,
        [
            $"日時: {entry.OccurredAt:yyyy/MM/dd HH:mm:ss}",
            $"アクション: {GetEventTypeText(entry.EventType)}",
            $"結果: {GetOutcomeText(entry.Outcome)}",
            $"パス: {BuildPathText(entry)}",
            $"ファイル名: {entry.TargetName ?? "-"}",
            $"内容: {entry.Message}",
            $"備考: {(string.IsNullOrWhiteSpace(entry.Note) ? "-" : entry.Note)}",
        ]);

    private static string BuildDetailRight(HistoryEntry entry) =>
        string.Join(Environment.NewLine,
        [
            $"ユーザー: {GetUserText(entry)}",
            $"方向: {entry.Direction}",
            $"注目理由: {BuildAttentionText(entry)}",
            $"Source: {entry.Source ?? "-"}",
            $"SourcePath: {entry.SourcePath ?? "-"}",
            $"DestinationPath: {entry.DestinationPath ?? "-"}",
            $"DestinationFolder: {entry.DestinationFolder ?? "-"}",
            $"FriendId: {entry.FriendId ?? "-"}",
        ]);

    private static string GetUserText(HistoryEntry entry)
        => string.IsNullOrWhiteSpace(entry.FriendName) ? "自分" : entry.FriendName!;

    private void FilterTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ApplyFilter();

    private void ClearFilterButton_Click(object sender, RoutedEventArgs e)
    {
        FilterFileName.Clear();
        FilterUser.Clear();
        FilterPath.Clear();
        FilterAction.Clear();
        FilterContent.Clear();
        FilterNote.Clear();
    }

    private void CopyRowMenuItem_Click(object sender, RoutedEventArgs e)
        => CopySelectedRow();

    private void HistoryDataGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.C &&
            System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            CopySelectedRow();
            e.Handled = true;
        }
    }

    private void CopySelectedRow()
    {
        if (HistoryDataGrid.SelectedItem is not HistoryRow row)
        {
            return;
        }

        string? header = (HistoryDataGrid.CurrentColumn?.Header as string);
        string value = header switch
        {
            "日時"       => row.TimeText,
            "ユーザー"   => row.UserText,
            "パス"       => row.PathText,
            "ファイル名" => row.FileNameText,
            "アクション" => row.EventTypeText,
            "内容"       => row.ContentText,
            "備考"       => row.NoteText,
            "結果"       => row.OutcomeText,
            _            => _entryMap.TryGetValue(row.EntryId, out HistoryEntry? e)
                               ? BuildDetailLeft(e) + Environment.NewLine + BuildDetailRight(e)
                               : string.Empty,
        };

        if (!string.IsNullOrEmpty(value))
        {
            System.Windows.Clipboard.SetText(value);
        }
    }

    private void HistoryDataGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (HistoryDataGrid.SelectedItem is not HistoryRow row ||
            string.IsNullOrWhiteSpace(row.EntryId) ||
            !_entryMap.TryGetValue(row.EntryId, out HistoryEntry? entry))
        {
            DetailLeft.Text = string.Empty;
            DetailRight.Text = string.Empty;
            return;
        }

        DetailLeft.Text = BuildDetailLeft(entry);
        DetailRight.Text = BuildDetailRight(entry);
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
    public bool IsFolder { get; init; }
    public string EventTypeText { get; init; } = string.Empty;
    public string ContentText { get; init; } = string.Empty;
    public string NoteText { get; init; } = string.Empty;
    public string OutcomeText { get; init; } = string.Empty;
    public string AttentionText { get; init; } = string.Empty;
    public MediaBrush RowBackground { get; init; } = MediaBrushes.White;
    public MediaBrush RowForeground { get; init; } = MediaBrushes.Black;
}

public sealed class DoubleToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? new GridLength(d) : GridLength.Auto;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
