using System.Text.RegularExpressions;
using Nadlan.Core.Files;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Machine;

public sealed record ImporterPackage(string File, string Platform, string Title, string Hint, long Size, long UploadedUtcMs, string? Sha256);

public sealed record ImporterVersion(string Version, long UploadedUtcMs, IReadOnlyList<ImporterPackage> Packages, string? Readme);

/// <summary>
/// The KAEK importer (polygon acquisition app) for the admin page's Downloads tab. release/2-upload-importer-s3.ps1 puts each
/// built version into the app's own storage folder, {RootFolder}/_downloads/kaek-importer/{version}/, which the server's S3
/// role can already read. The page lists the versions; a download is a fresh 10-minute S3 link, so no link ever goes stale.
/// </summary>
public sealed partial class ImporterDownloads
{
    public const string Prefix = "_downloads/kaek-importer/";
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(10);

    private static readonly Dictionary<string, (string Title, string Hint, int Order)> Platforms = new()
    {
        ["windows"] = ("Windows", "Windows 10/11, 64-bit. Extract, then run Install.cmd (no admin rights needed).", 0),
        ["mac-apple-silicon"] = ("Mac with Apple chip", "M1-M4 Macs, macOS 13+. Unpack, drag to Applications, approve once in Privacy & Security.", 1),
        ["mac-intel"] = ("Mac with Intel chip", "Older Intel Macs, macOS 13+. Same steps as Apple chip.", 2),
    };

    // NadlanKaekImporter-1.2.0-windows.zip / -mac-intel.tar.gz / -README.txt (+ .sha256 next to each package)
    [GeneratedRegex(@"^NadlanKaekImporter-(?<v>\d{1,4}\.\d{1,4}\.\d{1,4})-(?<rest>[a-z-]+(\.zip|\.tar\.gz)|README\.txt)(?<sum>\.sha256)?$")]
    private static partial Regex FileName();

    private readonly IObjectStorage _storage;
    private readonly ILogger<ImporterDownloads> _log;

    public ImporterDownloads(IObjectStorage storage, ILogger<ImporterDownloads> log)
    {
        _storage = storage;
        _log = log;
    }

    public async Task<object> ListAsync(CancellationToken ct)
    {
        if (!_storage.IsConfigured)
        {
            return new { storageConfigured = false, prefix = Prefix, versions = Array.Empty<ImporterVersion>() };
        }

        // Keys are "{version}/{file}"; anything else in the folder (latest.json, stray files) is ignored.
        var files = (await _storage.ListAsync(Prefix, ct))
            .Select(o => (Object: o, Parts: o.Key[Prefix.Length..].Split('/')))
            .Where(x => x.Parts.Length == 2 && FileName().Match(x.Parts[1]) is { Success: true } m && m.Groups["v"].Value == x.Parts[0])
            .ToList();

        var versions = new List<ImporterVersion>();
        foreach (var group in files.GroupBy(f => f.Parts[0]))
        {
            var byName = group.ToDictionary(f => f.Parts[1], f => f.Object);
            var packages = new List<ImporterPackage>();
            foreach (var (name, obj) in byName.Where(p => !p.Key.EndsWith(".sha256", StringComparison.Ordinal) && !p.Key.EndsWith("README.txt", StringComparison.Ordinal)))
            {
                var platform = FileName().Match(name).Groups["rest"].Value.Replace(".tar.gz", "").Replace(".zip", "");
                var info = Platforms.TryGetValue(platform, out var p) ? p : (Title: platform, Hint: "", Order: 9);
                string? sha = null;
                if (byName.ContainsKey(name + ".sha256"))
                {
                    var text = await _storage.ReadTextAsync($"{Prefix}{group.Key}/{name}.sha256", 1024, ct);
                    sha = text?.Split(' ', 2)[0].Trim() is { Length: 64 } h && h.All(Uri.IsHexDigit) ? h.ToLowerInvariant() : null;
                }

                packages.Add(new ImporterPackage(name, platform, info.Title, info.Hint, obj.Size, obj.LastModifiedUtc.ToUnixTimeMilliseconds(), sha));
            }

            var readme = byName.Keys.FirstOrDefault(k => k.EndsWith("-README.txt", StringComparison.Ordinal));
            versions.Add(new ImporterVersion(group.Key, group.Max(f => f.Object.LastModifiedUtc).ToUnixTimeMilliseconds(),
                packages.OrderBy(p => Platforms.TryGetValue(p.Platform, out var i) ? i.Order : 9).ToList(), readme));
        }

        return new
        {
            storageConfigured = true,
            prefix = Prefix,
            versions = versions.OrderByDescending(v => Version.Parse(v.Version)).ToList(),
        };
    }

    /// <summary>A 10-minute download link for one file of one version (only names the upload script makes).</summary>
    public async Task<string> GetLinkAsync(string version, string? file, string user, CancellationToken ct)
    {
        var match = FileName().Match(file ?? "");
        if (!match.Success || match.Groups["v"].Value != version)
        {
            throw new EntityNotFoundException("Download", $"{version}/{file}");
        }

        var key = $"{Prefix}{version}/{file}";
        if (await _storage.GetObjectSizeAsync(key, ct) is null)
        {
            throw new EntityNotFoundException("Download", $"{version}/{file}");
        }

        _log.LogInformation("Downloads: {User} downloaded {File}", user, file);
        return _storage.GetAttachmentUrl(key, file!, LinkLifetime);
    }
}
