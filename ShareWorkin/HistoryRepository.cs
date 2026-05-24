using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShareWorkin.SMB;

namespace ShareWorkin;

public enum HistoryChannel
{
    Access,
    Update,
    Notification,
}

public enum HistoryDirection
{
    None,
    Incoming,
    Outgoing,
}

public enum HistoryOutcome
{
    Info,
    Success,
    Warning,
    Failure,
}

public sealed class HistoryEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("channel")]
    public HistoryChannel Channel { get; set; }

    [JsonPropertyName("occurredAt")]
    public DateTime OccurredAt { get; set; } = DateTime.Now;

    [JsonPropertyName("friendId")]
    public string? FriendId { get; set; }

    [JsonPropertyName("friendName")]
    public string? FriendName { get; set; }

    [JsonPropertyName("direction")]
    public HistoryDirection Direction { get; set; } = HistoryDirection.None;

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

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("sourcePath")]
    public string? SourcePath { get; set; }

    [JsonPropertyName("destinationPath")]
    public string? DestinationPath { get; set; }

    [JsonPropertyName("destinationFolder")]
    public string? DestinationFolder { get; set; }

    [JsonPropertyName("outcome")]
    public HistoryOutcome Outcome { get; set; } = HistoryOutcome.Info;
}

internal sealed class HistoryStore
{
    [JsonPropertyName("entries")]
    public List<HistoryEntry> Entries { get; set; } = [];
}

public static class HistoryRepository
{
    private static readonly object Sync = new();
    private static readonly string HistoryPath = Path.Combine(
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        "history.json");
    private const int MaxEntries = 300;
    public static event Action<HistoryChannel>? HistoryChanged;

    public static void Append(HistoryEntry entry)
    {
        if (entry.Channel == HistoryChannel.Update)
        {
            SwkHistoryJournal.AppendOperation(
                channel: entry.Channel.ToString(),
                eventType: entry.EventType,
                message: entry.Message,
                outcome: entry.Outcome.ToString(),
                direction: entry.Direction.ToString(),
                friendId: entry.FriendId,
                friendName: entry.FriendName,
                targetName: entry.TargetName,
                pathText: entry.PathText,
                note: entry.Note,
                interactionEventId: entry.InteractionEventId,
                sourcePath: entry.SourcePath,
                destinationPath: entry.DestinationPath,
                destinationFolder: entry.DestinationFolder,
                source: string.IsNullOrWhiteSpace(entry.Source) ? null : entry.Source);
            RaiseChanged(entry.Channel);
            return;
        }

        lock (Sync)
        {
            HistoryStore store = LoadStoreCore();
            store.Entries.Insert(0, entry);
            if (store.Entries.Count > MaxEntries)
            {
                store.Entries.RemoveRange(MaxEntries, store.Entries.Count - MaxEntries);
            }
            SaveStoreCore(store);
        }

        RaiseChanged(entry.Channel);
    }

    public static IReadOnlyList<HistoryEntry> GetEntries(HistoryChannel channel, int maxCount = 100)
    {
        if (channel == HistoryChannel.Update)
        {
            lock (Sync)
            {
                List<HistoryEntry> merged = LoadStoreCore().Entries
                    .Where(e => e.Channel == HistoryChannel.Update)
                    .Concat(ReadJournalEntries(channel))
                    .OrderByDescending(e => e.OccurredAt)
                    .Take(Math.Max(1, maxCount))
                    .ToList();
                return merged;
            }
        }

        lock (Sync)
        {
            return LoadStoreCore().Entries
                .Where(e => e.Channel == channel)
                .OrderByDescending(e => e.OccurredAt)
                .Take(Math.Max(1, maxCount))
                .ToList();
        }
    }

    public static IReadOnlyList<HistoryEntry> GetFriendEntries(
        string friendId,
        HistoryDirection? direction = null,
        int maxCount = 20)
    {
        if (string.IsNullOrWhiteSpace(friendId))
        {
            return Array.Empty<HistoryEntry>();
        }

        lock (Sync)
        {
            IEnumerable<HistoryEntry> query = LoadStoreCore().Entries
                .Concat(ReadJournalEntries())
                .Where(e => string.Equals(e.FriendId, friendId, StringComparison.Ordinal))
                .OrderByDescending(e => e.OccurredAt);

            if (direction.HasValue)
            {
                query = query.Where(e => e.Direction == direction.Value);
            }

            return query.Take(Math.Max(1, maxCount)).ToList();
        }
    }

