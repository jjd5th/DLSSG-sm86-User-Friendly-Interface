using System.Net.Http;
using System.Windows;
using DLSSGManager.Core.Abstractions;
using DLSSGManager.Core.Compatibility;
using DLSSGManager.Core.Configuration;
using DLSSGManager.Core.Diagnostics;
using DLSSGManager.Core.Discovery;
using DLSSGManager.Core.Hardware;
using DLSSGManager.Core.Infrastructure;
using DLSSGManager.Core.Models;
using DLSSGManager.Core.Installation;
using DLSSGManager.Core.Persistence;
using DLSSGManager.Core.Releases;
using DLSSGManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DLSSGManager;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = new AppDataPaths();
        paths.Ensure();
        AppLog.DirectoryPath = paths.Logs;
        var settings = new LibraryStore(paths).LoadSettings();
        AppLog.Enabled = settings.DiagnosticLogging;
        AppLog.Prune(settings.LogRetentionDays);
        ThemeService.Apply(settings.Theme);

        var collection = new ServiceCollection();
        collection.AddSingleton(paths);
        collection.AddSingleton<ILibraryStore, LibraryStore>();
        collection.AddSingleton<IHardwareProbe, WindowsHardwareProbe>();
        collection.AddSingleton<ISystemCompatibilityService, SystemCompatibilityService>();
        collection.AddSingleton<IGpuDetectionService, GpuDetectionService>();
        collection.AddSingleton<IGameDiscoveryService, GameDiscoveryService>();
        collection.AddSingleton(sp => new GameCompatibilityService(sp.GetRequiredService<AppDataPaths>().CompatibilityOverride));
        collection.AddSingleton<IGameCompatibilityService>(sp => sp.GetRequiredService<GameCompatibilityService>());
        collection.AddSingleton<IConfigurationService, ConfigurationService>();
        collection.AddSingleton<IBackupService, BackupService>();
        collection.AddSingleton<IProcessInspector, WindowsProcessInspector>();
        collection.AddSingleton<IDiagnosticsService, DiagnosticsService>();
        collection.AddSingleton<SessionState>();
        collection.AddSingleton<Func<AppSettings>>(sp => () => sp.GetRequiredService<SessionState>().Settings);
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DLSSGManager/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        collection.AddSingleton(http);
        collection.AddSingleton<IReleaseClient, HttpReleaseClient>();
        collection.AddSingleton<IReleaseService, ReleaseService>();
        collection.AddSingleton<IInstallationService, InstallationService>();
        collection.AddSingleton<IUserPrompt, UserPrompt>();
        collection.AddSingleton<LibraryCoordinator>();
        collection.AddSingleton<MainViewModel>();
        collection.AddSingleton<MainWindow>();
        Services = collection.BuildServiceProvider();

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Unhandled UI exception.", args.Exception);
            MessageBox.Show(args.Exception.Message, "DLSSG Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var window = Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        _ = Services.GetRequiredService<MainViewModel>().InitializeAsync();
    }
}
