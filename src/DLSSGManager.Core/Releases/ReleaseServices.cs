using System.IO.Compression;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using DLSSGManager.Core.Abstractions;
using DLSSGManager.Core.Infrastructure;
using DLSSGManager.Core.Models;

namespace DLSSGManager.Core.Releases;

public static class DownloadPolicy
{
    public static void EnsureOfficial(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Downloads must use HTTPS.");
        var host = uri.IdnHost;
        var allowed = host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                      host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
                      host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                      host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        if (!allowed)
            throw new InvalidOperationException($"Refusing a download host that is not GitHub: {host}");
    }
}

public sealed class GitHubRateLimitException : Exception
{
    public GitHubRateLimitException(string message, DateTimeOffset? reset) : base(message) => Reset = reset;
    public DateTimeOffset? Reset { get; }
}

public static class GitHubReleaseParser
{
    public static IReadOnlyList<ModRelease> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The GitHub releases response was not a list.");
        var releases = new List<ModRelease>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                continue;
            var tag = item.GetProperty("tag_name").GetString() ?? "";
            var assets = new List<ReleaseAsset>();
            if (item.TryGetProperty("assets", out var assetArray))
            {
                foreach (var asset in assetArray.EnumerateArray())
                {
                    var download = asset.GetProperty("browser_download_url").GetString() ?? "";
                    assets.Add(new ReleaseAsset
                    {
                        Name = asset.GetProperty("name").GetString() ?? "",
                        DownloadUrl = download,
                        Size = asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                        Sha256 = ReadDigest(asset)
                    });
                }
            }
            DateTimeOffset? published = null;
            if (item.TryGetProperty("published_at", out var publishedAt) && publishedAt.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(publishedAt.GetString(), out var parsed))
                published = parsed;
            var release = new ModRelease
            {
                Tag = tag.TrimStart('v', 'V'),
                Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? tag : tag,
                Body = item.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                PublishedAt = published,
                Prerelease = item.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                HtmlUrl = item.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "",
                Assets = assets
            };
            releases.Add(new ModRelease
            {
                Tag = release.Tag,
                Name = release.Name,
                Body = release.Body,
                PublishedAt = release.PublishedAt,
                Prerelease = release.Prerelease,
                HtmlUrl = release.HtmlUrl,
                Assets = assets,
                PreferredPackage = SelectPackage(release)
            });
        }
        return releases;
    }

    public static ReleaseAsset? SelectPackage(ModRelease release)
    {
        var zips = release.Assets.Where(asset =>
            asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            asset.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)).ToList();
        return zips.FirstOrDefault(asset =>
                   asset.Name.Contains("dlssg-release", StringComparison.OrdinalIgnoreCase) &&
                   asset.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) &&
                   !Regex.IsMatch(asset.Name, @"-310\.\d", RegexOptions.IgnoreCase))
               ?? zips.FirstOrDefault(asset => asset.Name.Contains("x64", StringComparison.OrdinalIgnoreCase))
               ?? zips.FirstOrDefault();
    }

    public static ModRelease? Choose(IEnumerable<ModRelease> releases, bool includePrerelease, string? pinnedTag)
    {
        var list = releases.ToList();
        if (!string.IsNullOrWhiteSpace(pinnedTag))
        {
            var pin = pinnedTag.Trim().TrimStart('v', 'V');
            return list.FirstOrDefault(release => release.Tag.Equals(pin, StringComparison.OrdinalIgnoreCase));
        }
        return list
            .Where(release => includePrerelease || !release.Prerelease)
            .OrderByDescending(release => release.Tag, Comparer<string>.Create(ModVersion.Compare))
            .FirstOrDefault();
    }

    private static string? ReadDigest(JsonElement asset)
    {
        if (!asset.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String)
            return null;
        var text = digest.GetString() ?? "";
        const string prefix = "sha256:";
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text[prefix.Length..] : null;
    }
}

public sealed class HttpReleaseClient : IReleaseClient
{
    private readonly HttpClient _http;

