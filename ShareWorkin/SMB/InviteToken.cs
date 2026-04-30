using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareWorkin.SMB;

public sealed class InviteTokenPayload
{
    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("host")]
    public string HostMachineName { get; set; } = string.Empty;

    [JsonPropertyName("share")]
    public string ShareName { get; set; } = string.Empty;

    [JsonPropertyName("user")]
    public string UserName { get; set; } = "swkguest";

    [JsonPropertyName("pass")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("access")]
    public string AccessLevel { get; set; } = "Full";

    [JsonPropertyName("label")]
    public string ProfileLabel { get; set; } = string.Empty;

    [JsonPropertyName("issued")]
    public string IssuedAt { get; set; } = string.Empty;
}

public static class InviteToken
{
    private const string TokenPrefix = "SWK1.";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly byte[] SharedKey =
    {
        0x53, 0x68, 0x61, 0x72, 0x65, 0x57, 0x6F, 0x72,
        0x6B, 0x69, 0x6E, 0x2D, 0x76, 0x31, 0x2E, 0x30,
        0x34, 0x2D, 0x69, 0x6E, 0x76, 0x69, 0x74, 0x65,
        0x2D, 0x6B, 0x65, 0x79, 0x2D, 0x32, 0x35, 0x36,
    };

    public static string Encode(InviteTokenPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        string json = JsonSerializer.Serialize(payload);
        byte[] plaintext = Encoding.UTF8.GetBytes(json);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];

        using AesGcm aes = new(SharedKey, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        byte[] output = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, output, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, output, NonceSize + ciphertext.Length, TagSize);

        return TokenPrefix + Base64UrlEncode(output);
    }

    public static bool TryDecode(string token, out InviteTokenPayload? payload, out string? error)
    {
        payload = null;
        error = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            error = "招待コードが空です。";
            return false;
        }

        string trimmed = token.Trim();
        if (!trimmed.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            error = "招待コードの形式が違います。";
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Base64UrlDecode(trimmed[TokenPrefix.Length..]);
        }
        catch (FormatException)
        {
            error = "招待コードを読み取れません。";
            return false;
        }

        if (bytes.Length < NonceSize + TagSize)
        {
            error = "招待コードが短すぎます。";
            return false;
        }

        byte[] nonce = new byte[NonceSize];
        byte[] ciphertext = new byte[bytes.Length - NonceSize - TagSize];
        byte[] tag = new byte[TagSize];

        Buffer.BlockCopy(bytes, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(bytes, NonceSize, ciphertext, 0, ciphertext.Length);
        Buffer.BlockCopy(bytes, NonceSize + ciphertext.Length, tag, 0, TagSize);

        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using AesGcm aes = new(SharedKey, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            error = "招待コードが正しくありません。";
            return false;
        }

        try
        {
            string json = Encoding.UTF8.GetString(plaintext);
            payload = JsonSerializer.Deserialize<InviteTokenPayload>(json);
            if (payload is null)
            {
                error = "招待コードの中身を読み取れません。";
                return false;
            }
            return true;
        }
        catch (JsonException)
        {
            error = "招待コードの中身を読み取れません。";
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string encoded)
    {
        string padded = encoded.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
