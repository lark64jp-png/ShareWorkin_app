using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ShareWorkin.SMB;

namespace ShareWorkinAdminWorker;

internal static class Program
{
    private const string OpenShopOperation = "open-shop";

    [STAThread]
    private static int Main(string[] args)
    {
        SwkLogger.Info(
            $"AdminWorker startup: processPath={Environment.ProcessPath ?? "null"} args={string.Join(" ", args)}");

        if (TryParseOpenShopInvocation(args, out AdminCommandRequest? request, out string? resultPath))
        {
            return RunOpenShopOnce(request!, resultPath!);
        }

        using Mutex mutex = new(initiallyOwned: true, @"Local\ShareWorkinAdminWorker", out bool createdNew);
        if (!createdNew)
        {
            SwkLogger.Info("AdminWorker startup skipped: existing instance detected");
            return 0;
        }

        try
        {
            var server = new AdminPipeServer();
            server.RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            SwkLogger.Error("AdminWorker fatal error", ex);
            return 1;
        }
    }

    private static int RunOpenShopOnce(AdminCommandRequest request, string resultPath)
    {
        try
        {
            AdminCommandResponse response = SmbAdminOperations.Execute(request);
            WriteResponse(resultPath, response);
            return response.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            SwkLogger.Error("AdminWorker open-shop fatal error", ex);
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

    private static bool TryParseOpenShopInvocation(
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

        string? op = null;
        string? correlationId = null;
        string? shopRootPath = null;
        string? shareName = null;
        string? profileLabel = null;
        int accessRight = 0;

        foreach (string arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = arg[2..].Split('=', 2);
            string key = parts[0];
            string value = parts.Length == 2 ? parts[1] : string.Empty;

            switch (key)
            {
                case "op":
                    op = value;
                    break;
                case "corr":
                    correlationId = value;
                    break;
                case "shop-root":
                    shopRootPath = value;
                    break;
                case "share-name":
                    shareName = value;
                    break;
                case "profile-label":
                    profileLabel = value;
                    break;
                case "access-right":
                    _ = int.TryParse(value, out accessRight);
                    break;
                case "result-path":
                    resultPath = value;
                    break;
            }
        }

        if (!string.Equals(op, OpenShopOperation, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(correlationId) ||
            string.IsNullOrWhiteSpace(shopRootPath) ||
            string.IsNullOrWhiteSpace(shareName) ||
            string.IsNullOrWhiteSpace(resultPath))
        {
            return false;
        }

        request = new AdminCommandRequest
        {
            Cmd = AdminProtocol.OpenShopCommand,
            CorrelationId = correlationId,
            ShopRootPath = shopRootPath,
            ShareName = shareName,
            ProfileLabel = string.IsNullOrWhiteSpace(profileLabel) ? shareName : profileLabel,
            AccessRight = accessRight
        };
        return true;
    }
}
