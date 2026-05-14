using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareWorkin.SMB;

public static class SwkInstanceIdentity
{
    private static readonly object Sync = new();
    private static readonly string AppHomeDirectory = AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static readonly string IdentityPath = Path.Combine(AppHomeDirectory, "swk-instance.json");

    public static string GetOrCreateId()
    {
        lock (Sync)
        {
            string? existing = TryLoadId();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            string created = Guid.NewGuid().ToString("N");
            Save(created);
            return created;
        }
    }

    private static string? TryLoadId()
    {
        try
        {
            if (!File.Exists(IdentityPath))
            {
                return null;
            }

            IdentityFile? file = JsonSerializer.Deserialize<IdentityFile>(File.ReadAllText(IdentityPath));
            return string.IsNullOrWhiteSpace(file?.SwkInstanceId) ? null : file.SwkInstanceId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SwkLogger.Warn($"SwkInstanceIdentity load failed: {ex.Message}");
            return null;
        }
    }

    private static void Save(string id)
    {
        try
        {
            Directory.CreateDirectory(AppHomeDirectory);
            IdentityFile file = new()
            {
                SwkInstanceId = id,
                CreatedAt = DateTime.UtcNow.ToString("o"),
            };
            string json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(IdentityPath, json);
            SwkLogger.Info("SwkInstanceIdentity created");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SwkLogger.Warn($"SwkInstanceIdentity save failed: {ex.Message}");
        }
    }

    private sealed class IdentityFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("swkInstanceId")]
        public string SwkInstanceId { get; set; } = string.Empty;

        [JsonPropertyName("createdAt")]
        public string CreatedAt { get; set; } = string.Empty;
    }
}
