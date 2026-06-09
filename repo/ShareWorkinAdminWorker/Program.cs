using System.IO;
using System.Text.Json;
using ShareWorkin.SMB;

namespace ShareWorkinAdminWorker;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        SwkLogger.Info(
            $"AdminWorker startup: processPath={Environment.ProcessPath ?? "null"} args={string.Join(" ", args)}");

        if (TryParseInvocation(args, out AdminCommandRequest? request, out string? resultPath))
        {
            return RunOnce(request!, resultPath!);
        }

        SwkLogger.Warn("AdminWorker invocation rejected: required arguments were missing or invalid");
        return 1;
    }

    private static int RunOnce(AdminCommandRequest request, string resultPath)
    {
        try
        {
            AdminCommandResponse response = SmbAdminOperations.Execute(request);
            WriteResponse(resultPath, response);
            return response.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            SwkLogger.Error("AdminWorker fatal error", ex);
            WriteResponse(resultPath, new AdminCommandResponse
            {
                Ok = false,
                CorrelationId = request.CorrelationId,
                ErrorCode = AdminErrorCode.InternalError,
                ErrorMessage = ex.Message
            });
            return 1;
        }
    }

    private static void WriteResponse(string resultPath, AdminCommandResponse response)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath) ?? AppContext.BaseDirectory);
        File.WriteAllText(resultPath, JsonSerializer.Serialize(response));
    }

    private static bool TryParseInvocation(
        string[] args,
        out AdminCommandRequest? request,
        out string? resultPath)
    {
        request = null;
        resultPath = null;

        if (args.Length == 0)
        {
            return false;
        }

        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = arg[2..].Split('=', 2);
            string key = parts[0];
            string value = parts.Length == 2 ? parts[1] : string.Empty;
            parsed[key] = value;
        }

        if (!parsed.TryGetValue("result-path", out string? parsedResultPath) ||
            string.IsNullOrWhiteSpace(parsedResultPath))
        {
            return false;
        }

        parsed.TryGetValue("cmd", out string? cmd);
        parsed.TryGetValue("corr", out string? correlationId);
        if (string.IsNullOrWhiteSpace(cmd) || string.IsNullOrWhiteSpace(correlationId))
        {
            return false;
        }

        _ = int.TryParse(parsed.GetValueOrDefault("access-right"), out int accessRight);

        request = new AdminCommandRequest
        {
            Cmd = cmd,
            CorrelationId = correlationId,
            ShopRootPath = parsed.GetValueOrDefault("shop-root"),
            ShareName = parsed.GetValueOrDefault("share-name"),
            ProfileLabel = parsed.GetValueOrDefault("profile-label"),
            AccessRight = accessRight,
            TargetPath = parsed.GetValueOrDefault("target-path"),
            IsSharedOff = string.Equals(parsed.GetValueOrDefault("shared-off"), "1", StringComparison.Ordinal),
            IsReadOnly = string.Equals(parsed.GetValueOrDefault("read-only"), "1", StringComparison.Ordinal),
            PolicySourceFolder = parsed.GetValueOrDefault("policy-source-folder"),
            Reason = parsed.GetValueOrDefault("reason")
        };
        resultPath = parsedResultPath;
        return true;
    }
}
