using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Nadlan.Core.Validation;
using Nadlan.Host.Health;

namespace Nadlan.Host.Machine;

public sealed record WebFileEntry(
    string Name,
    string Kind,
    long? Size,
    long? ModifiedUtcMs,
    bool Patched,
    bool InRelease,
    IReadOnlyList<string> Encodings,
    bool Editable);

public sealed record WebListing(string Root, string Path, string? Parent, bool Writable, IReadOnlyList<WebFileEntry> Entries);

public sealed record WebPatch(string Path, long Size, long ModifiedUtcMs, bool InRelease, string? By, string? Sha256);

public sealed record WebPatchResult(string Path, long Size, long? BrotliSize, long? GzipSize, bool InRelease);

/// <summary>
/// The web files (wwwroot) and their hot patches. A release is installed read-only (root-owned, deploy.sh), so a patch never
/// touches it: patched files live in an overlay folder that is served IN FRONT of the release's wwwroot:
///   {state}/hotfix/{release}/wwwroot/...   the patched files, each with its .br and .gz made here
///   {state}/hotfix/{release}/log.jsonl     who patched or reverted what, when (outside the served folder)
/// {state} is the service's StateDirectory (/var/lib/nadlan) or App_Data on a developer PC. The overlay belongs to one
/// release: the next deploy starts clean (commit the fix to git too). Revert = delete the overlay copy.
/// </summary>
public sealed class WebFiles
{
    public const string WebRoot = "web";
    public const string AppRoot = "app";
    public const long MaxUploadBytes = 10 * 1024 * 1024;
    private const long MaxEditableBytes = 2 * 1024 * 1024;

