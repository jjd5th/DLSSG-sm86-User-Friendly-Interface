using System.Text.Json;
using System.Text.RegularExpressions;
using DLSSGManager.Core.Abstractions;
using DLSSGManager.Core.Infrastructure;
using DLSSGManager.Core.Models;
using Microsoft.Win32;

namespace DLSSGManager.Core.Discovery;

public static class GameIds
{
    public static string Create(LauncherKind launcher, string? storeId, string installPath)
    {
        var key = !string.IsNullOrWhiteSpace(storeId)
            ? $"{launcher}:{storeId}"
            : $"{launcher}:{Path.GetFullPath(installPath).TrimEnd('\\').ToLowerInvariant()}";
        return Hashing.Sha256Text(key)[..16];
    }
}

public static class SteamLibraryParser
{
    private static readonly HashSet<string> SkipAppIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "228980", "250820", "1391110", "1628350", "1493710", "2180100"
    };

    public static IReadOnlyList<string> ReadLibraryPaths(string vdfText, string? steamInstall)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(steamInstall))
            paths.Add(steamInstall);
        var root = VdfReader.Parse(vdfText);
        var folders = Find(root, "libraryfolders") ?? Find(root, "LibraryFolders");
        if (folders == null)
            return paths;
        foreach (var entry in folders)
        {
            switch (entry.Value)
            {
                case string path when LooksLikePath(path):
                    paths.Add(path);
                    break;
                case Dictionary<string, object> child:
                    if (child.TryGetValue("path", out var pathValue) && pathValue is string nested && LooksLikePath(nested))
                        paths.Add(nested);
                    break;
            }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static SteamAppManifest? ReadManifest(string acfText)
    {
        var root = VdfReader.Parse(acfText);
        var state = Find(root, "AppState");
        if (state == null)
            return null;
        var appId = Value(state, "appid");
        var name = Value(state, "name");
        var installDir = Value(state, "installdir");
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir))
            return null;
        int.TryParse(Value(state, "StateFlags"), out var flags);
        return new SteamAppManifest(appId, name, installDir, flags);
    }

    public static bool ShouldSkip(SteamAppManifest manifest) =>
        SkipAppIds.Contains(manifest.AppId) ||
        manifest.Name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) ||
        manifest.Name.Contains("Steamworks Common", StringComparison.OrdinalIgnoreCase) ||
        manifest.Name.Contains("Proton", StringComparison.OrdinalIgnoreCase) ||
        manifest.Name.Contains("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled(SteamAppManifest manifest) => manifest.StateFlags == 0 || (manifest.StateFlags & 4) != 0;

    private static Dictionary<string, object>? Find(Dictionary<string, object> root, string key) =>
        root.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;

    private static string? Value(Dictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var value) ? value as string : null;

    private static bool LooksLikePath(string path) => path.Contains('\\') || path.Contains('/') || path.Contains(':');
}

public sealed record SteamAppManifest(string AppId, string Name, string InstallDir, int StateFlags);

public static class ExecutableSelector
{
    private static readonly string[] Negative =
    [
        "crashhandler", "crashpad", "unitycrash", "uninstall", "setup", "installer", "launcher",
        "easyanticheat", "start_protected", "beservice", "battleye", "server", "dedicated",
        "config", "benchmark", "report", "helper", "overlay", "cef", "webhelper", "modmanager",
        "steamerror", "redist", "dotnet", "vc_redist", "crashreporter"
    ];

