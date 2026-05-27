using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareWorkin.SMB;

public sealed class SwkIncomingInteractionRecord
{
    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("occurredAt")]
    public string OccurredAt { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("senderMachineName")]
    public string? SenderMachineName { get; set; }

    [JsonPropertyName("senderDisplayName")]
    public string? SenderDisplayName { get; set; }

    [JsonPropertyName("senderSwkInstanceId")]
    public string? SenderSwkInstanceId { get; set; }

    [JsonPropertyName("senderShareName")]
    public string? SenderShareName { get; set; }

    [JsonPropertyName("receiverShareName")]
    public string? ReceiverShareName { get; set; }

    [JsonPropertyName("targetName")]
    public string? TargetName { get; set; }

    [JsonPropertyName("targetRelativePath")]
    public string? TargetRelativePath { get; set; }

    [JsonPropertyName("targetFullPath")]
    public string? TargetFullPath { get; set; }

    [JsonPropertyName("targetFolder")]
    public string? TargetFolder { get; set; }

    [JsonPropertyName("targetKind")]
    public string? TargetKind { get; set; }

    [JsonPropertyName("notificationType")]
    public string? NotificationType { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("sourceRoute")]
    public string? SourceRoute { get; set; }

    [JsonPropertyName("receivedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReceivedAt { get; set; }

    [JsonPropertyName("displayedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayedAt { get; set; }

    [JsonPropertyName("isSenderVerified")]
    public bool IsSenderVerified { get; set; }

    [JsonPropertyName("verifiedFriendId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VerifiedFriendId { get; set; }

    [JsonPropertyName("verifiedFriendName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VerifiedFriendName { get; set; }

    [JsonPropertyName("processedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProcessedAt { get; set; }
}

internal sealed class SwkIncomingInteractionStore
{
    [JsonPropertyName("entries")]
    public List<SwkIncomingInteractionRecord> Entries { get; set; } = [];
}

public static class SwkIncomingInteractionInbox
{
    private static readonly object Sync = new();
    private static readonly string StorePath = Path.Combine(
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        "incoming-interactions.json");
    private const int MaxEntries = 300;

    public static void Append(SwkIncomingInteractionRecord entry)
    {
        lock (Sync)
        {
            SwkIncomingInteractionStore store = LoadStoreCore();
            SwkIncomingInteractionRecord? existing = store.Entries.FirstOrDefault(e =>
                string.Equals(e.EventId, entry.EventId, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.ProcessedAt ??= entry.ProcessedAt;
                existing.TargetFullPath ??= entry.TargetFullPath;
                existing.TargetFolder ??= entry.TargetFolder;
                existing.ReceivedAt ??= entry.ReceivedAt;
                existing.DisplayedAt ??= entry.DisplayedAt;
                existing.VerifiedFriendId ??= entry.VerifiedFriendId;
                existing.VerifiedFriendName ??= entry.VerifiedFriendName;
                existing.IsSenderVerified = existing.IsSenderVerified || entry.IsSenderVerified;
                SaveStoreCore(store);
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

    public static IReadOnlyList<SwkIncomingInteractionRecord> GetUnprocessed()
    {
        lock (Sync)
        {
            return LoadStoreCore().Entries
                .Where(e => string.IsNullOrWhiteSpace(e.ProcessedAt))
                .OrderBy(e => e.OccurredAt)
                .ToList();
        }
    }

    public static void MarkProcessed(string eventId, DateTime processedAt)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return;
        }

        lock (Sync)
        {
            SwkIncomingInteractionStore store = LoadStoreCore();
            SwkIncomingInteractionRecord? existing = store.Entries.FirstOrDefault(e =>
                string.Equals(e.EventId, eventId, StringComparison.Ordinal));
            if (existing is null)
            {
                return;
            }

            existing.ProcessedAt = processedAt.ToString("o");
            SaveStoreCore(store);
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
            SwkIncomingInteractionStore store = LoadStoreCore();
            SwkIncomingInteractionRecord? existing = store.Entries.FirstOrDefault(e =>
                string.Equals(e.EventId, eventId, StringComparison.Ordinal));
            if (existing is null)
            {
                return;
            }

            existing.DisplayedAt = displayedAt.ToString("o");
            SaveStoreCore(store);
        }
    }

    private static SwkIncomingInteractionStore LoadStoreCore()
    {
        try
        {
            if (!File.Exists(StorePath))
            {
                return new SwkIncomingInteractionStore();
            }

            using FileStream stream = File.OpenRead(StorePath);
            return JsonSerializer.Deserialize<SwkIncomingInteractionStore>(stream) ?? new SwkIncomingInteractionStore();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            SwkLogger.Warn($"SwkIncomingInteractionInbox.LoadStoreCore failed: {ex.Message}");
            return new SwkIncomingInteractionStore();
        }
    }

    private static void SaveStoreCore(SwkIncomingInteractionStore store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        using FileStream stream = File.Create(StorePath);
        JsonSerializer.Serialize(stream, store, new JsonSerializerOptions { WriteIndented = true });
    }
}