    /// <summary>Text files: served pre-compressed, so a patch makes their .br and .gz.</summary>
    public static readonly HashSet<string> CompressibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".css", ".js", ".mjs", ".json", ".svg", ".txt", ".map", ".xml",
    };

    private static readonly HashSet<string> AllowedExtensions = new(CompressibleExtensions, StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".ico", ".woff", ".woff2", ".ttf",
    };

    private static readonly (string Encoding, string Suffix)[] Compressed = { ("br", ".br"), ("gzip", ".gz") };

    private readonly string _baseRoot;
    private readonly string _overlayRoot;
    private readonly string _patchRoot;
    private readonly string _appRoot;
    private readonly ILogger<WebFiles> _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public WebFiles(IWebHostEnvironment env, ILogger<WebFiles> log)
    {
        _log = log;
        _baseRoot = Path.GetFullPath(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"));
        var state = (Environment.GetEnvironmentVariable("STATE_DIRECTORY") ?? "").Split(':')[0];
        if (string.IsNullOrWhiteSpace(state))
        {
            state = Path.Combine(env.ContentRootPath, "App_Data");
        }

        var release = string.Concat(HealthEndpoints.Version.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '_' ? c : '_'));
        _patchRoot = Path.GetFullPath(Path.Combine(state, "hotfix", release));
        _overlayRoot = Path.Combine(_patchRoot, "wwwroot");
        Directory.CreateDirectory(_overlayRoot);

        // On the server the content root is /opt/nadlan/current/app; its parent has RELEASE, server/ and dbtool/ too.
        var parent = Path.GetDirectoryName(Path.GetFullPath(env.ContentRootPath).TrimEnd(Path.DirectorySeparatorChar));
        _appRoot = parent is not null && File.Exists(Path.Combine(parent, "RELEASE")) ? parent : Path.GetFullPath(env.ContentRootPath);
    }

    public string OverlayRoot => _overlayRoot;

    /// <summary>What the site serves: a patched file first, else the release's own.</summary>
    public IFileProvider CreateServedFiles(IFileProvider releaseFiles) => new CompositeFileProvider(new PhysicalFileProvider(_overlayRoot), releaseFiles);

    public object Describe() => new
    {
        release = HealthEndpoints.Version,
        roots = new[]
        {
            new { key = WebRoot, name = "Web files (live)", path = _baseRoot, writable = true },
            new { key = AppRoot, name = "Release folder (read-only)", path = _appRoot, writable = false },
        },
        patchFolder = _overlayRoot,
        maxUploadBytes = MaxUploadBytes,
        extensions = AllowedExtensions.Order(StringComparer.OrdinalIgnoreCase),
    };

    // ------------------------------------------------------------------ browsing

    public WebListing List(string root, string? relPath)
    {
        var segments = Segments(relPath);
        var path = string.Join('/', segments);
        var parent = segments.Length == 0 ? null : string.Join('/', segments[..^1]);
        if (root == AppRoot)
        {
            var dir = Resolve(_appRoot, segments);
            if (!Directory.Exists(dir))
            {
                throw NotFound(path);
            }

            var entries = new DirectoryInfo(dir).EnumerateFileSystemInfos()
                .Select(i => i is DirectoryInfo
                    ? new WebFileEntry(i.Name, "dir", null, Ms(i.LastWriteTimeUtc), false, true, [], false)
                    : new WebFileEntry(i.Name, "file", ((FileInfo)i).Length, Ms(i.LastWriteTimeUtc), false, true, [], false));
            return new WebListing(root, path, parent, false, Sort(entries));
        }

        RequireWebRoot(root);
        var baseDir = Resolve(_baseRoot, segments);
        var overlayDir = Resolve(_overlayRoot, segments);
        if (!Directory.Exists(baseDir) && !Directory.Exists(overlayDir))
        {
            throw NotFound(path);
        }

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { baseDir, overlayDir }.Where(Directory.Exists))
        {
            names.UnionWith(Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).OfType<string>());
        }

        var list = new List<WebFileEntry>();
        foreach (var name in names)
        {
            // app.js.br / app.js.gz are shown as app.js's encodings, not as files of their own.
            if (CompressedSuffix(name) is { } suffix && names.Contains(name[..^suffix.Length]))
            {
                continue;
            }

            var inRelease = Path.Exists(Path.Combine(baseDir, name));
            if (Directory.Exists(Path.Combine(baseDir, name)) || Directory.Exists(Path.Combine(overlayDir, name)))
            {
                list.Add(new WebFileEntry(name, "dir", null, null, false, inRelease, [], false));
                continue;
            }

            var patched = File.Exists(Path.Combine(overlayDir, name));
            var file = new FileInfo(Path.Combine(patched ? overlayDir : baseDir, name));
            list.Add(new WebFileEntry(name, "file", file.Length, Ms(file.LastWriteTimeUtc), patched, inRelease,
                Compressed.Where(c => ServedSibling(file.FullName, c.Suffix)).Select(c => c.Encoding).ToList(),
                IsEditable(name, file.Length)));
        }

        return new WebListing(root, path, parent, true, Sort(list));
    }

    /// <summary>A file to download: the one the site serves (web) or the release's (app).</summary>
    public string ResolveDownload(string root, string? relPath)
    {
        var segments = Segments(relPath);
        if (segments.Length == 0)
        {
            throw NotFound("");
        }

        var full = root == AppRoot ? Resolve(_appRoot, segments) : ServedPath(root, segments);
        return File.Exists(full) ? full : throw NotFound(string.Join('/', segments));
    }

    public (string Text, bool Patched, long ModifiedUtcMs) ReadText(string? relPath)
    {
        var segments = Segments(relPath);
        var full = ServedPath(WebRoot, segments);
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            throw NotFound(string.Join('/', segments));
        }

        if (!IsEditable(info.Name, info.Length))
        {
            throw new DomainValidationException("FILE_NOT_EDITABLE", "Only text files (html, css, js, json, svg...) up to 2 MB can be edited here.");
        }

        return (File.ReadAllText(full), full.StartsWith(_overlayRoot, StringComparison.Ordinal), Ms(info.LastWriteTimeUtc));
    }

    public IReadOnlyList<WebPatch> ListPatches()
    {
        var log = ReadLog();
        return Directory.EnumerateFiles(_overlayRoot, "*", SearchOption.AllDirectories)
            .Where(f => CompressedSuffix(f) is null || !File.Exists(f[..^CompressedSuffix(f)!.Length]))
            .Select(f =>
            {
                var rel = Path.GetRelativePath(_overlayRoot, f).Replace('\\', '/');
                var info = new FileInfo(f);
                log.TryGetValue(rel, out var entry);
                return new WebPatch(rel, info.Length, Ms(info.LastWriteTimeUtc), File.Exists(Path.Combine(_baseRoot, rel)), entry.By, entry.Sha256);
            })
            .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ------------------------------------------------------------------ patching

    /// <summary>
    /// Puts a file into the overlay (new or replacing the served one) and, for text files, its .br and .gz. Each file goes to
    /// a temp file first and is then moved in, so the site never serves half a file. The plain file moves in first and its
    /// compressed copies after, carrying its time stamp: until they arrive, the older copies don't match and the plain file
    /// is served (see <see cref="ServedSibling"/>).
    /// </summary>
    public async Task<WebPatchResult> PatchAsync(string? relDir, string fileName, Stream content, string user, CancellationToken ct)
    {
        var dirSegments = Segments(relDir);
        var name = CheckFileName(fileName);
        var baseDir = Resolve(_baseRoot, dirSegments);
        var overlayDir = Resolve(_overlayRoot, dirSegments);
        if (!Directory.Exists(baseDir) && !Directory.Exists(overlayDir))
        {
            throw new DomainValidationException("FOLDER_NOT_FOUND", "That folder doesn't exist in the web files.");
        }

        var rel = string.Join('/', dirSegments.Append(name));
        var tempDir = Path.Combine(_patchRoot, "tmp");
        Directory.CreateDirectory(tempDir);
        var temp = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
        var tempFiles = new List<string> { temp };
        await _writeLock.WaitAsync(ct);
        try
        {
            long size;
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                await CopyLimitedAsync(content, target, ct);
                size = target.Length;
            }

            if (size == 0)
            {
                throw new DomainValidationException("FILE_EMPTY", "The file is empty.");
            }

            string sha;
            await using (var read = File.OpenRead(temp))
            {
                sha = Convert.ToHexString(await SHA256.HashDataAsync(read, ct)).ToLowerInvariant();
            }

            var compress = CompressibleExtensions.Contains(Path.GetExtension(name));
            long? brSize = null, gzSize = null;
            if (compress)
            {
                brSize = await CompressAsync(temp, temp + ".br", s => new BrotliStream(s, CompressionLevel.SmallestSize), ct);
                gzSize = await CompressAsync(temp, temp + ".gz", s => new GZipStream(s, CompressionLevel.SmallestSize), ct);
                tempFiles.Add(temp + ".br");
                tempFiles.Add(temp + ".gz");
            }

            Directory.CreateDirectory(overlayDir);
            var final = Path.Combine(overlayDir, name);
            File.Move(temp, final, overwrite: true);
            var stamp = File.GetLastWriteTimeUtc(final);
            foreach (var (_, suffix) in Compressed)
            {
                if (compress)
                {
                    File.SetLastWriteTimeUtc(temp + suffix, stamp);
                    File.Move(temp + suffix, final + suffix, overwrite: true);
                }
                else
                {
                    File.Delete(final + suffix); // stale copies of an earlier text upload under this name
                }
            }

            var inRelease = File.Exists(Path.Combine(baseDir, name));
            AppendLog(new { utc = DateTimeOffset.UtcNow.ToString("o"), user, action = "patch", path = rel, size, sha256 = sha });
            _log.LogWarning("Web files: {User} hot-patched {Path} ({Size} bytes, sha256 {Sha})", user, rel, size, sha);
            return new WebPatchResult(rel, size, brSize, gzSize, inRelease);
        }
        finally
        {
            _writeLock.Release();
            foreach (var f in tempFiles)
            {
                TryDelete(f);
            }
        }
    }

    /// <summary>Removes a patch: the release's own file is served again (or, for a file the release doesn't have, it is gone).</summary>
    public async Task<bool> RevertAsync(string? relPath, string user, CancellationToken ct)
    {
        var segments = Segments(relPath);
        var overlay = Resolve(_overlayRoot, segments);
        var rel = string.Join('/', segments);
        await _writeLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(overlay))
            {
                throw new DomainValidationException("NOT_PATCHED", $"{rel} is not patched; release files can't be deleted from here.");
            }

            File.Delete(overlay);
            foreach (var (_, suffix) in Compressed)
            {
                File.Delete(overlay + suffix);
            }

            AppendLog(new { utc = DateTimeOffset.UtcNow.ToString("o"), user, action = "revert", path = rel });
            _log.LogWarning("Web files: {User} reverted the patch of {Path}", user, rel);
            return File.Exists(Resolve(_baseRoot, segments));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ------------------------------------------------------------------ serving pre-compressed copies

    /// <summary>
    /// Whether "file + suffix" (.br/.gz) may be served instead of the file: it must sit next to it, in the same layer. A
    /// release's copies were built together with it and never change. A patch's copies must be at least as new as the patched
    /// file: otherwise they belong to an earlier patch and the plain file is served.
    /// </summary>
    public bool ServedSibling(string physicalPath, string suffix)
    {
        var sibling = new FileInfo(physicalPath + suffix);
        if (!sibling.Exists)
        {
            return false;
        }

        return !physicalPath.StartsWith(_overlayRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || sibling.LastWriteTimeUtc >= File.GetLastWriteTimeUtc(physicalPath);
    }

    public static string? CompressedSuffix(string name) =>
        Compressed.Select(c => c.Suffix).FirstOrDefault(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)
            && CompressibleExtensions.Contains(Path.GetExtension(name[..^s.Length])));

    // ------------------------------------------------------------------ helpers

    private string ServedPath(string root, string[] segments)
    {
        RequireWebRoot(root);
        var overlay = Resolve(_overlayRoot, segments);
        return File.Exists(overlay) ? overlay : Resolve(_baseRoot, segments);
    }

    private static void RequireWebRoot(string root)
    {
        if (root != WebRoot)
        {
            throw new DomainValidationException("ROOT_UNKNOWN", $"Unknown folder '{root}'. Use '{WebRoot}' or '{AppRoot}'.");
        }
    }

    /// <summary>"js/x.js" -> ["js", "x.js"]; anything that could leave the root ("..", "C:", "\\server") is refused.</summary>
    private static string[] Segments(string? relPath)
    {
        var segments = (relPath ?? "").Split('/', '\\').Where(s => s.Length > 0).ToArray();
        foreach (var s in segments)
        {
            if (s is "." or ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || s.Contains(':') || s.Any(char.IsControl))
            {
                throw new DomainValidationException("PATH_INVALID", "That path is not allowed.");
            }
        }

        return segments;
    }

    private static string Resolve(string root, string[] segments)
    {
        var full = Path.GetFullPath(Path.Combine(new[] { root }.Concat(segments).ToArray()));
        return full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full
            : throw new DomainValidationException("PATH_INVALID", "That path is not allowed.");
    }

    private static string CheckFileName(string fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/')).Trim();
        if (name.Length is 0 or > 120 || name.StartsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Any(char.IsControl)
            || name.Contains(':'))
        {
            throw new DomainValidationException("FILE_NAME_INVALID", "That file name is not allowed.");
        }

        if (CompressedSuffix(name) is not null)
        {
            throw new DomainValidationException("FILE_NAME_INVALID", "Upload the plain file; its .br and .gz are made on the server.");
        }

        return AllowedExtensions.Contains(Path.GetExtension(name))
            ? name
            : throw new DomainValidationException("FILE_TYPE_NOT_ALLOWED",
                $"Web files can be {string.Join(", ", AllowedExtensions.Order(StringComparer.OrdinalIgnoreCase))}.");
    }

    private static bool IsEditable(string name, long size) => size <= MaxEditableBytes && CompressibleExtensions.Contains(Path.GetExtension(name));

    private static async Task CopyLimitedAsync(Stream source, Stream target, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxUploadBytes)
            {
                throw new DomainValidationException("FILE_TOO_LARGE", $"Web files can be at most {MaxUploadBytes / 1024 / 1024} MB.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static async Task<long> CompressAsync(string source, string target, Func<Stream, Stream> wrap, CancellationToken ct)
    {
        await using (var input = File.OpenRead(source))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
        await using (var compressor = wrap(output))
        {
            await input.CopyToAsync(compressor, ct);
        }

        return new FileInfo(target).Length;
    }

    private string LogPath => Path.Combine(_patchRoot, "log.jsonl");

    private void AppendLog(object entry)
    {
        try
        {
            File.AppendAllText(LogPath, JsonSerializer.Serialize(entry) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Web files: patch log not written");
        }
    }

    /// <summary>Latest "patch" entry per path (who and which content), from the release's patch log.</summary>
    private Dictionary<string, (string? By, string? Sha256)> ReadLog()
    {
        var result = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(LogPath))
        {
            return result;
        }

        foreach (var line in File.ReadLines(LogPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (e.TryGetProperty("action", out var a) && a.GetString() == "patch" && e.TryGetProperty("path", out var p) && p.GetString() is { } path)
                {
                    result[path] = (e.TryGetProperty("user", out var u) ? u.GetString() : null, e.TryGetProperty("sha256", out var s) ? s.GetString() : null);
                }
            }
            catch (JsonException)
            {
                // a torn line from a crash: skip it
            }
        }

        return result;
    }

    private static IReadOnlyList<WebFileEntry> Sort(IEnumerable<WebFileEntry> entries)
        => entries.OrderBy(e => e.Kind == "dir" ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static EntityNotFoundException NotFound(string path) => new("File", $"'{path}'"); // FILE_NOT_FOUND

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a temp file; the next patch's tmp folder tolerates leftovers
        }
    }
}