    public static ExecutableSelection Select(string root)
    {
        if (!Directory.Exists(root))
            return new ExecutableSelection { Confidence = ScanConfidence.Low, Reason = "The install directory does not exist." };

        var scored = new List<ExecutableChoice>();
        foreach (var exe in EnumerateSafe(root, "*.exe", 5).Take(400))
        {
            var name = Path.GetFileName(exe);
            var lower = exe.Replace('/', '\\').ToLowerInvariant();
            var score = 10;
            var notes = new List<string>();
            if (lower.Contains("-win64-shipping.exe"))
            {
                score += 120;
                notes.Add("Unreal shipping executable");
            }
            if (lower.Contains(@"\binaries\win64\"))
            {
                score += 70;
                notes.Add("Binaries\\Win64");
            }
            else if (lower.Contains(@"\bin\x64\") || lower.Contains(@"\win64\"))
            {
                score += 35;
                notes.Add("64-bit binary directory");
            }
            var folder = Path.GetFileName(root.TrimEnd('\\'));
            if (!string.IsNullOrWhiteSpace(folder) && name.Contains(folder.Split(' ')[0], StringComparison.OrdinalIgnoreCase) && folder.Length > 2)
            {
                score += 30;
                notes.Add("name matches the install folder");
            }
            try
            {
                if (new FileInfo(exe).Length > 30_000_000)
                    score += 20;
                else if (new FileInfo(exe).Length > 5_000_000)
                    score += 10;
            }
            catch
            {
                score -= 20;
            }
            var fileLower = name.ToLowerInvariant();
            if (Negative.Any(token => fileLower.Contains(token) || lower.Contains($@"\{token}")))
            {
                score -= 160;
                notes.Add("looks like a launcher, tool, server, or anti-cheat executable");
            }
            var directory = Path.GetDirectoryName(exe) ?? root;
            if (File.Exists(Path.Combine(directory, "nvngx_dlssg.dll")) || File.Exists(Path.Combine(directory, "sl.dlss_g.dll")))
            {
                score += 50;
                notes.Add("frame-generation DLL beside the executable");
            }
            scored.Add(new ExecutableChoice { Path = exe, Score = score, Reason = string.Join(", ", notes) });
        }

        var ordered = scored.OrderByDescending(choice => choice.Score).Take(8).ToList();
        foreach (var choice in ordered.Take(4))
        {
            var imports = PeImports.ImportDlls(choice.Path);
            var has12 = imports.Any(name => name.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase));
            var has11 = imports.Any(name => name.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase));
            var adjusted = choice.Score + (has12 ? 25 : 0) - (has11 && !has12 ? 15 : 0);
            var reason = choice.Reason;
            if (has12)
                reason = string.IsNullOrEmpty(reason) ? "imports d3d12.dll" : reason + ", imports d3d12.dll";
            ordered[ordered.IndexOf(choice)] = new ExecutableChoice { Path = choice.Path, Score = adjusted, Reason = reason };
        }
        ordered = ordered.OrderByDescending(choice => choice.Score).ToList();
        if (ordered.Count == 0)
            return new ExecutableSelection { Confidence = ScanConfidence.Low, Reason = "No executable files were found." };

        var best = ordered[0];
        var second = ordered.Count > 1 ? ordered[1].Score : int.MinValue;
        var confidence = best.Score >= 80 && best.Score - second >= 20
            ? ScanConfidence.High
            : best.Score >= 45
                ? ScanConfidence.Medium
                : ScanConfidence.Low;
        return new ExecutableSelection
        {
            Path = best.Path,
            Confidence = confidence,
            Reason = $"{best.Reason} (score {best.Score})",
            Options = ordered
        };
    }

    public static IEnumerable<string> EnumerateSafe(string root, string pattern, int maxDepth)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, pattern);
            }
            catch
            {
                continue;
            }
            foreach (var file in files)
                yield return file;
            if (depth >= maxDepth)
                continue;
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.Equals("_Redist", StringComparison.OrdinalIgnoreCase) || name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    continue;
                stack.Push((child, depth + 1));
            }
        }
    }
}

