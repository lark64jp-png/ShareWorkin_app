using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareWorkin.SMB;

public sealed class SwkHistoryJournalRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "Update";

    [JsonPropertyName("occurredAt")]
    public DateTime OccurredAt { get; set; } = DateTime.Now;

    [JsonPropertyName("friendId")]
    public string? FriendId { get; set; }

    [JsonPropertyName("friendName")]
    public string? FriendName { get; set; }

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "None";

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("targetName")]
    public string? TargetName { get; set; }

    [JsonPropertyName("path")]
    public string? PathText { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("interactionEventId")]
    public string? InteractionEventId { get; set; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "Info";

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("logLevel")]
    public string? LogLevel { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("sourcePath")]
    public string? SourcePath { get; set; }

    [JsonPropertyName("destinationPath")]
    public string? DestinationPath { get; set; }

    [JsonPropertyName("destinationFolder")]
    public string? DestinationFolder { get; set; }
}

public static class SwkHistoryJournal
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly string JournalPath = Path.Combine(
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        "history-journal.jsonl");
    public static event Action<string>? RecordAppended;

    public static void AppendOperation(
        string channel,
        string eventType,
        string message,
        string outcome = "Info",
        string direction = "None",
        string? friendId = null,
        string? friendName = null,
        string? targetName = null,
        string? pathText = null,
        string? note = null,
        string? interactionEventId = null,
        string? sourcePath = null,
        string? destinationPath = null,
        string? destinationFolder = null,
        string? source = null)
    {
        Append(new SwkHistoryJournalRecord
        {
            Channel = string.IsNullOrWhiteSpace(channel) ? "Update" : channel,
            EventType = eventType,
            Message = message,
            Outcome = string.IsNullOrWhiteSpace(outcome) ? "Info" : outcome,
            Direction = string.IsNullOrWhiteSpace(direction) ? "None" : direction,
            FriendId = friendId,
            FriendName = friendName,
            TargetName = targetName,
            PathText = pathText,
            Note = note,
            InteractionEventId = interactionEventId,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            DestinationFolder = destinationFolder,
            Source = source,
        });
    }

    public static void AppendLog(SwkLogLevel level, string message, string? source = null, string? targetName = null, string? pathText = null)
    {
        Append(new SwkHistoryJournalRecord
        {
            Channel = "Log",
            EventType = "Log",
            Message = message,
            Outcome = level switch
            {
                SwkLogLevel.Warn => "Warning",
                SwkLogLevel.Error => "Failure",
                _ => "Info",
            },
            Direction = "None",
            Source = source ?? "SwkLogger",
            LogLevel = level.ToString(),
            TargetName = targetName,
            PathText = pathText,
        });
    }

    public static IReadOnlyList<SwkHistoryJournalRecord> ReadEntries(string? channel = null, int? maxCount = null)
    {
        lock (Sync)
        {
            if (!File.Exists(JournalPath))
            {
                return [];
            }

            int? limit = maxCount.HasValue ? Math.Max(1, maxCount.Value) : null;
            Queue<SwkHistoryJournalRecord>? tail = limit.HasValue ? new Queue<SwkHistoryJournalRecord>(limit.Value) : null;
            List<SwkHistoryJournalRecord> records = limit.HasValue ? [] : new List<SwkHistoryJournalRecord>();
            try
            {
                foreach (string line in File.ReadLines(JournalPath, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        SwkHistoryJournalRecord? record = JsonSerializer.Deserialize<SwkHistoryJournalRecord>(line);
                        if (record is null)
                        {
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(channel) &&
                            !string.Equals(record.Channel, channel, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (tail is not null)
                        {
                            if (tail.Count == limit)
                            {
                                tail.Dequeue();
                            }

                            tail.Enqueue(record);
                        }
                        else
                        {
                            records.Add(record);
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SwkHistoryJournal.ReadEntries failed: {ex.Message}");
                return [];
            }

            if (tail is not null)
            {
                records = tail.ToList();
            }

            records.Reverse();
            return records;
        }
    }

    public static int DeleteEntries(string channel, DateTime? deleteThrough = null)
    {
        lock (Sync)
        {
            List<SwkHistoryJournalRecord> records = LoadAllRecordsCore();
            int beforeCount = records.Count;

            records = records
                .Where(record =>
                    !string.Equals(record.Channel, channel, StringComparison.OrdinalIgnoreCase) ||
                    (deleteThrough.HasValue && record.OccurredAt > deleteThrough.Value))
                .ToList();

            SaveAllRecordsCore(records);
            int deletedCount = beforeCount - records.Count;
            if (deletedCount > 0)
            {
                RecordAppended?.Invoke(channel);
            }

            return deletedCount;
        }
    }

    private static void Append(SwkHistoryJournalRecord record)
    {
        try
        {
            string line = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;
            lock (Sync)
            {
                File.AppendAllText(JournalPath, line, Encoding.UTF8);
            }

            RecordAppended?.Invoke(record.Channel);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SwkHistoryJournal.Append failed: {ex.Message}");
        }
    }

    private static List<SwkHistoryJournalRecord> LoadAllRecordsCore()
    {
        if (!File.Exists(JournalPath))
        {
            return [];
        }

        List<SwkHistoryJournalRecord> records = [];
        foreach (string line in File.ReadLines(JournalPath, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                SwkHistoryJournalRecord? record = JsonSerializer.Deserialize<SwkHistoryJournalRecord>(line);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
            }
        }

        return records;
    }

    private static void SaveAllRecordsCore(IReadOnlyList<SwkHistoryJournalRecord> records)
    {
        if (records.Count == 0)
        {
            if (File.Exists(JournalPath))
            {
                File.Delete(JournalPath);
            }

            return;
        }

        StringBuilder builder = new();
        foreach (SwkHistoryJournalRecord record in records)
        {
            builder.AppendLine(JsonSerializer.Serialize(record, JsonOptions));
        }

        File.WriteAllText(JournalPath, builder.ToString(), Encoding.UTF8);
    }
}