    public HttpReleaseClient(HttpClient http) => _http = http;

    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        DownloadPolicy.EnsureOfficial(new Uri(url));
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw RateLimit(response);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadAsync(ReleaseAsset asset, string destination, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var current = new Uri(asset.DownloadUrl);
        for (var hop = 0; hop < 5; hop++)
        {
            DownloadPolicy.EnsureOfficial(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var next = response.Headers.Location ?? throw new InvalidOperationException("The download redirected without a location.");
                current = next.IsAbsoluteUri ? next : new Uri(current, next);
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw RateLimit(response);
            response.EnsureSuccessStatusCode();
            var partial = destination + ".partial";
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = File.Create(partial))
            {
                var buffer = new byte[81920];
                var total = response.Content.Headers.ContentLength ?? asset.Size;
                long readTotal = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    readTotal += read;
                    if (total > 0)
                        progress?.Report(readTotal * 100d / total);
                }
            }
            var hash = Hashing.Sha256File(partial);
            if (!string.IsNullOrWhiteSpace(asset.Sha256) && !hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidOperationException("The download SHA-256 does not match the digest published with the GitHub release.");
            }
            File.Move(partial, destination, overwrite: true);
            return;
        }
        throw new InvalidOperationException("The download redirected too many times.");
    }

    private static GitHubRateLimitException RateLimit(HttpResponseMessage response)
    {
        DateTimeOffset? reset = null;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) && long.TryParse(values.FirstOrDefault(), out var unix))
            reset = DateTimeOffset.FromUnixTimeSeconds(unix);
        var when = reset?.ToLocalTime().ToString("g") ?? "later";
        return new GitHubRateLimitException($"GitHub rate limit reached. Try again after {when}. Cached data will be used when it exists.", reset);
    }
}

public static class ZipExtractor
{
    public static void Extract(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
            {
                if (PathSafety.IsUnsafeRelative(entry.FullName.TrimEnd('/')))
                    throw new InvalidOperationException("The archive contains an unsafe directory entry.");
                Directory.CreateDirectory(PathSafety.CombineUnderRoot(destination, entry.FullName.TrimEnd('/')));
                continue;
            }
            if (PathSafety.IsUnsafeRelative(entry.FullName))
                throw new InvalidOperationException($"The archive contains an unsafe path: {entry.FullName}");
            var target = PathSafety.CombineUnderRoot(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }
}

public static class PackageInspector
{
    public static InstallationManifest Inspect(string extractedDirectory, string releaseTag)
    {
        var root = FindRoot(extractedDirectory);
        var files = SafeFiles(root);
        var hasProxy = files.Any(path => Path.GetFileName(path).Equals("version.dll", StringComparison.OrdinalIgnoreCase) && IsNearRoot(root, path));
        var hasIni = files.Any(path => Path.GetFileName(path).Equals("dlssg_sm86.ini", StringComparison.OrdinalIgnoreCase) && IsNearRoot(root, path));
        var payload = Path.Combine(root, "dlssg_sm86");
        var hasRuntime = File.Exists(Path.Combine(payload, "runtime", "nvngx_dlssg.dll"));
        var hasBackend = Directory.Exists(Path.Combine(payload, "backend")) && Directory.EnumerateFiles(Path.Combine(payload, "backend"), "*.dll").Any();
        var hasKernels = Directory.Exists(Path.Combine(payload, "kernels")) && Directory.EnumerateFiles(Path.Combine(payload, "kernels"), "*.dll").Any();
        var manifestFiles = new List<ManifestFile>();
        if (hasProxy && hasIni && hasRuntime && hasBackend && hasKernels)
        {
            AddIfExists(manifestFiles, root, "version.dll", "version.dll", true, "proxy");
            AddIfExists(manifestFiles, root, "dlssg_sm86.ini", "dlssg_sm86.ini", true, "config");
            AddTree(manifestFiles, root, payload, "dlssg_sm86");
            AddAlternatives(manifestFiles, root);
            return Build(releaseTag, root, "SingleProxyPayload", true, null, 5, "310.9", true, manifestFiles);
        }

        if (hasProxy && hasIni && !hasRuntime)
        {
            AddIfExists(manifestFiles, root, "version.dll", "version.dll", true, "proxy");
            AddIfExists(manifestFiles, root, "dlssg_sm86.ini", "dlssg_sm86.ini", true, "config");
            return Build(releaseTag, root, "EmbeddedProxy", false,
                "This package looks like a pre-0.4 embedded proxy build. Confirm the file list before installing. Only version.dll and dlssg_sm86.ini are selected.",
                3, "", false, manifestFiles);
        }

        return new InstallationManifest
        {
            Id = releaseTag + "-unknown",
            ReleaseTag = releaseTag,
            Layout = "Unknown",
            Confident = false,
            Uncertainty = "The archive does not contain the documented 0.4.0 set (version.dll, dlssg_sm86.ini, and dlssg_sm86\\runtime, backend, and kernels) or a recognizable older proxy package.",
            PackageRoot = root,
            Files = manifestFiles,
            MaxGeneratedFrames = 3
        };
    }

