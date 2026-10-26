using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using DLSSGManager.Core.Abstractions;
using DLSSGManager.Core.Discovery;
using DLSSGManager.Core.Infrastructure;
using DLSSGManager.Core.Models;
using DLSSGManager.Core.Releases;

namespace DLSSGManager;

public sealed class LibraryCoordinator
{
    private readonly ILibraryStore _store;
    private readonly IGameDiscoveryService _discovery;
    private readonly IGameCompatibilityService _compatibility;
    private readonly IGpuDetectionService _gpu;
    private readonly IReleaseService _releases;
    private readonly IInstallationService _install;
    private readonly IConfigurationService _configuration;
    private readonly IDiagnosticsService _diagnostics;
    private readonly IUserPrompt _prompt;
    private readonly AppDataPaths _paths;
    private readonly HttpClient _artwork = new() { Timeout = TimeSpan.FromSeconds(20) };

    public LibraryCoordinator(
        ILibraryStore store,
        IGameDiscoveryService discovery,
        IGameCompatibilityService compatibility,
        IGpuDetectionService gpu,
        IReleaseService releases,
        IInstallationService install,
        IConfigurationService configuration,
        IDiagnosticsService diagnostics,
        IUserPrompt prompt,
        AppDataPaths paths,
        SessionState session)
    {
        _store = store;
        _discovery = discovery;
        _compatibility = compatibility;
        _gpu = gpu;
        _releases = releases;
        _install = install;
        _configuration = configuration;
        _diagnostics = diagnostics;
        _prompt = prompt;
        _paths = paths;
        Session = session;
    }

    public SessionState Session { get; }

    public async Task InitializeAsync()
    {
        Session.Settings = _store.LoadSettings();
        var recovered = _install.RecoverInterrupted();
        if (recovered > 0)
            Session.StatusText = $"Recovered {recovered} interrupted installation(s).";
        var loaded = _store.LoadGames().Select(game => new GameItem(game)).ToList();
        Session.ReplaceGames(loaded);
        await RefreshHardwareAsync();
        if (Session.Settings.CheckUpdatesOnStartup)
            await CheckUpdatesAsync(CancellationToken.None);
        if (Session.Settings.ScanOnStartup)
            await ScanAsync(CancellationToken.None);
        else
            await RefreshInstallStatesAsync();
    }

