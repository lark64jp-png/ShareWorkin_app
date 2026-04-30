using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace ShareWorkin.SMB;

public sealed record PowerShellResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

public static class PowerShellRunner
{
    private const string ExecutableName = "powershell.exe";

    private const string OutputEncodingPrelude =
        "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [System.Text.Encoding]::UTF8; ";

    public static PowerShellResult Run(string script, int timeoutMs = 15000)
    {
        ArgumentNullException.ThrowIfNull(script);
        return RunWithArgs(BuildCommandArgs(script), timeoutMs, environment: null);
    }

    public static PowerShellResult RunWithEnvironment(
        string script,
        string envName,
        string envValue,
        int timeoutMs = 15000)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentException.ThrowIfNullOrEmpty(envName);
        ArgumentNullException.ThrowIfNull(envValue);

        Dictionary<string, string> env = new(StringComparer.Ordinal)
        {
            [envName] = envValue,
        };
        return RunWithArgs(BuildCommandArgs(script), timeoutMs, env);
    }

    public static PowerShellResult RunWithArgs(IReadOnlyList<string> arguments, int timeoutMs)
        => RunWithArgs(arguments, timeoutMs, environment: null);

    public static PowerShellResult RunWithArgs(
        IReadOnlyList<string> arguments,
        int timeoutMs,
        IReadOnlyDictionary<string, string>? environment)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        ProcessStartInfo psi = new()
        {
            FileName = ExecutableName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach (KeyValuePair<string, string> kv in environment)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        StringBuilder stdoutBuilder = new();
        StringBuilder stderrBuilder = new();

        using Process process = new() { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdoutBuilder.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderrBuilder.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            SwkLogger.Error("PowerShell process could not be started", ex);
            return new PowerShellResult(-1, string.Empty, ex.Message, TimedOut: false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        bool exited = process.WaitForExit(timeoutMs);
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                SwkLogger.Warn($"PowerShell process kill failed: {ex.Message}");
            }
            SwkLogger.Warn($"PowerShell timed out after {timeoutMs} ms");
            return new PowerShellResult(-1, stdoutBuilder.ToString(), stderrBuilder.ToString(), TimedOut: true);
        }

        process.WaitForExit();

        return new PowerShellResult(
            process.ExitCode,
            stdoutBuilder.ToString(),
            stderrBuilder.ToString(),
            TimedOut: false);
    }

    private static IReadOnlyList<string> BuildCommandArgs(string script)
    {
        return new[]
        {
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy", "Bypass",
            "-OutputFormat", "Text",
            "-Command", OutputEncodingPrelude + script,
        };
    }
}
