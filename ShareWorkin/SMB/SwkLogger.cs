using System;
using System.IO;
using System.Text;

namespace ShareWorkin.SMB;

public enum SwkLogLevel
{
    Info,
    Warn,
    Error,
}

public static class SwkLogger
{
    private static readonly object Sync = new();

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShareWorkin",
        "logs");

    public static void Info(string message) => Write(SwkLogLevel.Info, message);

    public static void Warn(string message) => Write(SwkLogLevel.Warn, message);

    public static void Error(string message) => Write(SwkLogLevel.Error, message);

    public static void Error(string message, Exception ex)
        => Write(SwkLogLevel.Error, $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(SwkLogLevel level, string message)
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
        }
        catch
        {
        }
    }
}
