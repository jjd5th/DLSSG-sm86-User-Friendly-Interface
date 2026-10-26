namespace DLSSGManager.Core.Models;

public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
    Microsoft,
    Other
}

public enum SystemCompatibilityStatus
{
    Compatible,
    PotentiallyCompatible,
    ActionRequired,
    Unsupported
}

public enum GameCompatibilityStatus
{
    Confirmed,
    Likely,
    Unknown,
    Unsupported,
    Blocked
}

public enum ModInstallState
{
    NotInstalled,
    InstalledVerified,
    UpdateAvailable,
    ConfigurationModified,
    Conflicting,
    MissingFiles,
    RepairRequired,
    ManualIntervention
}

public enum LauncherKind
{
    Steam,
    Epic,
    Gog,
    Xbox,
    Ea,
    Ubisoft,
    BattleNet,
    Manual
}

public enum ScanConfidence
{
    Low,
    Medium,
    High
}

public enum RuntimeActivation
{
    NotChecked,
    NoLog,
    InstalledOnly,
    ActivationRecorded
}

public sealed class GpuDevice
{
    public required string Name { get; init; }
    public GpuVendor Vendor { get; init; }
    public string VendorName { get; init; } = "Unknown";
    public string Architecture { get; init; } = "Unknown";
    public string ComputeCapability { get; init; } = "Unknown";
    public string ShaderModel { get; init; } = "Unknown";
    public ulong DedicatedVideoMemory { get; init; }
    public uint DeviceId { get; init; }
    public bool IsDiscrete { get; init; }
    public bool IsSupportedRtx { get; init; }
    public bool IsNewerArchitecture { get; init; }
    public string? DriverVersion { get; init; }
    public long Luid { get; init; }
    public string MemoryText => DedicatedVideoMemory == 0
        ? "Unknown"
        : $"{DedicatedVideoMemory / 1024d / 1024d / 1024d:0.0} GB";
}

public sealed class SystemCompatibilityReport
{
    public required SystemCompatibilityStatus Status { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
    public required IReadOnlyList<GpuDevice> Gpus { get; init; }
    public GpuDevice? PrimaryGpu { get; init; }
    public string OperatingSystem { get; init; } = "";
    public string? NvidiaDriverVersion { get; init; }
    public bool? HagsEnabled { get; init; }
    public string HagsDetail { get; init; } = "";
    public IReadOnlyList<string> Features { get; init; } = [];
    public bool InstallAllowed { get; init; }
    public bool RequiresAcknowledgement { get; init; }
}

public sealed class HardwareSnapshot
{
    public required IReadOnlyList<GpuDevice> Gpus { get; init; }
    public required string OperatingSystem { get; init; }
    public required bool Is64Bit { get; init; }
    public required int BuildNumber { get; init; }
    public string? NvidiaDriverVersion { get; init; }
    public bool? HagsEnabled { get; init; }
    public string HagsDetail { get; init; } = "";
}

public sealed class RawGpuAdapter
{
    public required string Name { get; init; }
    public uint VendorId { get; init; }
    public uint DeviceId { get; init; }
    public ulong DedicatedVideoMemory { get; init; }
    public long Luid { get; init; }
    public bool IsSoftware { get; init; }
    public string? DriverVersion { get; init; }
}

public sealed class RawOperatingSystem
{
    public string ProductName { get; set; } = "";
    public string DisplayVersion { get; set; } = "";
    public int Build { get; set; }
    public bool Is64Bit { get; set; }
}

public sealed class HagsProbeResult
{
    public bool? Enabled { get; init; }
    public string Detail { get; init; } = "";
}

public sealed class GameRecord
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required LauncherKind Launcher { get; init; }
    public string? StoreId { get; init; }
    public required string InstallPath { get; set; }
    public string? ExecutablePath { get; set; }
    public ScanConfidence ExecutableConfidence { get; set; }
    public string ExecutableReason { get; set; } = "";
    public bool UserChoseExecutable { get; set; }
    public bool Excluded { get; set; }
    public string? ArtworkPath { get; set; }
    public string? Stamp { get; set; }
    public string? PreferredGpu { get; set; }
    public DateTimeOffset DiscoveredUtc { get; set; }
    public GameCompatibilityDecision? Compatibility { get; set; }
    public InstallationRecord? Installation { get; set; }
}

public sealed class GameCompatibilityDecision
{
    public GameCompatibilityStatus Status { get; init; }
    public ScanConfidence Confidence { get; init; }
    public string Summary { get; init; } = "";
    public string Explanation { get; init; } = "";
    public List<string> Evidence { get; init; } = [];
    public bool AutomaticInstallAllowed { get; init; }
    public bool ManualInstallAllowed { get; init; }
    public bool Supports6x { get; init; }
    public string SixXReason { get; init; } = "";
    public string GraphicsApi { get; init; } = "Unknown";
    public bool ProtectedDirectory { get; init; }
    public List<string> AntiCheat { get; init; } = [];
}

