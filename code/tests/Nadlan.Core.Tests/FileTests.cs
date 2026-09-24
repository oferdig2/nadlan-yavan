using System.Security.Cryptography;
using System.Text;
using Nadlan.Core.Files;
using Nadlan.Core.Validation;
using Nadlan.Storage.S3;

namespace Nadlan.Core.Tests;

public class FileTests
{
    [Fact]
    public async Task Upload_start_creates_pending_row_with_scoped_key_and_part_plan()
    {
        var (service, files, storage) = NewService();

        var session = await service.StartUploadAsync(new StartUploadRequest("asset", 5, 1, @"C:\photos\Βίλα 1.jpg", "image/jpeg", 20L * 1024 * 1024));

        var row = files.Rows.Single();
        Assert.Equal("Asset", row.AttachedToType);                          // normalized target type
        Assert.StartsWith("asset/5/", row.StorageKey);                       // target/id/guid/name, relative to RootFolder
        Assert.EndsWith("/1.jpg", row.StorageKey);                           // ASCII-only key segment
        Assert.Equal("Βίλα 1.jpg", row.OriginalFileName);                    // real name kept, path stripped
        Assert.Equal(FileUploadStatus.Pending, row.UploadStatus);
        Assert.Equal(3, session.PartCount);                                  // 20 MiB in 8 MiB parts
        Assert.Contains("filename*=UTF-8''%CE%92", storage.LastDisposition); // Greek name survives download
    }

    [Fact]
    public void Huge_files_get_bigger_parts_to_stay_within_10000()
    {
        var (service, _, _) = NewService();
        var fileSize = 200L * 1024 * 1024 * 1024; // 200 GB

        var part = service.PartSizeFor(fileSize);

        Assert.True((fileSize + part - 1) / part <= 10_000);
        Assert.Equal(0, part % (1024 * 1024));
    }

    [Fact]
    public async Task Complete_rejects_when_stored_size_differs()
    {
        var (service, files, storage) = NewService();
        var session = await service.StartUploadAsync(new StartUploadRequest("Asset", 5, 1, "a.pdf", "application/pdf", 1000));
        storage.StoredSize = 999;

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CompleteUploadAsync(session.FileAttachmentId, new[] { new UploadedPart(1, "\"x\"") }));

        Assert.Equal("FILE_SIZE_MISMATCH", ex.Code);
        Assert.Empty(files.Rows);
    }

    [Fact]
    public async Task Complete_marks_ready()
    {
        var (service, files, storage) = NewService();
        var session = await service.StartUploadAsync(new StartUploadRequest("Parcel", 1, 1, "a.pdf", null, 1000));
        storage.StoredSize = 1000;

        await service.CompleteUploadAsync(session.FileAttachmentId, new[] { new UploadedPart(1, "\"x\"") });

        Assert.Equal(FileUploadStatus.Ready, files.Rows.Single().UploadStatus);
        Assert.Equal("application/octet-stream", files.Rows.Single().MimeType);
    }

    [Theory]
    [InlineData("User", 1, "FILE_TARGET_NOT_FOUND")]   // no users yet
    [InlineData("Deal", 1, "FILE_TARGET_INVALID")]
    [InlineData("Asset", 404, "FILE_TARGET_NOT_FOUND")]
    public async Task Upload_needs_a_real_target(string type, long id, string code)
    {
        var (service, _, _) = NewService();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.StartUploadAsync(new StartUploadRequest(type, id, 1, "a.pdf", null, 10)));

        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void CloudFront_signature_verifies_against_the_canned_policy()
    {
        using var rsa = RSA.Create(2048);
        using var signer = new CloudFrontUrlSigner(rsa.ExportRSAPrivateKeyPem(), "K2ABCDEF");
        var expires = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);

        var signed = signer.Sign("https://d1.cloudfront.net/dev/asset/5/a/photo.jpg", expires);

        var query = new Uri(signed).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal("1900000000", query["Expires"]);
        Assert.Equal("K2ABCDEF", query["Key-Pair-Id"]);
        Assert.DoesNotContain('+', query["Signature"]);
        var signature = Convert.FromBase64String(query["Signature"].Replace('-', '+').Replace('_', '=').Replace('~', '/'));
        var policy = "{\"Statement\":[{\"Resource\":\"https://d1.cloudfront.net/dev/asset/5/a/photo.jpg\",\"Condition\":{\"DateLessThan\":{\"AWS:EpochTime\":1900000000}}}]}";
        Assert.True(rsa.VerifyData(Encoding.UTF8.GetBytes(policy), signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
    }

    private static (FileService Service, FakeFiles Files, FakeStorage Storage) NewService()
    {
        var files = new FakeFiles();
        var storage = new FakeStorage();
        return (new FileService(files, new FakeTargets(), storage, new FileStorageSettings()), files, storage);
    }

    private sealed class FakeFiles : IFileAttachmentStore
    {
        public List<FileAttachment> Rows { get; } = new();

        public Task<long> InsertPendingAsync(FileAttachment f, CancellationToken ct = default)
        {
            Rows.Add(f with { FileAttachmentId = Rows.Count + 1 });
            return Task.FromResult((long)Rows.Count);
        }

        public Task<FileAttachment?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Rows.FirstOrDefault(r => r.FileAttachmentId == id));
        public Task<IReadOnlyList<FileListItem>> ListReadyAsync(string t, long id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> MarkReadyAsync(long id, CancellationToken ct = default)
        {
            var i = Rows.FindIndex(r => r.FileAttachmentId == id && r.UploadStatus == FileUploadStatus.Pending);
            if (i < 0)
            {
                return Task.FromResult(false);
            }

            Rows[i] = Rows[i] with { UploadStatus = FileUploadStatus.Ready, S3UploadId = null };
            return Task.FromResult(true);
        }

        public Task UpdateMetadataAsync(long id, int t, string? c, string? n, int? s, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(long id, CancellationToken ct = default)
        {
            Rows.RemoveAll(r => r.FileAttachmentId == id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FileAttachment>> ListStalePendingAsync(DateTime before, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileAttachment>>(Rows.Where(r => r.UploadStatus == FileUploadStatus.Pending && r.UploadedUtc < before).Take(limit).ToList());

        public Task<IReadOnlyList<FileType>> ListTypesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileType>>(new[] { new FileType(1, "PHOTO", "Photo", "Marketing", true, 10) });
    }

    private sealed class FakeTargets : IFileTargetResolver
    {
        public Task<bool> ExistsAsync(string type, long id, CancellationToken ct = default)
            => Task.FromResult(type is FileTargetTypes.Asset or FileTargetTypes.Parcel && id < 100);
    }

    private sealed class FakeStorage : IObjectStorage
    {
        public string LastDisposition { get; private set; } = "";
        public long? StoredSize { get; set; }
        public bool IsConfigured => true;

        public Task<string> StartMultipartUploadAsync(string key, string contentType, string contentDisposition, CancellationToken ct = default)
        {
            LastDisposition = contentDisposition;
            return Task.FromResult("upload-1");
        }

        public string GetPartUploadUrl(string key, string uploadId, int partNumber, TimeSpan lifetime) => $"https://s3/{key}?part={partNumber}";
        public Task<IReadOnlyList<UploadedPart>?> ListUploadedPartsAsync(string key, string uploadId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UploadedPart>?>(Array.Empty<UploadedPart>());
        public Task CompleteMultipartUploadAsync(string key, string uploadId, IReadOnlyList<UploadedPart> parts, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long?> GetObjectSizeAsync(string key, CancellationToken ct = default) => Task.FromResult(StoredSize);
        public Task DeleteObjectAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
