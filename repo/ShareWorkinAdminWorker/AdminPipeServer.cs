using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ShareWorkin.SMB;

namespace ShareWorkinAdminWorker;

internal sealed class AdminPipeServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    public async Task RunAsync()
    {
        while (true)
        {
            using NamedPipeServerStream pipe = CreatePipeServer();
            await pipe.WaitForConnectionAsync().ConfigureAwait(false);
            await HandleClientAsync(pipe).ConfigureAwait(false);
        }
    }

    private static NamedPipeServerStream CreatePipeServer()
    {
        PipeSecurity security = BuildPipeSecurity();
        return NamedPipeServerStreamAcl.Create(
            AdminProtocol.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security,
            HandleInheritability.None);
    }

    private static PipeSecurity BuildPipeSecurity()
    {
        var security = new PipeSecurity();
        SecurityIdentifier currentUserSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current user SID could not be resolved.");
        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        security.AddAccessRule(new PipeAccessRule(currentUserSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(adminsSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(systemSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        try
        {
            string? line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            AdminCommandRequest request = JsonSerializer.Deserialize<AdminCommandRequest>(line, JsonOptions)
                ?? new AdminCommandRequest();

            if (!TryValidateClientProcess(pipe, out string clientDescription))
            {
                SwkLogger.Warn($"AdminWorker rejected client corr={request.CorrelationId} client={clientDescription}");
                await writer.WriteLineAsync(JsonSerializer.Serialize(new AdminCommandResponse
                {
                    Ok = false,
                    CorrelationId = request.CorrelationId,
                    ErrorCode = AdminErrorCode.UnauthorizedClient,
                    ErrorMessage = "Unauthorized pipe client."
                })).ConfigureAwait(false);
                return;
            }

            SwkLogger.Info($"AdminWorker accepted client corr={request.CorrelationId} client={clientDescription} cmd={request.Cmd}");
            AdminCommandResponse response = SmbAdminOperations.Execute(request);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SwkLogger.Error("AdminWorker.HandleClient error", ex);
            try
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new AdminCommandResponse
                {
                    Ok = false,
                    ErrorCode = AdminErrorCode.InternalError,
                    ErrorMessage = ex.Message
                })).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static bool TryValidateClientProcess(NamedPipeServerStream pipe, out string clientDescription)
    {
        clientDescription = "unknown";
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid == 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById((int)pid);
            string processName = process.ProcessName;
            string? processPath = null;
            try
            {
                processPath = process.MainModule?.FileName;
            }
            catch
            {
            }

            clientDescription = $"{processName}:{pid}";
            if (!string.Equals(processName, AdminProtocol.TrayProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(processPath) ||
                   string.Equals(
                       Path.GetFileName(processPath),
                       $"{AdminProtocol.TrayProcessName}.exe",
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