public sealed class InstallationRecord
{
    public required string GameId { get; init; }
    public required string ReleaseTag { get; set; }
    public required string ManifestId { get; set; }
    public required string InstallDirectory { get; set; }
    public required string ProxyFileName { get; set; }
    public ModInstallState State { get; set; }
    public DateTimeOffset InstalledUtc { get; set; }
    public DateTimeOffset? VerifiedUtc { get; set; }
    public bool ConfigurationModified { get; set; }
    public string? PreviousReleaseTag { get; set; }
    public string? RollbackDirectory { get; set; }
    public string? LastError { get; set; }
    public RuntimeActivation Activation { get; set; }
    public string ActivationDetail { get; set; } = "";
    public List<OwnedFileRecord> Files { get; set; } = [];
}

public sealed class OwnedFileRecord
{
    public required string RelativePath { get; init; }
    public required string Sha256 { get; set; }
    public long Size { get; set; }
    public bool OriginalExisted { get; set; }
    public string? OriginalSha256 { get; set; }
    public string? BackupPath { get; set; }
    public bool IsConfiguration { get; set; }
}

public sealed class OperationLogEntry
{
    public long Id { get; init; }
    public DateTimeOffset Utc { get; init; }
    public string? GameId { get; init; }
    public string Action { get; init; } = "";
    public string Result { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed class AppSettings
{
    public bool ScanOnStartup { get; set; } = true;
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool IncludePrerelease { get; set; }
    public bool DiscoverSteam { get; set; } = true;
    public bool DiscoverEpic { get; set; } = true;
    public bool DiscoverGog { get; set; } = true;
    public bool DiscoverXbox { get; set; } = true;
    public bool DiscoverEa { get; set; } = true;
    public bool DiscoverUbisoft { get; set; } = true;
    public bool DiscoverBattleNet { get; set; } = true;
    public List<string> CustomDirectories { get; set; } = [];
    public string? PinnedModVersion { get; set; }
    public string DefaultPreset { get; set; } = "Recommended";
    public string? BackupDirectory { get; set; }
    public string? CacheDirectory { get; set; }
    public int LogRetentionDays { get; set; } = 14;
    public string Theme { get; set; } = "Dark";
    public bool DiagnosticLogging { get; set; } = true;
    public string ModRepository { get; set; } = "sdli1995/dlssg_for_sm86";
    public string ManagerRepository { get; set; } = "jjd5th/DLSSG_Manager_UI";
    public DateTimeOffset? LastScanUtc { get; set; }
}

public sealed class ReleaseAsset
{
    public required string Name { get; init; }
    public required string DownloadUrl { get; init; }
    public long Size { get; init; }
    public string? Sha256 { get; init; }
}

public sealed class ModRelease
{
    public required string Tag { get; init; }
    public required string Name { get; init; }
    public required string Body { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public bool Prerelease { get; init; }
    public string HtmlUrl { get; init; } = "";
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];
    public ReleaseAsset? PreferredPackage { get; init; }
}

public sealed class ReleaseCheckResult
{
    public IReadOnlyList<ModRelease> Releases { get; init; } = [];
    public ModRelease? Selected { get; init; }
    public ModRelease? ManagerLatest { get; init; }
    public string ManagerMessage { get; init; } = "";
    public bool ManagerUpdateAvailable { get; init; }
    public bool FromCache { get; init; }
    public string? Warning { get; init; }
}

public sealed class ManifestFile
{
    public required string PackageRelativePath { get; init; }
    public required string DestinationRelativePath { get; init; }
    public bool Required { get; init; }
    public string Role { get; init; } = "payload";
}

public sealed class InstallationManifest
{
    public required string Id { get; init; }
    public required string ReleaseTag { get; init; }
    public required string Layout { get; init; }
    public bool Confident { get; init; }
    public string? Uncertainty { get; init; }
    public string DefaultProxy { get; init; } = "version.dll";
    public IReadOnlyList<string> ProxyOptions { get; init; } = ProxyNames.Order;
    public string ConfigFileName { get; init; } = "dlssg_sm86.ini";
    public string? PayloadDirectory { get; init; }
    public int MaxGeneratedFrames { get; init; } = 3;
    public string RuntimeModel { get; init; } = "";
    public bool SupportsPresetC { get; init; }
    public bool SupportsSuperResolution { get; init; }
    public bool SupportsXessEngine { get; init; }
    public IReadOnlyList<string> SupportedArchitectures { get; init; } = [];
    public IReadOnlyList<string> SupportedApis { get; init; } = ["D3D12"];
    public IReadOnlyList<ManifestFile> Files { get; init; } = [];
    public string PackageRoot { get; init; } = "";
    public string UninstallNote { get; init; } = "Remove the proxy DLL, dlssg_sm86.ini, and the dlssg_sm86 directory, then restore backed-up files.";
}

public static class ProxyNames
{
    public static readonly string[] Order =
    [
        "version.dll",
        "winmm.dll",
        "dbghelp.dll",
        "dinput8.dll",
        "dxgi.dll",
        "d3d12.dll"
    ];

    public static bool IsProxy(string fileName) =>
        Order.Contains(fileName, StringComparer.OrdinalIgnoreCase);
}

public sealed class SignatureInfo
{
    public bool IsSigned { get; init; }
    public bool IsTrusted { get; init; }
    public string Subject { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed class PreparedPackage
{
    public required ModRelease Release { get; init; }
    public required string ArchivePath { get; init; }
    public required string ExtractedPath { get; init; }
    public required InstallationManifest Manifest { get; init; }
    public required SignatureInfo Signature { get; init; }
    public string Sha256 { get; init; } = "";
}

public sealed class PlannedFile
{
    public string? SourcePath { get; init; }
    public string? Content { get; init; }
    public required string DestinationRelativePath { get; init; }
    public bool IsConfiguration { get; init; }
    public bool DeleteOnly { get; init; }
}

public sealed class DllConflict
{
    public required string RelativePath { get; init; }
    public required string Reason { get; init; }
}

public sealed class OperationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string? ErrorCode { get; init; }
    public List<string> Log { get; init; } = [];
    public List<DllConflict> Conflicts { get; init; } = [];
    public List<string> Warnings { get; init; } = [];

    public static OperationResult Ok(string message, IEnumerable<string>? log = null, IEnumerable<string>? warnings = null) =>
        new()
        {
            Success = true,
            Message = message,
            Log = log?.ToList() ?? [],
            Warnings = warnings?.ToList() ?? []
        };

    public static OperationResult Fail(string message, string code, IEnumerable<string>? log = null, IEnumerable<DllConflict>? conflicts = null) =>
        new()
        {
            Success = false,
            Message = message,
            ErrorCode = code,
            Log = log?.ToList() ?? [],
            Conflicts = conflicts?.ToList() ?? []
        };
}

public sealed class InstallRequest
{
    public required GameRecord Game { get; init; }
    public required PreparedPackage Package { get; init; }
    public required SystemCompatibilityReport System { get; init; }
    public bool AcknowledgeSystemWarning { get; init; }
    public bool AllowUnknownGame { get; init; }
    public bool AllowLowConfidenceExecutable { get; init; }
    public bool AllowUncertainManifest { get; init; }
    public bool OverwriteConflicts { get; init; }
    public bool RemoveUnownedProxies { get; init; }
    public string? ProxyFileName { get; init; }
    public string? ConfigurationText { get; init; }
    public bool IsUpdate { get; set; }
}

public sealed class ScanRequest
{
    public bool Steam { get; init; } = true;
    public bool Epic { get; init; } = true;
    public bool Gog { get; init; } = true;
    public bool Xbox { get; init; } = true;
    public bool Ea { get; init; } = true;
    public bool Ubisoft { get; init; } = true;
    public bool BattleNet { get; init; } = true;
    public IReadOnlyList<string> ManualDirectories { get; init; } = [];
    public IProgress<ScanProgress>? Progress { get; init; }
}

public sealed class ScanProgress
{
    public string Message { get; init; } = "";
    public int CompletedSteps { get; init; }
    public int TotalSteps { get; init; }
}

public sealed class DiscoveryResult
{
    public List<GameRecord> Games { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class ExecutableChoice
{
    public required string Path { get; init; }
    public int Score { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class ExecutableSelection
{
    public string? Path { get; init; }
    public ScanConfidence Confidence { get; init; }
    public string Reason { get; init; } = "";
    public IReadOnlyList<ExecutableChoice> Options { get; init; } = [];
}

public sealed class CompatibilityDatabase
{
    public int Schema { get; set; } = 1;
    public string Source { get; set; } = "";
    public string Updated { get; set; } = "";
    public List<CompatibilityEntry> Entries { get; set; } = [];
}

public sealed class CompatibilityEntry
{
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public string? Launcher { get; set; }
    public string? StoreId { get; set; }
    public string? ExecutablePattern { get; set; }
    public string GraphicsApi { get; set; } = "D3D12";
    public string Status { get; set; } = "unknown";
    public List<string> VerifiedModVersions { get; set; } = [];
    public List<string> Issues { get; set; } = [];
    public bool Supports6x { get; set; }
    public string Notes { get; set; } = "";
    public string Source { get; set; } = "";
    public string Verified { get; set; } = "";
    public string VerificationKind { get; set; } = "community";
}

public sealed class DiagnosticReport
{
    public string Text { get; init; } = "";
    public DateTimeOffset CreatedUtc { get; init; }
}
