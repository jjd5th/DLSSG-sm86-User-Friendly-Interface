using DLSSGManager.Core.Configuration;
using DLSSGManager.Core.Models;

namespace DLSSGManager.Core.Abstractions;

public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    byte[] ReadAllBytes(string path);
    string ReadAllText(string path);
    void WriteAllBytes(string path, byte[] content);
    void WriteAllText(string path, string content);
    void Copy(string source, string destination, bool overwrite);
    void DeleteFile(string path);
    void CreateDirectory(string path);
    void DeleteDirectory(string path);
    IEnumerable<string> EnumerateFiles(string path, string searchPattern);
    IEnumerable<string> EnumerateDirectories(string path);
    long Length(string path);
    DateTime LastWriteUtc(string path);
    Stream OpenRead(string path);
    void Move(string source, string destination, bool overwrite);
    long FreeSpace(string path);
    bool TryProbeWritable(string directory, out string? error);
}

public interface IHardwareProbe
{
    IReadOnlyList<RawGpuAdapter> QueryAdapters();
    RawOperatingSystem QueryOperatingSystem();
    HagsProbeResult QueryHags();
    string? QueryNvidiaDriverVersion();
}

public interface IGpuDetectionService
{
    SystemCompatibilityReport Detect();
}

public interface ISystemCompatibilityService
{
    SystemCompatibilityReport Evaluate(HardwareSnapshot snapshot);
}

public interface IGameDiscoveryService
{
    Task<DiscoveryResult> ScanAsync(ScanRequest request, IReadOnlyList<GameRecord> existing, CancellationToken cancellationToken);
}

public interface IGameCompatibilityService
{
    GameCompatibilityDecision Evaluate(GameRecord game);
    CompatibilityDatabase Database { get; }
    ImportResult Import(string json, bool replace);
}

public sealed class ImportResult
{
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Downgraded { get; init; }
    public string Message { get; init; } = "";
}

public interface IReleaseClient
{
    Task<string> GetStringAsync(string url, CancellationToken cancellationToken);
    Task DownloadAsync(ReleaseAsset asset, string destination, IProgress<double>? progress, CancellationToken cancellationToken);
}

public interface IReleaseService
{
    Task<ReleaseCheckResult> CheckAsync(bool includePrerelease, string? pinnedTag, CancellationToken cancellationToken);
    Task<PreparedPackage> PrepareAsync(ModRelease release, IProgress<double>? progress, CancellationToken cancellationToken);
    Task<string> DownloadManagerPackageAsync(ModRelease release, string directory, IProgress<double>? progress, CancellationToken cancellationToken);
}

public interface IInstallationService
{
    OperationResult Install(InstallRequest request, CancellationToken cancellationToken);
    OperationResult Uninstall(GameRecord game, bool restoreOriginalsOnly, CancellationToken cancellationToken);
    OperationResult Repair(GameRecord game, PreparedPackage package, CancellationToken cancellationToken);
    OperationResult Rollback(GameRecord game, CancellationToken cancellationToken);
    InstallationRecord Inspect(GameRecord game, InstallationRecord? known, string? latestTag);
    IReadOnlyList<DllConflict> DetectConflicts(string installDirectory, IEnumerable<PlannedFile> plan, IEnumerable<OwnedFileRecord> owned);
    int RecoverInterrupted();
    int UnresolvedRecoveries { get; }
}

public interface IBackupService
{
    string BackupFile(string sourcePath, string backupDirectory);
    void RestoreFile(string backupPath, string destinationPath);
}

public interface IConfigurationService
{
    GameConfig Load(string path);
    GameConfig Parse(string text);
    IReadOnlyList<string> Validate(GameConfig config, ConfigCapabilities capabilities, bool strictMultipliers);
    void ApplyPreset(GameConfig config, string preset, ConfigCapabilities capabilities);
    IReadOnlyList<string> Migrate(IniDocument from, IniDocument onto);
    IReadOnlyList<FrameMultiplierOption> Multipliers(ConfigCapabilities capabilities);
    void Save(string path, GameConfig config);
}

public interface IProcessInspector
{
    bool IsExecutableRunning(string executablePath);
}

public interface ILibraryStore
{
    IReadOnlyList<GameRecord> LoadGames();
    void SaveGames(IEnumerable<GameRecord> games);
    void SaveInstallation(InstallationRecord record);
    void DeleteInstallation(string gameId);
    InstallationRecord? LoadInstallation(string gameId);
    void AppendLog(string? gameId, string action, string result, string detail);
    IReadOnlyList<OperationLogEntry> RecentLog(string? gameId, int take);
    AppSettings LoadSettings();
    void SaveSettings(AppSettings settings);
}

