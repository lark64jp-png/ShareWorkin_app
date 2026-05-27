using System;
using System.Security.Cryptography;
using System.Text;

namespace ShareWorkin.SMB;

public static class EntryPasswordManager
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 120_000;

    public static bool IsConfigured =>
        SecureStorage.ContainsKey(SecureStorage.KeyEntryPasswordHash) &&
        SecureStorage.ContainsKey(SecureStorage.KeyEntryPasswordSalt);

    public static bool SetPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Hash(password, salt);
        SecureStorage.Set(SecureStorage.KeyEntryPasswordSalt, Convert.ToBase64String(salt));
        SecureStorage.Set(SecureStorage.KeyEntryPasswordHash, Convert.ToBase64String(hash));
        return true;
    }

    public static bool Verify(string password)
    {
        string? saltText = SecureStorage.Get(SecureStorage.KeyEntryPasswordSalt);
        string? hashText = SecureStorage.Get(SecureStorage.KeyEntryPasswordHash);
        if (string.IsNullOrWhiteSpace(saltText) || string.IsNullOrWhiteSpace(hashText))
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(saltText);
            byte[] expected = Convert.FromBase64String(hashText);
            byte[] actual = Hash(password, salt);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Hash(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);
}
