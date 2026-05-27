using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

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
            string currentBinding = GetCurrentMachineBinding();
            IdentityFile? existing = TryLoadIdentity();
            if (!string.IsNullOrWhiteSpace(existing?.SwkInstanceId))
            {
                if (string.IsNullOrWhiteSpace(existing.MachineBinding))
                {
                    Save(existing.SwkInstanceId, currentBinding);
                    return existing.SwkInstanceId;
                }

                if (string.Equals(existing.MachineBinding, currentBinding, StringComparison.OrdinalIgnoreCase))
                {
                    return existing.SwkInstanceId;
                }

                SwkLogger.Warn("SwkInstanceIdentity binding mismatch detected; regenerating instance id for this machine");
            }

            string created = Guid.NewGuid().ToString("N");
            Save(created, currentBinding);
            return created;
        }
    }

    private static IdentityFile? TryLoadIdentity()
    {
        try
        {
            if (!File.Exists(IdentityPath))
            {
                return null;
            }

            IdentityFile? file = JsonSerializer.Deserialize<IdentityFile>(File.ReadAllText(IdentityPath));
            return string.IsNullOrWhiteSpace(file?.SwkInstanceId) ? null : file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SwkLogger.Warn($"SwkInstanceIdentity load failed: {ex.Message}");
            return null;
        }
    }

    private static void Save(string id, string machineBinding)
    {
        try
        {
            Directory.CreateDirectory(AppHomeDirectory);
            IdentityFile file = new()
            {
                SwkInstanceId = id,
                MachineBinding = machineBinding,
                MachineName = Environment.MachineName,
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

    private static string GetCurrentMachineBinding()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid",
                null);
            if (value is string text && !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            SwkLogger.Warn($"SwkInstanceIdentity machine binding fallback: {ex.Message}");
        }

        return Environment.MachineName.Trim();
    }

    private sealed class IdentityFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 2;

        [JsonPropertyName("swkInstanceId")]
        public string SwkInstanceId { get; set; } = string.Empty;

        [JsonPropertyName("machineBinding")]
        public string MachineBinding { get; set; } = string.Empty;

        [JsonPropertyName("machineName")]
        public string MachineName { get; set; } = string.Empty;

        [JsonPropertyName("createdAt")]
        public string CreatedAt { get; set; } = string.Empty;
    }
}
