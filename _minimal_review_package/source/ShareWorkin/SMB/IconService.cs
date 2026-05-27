using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ShareWorkin.SMB;

// アイコンファイル管理。
// 草案6 §A: 画像はアプリフォルダー直下の icons\ 配下に置く。
//   icons\library\ ─ アプリ同梱の16種（インストーラー配置）
//   icons\custom\  ─ 利用者が指定したオリジナル画像（コピー保存）
public static class IconService
{
    public const string LibraryPrefix = "lib:";
    public const string CustomPrefix = "user:";

    private static readonly string IconsRoot = Path.Combine(
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        "icons");

    public static string LibraryDirectory => Path.Combine(IconsRoot, "library");
    public static string CustomDirectory => Path.Combine(IconsRoot, "custom");

    // ライブラリの16種定義。表示順とアプリ同梱の安定IDを兼ねる。
    public static readonly IReadOnlyList<string> LibraryNames = new[]
    {
        "thumb", "hood", "cup", "ninja",
        "rose", "bowl", "moon", "purse",
        "face_a", "face_b", "face_c", "face_d",
        "face_e", "face_f", "face_g", "face_h",
    };

    public static string MakeLibraryKey(string name) => LibraryPrefix + name;
    public static string MakeCustomKey(string fileName) => CustomPrefix + fileName;

    // IconKey から実ファイルパスを返す。未指定・該当なしは null。
    public static string? ResolvePath(string? iconKey)
    {
        if (string.IsNullOrWhiteSpace(iconKey)) return null;
        if (iconKey.StartsWith(LibraryPrefix, StringComparison.Ordinal))
        {
            string name = iconKey[LibraryPrefix.Length..];
            if (string.IsNullOrEmpty(name)) return null;
            string path = Path.Combine(LibraryDirectory, name + ".png");
            return File.Exists(path) ? path : null;
        }
        if (iconKey.StartsWith(CustomPrefix, StringComparison.Ordinal))
        {
            string fileName = iconKey[CustomPrefix.Length..];
            if (string.IsNullOrEmpty(fileName)) return null;
            string path = Path.Combine(CustomDirectory, fileName);
            return File.Exists(path) ? path : null;
        }
        return null;
    }

    // 利用者指定のオリジナル画像を icons\custom\ にコピーし、IconKey を返す。
    // friendId をファイル名に使うことで友達ごとに1ファイル・上書きで成立させる。
    public static string ImportCustom(string sourcePath, string friendId)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException(sourcePath);
        Directory.CreateDirectory(CustomDirectory);
        string ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrEmpty(ext)) ext = ".jpg";
        string fileName = friendId + ext.ToLowerInvariant();
        string dst = Path.Combine(CustomDirectory, fileName);
        // 同じ friendId で別拡張子のものが残っているとゴミになるため、まず掃除
        foreach (string old in Directory.EnumerateFiles(CustomDirectory, friendId + ".*"))
        {
            if (!string.Equals(old, dst, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(old); } catch { /* 残ってもキー側で整合する */ }
            }
        }
        File.Copy(sourcePath, dst, overwrite: true);
        return MakeCustomKey(fileName);
    }
}
