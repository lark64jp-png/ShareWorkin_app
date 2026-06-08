using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ShareWorkin.SMB;

namespace ShareWorkinTray;

internal sealed class AdminWorkerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AdminCommandResponse OpenShop(string shopRootPath, string shareName, string profileLabel, ShareAccessRight accessRight)
        => SendWithRecovery(new AdminCommandRequest
        {
            Cmd = AdminProtocol.OpenShopCommand,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ShopRootPath = shopRootPath,
            ShareName = shareName,
            ProfileLabel = profileLabel,
            AccessRight = accessRight == ShareAccessRight.Read ? 1 : 0
        }, timeoutMs: 60000);

    public AdminCommandResponse CloseShop(string shopRootPath, string shareName)
        => SendWithRecovery(new AdminCommandRequest
        {
            Cmd = AdminProtocol.CloseShopCommand,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ShopRootPath = shopRootPath,
            ShareName = shareName
        }, timeoutMs: 30000);

    public AdminCommandResponse SetSubfolderPermission(string shopRootPath, string targetPath, bool isSharedOff, bool isReadOnly)
        => SendWithRecovery(new AdminCommandRequest
        {
            Cmd = AdminProtocol.SetSubfolderPermissionCommand,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ShopRootPath = shopRootPath,
            TargetPath = targetPath,
            IsSharedOff = isSharedOff,
            IsReadOnly = isReadOnly
        }, timeoutMs: 30000);

    public AdminCommandResponse ResetPathToInherited(string shopRootPath, string targetPath)
        => SendWithRecovery(new AdminCommandRequest
        {
            Cmd = AdminProtocol.ResetPathToInheritedCommand,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ShopRootPath = shopRootPath,
            TargetPath = targetPath
        }, timeoutMs: 30000);

    public AdminCommandResponse MarkActionAftercare(
        string shopRootPath,
        string affectedPath,
        string policySourceFolder,
        SharePolicyRepairReason reason)
        => SendWithRecovery(new AdminCommandRequest
        {
            Cmd = AdminProtocol.MarkActionAftercareCommand,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ShopRootPath = shopRootPath,
            TargetPath = affectedPath,
            PolicySourceFolder = policySourceFolder,
            Reason = reason.ToString()
        }, timeoutMs: 30000);

    private static AdminCommandResponse SendWithRecovery(AdminCommandRequest request, int timeoutMs)
    {
        AdminCommandResponse? response = TrySend(request, timeoutMs);
        if (response is not null)
        {
            return response;
        }

        StartHelperFromScheduledTask();
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);
            response = TrySend(request, timeoutMs);
            if (response is not null)
            {
                return response;
            }
        }

        return new AdminCommandResponse
        {
            Ok = false,
            CorrelationId = request.CorrelationId,
            ErrorCode = AdminErrorCode.HelperUnavailable,
            ErrorMessage = "管理者ヘルパーに接続できませんでした。"
        };
    }

    private static AdminCommandResponse? TrySend(AdminCommandRequest request, int timeoutMs)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", AdminProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(Math.Max(1, timeoutMs));
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(request));
            using var cts = new CancellationTokenSource(timeoutMs);
            string? responseJson = reader.ReadLineAsync(cts.Token).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AdminCommandResponse>(responseJson, JsonOptions);
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"AdminWorkerClient send failed: cmd={request.Cmd} corr={request.CorrelationId} message={ex.Message}");
            return null;
        }
    }

    private static void StartHelperFromScheduledTask()
    {
        try
        {
            SwkLogger.Info("AdminWorkerClient start request: route=scheduled-task");
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Run /TN \"{AdminProtocol.HelperTaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            _ = process;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"AdminWorkerClient start request failed: {ex.Message}");
        }
    }
}