public static class SteamDiscovery
{
    public static string? FindInstallPath()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(@"Software\Valve\Steam") ?? baseKey.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                    var path = key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string;
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                        return path;
                }
                catch
                {
                    // Keep searching the other views.
                }
            }
        }
        var guess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(guess) ? guess : null;
    }

    public static IEnumerable<GameRecord> Discover(string? steamInstall, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(steamInstall) || !Directory.Exists(steamInstall))
        {
            warnings.Add("Steam is not installed, or its directory could not be read.");
            yield break;
        }
        var vdf = Path.Combine(steamInstall, "steamapps", "libraryfolders.vdf");
        var libraries = new List<string> { steamInstall };
        if (File.Exists(vdf))
        {
            try
            {
                libraries = SteamLibraryParser.ReadLibraryPaths(File.ReadAllText(vdf), steamInstall).ToList();
            }
            catch (Exception ex)
            {
                warnings.Add("Steam libraryfolders.vdf could not be parsed: " + ex.Message);
            }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps))
            {
                if (!Directory.Exists(library))
                    warnings.Add($"A Steam library could not be opened and was skipped: {library}");
                continue;
            }
            IEnumerable<string> manifests;
            try
            {
                manifests = Directory.EnumerateFiles(apps, "appmanifest_*.acf");
            }
            catch (Exception ex)
            {
                warnings.Add($"Steam library {library} could not be listed: {ex.Message}");
                continue;
            }
            foreach (var manifestPath in manifests)
            {
                SteamAppManifest? manifest;
                try
                {
                    manifest = SteamLibraryParser.ReadManifest(File.ReadAllText(manifestPath));
                }
                catch
                {
                    continue;
                }
                if (manifest == null || SteamLibraryParser.ShouldSkip(manifest) || !SteamLibraryParser.IsInstalled(manifest))
                    continue;
                var install = Path.Combine(apps, "common", manifest.InstallDir);
                if (!seen.Add(install))
                    continue;
                if (!Directory.Exists(install))
                    continue;
                var selection = ExecutableSelector.Select(install);
                yield return new GameRecord
                {
                    Id = GameIds.Create(LauncherKind.Steam, manifest.AppId, install),
                    Name = manifest.Name,
                    Launcher = LauncherKind.Steam,
                    StoreId = manifest.AppId,
                    InstallPath = install,
                    ExecutablePath = selection.Path,
                    ExecutableConfidence = selection.Confidence,
                    ExecutableReason = selection.Reason,
                    DiscoveredUtc = DateTimeOffset.UtcNow,
                    PreferredGpu = GpuPreference.Describe(selection.Path)
                };
            }
        }
    }
}

public static class EpicDiscovery
{
    public static IEnumerable<GameRecord> Discover(List<string> warnings)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir))
        {
            warnings.Add("Epic Games manifests were not found.");
            yield break;
        }
        foreach (var file in SafeFiles(dir, "*.item", warnings))
        {
            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(File.ReadAllText(file));
            }
            catch
            {
                continue;
            }
            using (json)
            {
                var root = json.RootElement;
                if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True)
                    continue;
                var location = Text(root, "InstallLocation");
                var name = Text(root, "DisplayName");
                var exe = Text(root, "LaunchExecutable");
                var id = Text(root, "CatalogItemId") ?? Text(root, "MainGameAppName");
                if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(name) || !Directory.Exists(location))
                    continue;
                var launch = string.IsNullOrWhiteSpace(exe) ? null : Path.Combine(location, exe.Replace('/', Path.DirectorySeparatorChar));
                var selection = File.Exists(launch) ? new ExecutableSelection { Path = launch, Confidence = ScanConfidence.High, Reason = "Epic launch executable" } : ExecutableSelector.Select(location);
                yield return DiscoveredGame.Create(LauncherKind.Epic, name, id, location, selection);
            }
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static IEnumerable<string> SafeFiles(string dir, string pattern, List<string> warnings)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern).ToList();
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not list {dir}: {ex.Message}");
            return [];
        }
    }
}

