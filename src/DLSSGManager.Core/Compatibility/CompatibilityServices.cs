using System.Text.Json;
using DLSSGManager.Core.Abstractions;
using DLSSGManager.Core.Discovery;
using DLSSGManager.Core.Infrastructure;
using DLSSGManager.Core.Models;

namespace DLSSGManager.Core.Compatibility;

public static class CompatibilityClassifier
{
    public static GameCompatibilityDecision Classify(GameSignals signals)
    {
        var evidence = new List<string>(signals.Notes);
        if (signals.DatabaseEntry != null)
            evidence.Add($"Compatibility database: {signals.DatabaseEntry.Name} is {signals.DatabaseEntry.Status} ({signals.DatabaseEntry.VerificationKind}, {signals.DatabaseEntry.Verified}). {signals.DatabaseEntry.Notes}");

        if (signals.ProtectedDirectory)
        {
            return Decide(GameCompatibilityStatus.Blocked, ScanConfidence.High,
                "This install is in a protected package directory.",
                "WindowsApps packages are not modified. DLSSG Manager does not bypass package protection.",
                evidence, automatic: false, manual: false, signals);
        }

        if (signals.AntiCheat.Count > 0)
        {
            evidence.Add("Anti-cheat files: " + string.Join(", ", signals.AntiCheat));
            return Decide(GameCompatibilityStatus.Blocked, ScanConfidence.High,
                "Anti-cheat is present, so the mod will not be installed.",
                "Proxy DLLs are not installed into games with Easy Anti-Cheat, BattlEye, Vanguard, GameGuard, or similar local anti-cheat. Nothing is injected and anti-cheat is not disabled.",
                evidence, automatic: false, manual: false, signals);
        }

        var entry = signals.DatabaseEntry;
        if (entry != null && entry.Status.Equals("unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return Decide(GameCompatibilityStatus.Unsupported, ScanConfidence.High,
                "The compatibility database marks this game unsupported.",
                entry.Notes,
                evidence, false, false, signals);
        }

        if (entry != null && entry.Status.Equals("confirmed", StringComparison.OrdinalIgnoreCase) && IsTrusted(entry))
        {
            return Decide(GameCompatibilityStatus.Confirmed, ScanConfidence.High,
                "Upstream testing reports this game working.",
                "Confirmed means a trusted upstream or maintainer report, not merely that a DLL is present. Local files are still listed as supporting evidence.",
                evidence, signals.ExecutableConfidence != ScanConfidence.Low, true, signals);
        }

        var fg = signals.HasFrameGenerationDll || signals.HasStreamlineFrameGeneration;
        var dx12 = signals.HasD3D12 || signals.HasStreamline;
        if (fg && dx12)
        {
            var trustedLikely = entry != null && entry.Status.Equals("likely", StringComparison.OrdinalIgnoreCase);
            return Decide(GameCompatibilityStatus.Likely, trustedLikely ? ScanConfidence.Medium : ScanConfidence.Medium,
                "DLSS Frame Generation files were found, but this install is not in the verified set.",
                "nvngx_dlssg.dll or a Streamline frame-generation plugin is present along with a DirectX 12 signal. That is not the same as a verified report.",
                evidence, signals.ExecutableConfidence != ScanConfidence.Low, true, signals);
        }

        if (signals.HasSuperResolutionDll && !fg)
            evidence.Add("NVIDIA DLSS Super Resolution was found. Super Resolution alone does not mean the game supports DLSS Frame Generation.");

        if (signals.VulkanOnly)
        {
            return Decide(GameCompatibilityStatus.Unsupported, ScanConfidence.Medium,
                "The selected executable looks Vulkan-only.",
                "Upstream 0.4.0 does not support frame generation on Vulkan. Super Resolution notes for Vulkan are not treated as frame-generation support.",
                evidence, false, false, signals);
        }

        if (signals.DirectX11Only)
        {
            return Decide(GameCompatibilityStatus.Unsupported, ScanConfidence.Medium,
                "The selected executable looks DirectX 11-only.",
                "DLSSG for SM86 requires a D3D12 game.",
                evidence, false, false, signals);
        }

        return Decide(GameCompatibilityStatus.Unknown, ScanConfidence.Low,
            "There is not enough evidence to classify this game.",
            "A DirectX 12 game, or a game with DLSS Super Resolution, is not assumed to support DLSS Frame Generation. Installation stays manual.",
            evidence, false, true, signals);
    }

