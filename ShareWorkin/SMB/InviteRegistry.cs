using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareWorkin.SMB;

/// <summary>
/// 発行済み招待 ID の管理。一回性を保証するため、
/// 店主が発行したコードと、それが使われたかどうかを invites.json に記録する。
/// </summary>
public static class InviteRegistry
{
    private static readonly string InvitesPath = Path.Combine(
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        "invites.json");

    private static readonly object FileLock = new();

    public sealed class InviteRecord
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("shareName")]
        public string ShareName { get; set; } = string.Empty;

        [JsonPropertyName("accessLevel")]
        public string AccessLevel { get; set; } = "Full";

        [JsonPropertyName("issuedAt")]
        public string IssuedAt { get; set; } = string.Empty;

        [JsonPropertyName("usedAt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UsedAt { get; set; }

        [JsonPropertyName("usedBy")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UsedBy { get; set; }

        [JsonIgnore]
        public bool IsUsed => !string.IsNullOrEmpty(UsedAt);
    }

    /// <summary>
    /// 新しい招待 ID を発行し、未使用として記録する。
    /// </summary>
    public static string Issue(string shareName, string label, string accessLevel)
    {
        string id = Guid.NewGuid().ToString("N");
        var record = new InviteRecord
        {
            Id = id,
            Label = label ?? string.Empty,
            ShareName = shareName ?? string.Empty,
            AccessLevel = accessLevel ?? "Full",
            IssuedAt = DateTime.UtcNow.ToString("o"),
        };

        lock (FileLock)
        {
            List<InviteRecord> all = LoadAllInternal();
            all.Add(record);
            SaveAllInternal(all);
        }

        SwkLogger.Info($"InviteRegistry: issued id={id} share='{shareName}' label='{label}'");
        return id;
    }

    /// <summary>
    /// 指定 ID が「発行済みかつ未使用」であるか確認する。
    /// </summary>
    public static InviteRecord? FindUnused(string inviteId, string shareName)
    {
        if (string.IsNullOrWhiteSpace(inviteId)) return null;

        lock (FileLock)
        {
            List<InviteRecord> all = LoadAllInternal();
            return all.FirstOrDefault(r =>
                string.Equals(r.Id, inviteId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.ShareName, shareName, StringComparison.OrdinalIgnoreCase) &&
                !r.IsUsed);
        }
    }

    /// <summary>
    /// 招待 ID を使用済みにマークする。承認後、パスワードを返す直前に呼ぶ。
    /// </summary>
    public static bool MarkUsed(string inviteId, string usedByMachineName)
    {
        if (string.IsNullOrWhiteSpace(inviteId)) return false;

        lock (FileLock)
        {
            List<InviteRecord> all = LoadAllInternal();
            InviteRecord? target = all.FirstOrDefault(r =>
                string.Equals(r.Id, inviteId, StringComparison.OrdinalIgnoreCase));
            if (target is null || target.IsUsed) return false;

            target.UsedAt = DateTime.UtcNow.ToString("o");
            target.UsedBy = usedByMachineName ?? string.Empty;
            SaveAllInternal(all);
        }

        SwkLogger.Info($"InviteRegistry: marked used id={inviteId} by='{usedByMachineName}'");
        return true;
    }

    public static IReadOnlyList<InviteRecord> LoadAll()
    {
        lock (FileLock)
        {
            return LoadAllInternal();
        }
    }

    private static List<InviteRecord> LoadAllInternal()
    {
        if (!File.Exists(InvitesPath)) return new List<InviteRecord>();

        try
        {
            string json = File.ReadAllText(InvitesPath);
            InvitesFile? file = JsonSerializer.Deserialize<InvitesFile>(json);
            return file?.Invites ?? new List<InviteRecord>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SwkLogger.Warn($"InviteRegistry.LoadAll failed: {ex.Message}");
            return new List<InviteRecord>();
        }
    }

    private static void SaveAllInternal(List<InviteRecord> records)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InvitesPath)!);
            InvitesFile file = new() { Version = 1, Invites = records };
            string json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(InvitesPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SwkLogger.Warn($"InviteRegistry.Save failed: {ex.Message}");
        }
    }

    private sealed class InvitesFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("invites")]
        public List<InviteRecord> Invites { get; set; } = new();
    }
}
