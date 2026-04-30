using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShareWorkin.SMB;

public static class SecureStorage
{
    private static readonly object Sync = new();

    private static readonly string StorageDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShareWorkin");

    private static readonly string StoragePath = Path.Combine(StorageDirectory, "secure.dat");

    public const string KeySwkGuestPassword = "swkguest.password";
    public const string KeySwkGuestCreatedAt = "swkguest.createdAt";
    public const string KeyEntryPasswordHash = "entry.passwordHash";
    public const string KeyEntryPasswordSalt = "entry.passwordSalt";

    public static string? Get(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Dictionary<string, string> values = LoadInternal();
        return values.TryGetValue(key, out string? value) ? value : null;
    }

    public static void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        lock (Sync)
        {
            Dictionary<string, string> values = LoadInternal();
            values[key] = value;
            SaveInternal(values);
        }
    }

    public static bool Remove(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        lock (Sync)
        {
            Dictionary<string, string> values = LoadInternal();
            if (!values.Remove(key))
            {
                return false;
            }
            SaveInternal(values);
            return true;
        }
    }

    public static bool ContainsKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Dictionary<string, string> values = LoadInternal();
        return values.ContainsKey(key);
    }

    public static void Clear()
    {
        lock (Sync)
        {
            try
            {
                if (File.Exists(StoragePath))
                {
                    File.Delete(StoragePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SwkLogger.Warn($"SecureStorage.Clear failed: {ex.Message}");
            }
        }
    }

    private static Dictionary<string, string> LoadInternal()
    {
        if (!File.Exists(StoragePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            byte[] encrypted = File.ReadAllBytes(StoragePath);
            byte[] decrypted = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(decrypted);
            Dictionary<string, string>? values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return values ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or UnauthorizedAccessException)
        {
            SwkLogger.Error("SecureStorage load failed", ex);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void SaveInternal(Dictionary<string, string> values)
    {
        try
        {
            Directory.CreateDirectory(StorageDirectory);
            string json = JsonSerializer.Serialize(values);
            byte[] data = Encoding.UTF8.GetBytes(json);
            byte[] encrypted = ProtectedData.Protect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(StoragePath, encrypted);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            SwkLogger.Error("SecureStorage save failed", ex);
            throw;
        }
    }
}