public interface IDiagnosticsService
{
    DiagnosticReport Build(DiagnosticContext context);
    string Export(DiagnosticReport report, string directory);
    string ReadModLogs(string? installDirectory);
    RuntimeActivation ReadActivation(string? installDirectory, out string detail);
}

public sealed class DiagnosticContext
{
    public string ApplicationVersion { get; init; } = "";
    public SystemCompatibilityReport? System { get; init; }
    public ReleaseCheckResult? Releases { get; init; }
    public IReadOnlyList<GameRecord> Games { get; init; } = [];
    public IReadOnlyList<OperationLogEntry> History { get; init; } = [];
}

public sealed class GameConfig
{
    public GameConfig(IniDocument document) => Document = document;
    public IniDocument Document { get; }

    public bool Enabled
    {
        get => Document.Get("General", "Enabled") != "0";
        set => Document.Set("General", "Enabled", value ? "1" : "0");
    }

    public int Optimized
    {
        get => int.TryParse(Document.Get("DLSSG", "Optimized") ?? Document.Get("FrameGeneration", "Optimized"), out var v) ? v : 2;
        set => Document.Set("DLSSG", "Optimized", value.ToString());
    }

    public int MaxGeneratedFrames
    {
        get => int.TryParse(Document.Get("FrameGeneration", "MaxGeneratedFrames"), out var v) ? v : 3;
        set => Document.Set("FrameGeneration", "MaxGeneratedFrames", value.ToString());
    }

    public string Engine
    {
        get => Document.Get("FrameGeneration", "Engine") ?? "DLSS";
        set => Document.Set("FrameGeneration", "Engine", value);
    }

    public string Preset
    {
        get => Document.Get("DLSSG", "Preset") ?? "Auto";
        set => Document.Set("DLSSG", "Preset", value);
    }

    public string SuperResolutionMode
    {
        get => Document.Get("SuperResolution", "Mode") ?? "Off";
        set => Document.Set("SuperResolution", "Mode", value);
    }

    public int LoggingLevel
    {
        get => int.TryParse(Document.Get("Logging", "Level"), out var v) ? v : 1;
        set => Document.Set("Logging", "Level", value.ToString());
    }

    public bool FrameTiming
    {
        get => Document.Get("Logging", "FrameTiming") == "1";
        set
        {
            if (value)
                Document.Set("Logging", "FrameTiming", "1");
            else
                Document.Remove("Logging", "FrameTiming");
        }
    }

    public string FlowResolution
    {
        get => Document.Get("XeSSFG", "FlowResolution") ?? "Middle";
        set => Document.Set("XeSSFG", "FlowResolution", value);
    }

    public bool UseHudless
    {
        get => Document.Get("XeSSFG", "UseHudless") != "0";
        set => Document.Set("XeSSFG", "UseHudless", value ? "1" : "0");
    }

    public string? CatmullRomWarp
    {
        get => Document.Get("FrameGeneration", "CatmullRomWarp");
        set => SetOptional("FrameGeneration", "CatmullRomWarp", value);
    }

    public string? SpoofArchToGame
    {
        get => Document.Get("Compatibility", "SpoofArchToGame");
        set => SetOptional("Compatibility", "SpoofArchToGame", value);
    }

    public string? AccelerateNewerGpus
    {
        get => Document.Get("Compatibility", "AccelerateNewerGpus");
        set => SetOptional("Compatibility", "AccelerateNewerGpus", value);
    }

    public string? SuperResolutionTier
    {
        get => Document.Get("SuperResolution", "Tier");
        set => SetOptional("SuperResolution", "Tier", value);
    }

    public string Text
    {
        get => Document.ToString();
        set => Document.ReplaceWith(IniDocument.Parse(value));
    }

    private void SetOptional(string section, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            Document.Remove(section, key);
        else
            Document.Set(section, key, value);
    }
}

public sealed class ConfigCapabilities
{
    public int MaxGeneratedFrames { get; init; } = 3;
    public bool SupportsPresetC { get; init; }
    public bool SupportsSuperResolution { get; init; }
    public bool SupportsXess { get; init; }
    public bool GameSupports6x { get; init; }
    public string SixXReason { get; init; } = "6× is available only when the installed runtime allows five generated frames and the game's frame-generation plugin is new enough, or an upstream report says this game runs 6×.";
}

public sealed class FrameMultiplierOption
{
    public FrameMultiplierOption(int value, string label, string description, bool enabled, string? disabledReason)
    {
        Value = value;
        Label = label;
        Description = description;
        Enabled = enabled;
        DisabledReason = disabledReason;
    }

    public int Value { get; }
    public string Label { get; }
    public string Description { get; }
    public bool Enabled { get; }
    public string? DisabledReason { get; }
}
