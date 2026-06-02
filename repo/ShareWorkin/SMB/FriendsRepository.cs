using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShareWorkin.SMB;

public static class FriendsRepository
{
    // 新しい保存先: アプリフォルダー内（§A 準拠）
    private static readonly string FriendsPath = Path.Combine(
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        "friends.json");

    // 旧保存先: マイグレーション用
    private static readonly string LegacyFriendsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShareWorkin",
        "friends.json");

    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("ShareWorkin/Friend/Password");

    // 初期化時に旧ファイルのマイグレーションを実施
    static FriendsRepository()
    {
        TryMigrateLegacyFriendsFile();
    }

    /// <summary>
    /// 旧保存先（%LocalAppData%\ShareWorkin\friends.json）から新保存先（アプリフォルダー）へマイグレーション
    /// </summary>
    private static void TryMigrateLegacyFriendsFile()
    {
        try
        {
            // 新しい場所にすでに存在する場合は、旧ファイルを削除して完了
            if (File.Exists(FriendsPath))
            {
                if (File.Exists(LegacyFriendsPath))
                {
                    File.Delete(LegacyFriendsPath);
                    SwkLogger.Info("Migrated friends.json: deleted legacy file");
                }
                return;
            }

            // 旧ファイルが存在し、新しい場所にはない場合 → マイグレーション
            if (File.Exists(LegacyFriendsPath))
            {
                string json = File.ReadAllText(LegacyFriendsPath);
                Directory.CreateDirectory(Path.GetDirectoryName(FriendsPath)!);
                File.WriteAllText(FriendsPath, json);
                File.Delete(LegacyFriendsPath);
                SwkLogger.Info("Migrated friends.json: moved from %LocalAppData% to app folder");
            }
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"TryMigrateLegacyFriendsFile failed: {ex.Message}");
            // マイグレーション失敗は致命的ではない。通常の LoadAll で新しい場所を作成する
        }
    }

    public static IReadOnlyList<Friend> LoadAll()
    {
        if (!File.Exists(FriendsPath))
        {
            return Array.Empty<Friend>();
        }

        try
        {
            string json = File.ReadAllText(FriendsPath);
            FriendsFile? file = JsonSerializer.Deserialize<FriendsFile>(json);
            return file?.Friends ?? new List<Friend>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SwkLogger.Warn($"FriendsRepository.LoadAll failed: {ex.Message}");
            return Array.Empty<Friend>();
        }
    }

    public static bool SaveAll(IReadOnlyList<Friend> friends)
    {
        ArgumentNullException.ThrowIfNull(friends);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FriendsPath)!);
            FriendsFile file = new() { Version = 1, Friends = friends.ToList() };
            string json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FriendsPath, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SwkLogger.Warn($"FriendsRepository.SaveAll failed: {ex.Message}");
            return false;
        }
    }

    public static string ProtectPassword(string plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return string.Empty;
        }

        try
        {
            byte[] cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain),
                DpapiEntropy,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipher);
        }
        catch (CryptographicException ex)
        {
            SwkLogger.Warn($"ProtectPassword failed: {ex.Message}");
            return string.Empty;
        }
    }

    public static string UnprotectPassword(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
        {
            return string.Empty;
        }

        try
        {
            byte[] cipher = Convert.FromBase64String(protectedBase64);
            byte[] plain = ProtectedData.Unprotect(cipher, DpapiEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            SwkLogger.Warn($"UnprotectPassword failed: {ex.Message}");
            return string.Empty;
        }
    }

    private sealed class FriendsFile
    {
        public int Version { get; set; } = 1;
        public List<Friend> Friends { get; set; } = new();
    }
}