public static class GogDiscovery
{
    public static IEnumerable<GameRecord> Discover(List<string> warnings)
    {
        var found = false;
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            foreach (var path in new[] { @"SOFTWARE\GOG.com\Games", @"SOFTWARE\WOW6432Node\GOG.com\Games" })
            {
                RegistryKey? key = null;
                try
                {
                    key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(path);
                }
                catch (Exception ex)
                {
                    warnings.Add("GOG registry could not be opened: " + ex.Message);
                }
                if (key == null)
                    continue;
                using (key)
                {
                    foreach (var subName in key.GetSubKeyNames())
                    {
                        using var game = key.OpenSubKey(subName);
                        var install = game?.GetValue("path") as string;
                        var name = game?.GetValue("gameName") as string ?? subName;
                        var exe = game?.GetValue("exe") as string;
                        if (string.IsNullOrWhiteSpace(install) || !Directory.Exists(install))
                            continue;
                        found = true;
                        var selection = File.Exists(exe)
                            ? new ExecutableSelection { Path = exe, Confidence = ScanConfidence.High, Reason = "GOG registry executable" }
                            : ExecutableSelector.Select(install);
                        yield return DiscoveredGame.Create(LauncherKind.Gog, name, subName, install, selection);
                    }
                }
            }
        }
        if (!found)
            warnings.Add("No GOG Galaxy games were found in the registry.");
    }
}

public static class OtherLauncherDiscovery
{
    public static IEnumerable<GameRecord> DiscoverXbox(List<string> warnings)
    {
        var roots = new[]
        {
            @"C:\XboxGames",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ModifiableWindowsApps"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps")
        };
        var any = false;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(root).ToList();
            }
            catch (Exception ex)
            {
                warnings.Add(root.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)
                    ? "Xbox / Microsoft Store package folders could not be read. Those installs are protected and are not modified. " + ex.Message
                    : $"Could not list {root}: {ex.Message}");
                continue;
            }
            foreach (var child in children)
            {
                var content = Directory.Exists(Path.Combine(child, "Content")) ? Path.Combine(child, "Content") : child;
                var marker = File.Exists(Path.Combine(content, "MicrosoftGame.config")) || File.Exists(Path.Combine(child, "MicrosoftGame.config"));
                if (!marker && !root.Contains("XboxGames", StringComparison.OrdinalIgnoreCase))
                    continue;
                any = true;
                var selection = ExecutableSelector.Select(content);
                yield return DiscoveredGame.Create(LauncherKind.Xbox, Path.GetFileName(child), null, content, selection);
            }
        }
        if (!any)
            warnings.Add("No Xbox or Microsoft Store games were found in XboxGames or ModifiableWindowsApps.");
    }

    public static IEnumerable<GameRecord> DiscoverEa(List<string> warnings)
    {
        var count = 0;
        foreach (var game in RegistryGames(RegistryHive.LocalMachine, new[]
        {
            @"SOFTWARE\EA Games",
            @"SOFTWARE\WOW6432Node\EA Games",
            @"SOFTWARE\Electronic Arts",
            @"SOFTWARE\WOW6432Node\Electronic Arts",
            @"SOFTWARE\WOW6432Node\Origin"
        }, LauncherKind.Ea, warnings))
        {
            count++;
            yield return game;
        }
        if (count == 0)
            warnings.Add("No EA App games were found in the registry.");
    }

    public static IEnumerable<GameRecord> DiscoverUbisoft(List<string> warnings)
    {
        var count = 0;
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            RegistryKey? key = null;
            try
            {
                key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs")
                      ?? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
            }
            catch (Exception ex)
            {
                warnings.Add("Ubisoft registry could not be opened: " + ex.Message);
            }
            if (key == null)
                continue;
            using (key)
            {
                foreach (var name in key.GetSubKeyNames())
                {
                    using var game = key.OpenSubKey(name);
                    var install = game?.GetValue("InstallDir") as string;
                    if (string.IsNullOrWhiteSpace(install) || !Directory.Exists(install))
                        continue;
                    count++;
                    yield return DiscoveredGame.Create(LauncherKind.Ubisoft, Path.GetFileName(install.TrimEnd('\\')), name, install, ExecutableSelector.Select(install));
                }
            }
        }
        if (count == 0)
            warnings.Add("No Ubisoft Connect games were found.");
    }

    public static IEnumerable<GameRecord> DiscoverBattleNet(List<string> warnings)
    {
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Battle.net", "Battle.net.config");
        if (!File.Exists(config))
        {
            warnings.Add("Battle.net configuration was not found.");
            yield break;
        }
        string text;
        try
        {
            text = File.ReadAllText(config);
        }
        catch (Exception ex)
        {
            warnings.Add("Battle.net configuration could not be read: " + ex.Message);
            yield break;
        }
        var matches = Regex.Matches(text, "\"(?:InstallPath|Path)\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in matches)
        {
            var path = match.Groups[1].Value.Replace("\\\\", "\\");
            if (!Directory.Exists(path) || !seen.Add(path))
                continue;
            if (path.Contains("Battle.net", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(path, "Battle.net.exe")))
                continue;
            yield return DiscoveredGame.Create(LauncherKind.BattleNet, Path.GetFileName(path.TrimEnd('\\')), null, path, ExecutableSelector.Select(path));
        }
    }

    private static IEnumerable<GameRecord> RegistryGames(RegistryHive hive, IEnumerable<string> paths, LauncherKind launcher, List<string> warnings)
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            foreach (var path in paths)
            {
                RegistryKey? key = null;
                try
                {
                    key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);
                }
                catch (Exception ex)
                {
                    warnings.Add($"{launcher} registry could not be opened: {ex.Message}");
                }
                if (key == null)
                    continue;
                using (key)
                {
                    foreach (var name in key.GetSubKeyNames())
                    {
                        using var game = key.OpenSubKey(name);
                        var install = game?.GetValue("Install Dir") as string ?? game?.GetValue("InstallDir") as string ?? game?.GetValue("Path") as string;
                        var display = game?.GetValue("DisplayName") as string ?? name;
                        if (string.IsNullOrWhiteSpace(install) || !Directory.Exists(install))
                            continue;
                        yield return DiscoveredGame.Create(launcher, display, name, install, ExecutableSelector.Select(install));
                    }
                }
            }
        }
    }
}

