using System.IO.Compression;
using System.Text.Json;
using DLSSGManager.Core.Abstractions;
using DLSSGManager.Core.Compatibility;
using DLSSGManager.Core.Configuration;
using DLSSGManager.Core.Discovery;
using DLSSGManager.Core.Hardware;
using DLSSGManager.Core.Infrastructure;
using DLSSGManager.Core.Installation;
using DLSSGManager.Core.Models;
using DLSSGManager.Core.Persistence;
using DLSSGManager.Core.Releases;

namespace DLSSGManager.Tests;

public class CoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dlssg-manager-tests", Guid.NewGuid().ToString("N"));

    public CoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp cleanup */ }
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3080", "Ampere", "8.6", "SM86", true, false)]
    [InlineData("NVIDIA GeForce RTX 2080 Ti", "Turing", "7.5", "SM75", true, false)]
    [InlineData("NVIDIA GeForce RTX 4090", "Ada Lovelace", "8.9", "SM89", true, true)]
    [InlineData("NVIDIA GeForce RTX 5070", "Blackwell", "12.0", "SM120", true, true)]
    [InlineData("NVIDIA GeForce GTX 1660", "Turing", "7.5", "SM75", false, false)]
    public void Identifies_gpu_architecture(string name, string architecture, string capability, string shader, bool supported, bool newer)
    {
        var gpu = GpuArchitectureCatalog.Describe(new RawGpuAdapter { Name = name, VendorId = 0x10DE, DedicatedVideoMemory = 8L << 30 }, "617.14");
        Assert.Equal(architecture, gpu.Architecture);
        Assert.Equal(capability, gpu.ComputeCapability);
        Assert.Equal(shader, gpu.ShaderModel);
        Assert.Equal(supported, gpu.IsSupportedRtx);
        Assert.Equal(newer, gpu.IsNewerArchitecture);
    }

    [Fact]
    public void Multi_gpu_prefers_supported_nvidia()
    {
        var intel = GpuArchitectureCatalog.Describe(new RawGpuAdapter { Name = "Intel UHD 770", VendorId = 0x8086, DedicatedVideoMemory = 128L << 20 }, null);
        var rtx = GpuArchitectureCatalog.Describe(new RawGpuAdapter { Name = "NVIDIA GeForce RTX 3070", VendorId = 0x10DE, DedicatedVideoMemory = 8L << 30 }, "617.14");
        var primary = SystemCompatibilityService.SelectPrimary([intel, rtx]);
        Assert.Equal("NVIDIA GeForce RTX 3070", primary!.Name);
    }

    [Fact]
    public void System_status_follows_upstream_requirements()
    {
        var service = new SystemCompatibilityService();
        Assert.Equal(SystemCompatibilityStatus.Compatible, service.Evaluate(Snap("NVIDIA GeForce RTX 3070", "617.14", true)).Status);
        Assert.Equal(SystemCompatibilityStatus.PotentiallyCompatible, service.Evaluate(Snap("NVIDIA GeForce RTX 4090", "617.14", true)).Status);
        Assert.Equal(SystemCompatibilityStatus.ActionRequired, service.Evaluate(Snap("NVIDIA GeForce RTX 3070", "617.14", false)).Status);
        Assert.Equal(SystemCompatibilityStatus.Unsupported, service.Evaluate(Snap("AMD Radeon RX 6800", "31.0.1", true, 0x1002)).Status);
        var mystery = GpuArchitectureCatalog.Describe(new RawGpuAdapter { Name = "NVIDIA GeForce RTX 9999", VendorId = 0x10DE, DedicatedVideoMemory = 8L << 30 }, "617.14");
        Assert.False(mystery.IsSupportedRtx);
        Assert.False(mystery.ArchitectureIdentified);
        Assert.Equal(SystemCompatibilityStatus.PotentiallyCompatible, service.Evaluate(Snap("NVIDIA GeForce RTX 9999", "617.14", true)).Status);
        Assert.Equal(SystemCompatibilityStatus.PotentiallyCompatible, service.Evaluate(Snap("NVIDIA GeForce RTX 3080", "560.94", true)).Status);
        Assert.Equal(SystemCompatibilityStatus.ActionRequired, service.Evaluate(Snap("NVIDIA GeForce RTX 3080", "536.23", true)).Status);
    }

    [Theory]
    [InlineData("32.0.15.7688", "576.88")]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("31.0.15.3623", "536.23")]
    public void Decodes_nvidia_driver_versions(string windowsVersion, string friendly) =>
        Assert.Equal(friendly, NvidiaDriverVersion.ToFriendly(windowsVersion));

    [Fact]
    public void Parses_steam_libraries_and_manifests()
    {
        var vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path" "C:\\Steam"
                    "apps" { "1091500" "1" }
                }
                "1" { "path" "D:\\SteamLibrary" }
            }
            """;
        var paths = SteamLibraryParser.ReadLibraryPaths(vdf, @"C:\Steam");
        Assert.Contains(@"D:\SteamLibrary", paths);
        var manifest = SteamLibraryParser.ReadManifest("""
            "AppState"
            {
                "appid" "1091500"
                "name" "Cyberpunk 2077"
                "installdir" "Cyberpunk 2077"
                "StateFlags" "4"
            }
            """);
        Assert.Equal("Cyberpunk 2077", manifest!.Name);
        Assert.True(SteamLibraryParser.IsInstalled(manifest));
        Assert.False(SteamLibraryParser.ShouldSkip(manifest));
    }

    [Fact]
    public void Skips_a_missing_steam_library_and_selects_shipping_exe()
    {
        var steam = Path.Combine(_root, "steam");
        var library = Path.Combine(_root, "library");
        var game = Path.Combine(library, "steamapps", "common", "Wukong");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(game, "b1", "Binaries", "Win64"));
        File.WriteAllText(Path.Combine(game, "launcher.exe"), "launcher");
        File.WriteAllBytes(Path.Combine(game, "b1", "Binaries", "Win64", "b1-Win64-Shipping.exe"), new byte[6_000_000]);
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            $$"""
            "libraryfolders"
            {
                "0" { "path" "{{steam.Replace("\\", "\\\\")}}" }
                "1" { "path" "Z:\\missing-library" }
                "2" { "path" "{{library.Replace("\\", "\\\\")}}" }
            }
            """);
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_2358720.acf"), """
            "AppState" { "appid" "2358720" "name" "Black Myth: Wukong" "installdir" "Wukong" "StateFlags" "4" }
            """);
        var warnings = new List<string>();
        var games = SteamDiscovery.Discover(steam, warnings).ToList();
        Assert.Contains(warnings, warning => warning.Contains("missing-library", StringComparison.OrdinalIgnoreCase));
        Assert.Single(games);
        Assert.EndsWith("b1-Win64-Shipping.exe", games[0].ExecutablePath);
        Assert.Equal(ScanConfidence.High, games[0].ExecutableConfidence);
    }

    [Fact]
    public void Classifies_compatibility_from_evidence_not_dlls_alone()
    {
        var blocked = CompatibilityClassifier.Classify(new GameSignals { AntiCheat = ["EasyAntiCheat.exe"], GraphicsApi = "D3D12" });
        Assert.Equal(GameCompatibilityStatus.Blocked, blocked.Status);
        Assert.False(blocked.ManualInstallAllowed);
        var sr = CompatibilityClassifier.Classify(new GameSignals { HasD3D12 = true, HasSuperResolutionDll = true, GraphicsApi = "D3D12" });
        Assert.Equal(GameCompatibilityStatus.Unknown, sr.Status);
        var likely = CompatibilityClassifier.Classify(new GameSignals { HasD3D12 = true, HasFrameGenerationDll = true, ExecutableConfidence = ScanConfidence.High, GraphicsApi = "D3D12" });
        Assert.Equal(GameCompatibilityStatus.Likely, likely.Status);
        Assert.True(likely.AutomaticInstallAllowed);
        var vulkan = CompatibilityClassifier.Classify(new GameSignals { VulkanOnly = true, GraphicsApi = "Vulkan" });
        Assert.Equal(GameCompatibilityStatus.Unsupported, vulkan.Status);
    }

    [Fact]
    public void Confirmed_database_entry_matches_store_id_and_community_reports_are_downgraded()
    {
        var service = new GameCompatibilityService();
        var game = new GameRecord
        {
            Id = "1",
            Name = "Something Else",
            Launcher = LauncherKind.Steam,
            StoreId = "1091500",
            InstallPath = _root,
            ExecutableConfidence = ScanConfidence.High
        };
        var decision = service.Evaluate(game);
        Assert.Equal(GameCompatibilityStatus.Confirmed, decision.Status);
        var imported = service.Import("""
            { "schema": 1, "entries": [ { "name": "Fan Report", "status": "confirmed", "verificationKind": "community", "notes": "works for me" } ] }
            """, replace: false);
        Assert.Equal(1, imported.Downgraded);
        Assert.Contains(service.Database.Entries, entry => entry.Name == "Fan Report" && entry.Status == "likely");
    }

    [Fact]
    public void Parses_github_releases_and_compares_versions()
    {
        var json = """
            [{
              "tag_name": "0.4.0", "name": "0.4.0", "draft": false, "prerelease": false,
              "published_at": "2026-10-14T12:43:06Z", "body": "notes", "html_url": "https://github.com/sdli1995/dlssg_for_sm86/releases/tag/0.4.0",
              "assets": [
                {"name": "dlssg-release-0.4.0-x64.zip", "browser_download_url": "https://github.com/sdli1995/dlssg_for_sm86/releases/download/0.4.0/dlssg-release-0.4.0-x64.zip", "size": 10, "digest": "sha256:abc"},
                {"name": "dlssg-release-0.4.0-x64-310.9.zip", "browser_download_url": "https://github.com/example.com/other.zip", "size": 4, "digest": "sha256:def"}
              ]
            }]
            """;
        var releases = GitHubReleaseParser.Parse(json);
        var package = releases[0].PreferredPackage;
        Assert.NotNull(package);
        Assert.Equal("abc", package.Sha256);
        Assert.Contains("github.com", package.DownloadUrl);
        Assert.True(ModVersion.IsNewer("0.4.0", "0.3.5"));
        Assert.True(ModVersion.IsNewer("0.10.0", "0.9.0"));
        Assert.Equal(0, ModVersion.Compare("v0.4.0", "0.4.0"));
    }

    [Fact]
    public void Rejects_unsafe_archives_and_unknown_layouts()
    {
        var zip = Path.Combine(_root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var entry = archive.CreateEntry("../escape.txt").Open();
            entry.Write(new byte[] { 1 }, 0, 1);
        }
        var dest = Path.Combine(_root, "out");
        Assert.Throws<InvalidOperationException>(() => ZipExtractor.Extract(zip, dest));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        var unknown = Path.Combine(_root, "unknown");
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "readme.txt"), "no layout");
        var manifest = PackageInspector.Inspect(unknown, "9.9.9");
        Assert.False(manifest.Confident);
        Assert.Equal("Unknown", manifest.Layout);
    }

    [Fact]
    public void Plans_single_proxy_package_without_unexpected_dlls()
    {
        var package = CreatePackage("0.4.0");
        var manifest = PackageInspector.Inspect(package, "0.4.0");
        Assert.True(manifest.Confident);
        Assert.Equal(5, manifest.MaxGeneratedFrames);
        var plan = InstallPlanner.Plan(manifest, "version.dll", null, null);
        Assert.Contains(plan, file => file.DestinationRelativePath.Equals("version.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan, file => file.DestinationRelativePath.Contains("nvngx_dlssg.dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan, file => file.DestinationRelativePath.Equals("random.dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan, file => file.DestinationRelativePath.Equals("winmm.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Edits_ini_without_dropping_comments_and_migrates_old_keys()
    {
        var text = """
            ; keep me
            [FrameGeneration]
            Optimized=1
            MaxGeneratedFrames=5

            [Logging]
            Level=2
            """;
        var config = new ConfigurationService().Parse(text);
        config.Enabled = true;
        config.Optimized = 2;
        var round = config.Text;
        Assert.Contains("; keep me", round);
        Assert.Contains("Level=2", round);
        Assert.Contains("[DLSSG]", round);
        var source = IniDocument.Parse(text);
        var target = IniDocument.Parse("[DLSSG]\r\nOptimized=2\r\n");
        var notes = new ConfigurationService().Migrate(source, target);
        Assert.Equal("1", target.Get("DLSSG", "Optimized"));
        Assert.Contains(notes, note => note.Contains("[DLSSG]"));
        var caps = new ConfigCapabilities { MaxGeneratedFrames = 5, GameSupports6x = false, SupportsPresetC = true };
        var options = new ConfigurationService().Multipliers(caps);
        Assert.False(options.Single(option => option.Value == 5).Enabled);
        Assert.True(options.Single(option => option.Value == 3).Enabled);
    }

    [Fact]
    public void Detects_unowned_dll_conflicts()
    {
        var game = Path.Combine(_root, "conflict");
        Directory.CreateDirectory(game);
        File.WriteAllBytes(Path.Combine(game, "version.dll"), [9, 9, 9]);
        var conflicts = ConflictDetector.Detect(game,
            [new PlannedFile { DestinationRelativePath = "version.dll", SourcePath = Path.Combine(CreatePackage("0.4.0"), "version.dll") }],
            [],
            path => File.Exists(path) ? Hashing.Sha256File(path) : null);
        Assert.Single(conflicts);
    }

    [Fact]
    public void Install_uninstall_rollback_and_recovery_are_reversible()
    {
        var harness = CreateHarness();
        var gameDir = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "game.exe");
        File.WriteAllBytes(exe, [1]);
        File.WriteAllBytes(Path.Combine(gameDir, "version.dll"), "ORIG"u8.ToArray());
        var game = TestGame(gameDir, exe);
        var first = PackageFor(CreatePackage("0.4.0", [7]), "0.4.0");
        var installed = harness.Service.Install(Request(game, first), CancellationToken.None);
        Assert.True(installed.Success, installed.Message);
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, "random.dll")));
        Assert.True(Directory.Exists(Path.Combine(gameDir, "dlssg_sm86")));

        File.WriteAllBytes(Path.Combine(gameDir, "dlssg_sm86", "backend", "sm86_backend.dll"), [3]);
        var inspected = harness.Service.Inspect(game, game.Installation, "0.4.0");
        Assert.Equal(ModInstallState.RepairRequired, inspected.State);
        File.WriteAllBytes(Path.Combine(gameDir, "dlssg_sm86", "backend", "sm86_backend.dll"), [2]);

        var foreign = Path.Combine(_root, "foreign");
        Directory.CreateDirectory(foreign);
        File.WriteAllBytes(Path.Combine(foreign, "game.exe"), [1]);
        File.WriteAllBytes(Path.Combine(foreign, "version.dll"), [4]);
        var foreignGame = TestGame(foreign, Path.Combine(foreign, "game.exe"));
        var refused = harness.Service.Install(Request(foreignGame, first, overwrite: false), CancellationToken.None);
        Assert.Equal("conflict", refused.ErrorCode);
        Assert.Equal(new byte[] { 4 }, File.ReadAllBytes(Path.Combine(foreign, "version.dll")));

        var second = PackageFor(CreatePackage("0.4.1", [8]), "0.4.1");
        var updateRequest = Request(game, second);
        updateRequest.IsUpdate = true;
        var updated = harness.Service.Install(updateRequest, CancellationToken.None);
        Assert.True(updated.Success, updated.Message);
        Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        var rolled = harness.Service.Rollback(game, CancellationToken.None);
        Assert.True(rolled.Success, rolled.Message);
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));

        var removed = harness.Service.Uninstall(game, false, CancellationToken.None);
        Assert.True(removed.Success, removed.Message);
        Assert.Equal("ORIG"u8.ToArray(), File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        Assert.False(Directory.Exists(Path.Combine(gameDir, "dlssg_sm86")));

        var partial = Path.Combine(_root, "partial.dll");
        File.WriteAllText(partial, "partial");
        var journal = new InstallJournal
        {
            Operations = [new JournalOperation { Kind = "create", Path = partial }]
        };
        File.WriteAllText(Path.Combine(harness.Paths.Journals, journal.Id + ".json"), JsonSerializer.Serialize(journal));
        Assert.Equal(1, harness.Service.RecoverInterrupted());
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public void Consecutive_updates_keep_the_pristine_original_and_a_stable_ownership_marker()
    {
        var harness = CreateHarness();
        var gameDir = Path.Combine(_root, "updates");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "game.exe");
        File.WriteAllBytes(exe, [1]);
        File.WriteAllBytes(Path.Combine(gameDir, "version.dll"), "ORIG"u8.ToArray());
        File.WriteAllBytes(Path.Combine(gameDir, "winmm.dll"), "OTHER"u8.ToArray());
        var game = TestGame(gameDir, exe);
        var first = PackageFor(CreatePackage("0.4.0", [7]), "0.4.0");
        var blocked = harness.Service.Install(Request(game, first), CancellationToken.None);
        Assert.Equal("extra-proxy", blocked.ErrorCode);
        Assert.Equal("OTHER"u8.ToArray(), File.ReadAllBytes(Path.Combine(gameDir, "winmm.dll")));

        var approved = Request(game, first);
        approved = new InstallRequest
        {
            Game = approved.Game,
            Package = approved.Package,
            System = approved.System,
            OverwriteConflicts = true,
            ApprovedRemovals = ["winmm.dll"]
        };
        var installed = harness.Service.Install(approved, CancellationToken.None);
        Assert.True(installed.Success, installed.Message);
        Assert.False(File.Exists(Path.Combine(gameDir, "winmm.dll")));
        var marker = Path.Combine(gameDir, "dlssg_sm86", "manager-ownership.json");
        var markerText = File.ReadAllText(marker);
        Assert.DoesNotContain("manager-ownership.json", markerText, StringComparison.OrdinalIgnoreCase);
        var markerRecord = game.Installation!.Files.Single(file => file.RelativePath.EndsWith("manager-ownership.json", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Hashing.Sha256File(marker), markerRecord.Sha256);
        var original = game.Installation.Files.Single(file => file.RelativePath.Equals("version.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Hashing.Sha256Text("ORIG"), original.OriginalSha256);

        var second = PackageFor(CreatePackage("0.4.1", [8]), "0.4.1");
        var update = Request(game, second);
        update.IsUpdate = true;
        Assert.True(harness.Service.Install(update, CancellationToken.None).Success);
        var third = PackageFor(CreatePackage("0.4.2", [9]), "0.4.2");
        var again = Request(game, third);
        again.IsUpdate = true;
        Assert.True(harness.Service.Install(again, CancellationToken.None).Success);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        var afterUpdates = game.Installation!.Files.Single(file => file.RelativePath.Equals("version.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(original.BackupPath, afterUpdates.BackupPath);
        Assert.Equal(original.OriginalSha256, afterUpdates.OriginalSha256);

        File.Delete(afterUpdates.BackupPath!);
        var fourth = PackageFor(CreatePackage("0.4.3", [10]), "0.4.3");
        var refused = Request(game, fourth);
        refused.IsUpdate = true;
        var stopped = harness.Service.Install(refused, CancellationToken.None);
        Assert.Equal("backup", stopped.ErrorCode);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));

        afterUpdates.BackupPath = original.BackupPath;
        File.WriteAllBytes(original.BackupPath!, "ORIG"u8.ToArray());
        var removed = harness.Service.Uninstall(game, false, CancellationToken.None);
        Assert.True(removed.Success, removed.Message);
        Assert.Equal("ORIG"u8.ToArray(), File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        Assert.Equal("OTHER"u8.ToArray(), File.ReadAllBytes(Path.Combine(gameDir, "winmm.dll")));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void Rollback_rejects_bad_snapshots_and_restores_after_a_midway_failure()
    {
        var harness = CreateHarness();
        var gameDir = Path.Combine(_root, "rollback");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "game.exe");
        File.WriteAllBytes(exe, [1]);
        File.WriteAllBytes(Path.Combine(gameDir, "version.dll"), "ORIG"u8.ToArray());
        File.WriteAllText(Path.Combine(gameDir, "keep-me.txt"), "leave this file");
        var game = TestGame(gameDir, exe);
        var first = PackageFor(CreatePackage("0.4.0", [7]), "0.4.0");
        Assert.True(harness.Service.Install(Request(game, first), CancellationToken.None).Success);
        var original = game.Installation!.Files.Single(file => file.RelativePath.Equals("version.dll", StringComparison.OrdinalIgnoreCase));
        var ini = "[General]\r\nEnabled=1\r\nNote=before-update\r\n";
        File.WriteAllText(Path.Combine(gameDir, "dlssg_sm86.ini"), ini);

        var secondRoot = CreatePackage("0.4.1", [8]);
        File.WriteAllBytes(Path.Combine(secondRoot, "dlssg_sm86", "notes.bin"), [42]);
        var second = PackageFor(secondRoot, "0.4.1");
        var update = Request(game, second);
        update.IsUpdate = true;
        Assert.True(harness.Service.Install(update, CancellationToken.None).Success);
        Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        Assert.Equal(original.BackupPath, game.Installation!.Files.Single(file => file.RelativePath.Equals("version.dll", StringComparison.OrdinalIgnoreCase)).BackupPath);
        var snapshot = game.Installation.RollbackDirectory!;
        var snapshotJson = File.ReadAllText(Path.Combine(snapshot, "snapshot.json"));

        harness.Processes.Running = true;
        var running = harness.Service.Rollback(game, CancellationToken.None);
        Assert.Equal("running", running.ErrorCode);
        Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        harness.Processes.Running = false;

        var payload = Path.Combine(snapshot, "files", "dlssg_sm86", "backend", "sm86_backend.dll");
        File.WriteAllBytes(payload, [99]);
        var mismatch = harness.Service.Rollback(game, CancellationToken.None);
        Assert.Equal("rollback", mismatch.ErrorCode);
        Assert.Contains("hash", mismatch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        File.WriteAllBytes(payload, [2]);

        File.Delete(payload);
        var missing = harness.Service.Rollback(game, CancellationToken.None);
        Assert.Equal("rollback", missing.ErrorCode);
        Assert.Contains("missing", missing.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        File.WriteAllBytes(payload, [2]);

        File.WriteAllText(Path.Combine(snapshot, "snapshot.json"), "{not json");
        var corrupt = harness.Service.Rollback(game, CancellationToken.None);
        Assert.Equal("rollback", corrupt.ErrorCode);
        Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        File.WriteAllText(Path.Combine(snapshot, "snapshot.json"), snapshotJson);

        harness.Service.FailAfterAppliedSteps = 1;
        var interrupted = harness.Service.Rollback(game, CancellationToken.None);
        Assert.False(interrupted.Success);
        Assert.Equal("rolled-back", interrupted.ErrorCode);
        Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(gameDir, "dlssg_sm86", "notes.bin")));
        Assert.True(File.Exists(Path.Combine(snapshot, "snapshot.json")));
        Assert.NotEmpty(Directory.EnumerateFiles(harness.Paths.Journals, "*.json"));

        harness.Service.FailAfterAppliedSteps = 0;
        var rolled = harness.Service.Rollback(game, CancellationToken.None);
        Assert.True(rolled.Success, rolled.Message);
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, "dlssg_sm86", "notes.bin")));
        Assert.Equal("leave this file", File.ReadAllText(Path.Combine(gameDir, "keep-me.txt")));
        Assert.Equal("0.4.0", game.Installation!.ReleaseTag);
        Assert.Equal(original.BackupPath, game.Installation.Files.Single(file => file.RelativePath.Equals("version.dll", StringComparison.OrdinalIgnoreCase)).BackupPath);
        Assert.Equal(original.OriginalSha256, game.Installation.Files.Single(file => file.RelativePath.Equals("version.dll", StringComparison.OrdinalIgnoreCase)).OriginalSha256);
        var marker = Path.Combine(gameDir, "dlssg_sm86", "manager-ownership.json");
        Assert.DoesNotContain("manager-ownership.json", File.ReadAllText(marker), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Hashing.Sha256File(marker), game.Installation.Files.Single(file => file.RelativePath.EndsWith("manager-ownership.json", StringComparison.OrdinalIgnoreCase)).Sha256);
        Assert.Equal(ini, File.ReadAllText(Path.Combine(gameDir, "dlssg_sm86.ini")));
    }

    [Fact]
    public void Interrupted_rollback_journal_is_recovered_and_a_broken_undo_is_kept()
    {
        var harness = CreateHarness();
        var gameDir = Path.Combine(_root, "journal");
        Directory.CreateDirectory(gameDir);
        var dll = Path.Combine(gameDir, "version.dll");
        File.WriteAllBytes(dll, [99]);
        var backup = Path.Combine(_root, "version-before.dll");
        File.WriteAllBytes(backup, [8]);
        var journal = new InstallJournal
        {
            GameId = "journal-game",
            State = "rollback",
            Operations = [new JournalOperation { Kind = "replace", Path = dll, BackupPath = backup, Existed = true }]
        };
        File.WriteAllText(Path.Combine(harness.Paths.Journals, journal.Id + ".json"), JsonSerializer.Serialize(journal));
        Assert.Equal(1, harness.Service.RecoverInterrupted());
        Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(dll));
        var saved = JsonSerializer.Deserialize<InstallJournal>(File.ReadAllText(Path.Combine(harness.Paths.Journals, journal.Id + ".json")));
        Assert.Equal("recovered", saved!.State);
        Assert.Equal(0, harness.Service.RecoverInterrupted());

        var stuck = new InstallJournal
        {
            State = "rollback",
            Operations = [new JournalOperation { Kind = "replace", Path = dll, BackupPath = Path.Combine(_root, "missing-backup.dll"), Existed = true }]
        };
        File.WriteAllBytes(dll, [99]);
        File.WriteAllText(Path.Combine(harness.Paths.Journals, stuck.Id + ".json"), JsonSerializer.Serialize(stuck));
        Assert.Equal(0, harness.Service.RecoverInterrupted());
        Assert.Equal(1, harness.Service.UnresolvedRecoveries);
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(dll));
        var kept = JsonSerializer.Deserialize<InstallJournal>(File.ReadAllText(Path.Combine(harness.Paths.Journals, stuck.Id + ".json")));
        Assert.Equal("recovery-incomplete", kept!.State);
        Assert.True(File.Exists(Path.Combine(harness.Paths.Journals, stuck.Id + ".json")));
    }

    [Fact]
    public void Repair_is_preflighted_and_a_midway_failure_restores_the_previous_files()
    {
        var harness = CreateHarness();
        var gameDir = Path.Combine(_root, "repair");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "game.exe");
        File.WriteAllBytes(exe, [1]);
        File.WriteAllBytes(Path.Combine(gameDir, "version.dll"), "USER"u8.ToArray());
        var game = TestGame(gameDir, exe);
        var packageRoot = CreatePackage("0.4.0", [7]);
        var package = PackageFor(packageRoot, "0.4.0");
        Assert.True(harness.Service.Install(Request(game, package), CancellationToken.None).Success);
        var iniPath = Path.Combine(gameDir, "dlssg_sm86.ini");
        File.WriteAllText(iniPath, "[General]\r\nEnabled=0\r\nCustom=kept\r\n");
        var backend = Path.Combine(gameDir, "dlssg_sm86", "backend", "sm86_backend.dll");
        var runtime = Path.Combine(gameDir, "dlssg_sm86", "runtime", "nvngx_dlssg.dll");
        File.WriteAllBytes(backend, [30]);

        File.WriteAllBytes(Path.Combine(gameDir, "version.dll"), [4, 4]);
        var refused = harness.Service.Repair(game, package, CancellationToken.None);
        Assert.Equal("ownership", refused.ErrorCode);
        Assert.Equal(new byte[] { 4, 4 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        Assert.Equal(new byte[] { 30 }, File.ReadAllBytes(backend));
        Assert.Equal("[General]\r\nEnabled=0\r\nCustom=kept\r\n", File.ReadAllText(iniPath));
        File.WriteAllBytes(Path.Combine(gameDir, "version.dll"), [7]);

        File.Delete(Path.Combine(packageRoot, "dlssg_sm86", "backend", "sm86_backend.dll"));
        var missingSource = harness.Service.Repair(game, package, CancellationToken.None);
        Assert.Equal("source", missingSource.ErrorCode);
        Assert.Equal(new byte[] { 30 }, File.ReadAllBytes(backend));
        File.WriteAllBytes(Path.Combine(packageRoot, "dlssg_sm86", "backend", "sm86_backend.dll"), [2]);
        File.WriteAllBytes(runtime, [31]);

        harness.Service.FailAfterAppliedSteps = 1;
        var failed = harness.Service.Repair(game, package, CancellationToken.None);
        Assert.Equal("rolled-back", failed.ErrorCode);
        Assert.Equal(new byte[] { 30 }, File.ReadAllBytes(backend));
        Assert.Equal(new byte[] { 31 }, File.ReadAllBytes(runtime));
        Assert.Equal("[General]\r\nEnabled=0\r\nCustom=kept\r\n", File.ReadAllText(iniPath));

        harness.Service.FailAfterAppliedSteps = 0;
        var repaired = harness.Service.Repair(game, package, CancellationToken.None);
        Assert.True(repaired.Success, repaired.Message);
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(backend));
        Assert.Equal(new byte[] { 3 }, File.ReadAllBytes(runtime));
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        Assert.Equal("[General]\r\nEnabled=0\r\nCustom=kept\r\n", File.ReadAllText(iniPath));
        Assert.Equal(ModInstallState.ConfigurationModified, game.Installation!.State);
    }

    [Fact]
    public void Snapshot_failures_keep_the_previous_snapshot()
    {
        var harness = CreateHarness();
        var gameDir = Path.Combine(_root, "snapshot");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "game.exe");
        File.WriteAllBytes(exe, [1]);
        File.WriteAllBytes(Path.Combine(gameDir, "version.dll"), "ORIG"u8.ToArray());
        var game = TestGame(gameDir, exe);
        Assert.True(harness.Service.Install(Request(game, PackageFor(CreatePackage("0.4.0", [7]), "0.4.0")), CancellationToken.None).Success);
        var update = Request(game, PackageFor(CreatePackage("0.4.1", [8]), "0.4.1"));
        update.IsUpdate = true;
        Assert.True(harness.Service.Install(update, CancellationToken.None).Success);
        var firstSnapshot = Path.Combine(harness.Paths.DefaultBackups, game.Id, "rollback", "0.4.0");
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(firstSnapshot, "files", "version.dll")));

        harness.Service.FailSnapshotBeforePromote = true;
        var next = Request(game, PackageFor(CreatePackage("0.4.2", [9]), "0.4.2"));
        next.IsUpdate = true;
        var stopped = harness.Service.Install(next, CancellationToken.None);
        Assert.Equal("snapshot", stopped.ErrorCode);
        Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(firstSnapshot, "files", "version.dll")));
        Assert.False(Directory.Exists(Path.Combine(harness.Paths.DefaultBackups, game.Id, "rollback", "0.4.1")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(harness.Paths.DefaultBackups, game.Id, "rollback"), "*.staging-*"));

        harness.Service.FailSnapshotBeforePromote = false;
        Assert.True(harness.Service.Install(next, CancellationToken.None).Success);
        var rolled = harness.Service.Rollback(game, CancellationToken.None);
        Assert.True(rolled.Success, rolled.Message);
        Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(Path.Combine(gameDir, "version.dll")));
        Assert.Equal("ORIG"u8.ToArray(), File.ReadAllBytes(game.Installation!.Files.Single(file => file.RelativePath.Equals("version.dll", StringComparison.OrdinalIgnoreCase)).BackupPath!));
    }

    [Fact]
    public void Startup_checks_pass_in_a_temporary_directory_and_fail_when_it_is_unusable()
    {
        var data = Path.Combine(_root, "self-test");
        var lines = new List<string>();
        Assert.Equal(0, StartupChecks.Run(data, lines));
        Assert.Contains(lines, line => line == "result: pass");
        Assert.Contains(lines, line => line.StartsWith("version: ", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(data, "library.db")));

        var blocked = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blocked, "file");
        var failed = new List<string>();
        Assert.NotEqual(0, StartupChecks.Run(blocked, failed));
        Assert.Contains(failed, line => line == "result: fail");
    }

    [Fact]
    public void Application_version_metadata_matches_the_release_tag_form()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DLSSGManager.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var project = File.ReadAllText(Path.Combine(dir!.FullName, "src", "DLSSGManager.App", "DLSSGManager.App.csproj"));
        Assert.Contains("<Version>1.0.0</Version>", project, StringComparison.Ordinal);
        Assert.Contains("<InformationalVersion>1.0.0</InformationalVersion>", project, StringComparison.Ordinal);
        Assert.Contains("<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>", project, StringComparison.Ordinal);
        var workflow = File.ReadAllText(Path.Combine(dir.FullName, ".github", "workflows", "release.yml"));
        Assert.Contains("does not match application version", workflow, StringComparison.Ordinal);
        Assert.Contains("--self-test", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Replaces_a_corrupt_library_database()
    {
        var root = Path.Combine(_root, "corrupt-db");
        Directory.CreateDirectory(root);
        var paths = new AppDataPaths(root);
        paths.Ensure();
        File.WriteAllText(paths.Database, "this is not sqlite");
        var store = new LibraryStore(paths);
        Assert.Empty(store.LoadGames());
        Assert.NotEmpty(Directory.EnumerateFiles(root, "library.db.corrupt-*"));
    }

    [Fact]
    public void Refuses_running_games_and_non_github_downloads()
    {
        var harness = CreateHarness(running: true);
        var gameDir = Path.Combine(_root, "running");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "game.exe");
        File.WriteAllBytes(exe, [1]);
        var result = harness.Service.Install(Request(TestGame(gameDir, exe), PackageFor(CreatePackage("0.4.0", [1]), "0.4.0")), CancellationToken.None);
        Assert.Equal("running", result.ErrorCode);
        Assert.Throws<InvalidOperationException>(() => DownloadPolicy.EnsureOfficial(new Uri("http://github.com/file.zip")));
        Assert.Throws<InvalidOperationException>(() => DownloadPolicy.EnsureOfficial(new Uri("https://example.com/file.zip")));
        DownloadPolicy.EnsureOfficial(new Uri("https://api.github.com/repos/sdli1995/dlssg_for_sm86/releases"));
    }

    [Fact]
    public async Task Manual_scan_survives_a_path_that_is_not_a_directory()
    {
        var file = Path.Combine(_root, "not-a-folder.txt");
        File.WriteAllText(file, "x");
        var discovery = new GameDiscoveryService();
        var result = await discovery.ScanAsync(new ScanRequest
        {
            Steam = false, Epic = false, Gog = false, Xbox = false, Ea = false, Ubisoft = false, BattleNet = false,
            ManualDirectories = [file]
        }, [], CancellationToken.None);
        Assert.Empty(result.Games);
        Assert.NotEmpty(result.Warnings);
    }

    private string CreatePackage(string tag, byte[]? proxy = null)
    {
        var root = Path.Combine(_root, "pkg-" + tag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "dlssg_sm86", "backend"));
        Directory.CreateDirectory(Path.Combine(root, "dlssg_sm86", "runtime"));
        Directory.CreateDirectory(Path.Combine(root, "dlssg_sm86", "kernels"));
        Directory.CreateDirectory(Path.Combine(root, "alternatives"));
        File.WriteAllBytes(Path.Combine(root, "version.dll"), proxy ?? [1, 2, 3]);
        File.WriteAllText(Path.Combine(root, "dlssg_sm86.ini"), "[General]\r\nEnabled=1\r\n[DLSSG]\r\nOptimized=2\r\n");
        File.WriteAllBytes(Path.Combine(root, "dlssg_sm86", "backend", "sm86_backend.dll"), [2]);
        File.WriteAllBytes(Path.Combine(root, "dlssg_sm86", "runtime", "nvngx_dlssg.dll"), [3]);
        File.WriteAllBytes(Path.Combine(root, "dlssg_sm86", "kernels", "fg_kernels.dll"), [4]);
        File.WriteAllBytes(Path.Combine(root, "random.dll"), [5]);
        File.WriteAllBytes(Path.Combine(root, "alternatives", "winmm.dll"), [6]);
        return root;
    }

    private PreparedPackage PackageFor(string directory, string tag) => new()
    {
        Release = new ModRelease { Tag = tag, Name = tag, Body = "notes" },
        ArchivePath = directory,
        ExtractedPath = directory,
        Manifest = PackageInspector.Inspect(directory, tag),
        Signature = new SignatureInfo { IsSigned = false, Message = "Test package." }
    };

    private static GameRecord TestGame(string directory, string exe) => new()
    {
        Id = Hashing.Sha256Text(directory)[..16],
        Name = "Test Game",
        Launcher = LauncherKind.Manual,
        InstallPath = directory,
        ExecutablePath = exe,
        ExecutableConfidence = ScanConfidence.High,
        Compatibility = new GameCompatibilityDecision
        {
            Status = GameCompatibilityStatus.Confirmed,
            Summary = "Test",
            AutomaticInstallAllowed = true,
            ManualInstallAllowed = true
        }
    };

    private static InstallRequest Request(GameRecord game, PreparedPackage package, bool overwrite = true) => new()
    {
        Game = game,
        Package = package,
        OverwriteConflicts = overwrite,
        System = new SystemCompatibilityReport
        {
            Status = SystemCompatibilityStatus.Compatible,
            Summary = "Compatible",
            Reasons = [],
            Gpus = [],
            InstallAllowed = true
        }
    };

    private Harness CreateHarness(bool running = false)
    {
        var paths = new AppDataPaths(Path.Combine(_root, "app-" + Guid.NewGuid().ToString("N")));
        var store = new LibraryStore(paths);
        var processes = new FakeProcesses(running);
        var service = new InstallationService(new BackupService(), processes, store, paths, () => new AppSettings(), new ConfigurationService());
        return new Harness(paths, service, processes);
    }

    private static HardwareSnapshot Snap(string name, string driver, bool? hags, uint vendor = 0x10DE) => new()
    {
        Gpus = [GpuArchitectureCatalog.Describe(new RawGpuAdapter { Name = name, VendorId = vendor, DedicatedVideoMemory = 8L << 30 }, driver)],
        OperatingSystem = "Windows 11 Pro",
        Is64Bit = true,
        BuildNumber = 26100,
        NvidiaDriverVersion = vendor == 0x10DE ? driver : null,
        HagsEnabled = hags,
        HagsDetail = "test"
    };

    private sealed record Harness(AppDataPaths Paths, InstallationService Service, FakeProcesses Processes);

    private sealed class FakeProcesses(bool running) : DLSSGManager.Core.Abstractions.IProcessInspector
    {
        public bool Running { get; set; } = running;
        public bool IsExecutableRunning(string executablePath) => Running;
    }
}
