using System.Security.Cryptography;
using DLSSGManager.Core.Abstractions;

namespace DLSSGManager.Core.Infrastructure;

public sealed class SystemFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void WriteAllBytes(string path, byte[] content) => File.WriteAllBytes(path, content);
    public void WriteAllText(string path, string content) => File.WriteAllText(path, content);
    public void Copy(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
    public void DeleteFile(string path) => File.Delete(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);
    public IEnumerable<string> EnumerateFiles(string path, string searchPattern) => Directory.EnumerateFiles(path, searchPattern);
    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);
    public long Length(string path) => new FileInfo(path).Length;
    public DateTime LastWriteUtc(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : Directory.GetLastWriteTimeUtc(path);
    public Stream OpenRead(string path) => File.OpenRead(path);
    public void Move(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);

    public long FreeSpace(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root))
            return 0;
        return new DriveInfo(root).AvailableFreeSpace;
    }

    public bool TryProbeWritable(string directory, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".dlssg-manager-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

public static class Hashing
{
    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Sha256Text(string text) => Sha256(System.Text.Encoding.UTF8.GetBytes(text));
}

public static class PathSafety
{
    public static bool IsUnsafeRelative(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return true;
        var normalized = relative.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':'))
            return true;
        return normalized.Split('/').Any(part => part == "..");
    }

    public static string CombineUnderRoot(string root, string relative)
    {
        if (IsUnsafeRelative(relative))
            throw new InvalidOperationException($"Refusing an unsafe relative path: {relative}");
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !full.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path escapes the target directory: {relative}");
        return full;
    }

    public static bool IsProtectedGamePath(string path)
    {
        var full = Path.GetFullPath(path);
        var windowsApps = full.Contains($"{Path.DirectorySeparatorChar}WindowsApps{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
        var modifiable = full.Contains($"{Path.DirectorySeparatorChar}ModifiableWindowsApps{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
        return windowsApps && !modifiable;
    }
}

public sealed class AppDataPaths
{
    public AppDataPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSSGManager");
    }

    public string Root { get; }
    public string Database => Path.Combine(Root, "library.db");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string Logs => Path.Combine(Root, "logs");
    public string Journals => Path.Combine(Root, "journals");
    public string DefaultBackups => Path.Combine(Root, "backups");
    public string DefaultCache => Path.Combine(Root, "cache");
    public string Artwork => Path.Combine(Root, "artwork");
    public string CompatibilityOverride => Path.Combine(Root, "compatibility-database.json");

    public void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Journals);
        Directory.CreateDirectory(DefaultBackups);
        Directory.CreateDirectory(DefaultCache);
        Directory.CreateDirectory(Artwork);
    }
}

public static class AppLog
{
    private static readonly object Gate = new();
    public static bool Enabled { get; set; } = true;
    public static string? DirectoryPath { get; set; }

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception == null ? message : message + Environment.NewLine + exception);

    public static void Prune(int retentionDays)
    {
        if (DirectoryPath == null || !Directory.Exists(DirectoryPath))
            return;
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays));
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch
            {
                // Retention is best-effort.
            }
        }
    }

    private static void Write(string level, string message)
    {
        if (!Enabled || DirectoryPath == null)
            return;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}";
            lock (Gate)
                File.AppendAllText(Path.Combine(DirectoryPath, "manager.log"), line);
        }
        catch
        {
            // Logging must not take down the app.
        }
    }
}

public static class Redaction
{
    public static string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var result = text;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            result = result.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        var user = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(user))
            result = result.Replace($@"\Users\{user}\", @"\Users\%USERNAME%\", StringComparison.OrdinalIgnoreCase);
        return result;
    }
}

public static class ModVersion
{
    public static int Compare(string? left, string? right)
    {
        var a = Parse(left);
        var b = Parse(right);
        var length = Math.Max(a.Count, b.Count);
        for (var i = 0; i < length; i++)
        {
            var av = i < a.Count ? a[i] : 0;
            var bv = i < b.Count ? b[i] : 0;
            var cmp = av.CompareTo(bv);
            if (cmp != 0)
                return cmp;
        }
        return 0;
    }

    public static bool IsNewer(string? candidate, string? installed) => Compare(candidate, installed) > 0;

    public static IReadOnlyList<int> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var trimmed = text.Trim().TrimStart('v', 'V');
        var cut = trimmed.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0)
            trimmed = trimmed[..cut];
        var parts = new List<int>();
        foreach (var piece in trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var digits = new string(piece.TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var value))
                parts.Add(value);
        }
        return parts;
    }
}

public static class NvidiaDriverVersion
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim();
        if (Version.TryParse(trimmed, out var direct) && direct.Major >= 100 && direct.Minor < 100 && direct.Build < 0)
        {
            version = direct;
            return true;
        }

        var friendly = ToFriendly(trimmed);
        var head = friendly.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? friendly;
        if (!Version.TryParse(head.Count(c => c == '.') == 1 ? head : head, out var parsed))
        {
            if (head.Count(c => c == '.') == 1 && Version.TryParse(head + ".0", out parsed))
            {
                version = parsed;
                return true;
            }
            return false;
        }
        if (parsed.Build < 0 && Version.TryParse(head + ".0", out var padded))
            parsed = padded;
        version = parsed;
        return parsed.Major > 0;
    }

    public static string ToFriendly(string text)
    {
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 4 && parts[3].Length >= 5 && parts[3].All(char.IsDigit))
            return parts[3].Insert(parts[3].Length - 2, ".");
        if (parts.Length == 4 && parts[2].Length >= 2 && parts[3].Length == 4 && parts[2].All(char.IsDigit) && parts[3].All(char.IsDigit))
        {
            var combined = parts[2][^1] + parts[3];
            return combined.Insert(combined.Length - 2, ".");
        }
        return text.Trim();
    }

    public static int CompareTo(string? version, int major, int minor)
    {
        if (!TryParse(version, out var parsed))
            return 0;
        return parsed.CompareTo(new Version(major, minor));
    }
}

public sealed class WindowsProcessInspector : IProcessInspector
{
    public bool IsExecutableRunning(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;
        string full;
        try
        {
            full = Path.GetFullPath(executablePath);
        }
        catch
        {
            return false;
        }
        var name = Path.GetFileNameWithoutExtension(full);
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
        {
            try
            {
                var file = process.MainModule?.FileName;
                if (file != null && string.Equals(Path.GetFullPath(file), full, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                return true;
            }
            finally
            {
                process.Dispose();
            }
        }
        return false;
    }
}