    private static InstallationManifest Build(string tag, string root, string layout, bool confident, string? uncertainty, int maxFrames, string runtime, bool presetC, List<ManifestFile> files)
    {
        var hasSr = File.Exists(Path.Combine(root, "dlssg_sm86", "sr", "nvngx_dlss.dll")) || File.Exists(Path.Combine(root, "dlssg_sm86", "sr", "sr_kernels.dll"));
        var hasXess = File.Exists(Path.Combine(root, "dlssg_sm86", "xefg", "xefg_kernels.dll"));
        var model = runtime;
        var runtimeDll = Path.Combine(root, "dlssg_sm86", "runtime", "nvngx_dlssg.dll");
        if (File.Exists(runtimeDll))
        {
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(runtimeDll).FileVersion ?? "";
            if (!string.IsNullOrWhiteSpace(version))
                model = version;
            presetC = version.StartsWith("310.9.16", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("0.4", StringComparison.OrdinalIgnoreCase);
            maxFrames = version.StartsWith("310.1", StringComparison.OrdinalIgnoreCase) ? 3 : 5;
        }
        return new InstallationManifest
        {
            Id = $"{tag}-{layout}",
            ReleaseTag = tag,
            Layout = layout,
            Confident = confident,
            Uncertainty = uncertainty,
            MaxGeneratedFrames = maxFrames,
            RuntimeModel = model,
            SupportsPresetC = presetC && confident,
            SupportsSuperResolution = hasSr,
            SupportsXessEngine = hasXess,
            SupportedArchitectures = ["SM75", "SM86", "SM89", "SM120"],
            PayloadDirectory = Directory.Exists(Path.Combine(root, "dlssg_sm86")) ? "dlssg_sm86" : null,
            PackageRoot = root,
            Files = files
        };
    }

    private static void AddTree(List<ManifestFile> files, string root, string directory, string destinationPrefix)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}logs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                continue;
            files.Add(new ManifestFile
            {
                PackageRelativePath = relative,
                DestinationRelativePath = relative,
                Required = relative.Contains($"{Path.DirectorySeparatorChar}runtime{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                           relative.Contains($"{Path.DirectorySeparatorChar}backend{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                           relative.Contains($"{Path.DirectorySeparatorChar}kernels{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase),
                Role = "payload"
            });
        }
    }

    private static void AddAlternatives(List<ManifestFile> files, string root)
    {
        var alternatives = Path.Combine(root, "alternatives");
        if (!Directory.Exists(alternatives))
            return;
        foreach (var name in ProxyNames.Order.Skip(1))
        {
            var path = Path.Combine(alternatives, name);
            if (File.Exists(path))
            {
                files.Add(new ManifestFile
                {
                    PackageRelativePath = Path.Combine("alternatives", name),
                    DestinationRelativePath = name,
                    Required = false,
                    Role = "alternative"
                });
            }
        }
    }

    private static void AddIfExists(List<ManifestFile> files, string root, string relative, string destination, bool required, string role)
    {
        if (File.Exists(Path.Combine(root, relative)))
        {
            files.Add(new ManifestFile
            {
                PackageRelativePath = relative,
                DestinationRelativePath = destination,
                Required = required,
                Role = role
            });
        }
    }

    private static string FindRoot(string extracted)
    {
        if (File.Exists(Path.Combine(extracted, "version.dll")) || File.Exists(Path.Combine(extracted, "dlssg_sm86.ini")))
            return extracted;
        var dirs = Directory.Exists(extracted) ? Directory.GetDirectories(extracted) : [];
        if (dirs.Length == 1 && (File.Exists(Path.Combine(dirs[0], "version.dll")) || File.Exists(Path.Combine(dirs[0], "dlssg_sm86.ini"))))
            return dirs[0];
        return extracted;
    }

    private static bool IsNearRoot(string root, string file) =>
        string.Equals(Path.GetDirectoryName(file)?.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static List<string> SafeFiles(string root)
    {
        if (!Directory.Exists(root))
            return [];
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(4000).ToList();
        }
        catch
        {
            return [];
        }
    }
}

public static class SignatureInspector
{
    public static SignatureInfo Inspect(string path)
    {
        if (!File.Exists(path))
            return new SignatureInfo { Message = "The proxy DLL is not in the package." };
        try
        {
            var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            var trusted = chain.Build(cert);
            var subject = cert.Subject;
            return new SignatureInfo
            {
                IsSigned = true,
                IsTrusted = trusted,
                Subject = subject,
                Message = trusted
                    ? $"Signed by {subject}."
                    : $"Signed by {subject}. The certificate is not in the Windows trusted store. Upstream DLSSG for SM86 uses a self-signed certificate (CN=DLSSG for SM86). SmartScreen may say the publisher is unknown. Do not disable Microsoft Defender."
            };
        }
        catch
        {
            return new SignatureInfo
            {
                IsSigned = false,
                Message = "The proxy DLL has no Authenticode signature. Installation can continue only after you confirm that, and Microsoft Defender should stay enabled."
            };
        }
    }
}

public sealed class ReleaseService : IReleaseService
{
    private readonly IReleaseClient _client;
    private readonly AppDataPaths _paths;
    private readonly Func<AppSettings> _settings;

