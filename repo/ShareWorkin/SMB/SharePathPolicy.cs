using System;
using System.IO;

namespace ShareWorkin.SMB;

public static class SharePathPolicy
{
    // Windows NetShareAdd の禁則文字。ファイル名禁則 + SMB 共有名追加禁則 (+ = ; , [ ])。
    private static readonly char[] ForbiddenShareNameChars =
        ['\\', '/', ':', '*', '?', '"', '<', '>', '|', '+', '=', ';', ',', '[', ']'];

    public static string DeriveShareName(string shopFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(shopFolder);
        string trimmed = shopFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        string root = Path.GetPathRoot(shopFolder) ?? string.Empty;
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).TrimEnd(':');
    }

    public static bool ValidateShareName(string name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "お店の名前を決められませんでした。フォルダー名を確認してください。";
            return false;
        }

        int badIdx = name.IndexOfAny(ForbiddenShareNameChars);
        if (badIdx >= 0)
        {
            char badChar = name[badIdx];
            error = $"フォルダー名には共有名に使えない記号「{badChar}」が含まれています。" +
                    $"フォルダー名から「{badChar}」を外してから共有開始してください。";
            return false;
        }

        if (name.Length > 80)
        {
            error = "お店の名前が長すぎます(80文字まで)。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static char? FindFirstInvalidShareNameChar(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        int idx = name.IndexOfAny(ForbiddenShareNameChars);
        return idx >= 0 ? name[idx] : null;
    }

    public static bool TryNormalizeLocalPath(string? path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(fullPath) ||
                fullPath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
                !Path.IsPathRooted(fullPath))
            {
                return false;
            }

            normalizedPath = fullPath;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsUnderRoot(string path, string rootPath, bool allowEqual = true)
    {
        if (!TryNormalizeLocalPath(path, out string normalizedPath) ||
            !TryNormalizeLocalPath(rootPath, out string normalizedRoot))
        {
            return false;
        }

        if (allowEqual &&
            string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
