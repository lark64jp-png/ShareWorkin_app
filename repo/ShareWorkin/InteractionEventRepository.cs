using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShareWorkin.SMB;

namespace ShareWorkin;

public enum InteractionEventDirection
{
    Incoming,
    Outgoing,
}

public sealed class InteractionEventEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("occurredAt")]
    public DateTime OccurredAt { get; set; } = DateTime.Now;

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("direction")]
    public InteractionEventDirection Direction { get; set; } = InteractionEventDirection.Outgoing;

    [JsonPropertyName("senderName")]
    public string? SenderName { get; set; }

    [JsonPropertyName("senderMachineName")]
    public string? SenderMachineName { get; set; }

    [JsonPropertyName("receiverId")]
    public string? ReceiverId { get; set; }

    [JsonPropertyName("receiverName")]
    public string? ReceiverName { get; set; }

    [JsonPropertyName("receiverMachineName")]
    public string? ReceiverMachineName { get; set; }

    [JsonPropertyName("targetName")]
    public string? TargetName { get; set; }

    [JsonPropertyName("targetPath")]
    public string? TargetPath { get; set; }

    [JsonPropertyName("targetFolder")]
    public string? TargetFolder { get; set; }

    [JsonPropertyName("targetKind")]
    public string? TargetKind { get; set; }

    [JsonPropertyName("notificationType")]
    public string? NotificationType { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("messageEnabled")]
    public bool MessageEnabled { get; set; }

    [JsonPropertyName("receivedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ReceivedAt { get; set; }

    [JsonPropertyName("displayedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? DisplayedAt { get; set; }

    [JsonPropertyName("sourceRoute")]
    public string? SourceRoute { get; set; }
}

internal sealed class InteractionEventStore
{
    [JsonPropertyName("entries")]
    public List<InteractionEventEntry> Entries { get; set; } = [];
}

public static class InteractionEventRepository
{
    private static readonly object Sync = new();
    private static readonly string StorePath = Path.Combine(
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        "interaction-events.json");
    private const int MaxEntries = 300;

    public static void Append(InteractionEventEntry entry)
    {
        lock (Sync)
        {
            InteractionEventStore store = LoadStoreCore();
            if (store.Entries.Any(e => string.Equals(e.Id, entry.Id, StringComparison.Ordinal)))
            {
                return;
            }

            store.Entries.Insert(0, entry);
            if (store.Entries.Count > MaxEntries)
            {
                store.Entries.RemoveRange(MaxEntries, store.Entries.Count - MaxEntries);
            }

            SaveStoreCore(store);
        }
    }

    public static bool Exists(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return false;
        }

        lock (Sync)
        {
            return LoadStoreCore().Entries.Any(e => string.Equals(e.Id, eventId, StringComparison.Ordinal));
        }
    }

    public static void MarkDisplayed(string eventId, DateTime displayedAt)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return;
        }

        lock (Sync)
        {
            InteractionEventStore store = LoadStoreCore();
            InteractionEventEntry? entry = store.Entries.FirstOrDefault(e =>
                string.Equals(e.Id, eventId, StringComparison.Ordinal));
            if (entry is null)
            {
                return;
            }

            entry.DisplayedAt = displayedAt;
            SaveStoreCore(store);
        }
    }

    private static InteractionEventStore LoadStoreCore()
    {
        try
        {
            if (!File.Exists(StorePath))
            {
                return new InteractionEventStore();
            }

            using FileStream stream = File.OpenRead(StorePath);
            return JsonSerializer.Deserialize<InteractionEventStore>(stream) ?? new InteractionEventStore();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            SwkLogger.Warn($"InteractionEventRepository.LoadStoreCore failed: {ex.Message}");
            return new InteractionEventStore();
        }
    }

    private static void SaveStoreCore(InteractionEventStore store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        using FileStream stream = File.Create(StorePath);
        JsonSerializer.Serialize(stream, store, new JsonSerializerOptions { WriteIndented = true });
    }
}