    public ReleaseService(IReleaseClient client, AppDataPaths paths, Func<AppSettings> settings)
    {
        _client = client;
        _paths = paths;
        _settings = settings;
    }

    public async Task<ReleaseCheckResult> CheckAsync(bool includePrerelease, string? pinnedTag, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var cacheFile = Path.Combine(CacheRoot(settings), "releases.json");
        try
        {
            var modJson = await _client.GetStringAsync($"https://api.github.com/repos/{settings.ModRepository}/releases?per_page=20", cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            await File.WriteAllTextAsync(cacheFile, modJson, cancellationToken).ConfigureAwait(false);
            var releases = GitHubReleaseParser.Parse(modJson);
            var manager = await ReadManagerAsync(settings, cancellationToken).ConfigureAwait(false);
            return new ReleaseCheckResult
            {
                Releases = releases,
                Selected = GitHubReleaseParser.Choose(releases, includePrerelease, pinnedTag),
                ManagerLatest = manager.Release,
                ManagerMessage = manager.Message,
                ManagerUpdateAvailable = manager.Update
            };
        }
        catch (Exception ex) when (ex is GitHubRateLimitException or HttpRequestException or TaskCanceledException)
        {
            if (!File.Exists(cacheFile))
                throw;
            var cached = GitHubReleaseParser.Parse(await File.ReadAllTextAsync(cacheFile, cancellationToken).ConfigureAwait(false));
            return new ReleaseCheckResult
            {
                Releases = cached,
                Selected = GitHubReleaseParser.Choose(cached, includePrerelease, pinnedTag),
                FromCache = true,
                Warning = "GitHub could not be reached. Showing cached mod releases. " + ex.Message,
                ManagerMessage = "Manager update status is unavailable until GitHub can be reached."
            };
        }
    }

    public async Task<PreparedPackage> PrepareAsync(ModRelease release, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var asset = release.PreferredPackage ?? throw new InvalidOperationException($"{release.Tag} has no Windows zip asset on the GitHub release.");
        var settings = _settings();
        var folder = Path.Combine(CacheRoot(settings), release.Tag);
        Directory.CreateDirectory(folder);
        var archive = Path.Combine(folder, asset.Name);
        if (!File.Exists(archive) || (asset.Sha256 != null && !Hashing.Sha256File(archive).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)))
            await _client.DownloadAsync(asset, archive, progress, cancellationToken).ConfigureAwait(false);
        else if (asset.Sha256 == null)
            AppLog.Info($"Using cached {asset.Name} without a published checksum.");
        var extracted = Path.Combine(folder, "extracted");
        if (Directory.Exists(extracted))
            Directory.Delete(extracted, recursive: true);
        ZipExtractor.Extract(archive, extracted);
        var manifest = PackageInspector.Inspect(extracted, release.Tag);
        var proxy = Directory.EnumerateFiles(manifest.PackageRoot, "version.dll", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var signature = proxy == null ? new SignatureInfo { Message = "version.dll was not found at the package root." } : SignatureInspector.Inspect(proxy);
        return new PreparedPackage
        {
            Release = release,
            ArchivePath = archive,
            ExtractedPath = extracted,
            Manifest = manifest,
            Signature = signature,
            Sha256 = Hashing.Sha256File(archive)
        };
    }

    private async Task<(ModRelease? Release, string Message, bool Update)> ReadManagerAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var json = await _client.GetStringAsync($"https://api.github.com/repos/{settings.ManagerRepository}/releases?per_page=10", cancellationToken).ConfigureAwait(false);
            var releases = GitHubReleaseParser.Parse(json);
            var latest = GitHubReleaseParser.Choose(releases, settings.IncludePrerelease, null);
            if (latest == null)
                return (null, "No DLSSG Manager releases have been published.", false);
            var current = typeof(ReleaseService).Assembly.GetName().Version?.ToString() ?? "1.0.0";
            var update = ModVersion.IsNewer(latest.Tag, current);
            return (latest, update
                ? $"DLSSG Manager {latest.Tag} is published. This copy is {current}. Open the release page to download it. The app does not replace its own executable."
                : $"DLSSG Manager {current} is the newest published release.", update);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, "No DLSSG Manager releases have been published.", false);
        }
        catch (Exception ex)
        {
            return (null, "Manager releases could not be checked. " + ex.Message, false);
        }
    }