    public static string BuildDisplayText(IEnumerable<HistoryEntry> entries)
    {
        StringBuilder builder = new();
        foreach (HistoryEntry entry in entries.OrderByDescending(e => e.OccurredAt))
        {
            builder.Append(entry.OccurredAt.ToString("yyyy/MM/dd HH:mm:ss"));
            builder.Append("  ");
            builder.Append(entry.Message);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public static int DeleteEntries(HistoryChannel channel, DateTime? deleteThrough = null)
    {
        if (channel == HistoryChannel.Update)
        {
            int deletedJournalCount = SwkHistoryJournal.DeleteEntries(channel.ToString(), deleteThrough);
            int deletedLegacyCount = 0;

            lock (Sync)
            {
                HistoryStore store = LoadStoreCore();
                int beforeCount = store.Entries.Count;
                store.Entries = store.Entries
                    .Where(entry =>
                        entry.Channel != HistoryChannel.Update ||
                        (deleteThrough.HasValue && entry.OccurredAt > deleteThrough.Value))
                    .ToList();

                deletedLegacyCount = beforeCount - store.Entries.Count;
                if (deletedLegacyCount > 0 || deleteThrough is null)
                {
                    SaveStoreCore(store);
                }
            }

            if (deletedJournalCount > 0 || deletedLegacyCount > 0)
            {
                RaiseChanged(channel);
            }

            return deletedJournalCount + deletedLegacyCount;
        }

        lock (Sync)
        {
            HistoryStore store = LoadStoreCore();
            int beforeCount = store.Entries.Count;
            store.Entries = store.Entries
                .Where(entry =>
                    entry.Channel != channel ||
                    (deleteThrough.HasValue && entry.OccurredAt > deleteThrough.Value))
                .ToList();

            int deletedCount = beforeCount - store.Entries.Count;
            if (deletedCount > 0 || deleteThrough is null)
            {
                SaveStoreCore(store);
            }

            if (deletedCount > 0)
            {
                RaiseChanged(channel);
            }

            return deletedCount;
        }
    }

    private static HistoryStore LoadStoreCore()
    {
        try
        {
            if (!File.Exists(HistoryPath))
            {
                return new HistoryStore();
            }

            HistoryStore? store = JsonSerializer.Deserialize<HistoryStore>(
                File.ReadAllText(HistoryPath, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return store ?? new HistoryStore();
        }
        catch (Exception ex)
        {
            SMB.SwkLogger.Warn($"HistoryRepository.LoadStoreCore failed: {ex.Message}");
            return new HistoryStore();
        }
    }

    private static void SaveStoreCore(HistoryStore store)
    {
        try
        {
            File.WriteAllText(
                HistoryPath,
                JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            SMB.SwkLogger.Warn($"HistoryRepository.SaveStoreCore failed: {ex.Message}");
        }
    }

    private static IEnumerable<HistoryEntry> ReadJournalEntries(HistoryChannel? channel = null)
    {
        string? channelText = channel?.ToString();
        return SwkHistoryJournal.ReadEntries(channelText)
            .Select(MapJournalEntry);
    }

    private static HistoryEntry MapJournalEntry(SwkHistoryJournalRecord record)
    {
        return new HistoryEntry
        {
            Id = string.IsNullOrWhiteSpace(record.Id) ? Guid.NewGuid().ToString("N") : record.Id,
            Channel = ParseChannel(record.Channel),
            OccurredAt = record.OccurredAt,
            FriendId = record.FriendId,
            FriendName = record.FriendName,
            Direction = ParseDirection(record.Direction),
            EventType = string.IsNullOrWhiteSpace(record.EventType) ? "Log" : record.EventType,
            TargetName = record.TargetName,
            PathText = record.PathText,
            Message = BuildJournalMessage(record),
            InteractionEventId = record.InteractionEventId,
            Note = record.Note,
            Source = record.Source,
            SourcePath = record.SourcePath,
            DestinationPath = record.DestinationPath,
            DestinationFolder = record.DestinationFolder,
            Outcome = ParseOutcome(record.Outcome),
        };
    }

    private static string BuildJournalMessage(SwkHistoryJournalRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.LogLevel))
        {
            return record.Message;
        }

        return $"[{record.LogLevel}] {record.Message}";
    }

    private static HistoryChannel ParseChannel(string? channel)
        => Enum.TryParse(channel, ignoreCase: true, out HistoryChannel parsed)
            ? parsed
            : HistoryChannel.Update;

    private static HistoryDirection ParseDirection(string? direction)
        => Enum.TryParse(direction, ignoreCase: true, out HistoryDirection parsed)
            ? parsed
            : HistoryDirection.None;

    private static HistoryOutcome ParseOutcome(string? outcome)
        => Enum.TryParse(outcome, ignoreCase: true, out HistoryOutcome parsed)
            ? parsed
            : HistoryOutcome.Info;

    private static void RaiseChanged(HistoryChannel channel)
    {
        try
        {
            HistoryChanged?.Invoke(channel);
        }
        catch
        {
        }
    }
}
