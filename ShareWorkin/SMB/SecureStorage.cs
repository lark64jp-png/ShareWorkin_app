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

    // 草案4 §A: アプリは自分のアプリホルダーの外に書き込まない。
    // secure.dat はアプリホルダー直下で、DPAPI LocalMachine スコープで保管する
    // (機内のどの利用者からも復号できるようにする ── アプリホルダー集約と整合させるため)。
    private static readonly string StorageDirectory = AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static readonly string StoragePath = Path.Combine(StorageDirectory, "secure.dat");

    // v1.04 までの保管位置(CurrentUser スコープ)。アップグレード時に一度だけ拾い直す。
    private static readonly string LegacyStoragePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShareWorkin",
        "secure.dat");

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
        if (!File.Exists(StoragePath) && File.Exists(LegacyStoragePath))
        {
            TryMigrateLegacyStorage();
        }

        if (!File.Exists(StoragePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            byte[] encrypted = File.ReadAllBytes(StoragePath);
            byte[] decrypted = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.LocalMachine);
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

    private static void TryMigrateLegacyStorage()
    {
        // CurrentUser で復号できるのは v1.04 当時に保存した利用者本人だけ。
        // 復号できなければ静かに諦め、上位の EnsureAccount に鍵再生成経路を委ねる。
        try
        {
            byte[] encrypted = File.ReadAllBytes(LegacyStoragePath);
            byte[] decrypted = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(StorageDirectory);
            byte[] reencrypted = ProtectedData.Protect(decrypted, optionalEntropy: null, DataProtectionScope.LocalMachine);
            File.WriteAllBytes(StoragePath, reencrypted);
            File.Delete(LegacyStoragePath);
            SwkLogger.Info("Migrated secure.dat from %LocalAppData% to app folder (DPAPI scope: CurrentUser → LocalMachine)");
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            SwkLogger.Warn($"secure.dat migration skipped: {ex.Message}");
        }
    }

    private static void SaveInternal(Dictionary<string, string> values)
    {
        try
        {
            Directory.CreateDirectory(StorageDirectory);
            string json = JsonSerializer.Serialize(values);
            byte[] data = Encoding.UTF8.GetBytes(json);
            byte[] encrypted = ProtectedData.Protect(data, optionalEntropy: null, DataProtectionScope.LocalMachine);
            File.WriteAllBytes(StoragePath, encrypted);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            SwkLogger.Error("SecureStorage save failed", ex);
            throw;
        }
    }
}