    private string CacheRoot(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.CacheDirectory) ? _paths.DefaultCache : settings.CacheDirectory;
}

public static class InstallPlanner
{
    public static IReadOnlyList<PlannedFile> Plan(InstallationManifest manifest, string proxyFileName, string? configurationText, IEnumerable<string>? extraDeletes)
    {
        if (!ProxyNames.IsProxy(proxyFileName))
            throw new InvalidOperationException($"{proxyFileName} is not one of the documented proxy names.");
        var plan = new List<PlannedFile>();
        foreach (var file in manifest.Files)
        {
            if (file.Role == "alternative" && !file.DestinationRelativePath.Equals(proxyFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (file.Role == "proxy" && !file.DestinationRelativePath.Equals(proxyFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (file.Role == "config" && configurationText != null)
            {
                plan.Add(new PlannedFile
                {
                    Content = configurationText,
                    DestinationRelativePath = file.DestinationRelativePath,
                    IsConfiguration = true
                });
                continue;
            }
            plan.Add(new PlannedFile
            {
                SourcePath = Path.Combine(manifest.PackageRoot, file.PackageRelativePath),
                DestinationRelativePath = file.DestinationRelativePath,
                IsConfiguration = file.Role == "config"
            });
        }
        if (proxyFileName != "version.dll" && !plan.Any(file => file.DestinationRelativePath.Equals(proxyFileName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"The package does not contain the alternative proxy {proxyFileName}.");
        foreach (var extra in extraDeletes ?? [])
        {
            if (!plan.Any(file => file.DestinationRelativePath.Equals(extra, StringComparison.OrdinalIgnoreCase)))
            {
                plan.Add(new PlannedFile
                {
                    DestinationRelativePath = extra,
                    DeleteOnly = true
                });
            }
        }
        return plan;
    }
}
