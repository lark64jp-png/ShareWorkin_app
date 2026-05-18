using System;
using System.IO;
using System.Text;

namespace ShareWorkin.SMB;

public enum SwkLogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

public static class SwkLogger
{
    private static readonly object Sync = new();

    // 草案4 §A: ログもアプリホルダー直下に置く。
    private static readonly string LogDirectory = Path.Combine(
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        "logs");

    // 開発期間中の観測用。完成時は SwkLogger.Debug(...) 呼び出しを grep で
    // 機械的に削除できるよう、デバッグ専用ログはこの経路のみに統一する。
    public static void Debug(string message, string? targetName = null, string? pathText = null)
        => Write(SwkLogLevel.Debug, message, targetName, pathText);

    public static void Info(string message) => Write(SwkLogLevel.Info, message);

    public static void Warn(string message) => Write(SwkLogLevel.Warn, message);

    public static void Error(string message) => Write(SwkLogLevel.Error, message);

    public static void Error(string message, Exception ex)
        => Write(SwkLogLevel.Error, $"{message} :: {ex}");

    private static void Write(SwkLogLevel level, string message, string? targetName = null, string? pathText = null)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string fileName = $"swk_{DateTime.Now:yyyy-MM-dd}.log";
            string filePath = Path.Combine(LogDirectory, fileName);
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (Sync)
            {
                File.AppendAllText(filePath, line, Encoding.UTF8);
            }

            SwkHistoryJournal.AppendLog(level, message, targetName: targetName, pathText: pathText);
        }
        catch
        {
        }
    }
}
