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
        Assert.StartsWith("asset/5/", row.StorageKey);                       // target/id/guid.ext, relative to RootFolder
        Assert.Matches(@"^asset/5/[0-9a-f]{32}\.jpg$", row.StorageKey);    // no file name in the key (it is in every URL)
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

    [Fact]
    public void CloudFront_signed_url_carries_the_customer_file_name_inside_the_signature()
    {
        using var rsa = RSA.Create(2048);
        var options = new StorageOptions
        {
            RootFolder = "nadlan/prod",
            Delivery = new()
            {
                Mode = DeliveryModes.CloudFrontSigned, CloudFrontDomain = "d1.cloudfront.net", CloudFrontOriginPath = "/nadlan/prod",
                KeyPairId = "K2ABCDEF", PrivateKeyPem = rsa.ExportRSAPrivateKeyPem(), UrlMinutes = 60,
            },
        };
        using var provider = new CloudFrontSignedUrlProvider(options);

        var signed = provider.GetUrl("asset/5/a/ID_scan_Papadopoulos.jpg", "photo \"1\".jpg");

        var uri = new Uri(signed);
        var query = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal("inline; filename=\"photo 1.jpg\"", Uri.UnescapeDataString(query["response-content-disposition"]));
        // The policy's Resource is the URL with the override but without the signing parameters.
        var resource = signed[..signed.IndexOf("&Expires=", StringComparison.Ordinal)];
        Assert.StartsWith("https://d1.cloudfront.net/asset/5/a/ID_scan_Papadopoulos.jpg?response-content-disposition=", resource);
        var policy = "{\"Statement\":[{\"Resource\":\"" + resource + "\",\"Condition\":{\"DateLessThan\":{\"AWS:EpochTime\":" + query["Expires"] + "}}}]}";
        var signature = Convert.FromBase64String(query["Signature"].Replace('-', '+').Replace('_', '=').Replace('~', '/'));
        Assert.True(rsa.VerifyData(Encoding.UTF8.GetBytes(policy), signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
        // No name asked: the plain signed URL, as before.
        Assert.DoesNotContain("response-content-disposition", provider.GetUrl("asset/5/a/x.jpg", ""));
    }

    [Fact]
    public async Task Map_drop_cleanup_never_deletes_a_file_assigned_meanwhile()
    {
        var (service, files, storage) = NewService();
        var old = DateTime.UtcNow.AddDays(-6);
        files.Rows.Add(new FileAttachment { FileAttachmentId = 1, AttachedToType = FileTargetTypes.MapDrop, AttachedToId = 4, StorageKey = "mapdrop/4/a.jpg", UploadStatus = FileUploadStatus.Ready, UploadedUtc = old, HasThumbnail = true });
        files.Rows.Add(new FileAttachment { FileAttachmentId = 2, AttachedToType = FileTargetTypes.MapDrop, AttachedToId = 4, StorageKey = "mapdrop/4/b.mp4", UploadStatus = FileUploadStatus.Ready, UploadedUtc = old });
        var cutoff = DateTime.UtcNow.AddDays(-5);

        // File 2 was listed as expired, then assigned to a Parcel before the clean-up reached it.
        files.Rows[1] = files.Rows[1] with { AttachedToType = FileTargetTypes.Parcel, AttachedToId = 7 };

        Assert.True(await service.DeleteExpiredMapDropAsync(1, cutoff));
        Assert.False(await service.DeleteExpiredMapDropAsync(2, cutoff));

        Assert.Equal(new[] { "mapdrop/4/a.jpg", "mapdrop/4/a.jpg.thumb.jpg" }, storage.DeletedKeys);
        Assert.Single(files.Rows, r => r.FileAttachmentId == 2 && r.AttachedToType == FileTargetTypes.Parcel);
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

        public Task<bool> DeletePendingAsync(long id, CancellationToken ct = default)
            => Task.FromResult(Rows.RemoveAll(r => r.FileAttachmentId == id && r.UploadStatus == FileUploadStatus.Pending) > 0);

        public Task<IReadOnlyList<FileAttachment>> ListStalePendingAsync(DateTime before, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileAttachment>>(Rows.Where(r => r.UploadStatus == FileUploadStatus.Pending && r.UploadedUtc < before).Take(limit).ToList());

        public Task<IReadOnlyList<FileType>> ListTypesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileType>>(new[] { new FileType(1, "PHOTO", "Photo", "Marketing", true, 10) });

        public Task<bool> DeleteExpiredMapDropAsync(long id, DateTime before, CancellationToken ct = default)
            => Task.FromResult(Rows.RemoveAll(r => r.FileAttachmentId == id && r.AttachedToType == FileTargetTypes.MapDrop && r.UploadedUtc < before) > 0);
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
        public List<string> DeletedKeys { get; } = new();

        public Task DeleteObjectAsync(string key, CancellationToken ct = default)
        {
            DeletedKeys.Add(key);
            return Task.CompletedTask;
        }
    }
}
