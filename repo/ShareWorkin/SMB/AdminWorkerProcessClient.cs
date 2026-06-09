using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ShareWorkin.SMB;

public sealed class AdminWorkerProcessClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AdminCommandResponse Execute(AdminCommandRequest request, int timeoutMs = 30000)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            request.CorrelationId = Guid.NewGuid().ToString("N");
        }

        string resultPath = Path.Combine(
            Path.GetTempPath(),
            $"shareworkin-admin-{request.CorrelationId}.json");
        string? permEntriesPath = null;

        try
        {
            TryDeleteResultFile(resultPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveAdminWorkerExePath(),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            AddArgument(startInfo, "cmd", request.Cmd);
            AddArgument(startInfo, "corr", request.CorrelationId);
            AddArgument(startInfo, "shop-root", request.ShopRootPath);
            AddArgument(startInfo, "share-name", request.ShareName);
            AddArgument(startInfo, "profile-label", request.ProfileLabel);
            AddArgument(startInfo, "access-right", request.AccessRight.ToString());
            AddArgument(startInfo, "target-path", request.TargetPath);
            AddArgument(startInfo, "shared-off", request.IsSharedOff ? "1" : "0");
            AddArgument(startInfo, "read-only", request.IsReadOnly ? "1" : "0");
            AddArgument(startInfo, "policy-source-folder", request.PolicySourceFolder);
            AddArgument(startInfo, "reason", request.Reason);

            if (request.ApplyPermissionsOnOpen)
            {
                permEntriesPath = Path.Combine(
                    Path.GetTempPath(),
                    $"shareworkin-admin-perms-{request.CorrelationId}.json");
                File.WriteAllText(permEntriesPath, JsonSerializer.Serialize(request.PermissionEntries));
                AddArgument(startInfo, "perm-entries-path", permEntriesPath);
            }

            AddArgument(startInfo, "result-path", resultPath);

            SwkLogger.Info($"AdminWorkerProcessClient start: cmd={request.Cmd} corr={request.CorrelationId}");
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return BuildHelperUnavailable(request, "管理者処理を開始できませんでした。");
            }

            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    SwkLogger.Warn(
                        $"AdminWorkerProcessClient kill failed: corr={request.CorrelationId} message={ex.Message}");
                }

                return BuildHelperUnavailable(request, "管理者処理が時間内に完了しませんでした。");
            }

            if (!File.Exists(resultPath))
            {
                return BuildHelperUnavailable(request, "管理者処理の結果を受け取れませんでした。");
            }

            string responseJson = File.ReadAllText(resultPath);
            AdminCommandResponse? response = JsonSerializer.Deserialize<AdminCommandResponse>(responseJson, JsonOptions);
            return response ?? BuildHelperUnavailable(request, "管理者処理の結果を読み取れませんでした。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SwkLogger.Warn($"AdminWorkerProcessClient canceled: corr={request.CorrelationId} message={ex.Message}");
            return BuildHelperUnavailable(request, "管理者権限の許可がキャンセルされました。");
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"AdminWorkerProcessClient failed: corr={request.CorrelationId} message={ex.Message}");
            return BuildHelperUnavailable(request, "管理者処理を開始できませんでした。");
        }
        finally
        {
            TryDeleteResultFile(resultPath);
            if (permEntriesPath != null) TryDeleteResultFile(permEntriesPath);
        }
    }

    private static void AddArgument(ProcessStartInfo startInfo, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        startInfo.ArgumentList.Add($"--{key}={value}");
    }

    private static string ResolveAdminWorkerExePath()
    {
        string? processPath = Environment.ProcessPath;
        string exeDir = string.IsNullOrWhiteSpace(processPath)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory;
        string adminExePath = Path.Combine(exeDir, $"{AdminProtocol.HelperProcessName}.exe");
        if (!File.Exists(adminExePath))
        {
            throw new FileNotFoundException("ShareWorkinAdminWorker.exe was not found.", adminExePath);
        }

        return adminExePath;
    }

    private static AdminCommandResponse BuildHelperUnavailable(AdminCommandRequest request, string message) => new()
    {
        Ok = false,
        CorrelationId = request.CorrelationId,
        ErrorCode = AdminErrorCode.HelperUnavailable,
        ErrorMessage = message
    };

    private static void TryDeleteResultFile(string resultPath)
    {
        try
        {
            if (File.Exists(resultPath))
            {
                File.Delete(resultPath);
            }
        }
        catch
        {
        }
    }
}