public static class GpuPreference
{
    public static string? Describe(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            var value = key?.GetValue(executable) as string;
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (value.Contains("GpuPreference=2"))
                return "Windows high-performance preference";
            if (value.Contains("GpuPreference=1"))
                return "Windows power-saving preference";
            return value;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class GameDiscoveryService : IGameDiscoveryService
{
    public Task<DiscoveryResult> ScanAsync(ScanRequest request, IReadOnlyList<GameRecord> existing, CancellationToken cancellationToken)
    {
        return Task.Run(() => Scan(request, existing, cancellationToken), cancellationToken);
    }

    private static DiscoveryResult Scan(ScanRequest request, IReadOnlyList<GameRecord> existing, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var found = new List<GameRecord>();
        var steps = new (bool Enabled, string Name, Action Action)[]
        {
            (request.Steam, "Steam", () => found.AddRange(SteamDiscovery.Discover(SteamDiscovery.FindInstallPath(), warnings))),
            (request.Epic, "Epic Games", () => found.AddRange(EpicDiscovery.Discover(warnings))),
            (request.Gog, "GOG Galaxy", () => found.AddRange(GogDiscovery.Discover(warnings))),
            (request.Xbox, "Xbox / Microsoft Store", () => found.AddRange(OtherLauncherDiscovery.DiscoverXbox(warnings))),
            (request.Ea, "EA App", () => found.AddRange(OtherLauncherDiscovery.DiscoverEa(warnings))),
            (request.Ubisoft, "Ubisoft Connect", () => found.AddRange(OtherLauncherDiscovery.DiscoverUbisoft(warnings))),
            (request.BattleNet, "Battle.net", () => found.AddRange(OtherLauncherDiscovery.DiscoverBattleNet(warnings)))
        };
        var enabled = steps.Count(step => step.Enabled) + 1;
        var done = 0;
        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!step.Enabled)
                continue;
            request.Progress?.Report(new ScanProgress { Message = "Scanning " + step.Name, CompletedSteps = done, TotalSteps = enabled });
            try
            {
                step.Action();
            }
            catch (Exception ex)
            {
                warnings.Add(step.Name + " scan failed: " + ex.Message);
            }
            done++;
        }
        request.Progress?.Report(new ScanProgress { Message = "Scanning selected folders", CompletedSteps = done, TotalSteps = enabled });
        foreach (var directory in request.ManualDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory))
            {
                warnings.Add("Custom folder is missing and was skipped: " + directory);
                continue;
            }
            var selection = ExecutableSelector.Select(directory);
            found.Add(DiscoveredGame.Create(LauncherKind.Manual, Path.GetFileName(directory.TrimEnd('\\')), null, directory, selection));
        }

        var merged = Merge(existing, found);
        return new DiscoveryResult { Games = merged, Warnings = warnings };
    }

    public static List<GameRecord> Merge(IReadOnlyList<GameRecord> existing, IReadOnlyList<GameRecord> found)
    {
        var result = new List<GameRecord>();
        var byId = existing.ToDictionary(game => game.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var game in found)
        {
            if (byId.TryGetValue(game.Id, out var previous))
            {
                game.Excluded = previous.Excluded;
                game.ArtworkPath = previous.ArtworkPath ?? game.ArtworkPath;
                game.Compatibility = previous.Compatibility;
                game.Installation = previous.Installation;
                game.Stamp = previous.Stamp;
                if (previous.UserChoseExecutable && !string.IsNullOrWhiteSpace(previous.ExecutablePath))
                {
                    game.ExecutablePath = previous.ExecutablePath;
                    game.ExecutableConfidence = ScanConfidence.High;
                    game.ExecutableReason = "Chosen manually. " + previous.ExecutableReason;
                    game.UserChoseExecutable = true;
                }
                var stamp = Stamp(game);
                if (previous.Stamp == stamp && previous.Compatibility != null && !string.IsNullOrWhiteSpace(previous.ExecutablePath))
                    game.Compatibility = previous.Compatibility;
                else
                    game.Compatibility = null;
                game.Stamp = stamp;
            }
            else
            {
                game.Stamp = Stamp(game);
            }
            if (result.Any(item => item.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(game);
        }
        foreach (var previous in existing)
        {
            if (result.Any(game => game.Id.Equals(previous.Id, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (previous.Installation != null && previous.Installation.State != ModInstallState.NotInstalled)
                result.Add(previous);
        }
        return result;
    }

    public static string Stamp(GameRecord game)
    {
        long root = 0;
        long exe = 0;
        try
        {
            if (Directory.Exists(game.InstallPath))
                root = Directory.GetLastWriteTimeUtc(game.InstallPath).Ticks;
            if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath))
                exe = File.GetLastWriteTimeUtc(game.ExecutablePath).Ticks;
        }
        catch
        {
            return "";
        }
        return root + ":" + exe;
    }
}

internal static class DiscoveredGame
{
    public static GameRecord Create(LauncherKind launcher, string name, string? storeId, string install, ExecutableSelection selection) =>
        new()
        {
            Id = GameIds.Create(launcher, storeId, install),
            Name = name,
            Launcher = launcher,
            StoreId = storeId,
            InstallPath = install,
            ExecutablePath = selection.Path,
            ExecutableConfidence = selection.Confidence,
            ExecutableReason = selection.Reason,
            DiscoveredUtc = DateTimeOffset.UtcNow,
            PreferredGpu = GpuPreference.Describe(selection.Path)
        };
}
