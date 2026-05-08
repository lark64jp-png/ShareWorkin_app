using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareWorkin.SMB;

public sealed class ShopPermissionManifest
{
    public const string FileName = ".swk_permissions.json";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; set; } = string.Empty;

    [JsonPropertyName("entries")]
    public List<ShopPermissionManifestEntry> Entries { get; set; } = [];

    public static ShopPermissionManifest Load(string shopRootPath)
    {
        try
        {
            string path = Path.Combine(shopRootPath, FileName);
            if (!File.Exists(path))
            {
                return new ShopPermissionManifest();
            }

            ShopPermissionManifest? manifest =
                JsonSerializer.Deserialize<ShopPermissionManifest>(File.ReadAllText(path));
            return manifest ?? new ShopPermissionManifest();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SwkLogger.Warn($"ShopPermissionManifest.Load failed: {ex.Message}");
            return new ShopPermissionManifest();
        }
    }

    public static bool Save(string shopRootPath, IEnumerable<ShopPermissionManifestEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(shopRootPath);
            string path = Path.Combine(shopRootPath, FileName);
            ShopPermissionManifest manifest = new()
            {
                GeneratedAt = DateTime.UtcNow.ToString("o"),
                Entries = entries.ToList()
            };
            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden | FileAttributes.System);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SwkLogger.Warn($"ShopPermissionManifest.Save failed: {ex.Message}");
            return false;
        }
    }

    public ShopPermissionManifestEntry? FindEffectiveEntry(string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        ShopPermissionManifestEntry? best = null;
        int bestLength = -1;

        foreach (ShopPermissionManifestEntry entry in Entries)
        {
            string entryPath = NormalizeRelativePath(entry.RelativePath);
            if (entryPath.Length == 0)
            {
                continue;
            }

            bool match = string.Equals(normalized, entryPath, StringComparison.OrdinalIgnoreCase) ||
                         normalized.StartsWith(entryPath + "\\", StringComparison.OrdinalIgnoreCase);
            if (match && entryPath.Length > bestLength)
            {
                best = entry;
                bestLength = entryPath.Length;
            }
        }

        return best;
    }

    private static string NormalizeRelativePath(string path)
        => (path ?? string.Empty).Replace('/', '\\').Trim('\\');
}

public sealed class ShopPermissionManifestEntry
{
    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("users")]
    public List<string> Users { get; set; } = [];

    [JsonPropertyName("allowedMachineNames")]
    public List<string> AllowedMachineNames { get; set; } = [];

    [JsonPropertyName("readOnly")]
    public bool IsReadOnly { get; set; }

    [JsonPropertyName("sharedOff")]
    public bool IsSharedOff { get; set; }
}
