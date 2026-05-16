using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

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

    public static void Append(HistoryEntry entry)
    {
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
    }

    public static IReadOnlyList<HistoryEntry> GetEntries(HistoryChannel channel, int maxCount = 100)
    {
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
}