    private static bool IsTrusted(CompatibilityEntry entry) =>
        entry.VerificationKind.Equals("upstream", StringComparison.OrdinalIgnoreCase) ||
        entry.VerificationKind.Equals("maintainer", StringComparison.OrdinalIgnoreCase);

    private static GameCompatibilityDecision Decide(
        GameCompatibilityStatus status,
        ScanConfidence confidence,
        string summary,
        string explanation,
        List<string> evidence,
        bool automatic,
        bool manual,
        GameSignals signals)
    {
        var six = signals.DatabaseEntry is { Supports6x: true } && IsTrusted(signals.DatabaseEntry);
        var plugin = signals.StreamlinePluginVersion;
        if (!six && plugin != null && plugin >= new Version(2, 11))
            six = true;
        var reason = six
            ? "6× is listed because an upstream report or a Streamline frame-generation plugin of 2.11 or newer was found. The game still has to expose the multiplier."
            : "6× stays unavailable until the game's Streamline frame-generation plugin is 2.11 or newer, or an upstream report says this game runs 6×. Older 4× plugins cannot be raised by the mod.";
        return new GameCompatibilityDecision
        {
            Status = status,
            Confidence = confidence,
            Summary = summary,
            Explanation = explanation,
            Evidence = evidence,
            AutomaticInstallAllowed = automatic && status is GameCompatibilityStatus.Confirmed or GameCompatibilityStatus.Likely,
            ManualInstallAllowed = manual && status is not GameCompatibilityStatus.Blocked and not GameCompatibilityStatus.Unsupported,
            Supports6x = six && status is not GameCompatibilityStatus.Blocked and not GameCompatibilityStatus.Unsupported,
            SixXReason = reason,
            GraphicsApi = signals.GraphicsApi,
            ProtectedDirectory = signals.ProtectedDirectory,
            AntiCheat = signals.AntiCheat.ToList()
        };
    }
}

public sealed class GameSignals
{
    public bool HasD3D12 { get; init; }
    public bool HasStreamline { get; init; }
    public bool HasFrameGenerationDll { get; init; }
    public bool HasStreamlineFrameGeneration { get; init; }
    public bool HasSuperResolutionDll { get; init; }
    public bool VulkanOnly { get; init; }
    public bool DirectX11Only { get; init; }
    public bool ProtectedDirectory { get; init; }
    public List<string> AntiCheat { get; init; } = [];
    public List<string> Notes { get; init; } = [];
    public string GraphicsApi { get; init; } = "Unknown";
    public Version? StreamlinePluginVersion { get; init; }
    public ScanConfidence ExecutableConfidence { get; init; }
    public CompatibilityEntry? DatabaseEntry { get; init; }
}

public sealed class GameCompatibilityService : IGameCompatibilityService
{
    private readonly object _gate = new();
    private readonly string? _overridePath;

    public GameCompatibilityService(string? overridePath = null)
    {
        _overridePath = overridePath;
        Database = LoadBundled();
        if (overridePath != null && File.Exists(overridePath))
        {
            try
            {
                Import(File.ReadAllText(overridePath), replace: true);
            }
            catch
            {
                // Keep the bundled database if the override is unreadable.
            }
        }
    }

    public CompatibilityDatabase Database { get; private set; }

    public GameCompatibilityDecision Evaluate(GameRecord game)
    {
        var signals = Collect(game);
        return CompatibilityClassifier.Classify(signals);
    }

