using System.Windows.Forms;
using Kite.Core.Models;
using Kite.Core.Serialization;
using Kite.Core.Storage;
using Kite.Desktop.WinForms;
using Kite.Management.Core;
using Kite.Management.Web;
using Kite.Server.Hosting;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        KiteHomeLayout layout = KiteHomeLayout.ResolveFromArgsAndEnv(args);
        var inMemoryHost = new KiteInMemoryHost(layout);
        inMemoryHost.StartAsync(seedDefaults: true).GetAwaiter().GetResult();

        // Check if an external Kite.Server daemon is already active (< 1ms).
        // If a stale daemon from an older build is still running, stop it so the fresh in-process engine can bind 8080/8443.
        KiteServerEngine? embeddedEngine = null;
        Task? engineStartTask = null;
        var (existingStatus, _) = AtomicJsonStore.ReadWithHashAsync(
            layout.ServerStatusPath,
            KiteJsonContext.Default.ServerStatus).GetAwaiter().GetResult();

        bool isRunning = KiteManagementService.IsServerProcessRunning(existingStatus);
        if (isRunning && existingStatus is not null)
        {
            string serverAssemblyPath = typeof(KiteServerEngine).Assembly.Location;
            if (!string.IsNullOrEmpty(serverAssemblyPath) &&
                File.Exists(serverAssemblyPath) &&
                existingStatus.StartedAtUtc < File.GetLastWriteTimeUtc(serverAssemblyPath))
            {
                inMemoryHost.ManagementService.StopServerDaemonAsync().GetAwaiter().GetResult();
                isRunning = false;
            }
        }

        // Best-effort cleanup of any renamed .old.* binaries from previous hot builds
        try
        {
            foreach (string oldFile in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.old.*"))
            {
                try { File.Delete(oldFile); } catch { }
            }
        }
        catch { }

        if (!isRunning)
        {
            embeddedEngine = new KiteServerEngine(layout, enableKestrelListeners: true);
            engineStartTask = Task.Run(async () => await embeddedEngine.StartAsync(seedDefaultsIfEmpty: true));
        }

        try
        {
            using var mainForm = new KiteMainForm(inMemoryHost);
            Application.Run(mainForm);
        }
        finally
        {
            if (engineStartTask is not null)
            {
                try { engineStartTask.GetAwaiter().GetResult(); } catch { }
            }
            if (embeddedEngine is not null)
            {
                embeddedEngine.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            inMemoryHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
