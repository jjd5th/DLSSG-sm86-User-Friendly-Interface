using System.Text.Json;
using DLSSGManager.Core.Abstractions;
using DLSSGManager.Core.Configuration;
using DLSSGManager.Core.Infrastructure;
using DLSSGManager.Core.Models;
using DLSSGManager.Core.Releases;

namespace DLSSGManager.Core.Installation;

public sealed class BackupService : IBackupService
{
    public string BackupFile(string sourcePath, string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var name = Path.GetFileName(sourcePath);
        var destination = Path.Combine(backupDirectory, name + "." + Hashing.Sha256File(sourcePath)[..12] + ".bak");
        File.Copy(sourcePath, destination, overwrite: true);
        return destination;
    }

    public void RestoreFile(string backupPath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(backupPath, destinationPath, overwrite: true);
    }
}

public static class ConflictDetector
{
    public static IReadOnlyList<DllConflict> Detect(string installDirectory, IEnumerable<PlannedFile> plan, IEnumerable<OwnedFileRecord> owned, Func<string, string?> hashOf)
    {
        var conflicts = new List<DllConflict>();
        var ownedMap = owned.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan.Where(file => !file.DeleteOnly))
        {
            var relative = item.DestinationRelativePath;
            if (!ProxyNames.IsProxy(Path.GetFileName(relative)) && !relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                continue;
            var full = Path.Combine(installDirectory, relative);
            var hash = hashOf(full);
            if (hash == null)
                continue;
            if (ownedMap.TryGetValue(relative, out var record) && record.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(item.SourcePath) && File.Exists(item.SourcePath) && Hashing.Sha256File(item.SourcePath).Equals(hash, StringComparison.OrdinalIgnoreCase))
                continue;
            if (item.Content != null && Hashing.Sha256Text(item.Content).Equals(hash, StringComparison.OrdinalIgnoreCase))
                continue;
            conflicts.Add(new DllConflict
            {
                RelativePath = relative,
                Reason = ownedMap.ContainsKey(relative)
                    ? "The file was installed by DLSSG Manager and has since changed."
                    : "The file already exists and was not installed by DLSSG Manager. It will not be overwritten without approval."
            });
        }
        return conflicts;
    }
}

public sealed class InstallJournal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GameId { get; set; } = "";
    public string State { get; set; } = "in-progress";
    public List<JournalOperation> Operations { get; set; } = [];
}

public sealed class JournalOperation
{
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public string? BackupPath { get; set; }
    public bool Existed { get; set; }
}

public sealed class InstallationService : IInstallationService
{
    private readonly IBackupService _backups;
    private readonly IProcessInspector _processes;
    private readonly ILibraryStore _store;
    private readonly AppDataPaths _paths;
    private readonly Func<AppSettings> _settings;
    private readonly IConfigurationService _configuration;
    internal int FailAfterAppliedSteps { get; set; }
    internal bool FailSnapshotBeforePromote { get; set; }
    public int UnresolvedRecoveries { get; private set; }

    public InstallationService(IBackupService backups, IProcessInspector processes, ILibraryStore store, AppDataPaths paths, Func<AppSettings> settings, IConfigurationService configuration)
    {
        _backups = backups;
        _processes = processes;
        _store = store;
        _paths = paths;
        _settings = settings;
        _configuration = configuration;
    }

    public OperationResult Install(InstallRequest request, CancellationToken cancellationToken)
    {
        var log = new List<string>();
        var game = request.Game;
        var decision = game.Compatibility;
        if (request.System.Status == SystemCompatibilityStatus.Unsupported)
            return OperationResult.Fail(request.System.Summary + " " + string.Join(" ", request.System.Reasons), "system");
        if (request.System.RequiresAcknowledgement && !request.AcknowledgeSystemWarning)
            return OperationResult.Fail("The system compatibility warning has to be acknowledged before installation.", "system-ack");
        if (decision == null)
            return OperationResult.Fail("Scan this game before installing.", "compatibility");
        if (decision.Status is GameCompatibilityStatus.Blocked or GameCompatibilityStatus.Unsupported)
            return OperationResult.Fail(decision.Summary, "game-blocked");
        if (decision.Status == GameCompatibilityStatus.Unknown && !request.AllowUnknownGame)
            return OperationResult.Fail("Compatibility is unknown. Confirm a manual install before copying files.", "unknown-game");
        if (!decision.ManualInstallAllowed && !decision.AutomaticInstallAllowed)
            return OperationResult.Fail(decision.Summary, "game-blocked");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return OperationResult.Fail("Choose the rendering executable before installing.", "executable");
        if (game.ExecutableConfidence == ScanConfidence.Low && !request.AllowLowConfidenceExecutable)
            return OperationResult.Fail("The rendering executable is uncertain. Confirm it before installing.", "executable");
        if (!request.Package.Manifest.Confident && !request.AllowUncertainManifest)
            return OperationResult.Fail(request.Package.Manifest.Uncertainty ?? "The release layout is not recognized.", "manifest");
        var directory = Path.GetDirectoryName(game.ExecutablePath)!;
        if (PathSafety.IsProtectedGamePath(directory))
            return OperationResult.Fail("The executable is in a protected WindowsApps directory.", "protected");
        if (_processes.IsExecutableRunning(game.ExecutablePath))
            return OperationResult.Fail("The game is running. Close it before changing files.", "running");
        if (!_paths.TryWritable(directory, out var writeError))
            return OperationResult.Fail("The game folder is not writable. " + writeError, "access");

        var proxy = request.ProxyFileName ?? request.Package.Manifest.DefaultProxy;
        var existing = game.Installation ?? _store.LoadInstallation(game.Id);
        var unownedExtras = ProxyNames.Order.Where(name =>
            !name.Equals(proxy, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(directory, name)) &&
            !OwnedProxyMatches(directory, existing, name)).ToList();
        var approved = new HashSet<string>(request.ApprovedRemovals ?? [], StringComparer.OrdinalIgnoreCase);
        var unapproved = unownedExtras.Where(name => !approved.Contains(name)).ToList();
        if (unapproved.Count > 0)
            return OperationResult.Fail("These proxy DLLs were not installed by DLSSG Manager. Approve each file before installation can continue: " + string.Join(", ", unapproved) + ". Nothing was changed.", "extra-proxy");
        var deletes = OwnedProxiesToRemove(directory, existing, proxy);
        foreach (var name in approved)
        {
            if (!name.Equals(proxy, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(directory, name)))
                deletes.Add(name);
        }
        string? configText = request.ConfigurationText;
        var warnings = new List<string>();
        if (configText == null && existing != null)
        {
            var oldIni = Path.Combine(directory, "dlssg_sm86.ini");
            var newIni = Path.Combine(request.Package.Manifest.PackageRoot, "dlssg_sm86.ini");
            if (File.Exists(oldIni) && File.Exists(newIni))
            {
                var migrated = _configuration.Parse(File.ReadAllText(newIni));
                warnings.AddRange(_configuration.Migrate(IniDocument.Parse(File.ReadAllText(oldIni)), migrated.Document));
                configText = migrated.Text;
            }
        }
        IReadOnlyList<PlannedFile> plan;
        try
        {
            plan = InstallPlanner.Plan(request.Package.Manifest, proxy, configText, deletes);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message, "plan");
        }
        var conflicts = DetectConflicts(directory, plan, existing?.Files ?? []);
        if (conflicts.Count > 0 && !request.OverwriteConflicts)
            return OperationResult.Fail("Existing DLLs need approval before they can be replaced.", "conflict", conflicts: conflicts);

