using Nadlan.Config.MySql;
using Nadlan.Core.Files;
using Nadlan.Core.Parcels;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Tests;

/// <summary>Pins the fixes from the E2E review so they stay fixed.</summary>
public class ReviewRegressionTests
{
    [Fact]
    public void Search_box_edges_are_densified_so_geodesics_follow_the_map()
    {
        var polygon = new GeoBounds(20, 34, 28, 42).ToPolygon(); // 8° wide → 80 steps per edge

        Assert.Equal(2 * 81 + 1, polygon.Exterior.Count);
        Assert.All(polygon.Exterior.Take(81), p => Assert.Equal(34, p.Lat));
        Assert.Equal(polygon.Exterior[0], polygon.Exterior[^1]);
    }

    [Fact]
    public void Provisional_id_is_null_not_an_exception_for_punctuation_only_plots()
    {
        Assert.Null(ProvisionalRegistryId.TryCreate("SKR", "-", null, "5", null));
    }

    [Theory]
    [InlineData("plan.svg", "image/svg+xml", "attachment")]
    [InlineData("page.html", "application/octet-stream", "attachment")] // extension alone is enough
    [InlineData("photo.jpg", "image/jpeg", "inline")]
    [InlineData("title.pdf", "application/pdf", "inline")]
    public void Active_content_is_always_a_download(string name, string mime, string expected)
    {
        Assert.StartsWith(expected + ";", FileService.ContentDisposition(name, mime));
    }

    [Fact]
    public void Duplicate_keys_from_a_hand_edit_keep_the_last_value_like_IConfiguration()
    {
        var edited = """{ "Nadlan": { "Storage": { "Bucket": "old", "Bucket": "client-bucket" } } }""";

        var (merged, _) = AppConfigJson.AddMissing(edited, """{ "Nadlan": { "Storage": { "Region": "eu-central-1" } } }""");

        Assert.Contains("client-bucket", merged);
        Assert.DoesNotContain("\"old\"", merged);
    }

    [Fact]
    public void Set_with_different_casing_updates_the_existing_key()
    {
        var json = """{ "Nadlan": { "Storage": { "RootFolder": "nadlan/dev" } } }""";

        var updated = AppConfigJson.SetValue(json, "nadlan:storage:rootfolder", "nadlan/prod");

        Assert.Contains("\"RootFolder\": \"nadlan/prod\"", updated);
        Assert.DoesNotContain("nadlan/dev", updated);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(updated, "\"Nadlan\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    [Fact]
    public void Remove_deletes_an_obsolete_key()
    {
        var (json, removed) = AppConfigJson.RemoveValue("""{ "Nadlan": { "Storage": { "KeyPrefix": "dev", "Bucket": "b" } } }""", "Nadlan:Storage:KeyPrefix");

        Assert.True(removed);
        Assert.DoesNotContain("KeyPrefix", json);
        Assert.Contains("\"Bucket\"", json);
        Assert.False(AppConfigJson.RemoveValue(json, "Nadlan:Nope:X").Removed);
    }

    [Fact]
    public async Task Cancel_during_complete_removes_the_s3_object_instead_of_orphaning_it()
    {
        var files = new CancellingStore();
        var storage = new RecordingStorage { StoredSize = 10 };
        var service = new FileService(files, new AnyTarget(), storage, new FileStorageSettings());
        var session = await service.StartUploadAsync(new StartUploadRequest("Asset", 1, 1, "a.pdf", null, 10));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CompleteUploadAsync(session.FileAttachmentId, new[] { new UploadedPart(1, "\"e\"") }));

        Assert.Equal("FILE_UPLOAD_CANCELLED", ex.Code);
        Assert.Single(storage.Deleted);
    }

    [Fact]
    public async Task Sweep_aborts_and_removes_abandoned_uploads()
    {
        var files = new CancellingStore();
        var storage = new RecordingStorage();
        var service = new FileService(files, new AnyTarget(), storage, new FileStorageSettings());
        await service.StartUploadAsync(new StartUploadRequest("Asset", 1, 1, "a.pdf", null, 10));
        files.Row = files.Row! with { UploadedUtc = DateTime.UtcNow.AddDays(-2) };

        var swept = await service.SweepAbandonedUploadsAsync(TimeSpan.FromDays(1));

        Assert.Equal(1, swept);
        Assert.Single(storage.Aborted);
        Assert.Null(files.Row);
    }

    [Fact]
    public async Task Resume_after_s3_already_completed_marks_ready_instead_of_reuploading()
    {
        var files = new CancellingStore { MarkReadyResult = true };
        var storage = new RecordingStorage { Parts = null, StoredSize = 10 }; // multipart gone, object complete
        var service = new FileService(files, new AnyTarget(), storage, new FileStorageSettings());
        var session = await service.StartUploadAsync(new StartUploadRequest("Asset", 1, 1, "a.mp4", "video/mp4", 10));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.ListUploadedPartsAsync(session.FileAttachmentId));

        Assert.Equal("FILE_NOT_UPLOADING", ex.Code); // browser then shows "done", no second upload
        Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task Resume_after_upload_expired_clears_it_so_the_browser_restarts()
    {
        var files = new CancellingStore();
        var storage = new RecordingStorage { Parts = null, StoredSize = null };
        var service = new FileService(files, new AnyTarget(), storage, new FileStorageSettings());
        var session = await service.StartUploadAsync(new StartUploadRequest("Asset", 1, 1, "a.mp4", "video/mp4", 10));

        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.ListUploadedPartsAsync(session.FileAttachmentId));
        Assert.Null(files.Row);
    }