    public async Task RefreshHardwareAsync()
    {
        Session.StatusText = "Detecting GPU…";
        var report = await Task.Run(_gpu.Detect);
        Session.SystemReport = report;
        Session.StatusText = report.Summary;
    }

    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        Session.IsBusy = true;
        Session.ProgressIndeterminate = true;
        Session.StatusText = "Scanning installed games…";
        try
        {
            var settings = Session.Settings;
            var existing = Session.Games.Select(item => item.Record).ToList();
            var progress = new Progress<ScanProgress>(update => Session.StatusText = update.Message);
            var result = await _discovery.ScanAsync(new ScanRequest
            {
                Steam = settings.DiscoverSteam,
                Epic = settings.DiscoverEpic,
                Gog = settings.DiscoverGog,
                Xbox = settings.DiscoverXbox,
                Ea = settings.DiscoverEa,
                Ubisoft = settings.DiscoverUbisoft,
                BattleNet = settings.DiscoverBattleNet,
                ManualDirectories = settings.CustomDirectories,
                Progress = progress
            }, existing, cancellationToken);
            var latest = Session.Releases?.Selected?.Tag;
            var compatibilityProgress = new Progress<string>(text => Session.StatusText = text);
            await Task.Run(() =>
            {
                var index = 0;
                foreach (var game in result.Games)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    index++;
                    if (index == 1 || index % 5 == 0)
                        ((IProgress<string>)compatibilityProgress).Report($"Checking compatibility {index} of {result.Games.Count}…");
                    if (game.Compatibility == null)
                        game.Compatibility = _compatibility.Evaluate(game);
                    game.Installation = _install.Inspect(game, game.Installation ?? _store.LoadInstallation(game.Id), latest);
                }
            }, cancellationToken);
            var items = result.Games.Select(game => new GameItem(game)).ToList();
            Session.ReplaceGames(items);
            _store.SaveGames(result.Games);
            settings.LastScanUtc = DateTimeOffset.UtcNow;
            _store.SaveSettings(settings);
            Session.StatusText = $"Found {items.Count(item => !item.Record.Excluded)} games.";
            if (result.Warnings.Count > 0)
                Session.StatusText += " " + result.Warnings.Count + " launcher warnings.";
            _ = CacheArtworkAsync(items, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Session.StatusText = "Scan cancelled.";
        }
        finally
        {
            Session.IsBusy = false;
        }
    }

    public async Task CheckUpdatesAsync(CancellationToken cancellationToken)
    {
        Session.IsBusy = true;
        Session.StatusText = "Checking GitHub releases…";
        try
        {
            var result = await _releases.CheckAsync(Session.Settings.IncludePrerelease, Session.Settings.PinnedModVersion, cancellationToken);
            Session.Releases = result;
            Session.StatusText = result.Selected == null
                ? "No mod release was selected."
                : $"Latest mod release: {result.Selected.Tag}";
            if (!string.IsNullOrWhiteSpace(result.Warning))
                Session.StatusText = result.Warning;
            await RefreshInstallStatesAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Update check failed.", ex);
            _prompt.Error("Updates", ex.Message);
            Session.StatusText = "Update check failed.";
        }
        finally
        {
            Session.IsBusy = false;
        }
    }

    public async Task InstallAsync(GameItem item, bool update)
    {
        var decision = item.Record.Compatibility;
        var system = Session.SystemReport;
        if (system == null)
        {
            _prompt.Error("Install", "Hardware has not been detected yet.");
            return;
        }
        if (!system.InstallAllowed)
        {
            _prompt.Error("Install", system.Summary + Environment.NewLine + string.Join(Environment.NewLine, system.Reasons));
            return;
        }
        if (decision == null || decision.Status is GameCompatibilityStatus.Blocked or GameCompatibilityStatus.Unsupported)
        {
            _prompt.Error("Install", decision?.Explanation ?? "This game cannot be modified.");
            return;
        }
        var release = Session.Releases?.Selected;
        if (release?.PreferredPackage == null)
        {
            _prompt.Info("Install", "Check for updates first so a release package can be downloaded.");
            return;
        }
        Session.IsBusy = true;
        Session.StatusText = "Downloading " + release.Tag + "…";
        try
        {
            var package = await _releases.PrepareAsync(release, new Progress<double>(value =>
            {
                Session.ProgressIndeterminate = false;
                Session.Progress = value;
            }), CancellationToken.None);
            var directory = Path.GetDirectoryName(item.Record.ExecutablePath ?? "") ?? item.Record.InstallPath;
            var proxy = item.Record.Installation?.ProxyFileName;
            if (string.IsNullOrWhiteSpace(proxy) || !package.Manifest.ProxyOptions.Contains(proxy, StringComparer.OrdinalIgnoreCase))
                proxy = package.Manifest.DefaultProxy;
            var plan = InstallPlanner.Plan(package.Manifest, proxy, null, null);
            var conflicts = _install.DetectConflicts(directory, plan, item.Record.Installation?.Files ?? []);
            var extras = ProxyNames.Order.Where(name =>
                !name.Equals(proxy, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(directory, name))).ToList();
            var preview = new InstallPreview
            {
                GameName = item.Name,
                Executable = item.Record.ExecutablePath ?? "",
                Release = release.Tag,
                Compatibility = decision.Summary + " " + decision.Explanation,
                Manifest = package.Manifest.Confident
                    ? $"{package.Manifest.Layout}, runtime {package.Manifest.RuntimeModel}, max generated frames {package.Manifest.MaxGeneratedFrames}."
                    : package.Manifest.Uncertainty ?? "The package layout is uncertain.",
                Signature = package.Signature.Message,
                Warnings = BuildWarnings(item, system, package),
                Conflicts = conflicts,
                ExtraProxies = extras,
                ProxyOptions = package.Manifest.ProxyOptions.Where(name => package.Manifest.Files.Any(file => file.DestinationRelativePath.Equals(name, StringComparison.OrdinalIgnoreCase))).ToList(),
                DefaultProxy = proxy,
                NeedsAcknowledgement = system.RequiresAcknowledgement || decision.Status == GameCompatibilityStatus.Unknown || item.Record.ExecutableConfidence == ScanConfidence.Low || !package.Manifest.Confident || !package.Signature.IsTrusted
            };
            Session.IsBusy = false;
            var choice = _prompt.ConfirmInstall(preview);
            if (choice == null)
                return;
            Session.IsBusy = true;
            var result = await Task.Run(() => _install.Install(new InstallRequest
            {
                Game = item.Record,
                Package = package,
                System = system,
                AcknowledgeSystemWarning = true,
                AllowUnknownGame = true,
                AllowLowConfidenceExecutable = true,
                AllowUncertainManifest = true,
                OverwriteConflicts = choice.OverwriteConflicts || conflicts.Count == 0,
                RemoveUnownedProxies = choice.RemoveExtraProxies,
                ProxyFileName = choice.Proxy,
                IsUpdate = update || item.Record.Installation?.State == ModInstallState.UpdateAvailable
            }, CancellationToken.None));
            item.Record.Installation = _install.Inspect(item.Record, item.Record.Installation, release.Tag);
            item.Refresh();
            _store.SaveGames(Session.Games.Select(game => game.Record));
            Session.Notify();
            Show(result);
        }
        catch (Exception ex)
        {
            AppLog.Error("Install failed.", ex);
            _prompt.Error("Install", ex.Message);
        }
        finally
        {
            Session.IsBusy = false;
            Session.ProgressIndeterminate = true;
        }
    }

    public async Task UninstallAsync(GameItem item, bool restore)
    {
        if (!_prompt.Confirm(restore ? "Restore backup" : "Uninstall",
                restore
                    ? $"Restore original files and remove the mod files DLSSG Manager owns for {item.Name}?"
                    : $"Uninstall DLSSG for SM86 from {item.Name}? Original files that were backed up are restored. Unrelated files stay."))
            return;
        var result = await Task.Run(() => _install.Uninstall(item.Record, restore, CancellationToken.None));
        item.Record.Installation = _install.Inspect(item.Record, null, Session.Releases?.Selected?.Tag);
        item.Refresh();
        _store.SaveGames(Session.Games.Select(game => game.Record));
        Session.Notify();
        Show(result);
    }

    public async Task RepairAsync(GameItem item)
    {
        var release = Session.Releases?.Releases.FirstOrDefault(entry => entry.Tag == item.Record.Installation?.ReleaseTag) ?? Session.Releases?.Selected;
        if (release == null)
        {
            _prompt.Info("Repair", "Check for updates so the release package can be downloaded.");
            return;
        }
        Session.IsBusy = true;
        try
        {
            var package = await _releases.PrepareAsync(release, null, CancellationToken.None);
            var result = await Task.Run(() => _install.Repair(item.Record, package, CancellationToken.None));
            item.Refresh();
            _store.SaveGames(Session.Games.Select(game => game.Record));
            Session.Notify();
            Show(result);
        }
        catch (Exception ex)
        {
            _prompt.Error("Repair", ex.Message);
        }
        finally
        {
            Session.IsBusy = false;
        }
    }

    public async Task RollbackAsync(GameItem item)
    {
        var result = await Task.Run(() => _install.Rollback(item.Record, CancellationToken.None));
        item.Refresh();
        _store.SaveGames(Session.Games.Select(game => game.Record));
        Session.Notify();
        Show(result);
    }

    public async Task UpdateSelectedAsync(IEnumerable<GameItem> items)
    {
        var list = items.Where(item => item.Record.Installation?.State == ModInstallState.UpdateAvailable).ToList();
        if (list.Count == 0)
        {
            _prompt.Info("Update", "No selected games have a mod update.");
            return;
        }
        foreach (var item in list)
            await InstallAsync(item, true);
    }

    public void Configure(GameItem item)
    {
        var directory = item.Record.Installation?.InstallDirectory ?? Path.GetDirectoryName(item.Record.ExecutablePath ?? "");
        if (string.IsNullOrWhiteSpace(directory))
            return;
        var ini = Path.Combine(directory, "dlssg_sm86.ini");
        if (!File.Exists(ini))
        {
            _prompt.Error("Configure", "dlssg_sm86.ini was not found beside the rendering executable.");
            return;
        }
        var config = _configuration.Load(ini);
        var caps = CapabilitiesFor(item, directory);
        var saved = _prompt.EditConfiguration(new ConfigEditRequest
        {
            Title = item.Name,
            Release = item.Record.Installation?.ReleaseTag ?? "Unknown",
            Config = config,
            Capabilities = caps,
            Service = _configuration,
            Preset = Session.Settings.DefaultPreset
        });
        if (!saved)
            return;
        _configuration.Save(ini, config);
        var hash = Hashing.Sha256File(ini);
        var record = item.Record.Installation;
        var owned = record?.Files.FirstOrDefault(file => file.RelativePath.EndsWith("dlssg_sm86.ini", StringComparison.OrdinalIgnoreCase));
        if (owned != null && record != null)
        {
            owned.Sha256 = hash;
            owned.Size = new FileInfo(ini).Length;
            record.ConfigurationModified = false;
            record.State = ModInstallState.InstalledVerified;
            _store.SaveInstallation(record);
        }
        item.Record.Installation = _install.Inspect(item.Record, record, Session.Releases?.Selected?.Tag);
        item.Refresh();
        Session.Notify();
        _prompt.Info("Configure", "Saved. Exit the game completely and start it again. The mod does not reload the INI while it is running.");
    }

    public void ChooseExecutable(GameItem item)
    {
        var choice = _prompt.PickExecutable(item.Record.InstallPath);
        if (choice == null)
            return;
        var full = Path.GetFullPath(choice);
        var root = Path.GetFullPath(item.Record.InstallPath);
        if (!full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && !full.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            _prompt.Error("Executable", "Choose an executable inside this game's install folder, or change the install folder.");
            return;
        }
        item.Record.ExecutablePath = full;
        item.Record.UserChoseExecutable = true;
        item.Record.ExecutableConfidence = ScanConfidence.High;
        item.Record.ExecutableReason = "Chosen manually.";
        item.Record.Compatibility = _compatibility.Evaluate(item.Record);
        item.Record.Stamp = GameDiscoveryService.Stamp(item.Record);
        item.Refresh();
        _store.SaveGames(Session.Games.Select(game => game.Record));
        Session.Notify();
    }

    public void SetInstallFolder(GameItem item)
    {
        var folder = _prompt.PickFolder();
        if (folder == null || !Directory.Exists(folder))
            return;
        item.Record.InstallPath = folder;
        if (!item.Record.UserChoseExecutable)
        {
            var selection = ExecutableSelector.Select(folder);
            item.Record.ExecutablePath = selection.Path;
            item.Record.ExecutableConfidence = selection.Confidence;
            item.Record.ExecutableReason = selection.Reason;
        }
        item.Record.Compatibility = _compatibility.Evaluate(item.Record);
        item.Refresh();
        _store.SaveGames(Session.Games.Select(game => game.Record));
        Session.Notify();
    }

    public void Exclude(GameItem item)
    {
        item.Record.Excluded = !item.Record.Excluded;
        _store.SaveGames(Session.Games.Select(game => game.Record));
        Session.Notify();
    }

    public async Task AddFolderAsync()
    {
        var folder = _prompt.PickFolder();
        if (folder == null)
            return;
        if (!Session.Settings.CustomDirectories.Contains(folder, StringComparer.OrdinalIgnoreCase))
            Session.Settings.CustomDirectories.Add(folder);
        _store.SaveSettings(Session.Settings);
        await ScanAsync(CancellationToken.None);
    }

    public void OpenDirectory(GameItem item)
    {
        var path = item.Record.ExecutablePath ?? item.Record.InstallPath;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            _prompt.Error("Folder", "That path is not available.");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
    }

    public void ViewLog(GameItem item)
    {
        var directory = item.Record.Installation?.InstallDirectory ?? Path.GetDirectoryName(item.Record.ExecutablePath ?? "");
        var text = _diagnostics.ReadModLogs(directory);
        var activation = _diagnostics.ReadActivation(directory, out var detail);
        _prompt.ShowText("Mod log", detail + Environment.NewLine + "Activation: " + activation + Environment.NewLine + Environment.NewLine + text);
    }

    public void OpenGraphicsSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true });

    public void ShowHagsHelp()
    {
        _prompt.Info("Hardware-accelerated GPU scheduling",
            "DLSS Frame Generation needs Hardware-accelerated GPU scheduling. DLSSG Manager does not change this setting.\n\n" +
            "1. Open Windows Settings, System, Display, Graphics.\n" +
            "2. Open the default graphics settings or advanced graphics page.\n" +
            "3. Turn on Hardware-accelerated GPU scheduling.\n" +
            "4. Restart Windows.\n\n" +
            (Session.SystemReport?.HagsDetail ?? ""));
        OpenGraphicsSettings();
    }

    public void OpenReleasePage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public void SaveSettings()
    {
        _store.SaveSettings(Session.Settings);
        AppLog.Enabled = Session.Settings.DiagnosticLogging;
        ThemeService.Apply(Session.Settings.Theme);
        Session.StatusText = "Settings saved.";
    }

    public string PreviewDiagnostics() => _diagnostics.Build(CurrentDiagnostics()).Text;

    public void ExportDiagnostics()
    {
        var folder = _prompt.PickFolder();
        if (folder == null)
            return;
        var path = _diagnostics.Export(_diagnostics.Build(CurrentDiagnostics()), folder);
        _prompt.Info("Diagnostics", "Wrote a redacted report to " + path);
    }

    private DiagnosticContext CurrentDiagnostics() => new()
    {
        ApplicationVersion = typeof(LibraryCoordinator).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        System = Session.SystemReport,
        Releases = Session.Releases,
        Games = Session.Games.Select(item => item.Record).ToList(),
        History = _store.RecentLog(null, 40)
    };

    public void ImportCompatibility()
    {
        var file = _prompt.PickFile("JSON|*.json");
        if (file == null)
            return;
        try
        {
            var result = _compatibility.Import(File.ReadAllText(file), replace: false);
            _prompt.Info("Compatibility database", result.Message);
        }
        catch (Exception ex)
        {
            _prompt.Error("Compatibility database", ex.Message);
        }
    }

    public IReadOnlyList<OperationLogEntry> History(string? gameId) => _store.RecentLog(gameId, 12);

    private async Task RefreshInstallStatesAsync()
    {
        var latest = Session.Releases?.Selected?.Tag;
        await Task.Run(() =>
        {
            foreach (var item in Session.Games)
            {
                item.Record.Installation = _install.Inspect(item.Record, item.Record.Installation, latest);
                item.Refresh();
            }
        });
        Session.Notify();
    }

    private async Task CacheArtworkAsync(IEnumerable<GameItem> items, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            if (item.Record.Launcher != LauncherKind.Steam || string.IsNullOrWhiteSpace(item.Record.StoreId))
                continue;
            var path = Path.Combine(_paths.Artwork, item.Record.StoreId + ".jpg");
            try
            {
                if (!File.Exists(path))
                {
                    var url = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{item.Record.StoreId}/header.jpg";
                    var bytes = await _artwork.GetByteArrayAsync(url, cancellationToken);
                    if (bytes.Length > 200)
                        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                }
                if (File.Exists(path))
                {
                    item.Record.ArtworkPath = path;
                    await Application.Current.Dispatcher.InvokeAsync(item.Refresh);
                }
            }
            catch
            {
                // Artwork is optional.
            }
        }
        _store.SaveGames(Session.Games.Select(game => game.Record));
    }

    private static List<string> BuildWarnings(GameItem item, SystemCompatibilityReport system, PreparedPackage package)
    {
        var warnings = new List<string>();
        if (system.RequiresAcknowledgement)
            warnings.AddRange(system.Reasons);
        if (item.Record.Compatibility?.Status == GameCompatibilityStatus.Unknown)
            warnings.Add("Compatibility is unknown. This is a manual install.");
        if (item.Record.ExecutableConfidence == ScanConfidence.Low)
            warnings.Add("The rendering executable was not identified with high confidence.");
        if (!package.Manifest.Confident)
            warnings.Add(package.Manifest.Uncertainty ?? "Confirm the package layout.");
        if (!package.Signature.IsTrusted)
            warnings.Add(package.Signature.Message);
        warnings.Add("One proxy DLL is installed. version.dll is first. Alternatives replace it; they are not added alongside it. dxgi.dll and d3d12.dll are a last resort and never both.");
        warnings.Add("Do not disable Microsoft Defender.");
        return warnings;
    }

    private static ConfigCapabilities CapabilitiesFor(GameItem item, string directory)
    {
        var runtime = Path.Combine(directory, "dlssg_sm86", "runtime", "nvngx_dlssg.dll");
        var version = File.Exists(runtime) ? FileVersionInfo.GetVersionInfo(runtime).FileVersion ?? "" : "";
        var tag = item.Record.Installation?.ReleaseTag ?? "";
        var max = version.StartsWith("310.1", StringComparison.OrdinalIgnoreCase) ? 3 : version.Length > 0 || tag.StartsWith("0.4", StringComparison.OrdinalIgnoreCase) ? 5 : 3;
        return new ConfigCapabilities
        {
            MaxGeneratedFrames = max,
            SupportsPresetC = version.StartsWith("310.9.16", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("0.4", StringComparison.OrdinalIgnoreCase),
            SupportsSuperResolution = File.Exists(Path.Combine(directory, "dlssg_sm86", "sr", "sr_kernels.dll")) || File.Exists(Path.Combine(directory, "dlssg_sm86", "sr", "nvngx_dlss.dll")),
            SupportsXess = File.Exists(Path.Combine(directory, "dlssg_sm86", "xefg", "xefg_kernels.dll")),
            GameSupports6x = item.Record.Compatibility?.Supports6x == true,
            SixXReason = item.Record.Compatibility?.SixXReason ?? "6× is hidden until the game plugin or an upstream report says it is supported."
        };
    }

    private void Show(OperationResult result)
    {
        var text = result.Message;
        if (result.Warnings.Count > 0)
            text += Environment.NewLine + string.Join(Environment.NewLine, result.Warnings);
        if (result.Success)
            _prompt.Info("DLSSG Manager", text);
        else
            _prompt.Error("DLSSG Manager", text);
        Session.StatusText = result.Message;
    }
}