        var bytes = plan.Where(file => file.SourcePath != null && File.Exists(file.SourcePath)).Sum(file => new FileInfo(file.SourcePath!).Length);
        try
        {
            if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace < bytes + 200L * 1024 * 1024)
                return OperationResult.Fail("There is not enough free disk space to install and keep a backup.", "disk");
        }
        catch (Exception ex)
        {
            log.Add("Free space could not be checked: " + ex.Message);
        }

        foreach (var step in plan)
        {
            var prior = FindPrior(existing, step.DestinationRelativePath);
            if (prior is { OriginalExisted: true } && !PristineBackupOk(prior))
                return OperationResult.Fail("The original backup for " + step.DestinationRelativePath + " is missing or no longer matches. Nothing was changed.", "backup");
        }

        if (request.IsUpdate && existing != null)
        {
            try
            {
                SnapshotRollback(directory, existing);
            }
            catch (Exception ex)
            {
                return OperationResult.Fail("The previous version could not be snapshotted, so no game files were changed. " + ex.Message, "snapshot");
            }
        }

        var journal = new InstallJournal { GameId = game.Id };
        WriteJournal(journal);
        var backupRoot = Path.Combine(BackupRoot(), game.Id, DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
        Directory.CreateDirectory(backupRoot);
        var owned = new List<OwnedFileRecord>();
        try
        {
            foreach (var step in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = PathSafety.CombineUnderRoot(directory, step.DestinationRelativePath);
                if (step.DeleteOnly)
                {
                    if (!File.Exists(destination))
                        continue;
                    var currentHash = Hashing.Sha256File(destination);
                    var removedBackup = _backups.BackupFile(destination, backupRoot);
                    if (!Hashing.Sha256File(removedBackup).Equals(currentHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The backup of " + step.DestinationRelativePath + " could not be verified.");
                    journal.Operations.Add(new JournalOperation { Kind = "delete", Path = destination, BackupPath = removedBackup, Existed = true });
                    WriteJournal(journal);
                    var priorDelete = FindPrior(existing, step.DestinationRelativePath);
                    if (priorDelete is { OriginalExisted: true })
                    {
                        if (!PristineBackupOk(priorDelete))
                            throw new InvalidOperationException("The original backup for " + step.DestinationRelativePath + " cannot be restored.");
                        ReplaceFromBackup(priorDelete.BackupPath!, destination, priorDelete.OriginalSha256!);
                        log.Add("Restored the original " + step.DestinationRelativePath);
                    }
                    else if (priorDelete != null)
                    {
                        File.Delete(destination);
                        log.Add("Removed " + step.DestinationRelativePath);
                    }
                    else
                    {
                        File.Delete(destination);
                        owned.Add(new OwnedFileRecord
                        {
                            RelativePath = Normalize(step.DestinationRelativePath),
                            Sha256 = currentHash,
                            Size = 0,
                            OriginalExisted = true,
                            OriginalSha256 = currentHash,
                            BackupPath = removedBackup,
                            IntentionallyAbsent = true
                        });
                        log.Add("Backed up and removed " + step.DestinationRelativePath);
                    }
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var existed = File.Exists(destination);
                string? transactionBackup = null;
                string? currentHashBefore = null;
                if (existed)
                {
                    currentHashBefore = Hashing.Sha256File(destination);
                    transactionBackup = _backups.BackupFile(destination, backupRoot);
                    if (!Hashing.Sha256File(transactionBackup).Equals(currentHashBefore, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The backup of " + step.DestinationRelativePath + " could not be verified.");
                    journal.Operations.Add(new JournalOperation { Kind = "replace", Path = destination, BackupPath = transactionBackup, Existed = true });
                }
                else
                {
                    journal.Operations.Add(new JournalOperation { Kind = "create", Path = destination, Existed = false });
                }
                WriteJournal(journal);
                if (step.Content != null)
                    File.WriteAllText(destination, step.Content);
                else if (step.SourcePath != null)
                    File.Copy(step.SourcePath, destination, overwrite: true);
                else
                    throw new InvalidOperationException("A planned file has no source.");
                var hash = Hashing.Sha256File(destination);
                if (step.SourcePath != null && !hash.Equals(Hashing.Sha256File(step.SourcePath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(step.DestinationRelativePath + " failed verification.");
                var prior = FindPrior(existing, step.DestinationRelativePath);
                var keepOriginal = prior is { OriginalExisted: true };
                owned.Add(new OwnedFileRecord
                {
                    RelativePath = Normalize(step.DestinationRelativePath),
                    Sha256 = hash,
                    Size = new FileInfo(destination).Length,
                    OriginalExisted = keepOriginal || (prior == null && existed),
                    OriginalSha256 = keepOriginal ? prior!.OriginalSha256 : prior == null && existed ? currentHashBefore : null,
                    BackupPath = keepOriginal ? prior!.BackupPath : prior == null && existed ? transactionBackup : null,
                    IsConfiguration = step.IsConfiguration
                });
                log.Add("Installed " + step.DestinationRelativePath);
            }

            if (existing != null)
            {
                foreach (var prior in existing.Files.Where(file => file.IntentionallyAbsent))
                {
                    if (owned.Any(file => Normalize(file.RelativePath).Equals(Normalize(prior.RelativePath), StringComparison.OrdinalIgnoreCase)))
                        continue;
                    owned.Add(prior);
                }
            }

            var markerRel = string.IsNullOrWhiteSpace(request.Package.Manifest.PayloadDirectory)
                ? "manager-ownership.json"
                : Path.Combine(request.Package.Manifest.PayloadDirectory, "manager-ownership.json");
            var record = new InstallationRecord
            {
                GameId = game.Id,
                ReleaseTag = request.Package.Release.Tag,
                ManifestId = request.Package.Manifest.Id,
                InstallDirectory = directory,
                ProxyFileName = proxy,
                State = ModInstallState.InstalledVerified,
                InstalledUtc = DateTimeOffset.UtcNow,
                VerifiedUtc = DateTimeOffset.UtcNow,
                PreviousReleaseTag = existing?.ReleaseTag,
                RollbackDirectory = existing == null ? null : Path.Combine(BackupRoot(), game.Id, "rollback", existing.ReleaseTag),
                Files = owned.Where(file => !IsMarker(file.RelativePath)).ToList()
            };
            PublishMarker(record, directory, markerRel, journal);
            _store.SaveInstallation(record);
            game.Installation = record;
            journal.State = "complete";
            File.Delete(JournalPath(journal.Id));
            _store.AppendLog(game.Id, request.IsUpdate ? "update" : "install", "success", request.Package.Release.Tag);
            var message = request.IsUpdate
                ? $"Updated {game.Name} to {request.Package.Release.Tag}."
                : $"Installed {request.Package.Release.Tag} for {game.Name}.";
            return OperationResult.Ok(message + " Restart the game after any configuration change. File installation does not prove frame generation is active.", log, warnings);
        }
        catch (Exception ex)
        {
            RollbackJournal(journal);
            journal.State = "rolled-back";
            WriteJournal(journal);
            _store.AppendLog(game.Id, "install", "rolled-back", ex.Message);
            return OperationResult.Fail("Installation failed and the previous files were restored. " + ex.Message, "rolled-back", log);
        }
    }

    public OperationResult Uninstall(GameRecord game, bool restoreOriginalsOnly, CancellationToken cancellationToken)
    {
        var record = game.Installation ?? _store.LoadInstallation(game.Id);
        if (record == null)
            return OperationResult.Fail("DLSSG Manager has no record of files it installed for this game, so nothing will be deleted.", "ownership");
        if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && _processes.IsExecutableRunning(game.ExecutablePath))
            return OperationResult.Fail("The game is running. Close it first.", "running");
        var problems = new List<string>();
        foreach (var file in record.Files.Where(item => !IsMarker(item.RelativePath)))
        {
            var path = Path.Combine(record.InstallDirectory, file.RelativePath);
            if (file.OriginalExisted && !PristineBackupOk(file))
                problems.Add("The original backup for " + file.RelativePath + " is missing or does not match.");
            if (file.IntentionallyAbsent || !File.Exists(path))
                continue;
            var hash = Hashing.Sha256File(path);
            var configWeCreated = file.IsConfiguration && !file.OriginalExisted;
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) && !configWeCreated)
                problems.Add(file.RelativePath + " no longer matches the installed file.");
        }
        if (problems.Count > 0)
        {
            record.State = ModInstallState.ManualIntervention;
            record.LastError = string.Join(" ", problems);
            _store.SaveInstallation(record);
            return OperationResult.Fail("Ownership could not be confirmed, so no files were changed. " + record.LastError, "ownership");
        }

        var journal = new InstallJournal { GameId = game.Id };
        WriteJournal(journal);
        try
        {
            foreach (var file in record.Files.Where(item => !IsMarker(item.RelativePath)).OrderByDescending(item => item.RelativePath.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(record.InstallDirectory, file.RelativePath);
                if (file.IntentionallyAbsent)
                {
                    if (!File.Exists(path))
                        ReplaceFromBackup(file.BackupPath!, path, file.OriginalSha256!);
                    continue;
                }
                if (!File.Exists(path))
                    continue;
                if (file.OriginalExisted)
                {
                    var currentBackup = _backups.BackupFile(path, Path.Combine(BackupRoot(), game.Id, "uninstall-" + journal.Id));
                    journal.Operations.Add(new JournalOperation { Kind = "replace", Path = path, BackupPath = currentBackup, Existed = true });
                    WriteJournal(journal);
                    ReplaceFromBackup(file.BackupPath!, path, file.OriginalSha256!);
                }
                else
                {
                    var removed = _backups.BackupFile(path, Path.Combine(BackupRoot(), game.Id, "uninstall-" + journal.Id));
                    journal.Operations.Add(new JournalOperation { Kind = "delete", Path = path, BackupPath = removed, Existed = true });
                    WriteJournal(journal);
                    File.Delete(path);
                }
            }
            foreach (var relative in new[] { Path.Combine("dlssg_sm86", "manager-ownership.json"), "manager-ownership.json" })
            {
                var marker = Path.Combine(record.InstallDirectory, relative);
                if (!File.Exists(marker))
                    continue;
                var removed = _backups.BackupFile(marker, Path.Combine(BackupRoot(), game.Id, "uninstall-" + journal.Id));
                journal.Operations.Add(new JournalOperation { Kind = "delete", Path = marker, BackupPath = removed, Existed = true });
                WriteJournal(journal);
                File.Delete(marker);
            }
        }
        catch (Exception ex)
        {
            RollbackJournal(journal);
            return OperationResult.Fail("Uninstall stopped and the previous files were restored. " + ex.Message, "rolled-back");
        }
        File.Delete(JournalPath(journal.Id));
        var payload = Path.Combine(record.InstallDirectory, "dlssg_sm86");
        if (Directory.Exists(payload) && !Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).Any())
            Directory.Delete(payload, true);
        _store.DeleteInstallation(game.Id);
        game.Installation = null;
        _store.AppendLog(game.Id, restoreOriginalsOnly ? "restore-backup" : "uninstall", "success", record.ReleaseTag);
        return OperationResult.Ok(restoreOriginalsOnly
            ? $"Restored the original files and removed the mod files DLSSG Manager owns for {game.Name}."
            : $"Uninstalled DLSSG for SM86 from {game.Name} and restored original files.");
    }

    public OperationResult Repair(GameRecord game, PreparedPackage package, CancellationToken cancellationToken)
    {
        var record = game.Installation ?? _store.LoadInstallation(game.Id);
        if (record == null)
            return OperationResult.Fail("There is no recorded installation to repair.", "ownership");
        if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && _processes.IsExecutableRunning(game.ExecutablePath))
            return OperationResult.Fail("The game is running. Close it first.", "running");
        if (!_paths.TryWritable(record.InstallDirectory, out var writeError))
            return OperationResult.Fail("The game folder is not writable. " + writeError, "access");

        var steps = new List<(string Relative, string Source, string Destination, string Hash, OwnedFileRecord? Owned)>();
        foreach (var file in package.Manifest.Files.Where(item => item.Role is "payload" or "proxy"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Role == "proxy" && !file.DestinationRelativePath.Equals(record.ProxyFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            var relative = Normalize(file.DestinationRelativePath);
            if (relative.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                continue;
            var source = Path.Combine(package.Manifest.PackageRoot, file.PackageRelativePath);
            var destination = Path.Combine(record.InstallDirectory, relative);
            var owned = record.Files.FirstOrDefault(item => Normalize(item.RelativePath).Equals(relative, StringComparison.OrdinalIgnoreCase));
            if (owned?.IsConfiguration == true)
                continue;
            if (!File.Exists(source))
            {
                if (owned == null)
                    continue;
                if (!File.Exists(destination) || !Hashing.Sha256File(destination).Equals(owned.Sha256, StringComparison.OrdinalIgnoreCase))
                    return OperationResult.Fail("The repair source for " + relative + " is missing, so nothing was changed.", "source");
                continue;
            }
            var sourceHash = Hashing.Sha256File(source);
            if (File.Exists(destination))
            {
                var current = Hashing.Sha256File(destination);
                if (current.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (owned == null)
                    return OperationResult.Fail(relative + " is not part of the recorded installation. Nothing was changed.", "ownership");
                var stillOurs = current.Equals(owned.Sha256, StringComparison.OrdinalIgnoreCase);
                if (!stillOurs && owned.OriginalExisted)
                    return OperationResult.Fail(relative + " was changed by something else. Repair will not overwrite it, and nothing was changed.", "ownership");
            }
            else if (owned == null)
            {
                continue;
            }
            steps.Add((relative, source, destination, sourceHash, owned));
        }
        if (steps.Count == 0)
            return OperationResult.Ok("The recorded files already match the release. The configuration file was left as it is.");
        if (!HasFreeSpace(record.InstallDirectory, steps.Sum(step => new FileInfo(step.Source).Length)))
            return OperationResult.Fail("There is not enough free disk space to repair and keep a backup.", "disk");

        var journal = new InstallJournal { GameId = game.Id, State = "repair" };
        WriteJournal(journal);
        var applied = 0;
        var repaired = new List<string>();
        try
        {
            foreach (var step in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(step.Destination))
                {
                    var backup = _backups.BackupFile(step.Destination, Path.Combine(BackupRoot(), game.Id, "repair-" + journal.Id));
                    if (!Hashing.Sha256File(backup).Equals(Hashing.Sha256File(step.Destination), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The backup of " + step.Relative + " could not be verified.");
                    journal.Operations.Add(new JournalOperation { Kind = "replace", Path = step.Destination, BackupPath = backup, Existed = true });
                }
                else
                {
                    journal.Operations.Add(new JournalOperation { Kind = "create", Path = step.Destination, Existed = false });
                }
                WriteJournal(journal);
                CopyVerified(step.Source, step.Destination, step.Hash);
                repaired.Add(step.Relative);
                applied++;
                ThrowIfInjected(applied);
            }
            foreach (var step in steps)
            {
                if (step.Owned == null)
                {
                    record.Files.Add(new OwnedFileRecord
                    {
                        RelativePath = step.Relative,
                        Sha256 = step.Hash,
                        Size = new FileInfo(step.Destination).Length
                    });
                }
                else
                {
                    step.Owned.Sha256 = step.Hash;
                    step.Owned.Size = new FileInfo(step.Destination).Length;
                }
            }
            record.VerifiedUtc = DateTimeOffset.UtcNow;
            record.LastError = null;
            var markerRel = record.Files.Select(file => file.RelativePath).FirstOrDefault(IsMarker) ?? Path.Combine("dlssg_sm86", "manager-ownership.json");
            PublishMarker(record, record.InstallDirectory, markerRel, journal);
            game.Installation = Inspect(game, record, package.Release.Tag);
            _store.SaveInstallation(record);
            File.Delete(JournalPath(journal.Id));
            _store.AppendLog(game.Id, "repair", "success", string.Join(", ", repaired));
            return OperationResult.Ok("Repaired " + string.Join(", ", repaired) + ". The configuration file was not reset.");
        }
        catch (Exception ex)
        {
            return FinishFailedTransaction(journal, record, "Repair failed. " + ex.Message);
        }
    }

    public OperationResult Rollback(GameRecord game, CancellationToken cancellationToken)
    {
        var record = game.Installation ?? _store.LoadInstallation(game.Id);
        if (record?.RollbackDirectory == null || !Directory.Exists(record.RollbackDirectory))
            return OperationResult.Fail("There is no previous version snapshot to restore.", "rollback");
        if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && _processes.IsExecutableRunning(game.ExecutablePath))
            return OperationResult.Fail("The game is running. Close it first.", "running");
        if (!_paths.TryWritable(record.InstallDirectory, out var writeError))
            return OperationResult.Fail("The game folder is not writable. " + writeError, "access");
        var snapshotFile = Path.Combine(record.RollbackDirectory, "snapshot.json");
        if (!File.Exists(snapshotFile))
            return OperationResult.Fail("The rollback snapshot is incomplete. No files were changed.", "rollback");
        InstallationRecord snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<InstallationRecord>(File.ReadAllText(snapshotFile), Json()) ?? throw new InvalidOperationException("empty");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("The rollback snapshot could not be read. No files were changed. " + ex.Message, "rollback");
        }

        var problems = new List<string>();
        long bytes = 0;
        foreach (var file in snapshot.Files.Where(item => !item.IntentionallyAbsent))
        {
            var source = Path.Combine(record.RollbackDirectory, "files", file.RelativePath);
            if (!File.Exists(source))
                problems.Add(file.RelativePath + " is missing from the snapshot.");
            else if (!Hashing.Sha256File(source).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                problems.Add(file.RelativePath + " does not match the snapshot hash.");
            else
                bytes += new FileInfo(source).Length;
        }
        foreach (var file in record.Files.Where(item => !item.IntentionallyAbsent && !IsMarker(item.RelativePath)))
        {
            var path = Path.Combine(record.InstallDirectory, file.RelativePath);
            if (!File.Exists(path))
                continue;
            var hash = Hashing.Sha256File(path);
            var configWeCreated = file.IsConfiguration && !file.OriginalExisted;
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) && !configWeCreated)
                problems.Add(file.RelativePath + " was changed by something else.");
        }
        if (problems.Count > 0)
            return OperationResult.Fail(string.Join(" ", problems) + " No files were changed.", "rollback");
        if (!HasFreeSpace(record.InstallDirectory, bytes))
            return OperationResult.Fail("There is not enough free disk space to roll back and keep a backup.", "disk");

        var journal = new InstallJournal { GameId = game.Id, State = "rollback" };
        WriteJournal(journal);
        var applied = 0;
        try
        {
            foreach (var file in record.Files.Where(item => !IsMarker(item.RelativePath)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot.Files.Any(item => Normalize(item.RelativePath).Equals(Normalize(file.RelativePath), StringComparison.OrdinalIgnoreCase)))
                    continue;
                var path = Path.Combine(record.InstallDirectory, file.RelativePath);
                if (!File.Exists(path))
                    continue;
                var current = Hashing.Sha256File(path);
                if (!current.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) && !(file.IsConfiguration && !file.OriginalExisted))
                    throw new InvalidOperationException(file.RelativePath + " changed after the rollback check.");
                var backup = _backups.BackupFile(path, Path.Combine(BackupRoot(), game.Id, "rollback-tx-" + journal.Id));
                journal.Operations.Add(new JournalOperation { Kind = "delete", Path = path, BackupPath = backup, Existed = true });
                WriteJournal(journal);
                File.Delete(path);
                applied++;
                ThrowIfInjected(applied);
            }
            foreach (var file in snapshot.Files.Where(item => !item.IntentionallyAbsent))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.Combine(record.RollbackDirectory, "files", file.RelativePath);
                var destination = Path.Combine(record.InstallDirectory, file.RelativePath);
                if (File.Exists(destination))
                {
                    var backup = _backups.BackupFile(destination, Path.Combine(BackupRoot(), game.Id, "rollback-tx-" + journal.Id));
                    journal.Operations.Add(new JournalOperation { Kind = "replace", Path = destination, BackupPath = backup, Existed = true });
                }
                else
                {
                    journal.Operations.Add(new JournalOperation { Kind = "create", Path = destination, Existed = false });
                }
                WriteJournal(journal);
                CopyVerified(source, destination, file.Sha256);
                applied++;
                ThrowIfInjected(applied);
            }
            snapshot.PreviousReleaseTag = record.ReleaseTag;
            snapshot.RollbackDirectory = null;
            var markerRel = snapshot.Files.Select(file => file.RelativePath).FirstOrDefault(IsMarker) ?? Path.Combine("dlssg_sm86", "manager-ownership.json");
            PublishMarker(snapshot, record.InstallDirectory, markerRel, journal);
            _store.SaveInstallation(snapshot);
            game.Installation = snapshot;
            File.Delete(JournalPath(journal.Id));
            _store.AppendLog(game.Id, "rollback", "success", snapshot.ReleaseTag);
            return OperationResult.Ok($"Rolled {game.Name} back to {snapshot.ReleaseTag}. The original game-file backups were kept.");
        }
        catch (Exception ex)
        {
            return FinishFailedTransaction(journal, record, "Rollback failed. " + ex.Message);
        }
    }

    public InstallationRecord Inspect(GameRecord game, InstallationRecord? known, string? latestTag)
    {
        known ??= _store.LoadInstallation(game.Id);
        var directory = string.IsNullOrWhiteSpace(game.ExecutablePath) ? game.InstallPath : Path.GetDirectoryName(game.ExecutablePath)!;
        var marker = FindMarker(directory);
        known ??= marker;
        if (known == null)
        {
            var stray = File.Exists(Path.Combine(directory, "dlssg_sm86.ini")) || Directory.Exists(Path.Combine(directory, "dlssg_sm86"));
            return new InstallationRecord
            {
                GameId = game.Id,
                ReleaseTag = "",
                ManifestId = "",
                InstallDirectory = directory,
                ProxyFileName = "",
                State = stray ? ModInstallState.ManualIntervention : ModInstallState.NotInstalled,
                LastError = stray ? "DLSSG files are present, but this manager did not install them. They will not be deleted automatically." : null
            };
        }

        var missing = false;
        var changed = false;
        var configChanged = false;
        foreach (var file in known.Files.Where(item => !IsMarker(item.RelativePath) && !item.IntentionallyAbsent))
        {
            var path = Path.Combine(known.InstallDirectory, file.RelativePath);
            if (!File.Exists(path))
            {
                if (!file.IsConfiguration)
                    missing = true;
                continue;
            }
            var hash = Hashing.Sha256File(path);
            if (hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                continue;
            if (file.IsConfiguration)
                configChanged = true;
            else
                changed = true;
        }
        var activation = ActivationReader.Read(known.InstallDirectory, out var activationDetail);
        var state = ModInstallState.InstalledVerified;
        if (changed)
            state = ModInstallState.RepairRequired;
        else if (missing)
            state = ModInstallState.MissingFiles;
        else if (!string.IsNullOrWhiteSpace(latestTag) && ModVersion.IsNewer(latestTag, known.ReleaseTag))
            state = ModInstallState.UpdateAvailable;
        else if (configChanged)
            state = ModInstallState.ConfigurationModified;
        known.State = state;
        known.ConfigurationModified = configChanged;
        known.Activation = activation;
        known.ActivationDetail = activationDetail;
        known.VerifiedUtc = DateTimeOffset.UtcNow;
        return known;
    }

    public IReadOnlyList<DllConflict> DetectConflicts(string installDirectory, IEnumerable<PlannedFile> plan, IEnumerable<OwnedFileRecord> owned) =>
        ConflictDetector.Detect(installDirectory, plan, owned, path => File.Exists(path) ? Hashing.Sha256File(path) : null);

    public int RecoverInterrupted()
    {
        if (!Directory.Exists(_paths.Journals))
            return 0;
        var count = 0;
        UnresolvedRecoveries = 0;
        foreach (var file in Directory.EnumerateFiles(_paths.Journals, "*.json"))
        {
            InstallJournal? journal;
            try
            {
                journal = JsonSerializer.Deserialize<InstallJournal>(File.ReadAllText(file), Json());
            }
            catch
            {
                continue;
            }
            if (journal == null || journal.State is "complete" or "recovered" or "rolled-back")
                continue;
            var restored = RollbackJournal(journal);
            journal.State = restored ? "recovered" : "recovery-incomplete";
            File.WriteAllText(file, JsonSerializer.Serialize(journal, Json()));
            if (restored)
                count++;
            else
                UnresolvedRecoveries++;
        }
        return count;
    }

    private void SnapshotRollback(string directory, InstallationRecord existing)
    {
        var finalRoot = Path.Combine(BackupRoot(), existing.GameId, "rollback", existing.ReleaseTag);
        var staging = finalRoot + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(staging, "files"));
        try
        {
            var snapshotRecord = JsonSerializer.Deserialize<InstallationRecord>(JsonSerializer.Serialize(existing, Json()), Json())
                ?? throw new InvalidOperationException("The installation record could not be copied for the snapshot.");
            foreach (var file in snapshotRecord.Files.Where(item => !item.IntentionallyAbsent))
            {
                var source = Path.Combine(directory, file.RelativePath);
                if (!File.Exists(source))
                {
                    if (file.IsConfiguration)
                        continue;
                    throw new InvalidOperationException(file.RelativePath + " is missing, so the previous version was not snapshotted.");
                }
                var destination = Path.Combine(staging, "files", file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, true);
                var copied = Hashing.Sha256File(destination);
                if (!copied.Equals(Hashing.Sha256File(source), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The snapshot copy of " + file.RelativePath + " did not match.");
                if (file.IsConfiguration)
                    file.Sha256 = copied;
                else if (!copied.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The snapshot of " + file.RelativePath + " does not match the recorded installation.");
            }
            File.WriteAllText(Path.Combine(staging, "snapshot.json"), JsonSerializer.Serialize(snapshotRecord, Json()));
            _ = JsonSerializer.Deserialize<InstallationRecord>(File.ReadAllText(Path.Combine(staging, "snapshot.json")), Json())
                ?? throw new InvalidOperationException("The snapshot metadata could not be read back.");
            if (FailSnapshotBeforePromote)
                throw new InvalidOperationException("Snapshot promotion was stopped before the previous snapshot was replaced.");
            string? displaced = null;
            if (Directory.Exists(finalRoot))
            {
                displaced = finalRoot + ".previous-" + Guid.NewGuid().ToString("N");
                Directory.Move(finalRoot, displaced);
            }
            try
            {
                Directory.Move(staging, finalRoot);
            }
            catch
            {
                if (displaced != null && Directory.Exists(displaced) && !Directory.Exists(finalRoot))
                    Directory.Move(displaced, finalRoot);
                throw;
            }
            if (displaced != null && Directory.Exists(displaced))
            {
                try
                {
                    Directory.Delete(displaced, true);
                }
                catch (Exception ex)
                {
                    AppLog.Error("The previous snapshot was kept because it could not be removed.", ex);
                }
            }
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
            throw;
        }
    }

    private OperationResult FinishFailedTransaction(InstallJournal journal, InstallationRecord record, string message)
    {
        var restored = RollbackJournal(journal);
        if (!restored)
        {
            journal.State = "recovery-incomplete";
            WriteJournal(journal);
            record.State = ModInstallState.ManualIntervention;
            record.LastError = message + " The automatic undo did not finish. The journal and backups were kept.";
            _store.SaveInstallation(record);
            return OperationResult.Fail(record.LastError, "manual");
        }
        journal.State = "rolled-back";
        WriteJournal(journal);
        return OperationResult.Fail(message + " The files were returned to their previous state. Recovery information was kept.", "rolled-back");
    }

    private void ThrowIfInjected(int applied)
    {
        if (FailAfterAppliedSteps > 0 && applied >= FailAfterAppliedSteps)
            throw new InvalidOperationException("A failure was injected after " + applied + " file operation(s).");
    }

    private static void CopyVerified(string source, string destination, string expectedHash)
    {
        if (!File.Exists(source) || !Hashing.Sha256File(source).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The source no longer matches the expected hash: " + destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".dlssg-stage";
        File.Copy(source, temporary, overwrite: true);
        if (!Hashing.Sha256File(temporary).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temporary);
            throw new InvalidOperationException("The staged file did not match: " + destination);
        }
        try
        {
            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            throw;
        }
        if (!Hashing.Sha256File(destination).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The written file did not match: " + destination);
    }

    private static bool HasFreeSpace(string directory, long bytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            return !string.IsNullOrWhiteSpace(root) && new DriveInfo(root).AvailableFreeSpace >= bytes + 100L * 1024 * 1024;
        }
        catch
        {
            return true;
        }
    }

    private static HashSet<string> OwnedProxiesToRemove(string directory, InstallationRecord? existing, string keep)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existing == null)
            return names;
        foreach (var file in existing.Files)
        {
            var name = Path.GetFileName(file.RelativePath);
            if (!ProxyNames.IsProxy(name) || name.Equals(keep, StringComparison.OrdinalIgnoreCase))
                continue;
            var path = Path.Combine(directory, name);
            if (File.Exists(path) && Hashing.Sha256File(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                names.Add(name);
        }
        return names;
    }

    private static InstallationRecord? FindMarker(string directory)
    {
        foreach (var relative in new[] { Path.Combine("dlssg_sm86", "manager-ownership.json"), "manager-ownership.json" })
        {
            var path = Path.Combine(directory, relative);
            if (!File.Exists(path))
                continue;
            try
            {
                return JsonSerializer.Deserialize<InstallationRecord>(File.ReadAllText(path), Json());
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    private bool RollbackJournal(InstallJournal journal)
    {
        var restored = true;
        foreach (var operation in journal.Operations.AsEnumerable().Reverse())
        {
            try
            {
                if (operation.Kind == "create" && File.Exists(operation.Path))
                    File.Delete(operation.Path);
                else if (!string.IsNullOrWhiteSpace(operation.BackupPath) && File.Exists(operation.BackupPath))
                    File.Copy(operation.BackupPath, operation.Path, overwrite: true);
                else if (operation.Existed)
                    restored = false;
            }
            catch (Exception ex)
            {
                restored = false;
                AppLog.Error("Rollback step failed for " + operation.Path, ex);
            }
        }
        return restored;
    }

    private void WriteJournal(InstallJournal journal)
    {
        Directory.CreateDirectory(_paths.Journals);
        var target = JournalPath(journal.Id);
        var temporary = target + ".tmp";
        var json = JsonSerializer.Serialize(journal, Json());
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(true);
        }
        File.Move(temporary, target, overwrite: true);
    }

    private void PublishMarker(InstallationRecord record, string directory, string markerRel, InstallJournal journal)
    {
        record.Files = record.Files.Where(file => !IsMarker(file.RelativePath)).ToList();
        var path = PathSafety.CombineUnderRoot(directory, markerRel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        journal.Operations.Add(new JournalOperation { Kind = "create", Path = path, Existed = File.Exists(path) });
        if (File.Exists(path))
        {
            var markerBackup = _backups.BackupFile(path, Path.Combine(BackupRoot(), record.GameId, "marker-" + journal.Id));
            journal.Operations[^1] = new JournalOperation { Kind = "replace", Path = path, BackupPath = markerBackup, Existed = true };
        }
        WriteJournal(journal);
        File.WriteAllText(path, JsonSerializer.Serialize(record, Json()));
        if (File.ReadAllText(path).Contains("manager-ownership.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ownership metadata included its own filename. The marker was not updated.");
        record.Files.Add(new OwnedFileRecord
        {
            RelativePath = Normalize(markerRel),
            Sha256 = Hashing.Sha256File(path),
            Size = new FileInfo(path).Length,
            IsConfiguration = true
        });
    }

    private static void ReplaceFromBackup(string backupPath, string destination, string expectedHash)
    {
        if (!File.Exists(backupPath) || !Hashing.Sha256File(backupPath).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A backup could not be verified: " + destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".dlssg-restore";
        File.Copy(backupPath, temporary, overwrite: true);
        if (!Hashing.Sha256File(temporary).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temporary);
            throw new InvalidOperationException("The restored copy did not match the backup: " + destination);
        }
        File.Move(temporary, destination, overwrite: true);
    }

    private static OwnedFileRecord? FindPrior(InstallationRecord? existing, string relative)
    {
        if (existing == null)
            return null;
        var key = Normalize(relative);
        return existing.Files.FirstOrDefault(file => Normalize(file.RelativePath).Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PristineBackupOk(OwnedFileRecord file) =>
        file.OriginalExisted &&
        !string.IsNullOrWhiteSpace(file.BackupPath) &&
        !string.IsNullOrWhiteSpace(file.OriginalSha256) &&
        File.Exists(file.BackupPath) &&
        Hashing.Sha256File(file.BackupPath).Equals(file.OriginalSha256, StringComparison.OrdinalIgnoreCase);

    private static bool OwnedProxyMatches(string directory, InstallationRecord? existing, string name)
    {
        var prior = FindPrior(existing, name);
        if (prior == null || prior.IntentionallyAbsent)
            return false;
        var path = Path.Combine(directory, name);
        return File.Exists(path) && Hashing.Sha256File(path).Equals(prior.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMarker(string relative) =>
        Normalize(relative).EndsWith("manager-ownership.json", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string relative) => relative.Replace('/', '\\');

    private string JournalPath(string id) => Path.Combine(_paths.Journals, id + ".json");

    private string BackupRoot()
    {
        var custom = _settings().BackupDirectory;
        return string.IsNullOrWhiteSpace(custom) ? _paths.DefaultBackups : custom;
    }

    private static JsonSerializerOptions Json() => new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}

file static class PathProbe
{
    public static bool TryWritable(this AppDataPaths _, string directory, out string? error)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".dlssg-manager-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

public static class ActivationReader
{
    public static RuntimeActivation Read(string? installDirectory, out string detail)
    {
        detail = "No backend log was found. Installed files do not show that frame generation is active.";
        if (string.IsNullOrWhiteSpace(installDirectory))
            return RuntimeActivation.NotChecked;
        var logs = Path.Combine(installDirectory, "dlssg_sm86", "logs");
        if (!Directory.Exists(logs))
        {
            detail = "No dlssg_sm86\\logs directory yet. Launch the game with frame generation enabled, and set logging to 2 if you need a loader trace.";
            return RuntimeActivation.NoLog;
        }
        var backend = Directory.EnumerateFiles(logs, "backend_*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (backend == null)
        {
            detail = "A log folder exists, but there is no backend log.";
            return RuntimeActivation.NoLog;
        }
        string text;
        try
        {
            text = File.ReadAllText(backend);
        }
        catch (Exception ex)
        {
            detail = "The backend log could not be read: " + ex.Message;
            return RuntimeActivation.NoLog;
        }
        var hasInstall = text.Contains("\"install\"", StringComparison.OrdinalIgnoreCase) || text.Contains("install", StringComparison.OrdinalIgnoreCase);
        var active = text.Contains("\"active\":true", StringComparison.OrdinalIgnoreCase) || text.Contains("\"active\": true", StringComparison.OrdinalIgnoreCase);
        if (hasInstall && active)
        {
            detail = "The newest backend log contains an install record with route active=true. That is runtime evidence, separate from the files merely being present.";
            return RuntimeActivation.ActivationRecorded;
        }
        detail = "A backend log exists, but it does not contain both an install record and an active route. Frame generation is not reported as active.";
        return RuntimeActivation.InstalledOnly;
    }
}
