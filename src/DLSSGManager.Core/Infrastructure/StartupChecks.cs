using System.Text.Json;
using DLSSGManager.Core.Abstractions;
using DLSSGManager.Core.Compatibility;
using DLSSGManager.Core.Configuration;
using DLSSGManager.Core.Installation;
using DLSSGManager.Core.Models;
using DLSSGManager.Core.Persistence;
using DLSSGManager.Core.Releases;

namespace DLSSGManager.Core.Infrastructure;

public static class StartupChecks
{
    public static int Run(string dataDirectory, IList<string> lines)
    {
        var ok = true;
        AppDataPaths? paths = null;
        void Check(string name, Action action)
        {
            try
            {
                action();
                lines.Add(name + ": ok");
            }
            catch (Exception ex)
            {
                ok = false;
                lines.Add(name + ": fail " + ex.Message);
            }
        }

        Check("app-data", () =>
        {
            paths = new AppDataPaths(dataDirectory);
            paths.Ensure();
            var probe = Path.Combine(paths.Root, ".self-test-write");
            File.WriteAllText(probe, "ok");
            if (File.ReadAllText(probe) != "ok")
                throw new InvalidOperationException("The application data directory could not be read back.");
            File.Delete(probe);
        });
        Check("settings", () =>
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings()))
                ?? throw new InvalidOperationException("Settings could not be deserialized.");
            if (!settings.ModRepository.Equals("sdli1995/dlssg_for_sm86", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The mod repository default is not the upstream project.");
            if (!settings.ManagerRepository.Equals("jjd5th/DLSSG_Manager_UI", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The manager repository default is not this application.");
        });
        Check("sqlite", () =>
        {
            var store = new LibraryStore(paths ?? throw new InvalidOperationException("Application data was not created."));
            var loaded = store.LoadSettings();
            store.SaveSettings(loaded);
            _ = store.LoadGames();
        });
        Check("compatibility-database", () =>
        {
            var database = new GameCompatibilityService(paths?.CompatibilityOverride);
            if (database.Database.Schema != 1 || database.Database.Entries.Count == 0)
                throw new InvalidOperationException("The bundled compatibility database is empty.");
        });
        Check("services", () =>
        {
            var ready = paths ?? throw new InvalidOperationException("Application data was not created.");
            var store = new LibraryStore(ready);
            var settings = new AppSettings();
            var configuration = new ConfigurationService();
            _ = configuration.Parse("[General]\r\nEnabled=1\r\n");
            _ = new InstallationService(new BackupService(), new WindowsProcessInspector(), store, ready, () => settings, configuration);
            _ = new ReleaseService(new OfflineReleaseClient(), ready, () => settings);
        });
        lines.Add("version: " + AppVersion.Current());
        lines.Add(ok ? "result: pass" : "result: fail");
        return ok ? 0 : 1;
    }

    private sealed class OfflineReleaseClient : IReleaseClient
    {
        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Self-test does not use the network.");

        public Task DownloadAsync(ReleaseAsset asset, string destination, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Self-test does not use the network.");
    }
}
