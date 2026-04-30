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
    private static readonly string FriendsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShareWorkin",
        "friends.json");

    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("ShareWorkin/Friend/Password");

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