    public GameSignals Collect(GameRecord game) => Collect(game, Match(game));

    public ImportResult Import(string json, bool replace)
    {
        var incoming = JsonSerializer.Deserialize<CompatibilityDatabase>(json, JsonOptions()) ?? throw new InvalidOperationException("The compatibility file is empty.");
        if (incoming.Schema != 1)
            throw new InvalidOperationException("Only compatibility database schema 1 is supported.");
        var downgraded = 0;
        foreach (var entry in incoming.Entries)
        {
            if (entry.Status.Equals("confirmed", StringComparison.OrdinalIgnoreCase) &&
                entry.VerificationKind is not ("upstream" or "maintainer"))
            {
                entry.Status = "likely";
                entry.Notes = "Imported as likely. Unverified community reports are not treated as confirmed. " + entry.Notes;
                downgraded++;
            }
        }
        lock (_gate)
        {
            if (replace)
                Database = incoming;
            else
            {
                foreach (var entry in incoming.Entries)
                {
                    var index = Database.Entries.FindIndex(existing => Same(existing, entry));
                    if (index >= 0)
                        Database.Entries[index] = entry;
                    else
                        Database.Entries.Add(entry);
                }
            }
            if (!string.IsNullOrWhiteSpace(_overridePath))
            {
                var directory = Path.GetDirectoryName(_overridePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(_overridePath, JsonSerializer.Serialize(Database, JsonOptions()));
            }
        }
        return new ImportResult
        {
            Added = incoming.Entries.Count,
            Updated = replace ? 0 : incoming.Entries.Count,
            Downgraded = downgraded,
            Message = downgraded == 0
                ? $"Imported {incoming.Entries.Count} compatibility entries."
                : $"Imported {incoming.Entries.Count} entries. {downgraded} confirmed community reports were stored as likely."
        };
    }

    private static GameSignals Collect(GameRecord game, CompatibilityEntry? entry)
    {
        var notes = new List<string>();
        var antiCheat = new List<string>();
        var protectedPath = PathSafety.IsProtectedGamePath(game.InstallPath);
        if (protectedPath)
            notes.Add("Install path is inside WindowsApps.");
        var hasFg = false;
        var hasSr = false;
        var hasSl = false;
        var hasSlFg = false;
        var hasD3d12 = false;
        var hasD3d11 = false;
        var hasVulkan = false;
        Version? plugin = null;
        if (Directory.Exists(game.InstallPath))
        {
            foreach (var file in ExecutableSelector.EnumerateSafe(game.InstallPath, "*.*", 5).Take(6000))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("nvngx_dlssg.dll", StringComparison.OrdinalIgnoreCase))
                    hasFg = true;
                else if (name.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase))
                    hasSr = true;
                else if (name.Equals("sl.dlss_g.dll", StringComparison.OrdinalIgnoreCase))
                {
                    hasSlFg = true;
                    hasSl = true;
                    plugin ??= FileVersion(file);
                }
                else if (name.Equals("sl.interposer.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("sl.common.dll", StringComparison.OrdinalIgnoreCase))
                    hasSl = true;
                else if (name.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("D3D12Core.dll", StringComparison.OrdinalIgnoreCase))
                    hasD3d12 = true;
                else if (name.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) || name.Equals("start_protected_game.exe", StringComparison.OrdinalIgnoreCase))
                    antiCheat.Add(name);
                else if (name.Contains("BattlEye", StringComparison.OrdinalIgnoreCase) || name.StartsWith("BEService", StringComparison.OrdinalIgnoreCase))
                    antiCheat.Add(name);
                else if (name.Equals("vgc.exe", StringComparison.OrdinalIgnoreCase) || name.Contains("GameGuard", StringComparison.OrdinalIgnoreCase))
                    antiCheat.Add(name);
                var directory = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
                if (directory.Equals("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) || directory.Equals("BattlEye", StringComparison.OrdinalIgnoreCase))
                    antiCheat.Add(directory);
            }
        }
        else
            notes.Add("The install directory could not be read.");

        if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath))
        {
            var imports = PeImports.ImportDlls(game.ExecutablePath);
            hasD3d12 |= imports.Any(name => name.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase));
            hasD3d11 = imports.Any(name => name.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase));
            hasVulkan = imports.Any(name => name.Equals("vulkan-1.dll", StringComparison.OrdinalIgnoreCase));
            notes.Add("Executable: " + game.ExecutablePath);
            notes.Add("Executable confidence: " + game.ExecutableConfidence + ". " + game.ExecutableReason);
            if (imports.Count > 0)
                notes.Add("Imports: " + string.Join(", ", imports.Take(8)));
        }
        else
            notes.Add("No rendering executable has been selected.");

        if (hasFg)
            notes.Add("Found nvngx_dlssg.dll. DLL presence is one signal, not proof the mod will work.");
        if (hasSlFg)
            notes.Add("Found sl.dlss_g.dll" + (plugin == null ? "." : $" version {plugin}."));
        if (hasSl)
            notes.Add("NVIDIA Streamline files are present.");
        if (hasD3d12)
            notes.Add("DirectX 12 evidence was found.");
        if (!string.IsNullOrWhiteSpace(game.PreferredGpu))
            notes.Add("Per-game GPU preference: " + game.PreferredGpu);

        var api = hasD3d12 ? "D3D12" : hasVulkan && !hasD3d11 ? "Vulkan" : hasD3d11 ? "D3D11" : "Unknown";
        return new GameSignals
        {
            HasD3D12 = hasD3d12,
            HasStreamline = hasSl,
            HasFrameGenerationDll = hasFg,
            HasStreamlineFrameGeneration = hasSlFg,
            HasSuperResolutionDll = hasSr,
            VulkanOnly = hasVulkan && !hasD3d12 && !hasSlFg,
            DirectX11Only = hasD3d11 && !hasD3d12 && !hasSlFg && !hasFg,
            ProtectedDirectory = protectedPath,
            AntiCheat = antiCheat.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Notes = notes,
            GraphicsApi = api,
            StreamlinePluginVersion = plugin,
            ExecutableConfidence = game.ExecutableConfidence,
            DatabaseEntry = entry
        };
    }

    private CompatibilityEntry? Match(GameRecord game)
    {
        lock (_gate)
        {
            CompatibilityEntry? byId = null;
            if (!string.IsNullOrWhiteSpace(game.StoreId))
            {
                byId = Database.Entries.FirstOrDefault(entry =>
                    string.Equals(entry.StoreId, game.StoreId, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(entry.Launcher) || entry.Launcher.Equals(game.Launcher.ToString(), StringComparison.OrdinalIgnoreCase)));
            }
            if (byId != null)
                return byId;
            var norm = Normalize(game.Name);
            return Database.Entries.FirstOrDefault(entry =>
                Normalize(entry.Name) == norm || entry.Aliases.Any(alias => Normalize(alias) == norm));
        }
    }

    private static string Normalize(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static bool Same(CompatibilityEntry left, CompatibilityEntry right) =>
        (!string.IsNullOrWhiteSpace(left.StoreId) && left.StoreId == right.StoreId) || Normalize(left.Name) == Normalize(right.Name);

    private static Version? FileVersion(string path)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return Version.TryParse(info.FileVersion, out var version) ? version : null;
        }
        catch
        {
            return null;
        }
    }

    private static CompatibilityDatabase LoadBundled()
    {
        var assembly = typeof(GameCompatibilityService).Assembly;
        var name = assembly.GetManifestResourceNames().First(resource => resource.EndsWith("compatibility-database.json", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("The bundled compatibility database is missing.");
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<CompatibilityDatabase>(reader.ReadToEnd(), JsonOptions()) ?? new CompatibilityDatabase();
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