    [Theory]
    [InlineData("120,000", 120000)]
    [InlineData("1.234,5", 1234.5)]
    [InlineData("0,800", 0.8)]      // a leading 0 group is never thousands
    [InlineData("1.250.000", 1250000)]
    [InlineData("250.000", 250)]    // single dot stays a decimal point
    public void Greek_and_english_number_input(string text, double expected)
    {
        Assert.Equal((decimal)expected, Nadlan.Core.Text.TextNormalize.ParseDecimal(text));
    }

    [Theory]
    [InlineData("feed.atom", "application/atom+xml")]
    [InlineData("x.png", "application/rss+xml; charset=utf-8")]
    public void Any_xml_type_is_a_download(string name, string mime)
    {
        Assert.StartsWith("attachment;", FileService.ContentDisposition(name, mime));
    }

    // A store whose row is "deleted by a concurrent cancel" by the time the upload completes.
    private sealed class CancellingStore : IFileAttachmentStore
    {
        public FileAttachment? Row { get; set; }

        public Task<long> InsertPendingAsync(FileAttachment f, CancellationToken ct = default)
        {
            Row = f with { FileAttachmentId = 1, UploadedUtc = DateTime.UtcNow };
            return Task.FromResult(1L);
        }

        public Task<FileAttachment?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Row);
        public bool MarkReadyResult { get; set; } // false = cancelled meanwhile
        public Task<bool> MarkReadyAsync(long id, CancellationToken ct = default) => Task.FromResult(MarkReadyResult);
        public Task DeleteAsync(long id, CancellationToken ct = default) { Row = null; return Task.CompletedTask; }

        public Task<IReadOnlyList<FileAttachment>> ListStalePendingAsync(DateTime before, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileAttachment>>(Row is { } r && r.UploadedUtc < before ? new[] { r } : Array.Empty<FileAttachment>());

        public Task<IReadOnlyList<FileListItem>> ListReadyAsync(string t, long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateMetadataAsync(long id, int t, string? c, string? n, int? s, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<FileType>> ListTypesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileType>>(new[] { new FileType(1, "OTHER", "Other", "General", true, 1) });
    }

    private sealed class AnyTarget : IFileTargetResolver
    {
        public Task<bool> ExistsAsync(string type, long id, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class RecordingStorage : IObjectStorage
    {
        public List<string> Deleted { get; } = new();
        public List<string> Aborted { get; } = new();
        public long? StoredSize { get; set; }
        public bool IsConfigured => true;

        public Task<string> StartMultipartUploadAsync(string key, string contentType, string contentDisposition, CancellationToken ct = default) => Task.FromResult("u1");
        public string GetPartUploadUrl(string key, string uploadId, int partNumber, TimeSpan lifetime) => "";
        public IReadOnlyList<UploadedPart>? Parts { get; set; } = Array.Empty<UploadedPart>();
        public Task<IReadOnlyList<UploadedPart>?> ListUploadedPartsAsync(string key, string uploadId, CancellationToken ct = default) => Task.FromResult(Parts);
        public Task CompleteMultipartUploadAsync(string key, string uploadId, IReadOnlyList<UploadedPart> parts, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct = default) { Aborted.Add(key); return Task.CompletedTask; }
        public Task<long?> GetObjectSizeAsync(string key, CancellationToken ct = default) => Task.FromResult(StoredSize);
        public Task DeleteObjectAsync(string key, CancellationToken ct = default) { Deleted.Add(key); return Task.CompletedTask; }
    }
}
