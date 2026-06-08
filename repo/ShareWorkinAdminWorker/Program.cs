using System.Threading.Tasks;
using ShareWorkin.SMB;

namespace ShareWorkinAdminWorker;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        using Mutex mutex = new(initiallyOwned: true, @"Local\ShareWorkinAdminWorker", out bool createdNew);
        if (!createdNew)
        {
            SwkLogger.Info("AdminWorker startup skipped: existing instance detected");
            return 0;
        }

        SwkLogger.Info($"AdminWorker startup: processPath={Environment.ProcessPath ?? "null"}");
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
}
