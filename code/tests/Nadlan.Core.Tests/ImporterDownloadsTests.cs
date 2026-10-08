using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nadlan.Core.Files;
using Nadlan.Core.Validation;
using Nadlan.Host.Machine;

namespace Nadlan.Core.Tests;

/// <summary>Admin page, Downloads tab: which stored files count as importer versions, and which may be downloaded.</summary>
public class ImporterDownloadsTests
{
    private sealed class FakeStorage : IObjectStorage
    {
        public readonly Dictionary<string, (long Size, string? Text)> Objects = new();
        public bool IsConfigured => true;
        public Task<string> StartMultipartUploadAsync(string key, string contentType, string contentDisposition, CancellationToken ct = default) => throw new NotSupportedException();
        public string GetPartUploadUrl(string key, string uploadId, int partNumber, TimeSpan lifetime) => throw new NotSupportedException();
        public Task<IReadOnlyList<UploadedPart>?> ListUploadedPartsAsync(string key, string uploadId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CompleteMultipartUploadAsync(string key, string uploadId, IReadOnlyList<UploadedPart> parts, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteObjectAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long?> GetObjectSizeAsync(string key, CancellationToken ct = default) => Task.FromResult(Objects.TryGetValue(key, out var o) ? o.Size : (long?)null);

        public Task<IReadOnlyList<StoredObject>> ListAsync(string prefix, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StoredObject>>(Objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(o => new StoredObject(o.Key, o.Value.Size, DateTimeOffset.UnixEpoch.AddDays(o.Value.Size))).ToList());

        public Task<string?> ReadTextAsync(string key, int maxBytes, CancellationToken ct = default)
            => Task.FromResult(Objects.TryGetValue(key, out var o) ? o.Text : null);

        public string GetAttachmentUrl(string key, string downloadName, TimeSpan lifetime) => $"https://s3.test/{key}?as={downloadName}";
    }

    private const string P = ImporterDownloads.Prefix;
    private static readonly string Hash = new('a', 64);

    private static (ImporterDownloads Downloads, FakeStorage Storage) Make()
    {
        var s = new FakeStorage();
        s.Objects[$"{P}1.0.0/NadlanKaekImporter-1.0.0-windows.zip"] = (100, null);
        s.Objects[$"{P}1.10.0/NadlanKaekImporter-1.10.0-mac-intel.tar.gz"] = (300, null);
        s.Objects[$"{P}1.10.0/NadlanKaekImporter-1.10.0-windows.zip"] = (200, null);
        s.Objects[$"{P}1.10.0/NadlanKaekImporter-1.10.0-windows.zip.sha256"] = (80, $"{Hash}  NadlanKaekImporter-1.10.0-windows.zip\n");
        s.Objects[$"{P}1.10.0/NadlanKaekImporter-1.10.0-README.txt"] = (10, null);
        s.Objects[$"{P}latest.json"] = (5, "{}");
        s.Objects[$"{P}1.10.0/notes.exe"] = (5, null);
        s.Objects[$"{P}2.0.0/NadlanKaekImporter-1.9.0-windows.zip"] = (5, null); // version folder and name disagree
        return (new ImporterDownloads(s, NullLogger<ImporterDownloads>.Instance), s);
    }

    [Fact]
    public async Task Lists_versions_newest_first_with_platforms_checksums_and_readme()
    {
        var (downloads, _) = Make();
        var json = JsonDocument.Parse(JsonSerializer.Serialize(await downloads.ListAsync(default))).RootElement;
        var versions = json.GetProperty("versions");

        Assert.Equal(new[] { "1.10.0", "1.0.0" }, versions.EnumerateArray().Select(v => v.GetProperty("Version").GetString()));
        var latest = versions[0];
        Assert.Equal("NadlanKaekImporter-1.10.0-README.txt", latest.GetProperty("Readme").GetString());
        var packages = latest.GetProperty("Packages").EnumerateArray().ToList();
        Assert.Equal(new[] { "windows", "mac-intel" }, packages.Select(p => p.GetProperty("Platform").GetString())); // Windows first
        Assert.Equal(Hash, packages[0].GetProperty("Sha256").GetString());
        Assert.Equal(JsonValueKind.Null, packages[1].GetProperty("Sha256").ValueKind);
    }

    [Fact]
    public async Task Gives_links_only_for_files_the_upload_script_makes()
    {
        var (downloads, _) = Make();

        Assert.Contains("as=NadlanKaekImporter-1.10.0-windows.zip", await downloads.GetLinkAsync("1.10.0", "NadlanKaekImporter-1.10.0-windows.zip", "t", default));
        Assert.Contains("README", await downloads.GetLinkAsync("1.10.0", "NadlanKaekImporter-1.10.0-README.txt", "t", default));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => downloads.GetLinkAsync("1.10.0", "notes.exe", "t", default));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => downloads.GetLinkAsync("1.10.0", "NadlanKaekImporter-1.0.0-windows.zip", "t", default));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => downloads.GetLinkAsync("..", "../../db.sql", "t", default));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => downloads.GetLinkAsync("1.10.0", "NadlanKaekImporter-1.10.0-mac-apple-silicon.tar.gz", "t", default)); // not uploaded
    }
}
