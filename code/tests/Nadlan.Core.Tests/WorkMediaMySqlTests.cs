using Dapper;
using Nadlan.Core.Files;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Core.Work;
using Nadlan.Persistence.MySql.Files;
using Nadlan.Persistence.MySql.Security;
using Nadlan.Persistence.MySql.Work;

namespace Nadlan.Core.Tests;

/// <summary>Signed links in tests: the key, readable.</summary>
internal sealed class TestUrls : IFileUrlProvider
{
    public string GetUrl(string storageKey) => "https://files.test/" + storageKey;
}

/// <summary>
/// Work requests piece 3 against the throwaway MySQL: the contractor's track, the conversations, work pictures (with a
/// recording storage instead of S3), "Copy to parcel", the Parcels' photos for the contractor and the daily clean-up.
/// </summary>
public partial class WorkMySqlTests
{
    /// <summary>Records what the module asks S3 to do; an object "exists" once given a size.</summary>
    private sealed class RecordingStorage : IObjectStorage
    {
        public Dictionary<string, long> Objects { get; } = new();
        public List<string> Log { get; } = new();
        public bool IsConfigured => true;

        public Task<string> StartMultipartUploadAsync(string key, string contentType, string contentDisposition, CancellationToken ct = default)
        {
            Log.Add($"start {key} {contentType}");
            return Task.FromResult("upload-" + key);
        }

        public string GetPartUploadUrl(string key, string uploadId, int partNumber, TimeSpan lifetime) => $"https://s3.test/{key}?part={partNumber}";
        public Task<IReadOnlyList<UploadedPart>?> ListUploadedPartsAsync(string key, string uploadId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UploadedPart>?>(null);

        public Task CompleteMultipartUploadAsync(string key, string uploadId, IReadOnlyList<UploadedPart> parts, CancellationToken ct = default)
        {
            Log.Add($"complete {key} {parts.Count}");
            return Task.CompletedTask;
        }

        public Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct = default)
        {
            Log.Add($"abort {key}");
            return Task.CompletedTask;
        }

        public Task<long?> GetObjectSizeAsync(string key, CancellationToken ct = default) => Task.FromResult(Objects.TryGetValue(key, out var s) ? s : (long?)null);

        public Task DeleteObjectAsync(string key, CancellationToken ct = default)
        {
            Log.Add($"delete {key}");
            Objects.Remove(key);
            return Task.CompletedTask;
        }

        public Task PutObjectAsync(string key, byte[] content, string contentType, CancellationToken ct = default)
        {
            Log.Add($"put {key}");
            Objects[key] = content.Length;
            return Task.CompletedTask;
        }

        public Task CopyObjectAsync(string fromKey, string toKey, CancellationToken ct = default)
        {
            Log.Add($"copy {fromKey} -> {toKey}");
            Objects[toKey] = Objects[fromKey];
            return Task.CompletedTask;
        }
    }

    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };

    private (WorkMediaService Media, RecordingStorage Storage) Media(FixedClock clock)
    {
        var storage = new RecordingStorage();
        var settings = new FileStorageSettings { MaxFileSizeBytes = 1024L * 1024 * 1024 };
        var media = new WorkMediaService(new MySqlWorkStore(_db!), storage, settings, new MySqlFileAttachmentStore(_db!), new MySqlMapDropStore(_db!),
            new AccessPolicy(new MySqlAccessStore(_db!), new Nadlan.Persistence.MySql.Assets.MySqlAssetStore(_db!)), clock);
        return (media, storage);
    }

    /// <summary>A request whose PA1 Clear is approved (pictures allowed); PA1 Burn only requested.</summary>
    private async Task<(Link L, WorkRequest R, long Clear, long Burn)> ApprovedAsync(World w, string? name = null)
    {
        var l = await LinkAsync(w, name);
        var r = await l.Links.ResolveAsync(l.Token);
        var clear = await WorkIdAsync(l, w.PA1, w.Clear);
        await l.Links.OfferAsync(r, clear, 300m, null);
        await l.Work.ApproveAsync(w.DataPerson, l.Id, new[] { clear });
        return (l, r, clear, await WorkIdAsync(l, w.PA1, w.Burn));
    }

    /// <summary>Uploads a photo to the work through every step; returns its id.</summary>
    private static async Task<long> UploadAsync(WorkMediaService media, RecordingStorage storage, WorkRequest r, long workItemId, string name = "IMG_1.jpg",
        string mime = "image/jpeg", long size = 2000)
    {
        var session = await media.StartUploadAsync(r, workItemId, name, mime, size, new UploadPosition(38.57, 23.15, 4.5m));
        var key = storage.Log.Last()[6..^(mime.Length + 1)];
        storage.Objects[key] = size;
        await media.CompleteAsync(r, session.FileAttachmentId, new[] { new UploadedPart(1, "\"etag\"") });
        return session.FileAttachmentId;
    }

    // ---- location -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Location_points_are_kept_only_when_good_enough_and_the_purge_keeps_90_days()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var l = await LinkAsync(w);
        var r = await l.Links.ResolveAsync(l.Token);

        Assert.False(await l.Links.RecordLocationAsync(r, 38.57, 23.15, 150));   // worse than 100 m
        Assert.False(await l.Links.RecordLocationAsync(r, 38.57, 23.15, null));  // accuracy unknown
        Assert.False(await l.Links.RecordLocationAsync(r, 0, 0, 5));             // "no place"
        Assert.False(await l.Links.RecordLocationAsync(r, 95, 23.15, 5));        // not on earth
        Assert.True(await l.Links.RecordLocationAsync(r, 38.5701234567, 23.15, 12.34));
        Assert.False(await l.Links.RecordLocationAsync(r, 38.5702, 23.15, 5));   // 0 s later: dropped
        l.Clock.Now = l.Clock.Now.AddSeconds(30);
        Assert.True(await l.Links.RecordLocationAsync(r, 38.5703, 23.15, 100));  // exactly 100 m is fine

        var from = DateTime.UtcNow.AddHours(-1);
        var (latest, trail) = await l.Work.LocationsAsync(w.DataPerson, l.Id, from, DateTime.UtcNow.AddHours(1));
        Assert.Equal(2, trail.Count);
        Assert.Equal(38.5701235, trail[0].Latitude, 7);
        Assert.Equal(12.3m, trail[0].AccuracyM);
        Assert.Equal(38.5703, latest!.Latitude, 7);
        await Assert.ThrowsAsync<DomainValidationException>(() => l.Work.LocationsAsync(w.DataPerson, l.Id, from, from.AddDays(3)));

        // 91 and 89 days old: the daily job removes the first only.
        await using (var c = await _db!.OpenAsync())
        {
            await c.ExecuteAsync("""
                INSERT INTO work_location (work_request_id, latitude, longitude, accuracy_m, recorded_utc) VALUES
                (@id, 38.1, 23.1, 5, UTC_TIMESTAMP(3) - INTERVAL 91 DAY), (@id, 38.2, 23.2, 5, UTC_TIMESTAMP(3) - INTERVAL 89 DAY)
                """, new { id = l.Id });
        }

        var (media, _) = Media(new FixedClock(DateTime.UtcNow));
        var (purged, _) = await media.SweepAsync();
        Assert.True(purged >= 1);
        await using (var c = await _db!.OpenAsync())
        {
            Assert.Equal(new[] { 38.2, 38.5701235, 38.5703 }, (await c.QueryAsync<double>(
                "SELECT latitude FROM work_location WHERE work_request_id = @id ORDER BY recorded_utc", new { id = l.Id })).ToArray());
        }
    }

    [Fact]
    public async Task Consent_and_refusal_go_into_the_history()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var l = await LinkAsync(w);
        var r = await l.Links.ResolveAsync(l.Token);
        await l.Links.ConsentAsync(r, true);
        await l.Links.ConsentAsync(r, false);
        var actions = (await l.Store.ListEventsAsync(l.Id, 10)).Select(e => (e.Action, e.ActorKind)).ToList();
        Assert.Contains((WorkActions.LocationShared, WorkActorKinds.Contractor), actions);
        Assert.Contains((WorkActions.LocationRefused, WorkActorKinds.Contractor), actions);
    }

    // ---- the conversation -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Both_sides_talk_per_parcel_and_the_contractor_sees_staff_as_GreekPlot()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var l = await LinkAsync(w);
        var r = await l.Links.ResolveAsync(l.Token);

        await l.Links.SendMessageAsync(r, w.PA1, "  Can I start Monday?  ");
        await l.Work.SendMessageAsync(w.DataPerson, l.Id, w.PA1, "Yes, the gate code is 1234.");
        await l.Links.SendMessageAsync(r, w.PA2, "Truck can't reach this one.");

        var pa1 = await l.Store.ListMessagesAsync(l.Id, w.PA1);
        Assert.Equal(new[] { "Can I start Monday?", "Yes, the gate code is 1234." }, pa1.Select(m => m.Body));
        Assert.Equal("data", pa1[1].AuthorName);

        var shown = System.Text.Json.JsonSerializer.Serialize(Nadlan.Host.Work.WorkLinkEndpoints.MessagesView(r, pa1));
        Assert.Contains("\"author\":\"GreekPlot\"", shown);
        Assert.DoesNotContain("\"author\":\"data\"", shown); // never the staff member's name

        Assert.Equal("WORK_MESSAGE_EMPTY", (await Assert.ThrowsAsync<DomainValidationException>(() => l.Links.SendMessageAsync(r, w.PA1, "   "))).Code);
        Assert.Equal("WORK_MESSAGE_TOO_LONG", (await Assert.ThrowsAsync<DomainValidationException>(() => l.Links.SendMessageAsync(r, w.PA1, new string('x', 4001)))).Code);
        await l.Links.SendMessageAsync(r, w.PA1, new string('x', 4000));
        Assert.Equal("WORK_NOT_FOUND", (await Assert.ThrowsAsync<WorkConflictException>(() => l.Links.SendMessageAsync(r, w.PB, "not mine"))).Code);
        await Assert.ThrowsAsync<EntityNotFoundException>(() => l.Work.SendMessageAsync(w.DataPerson, l.Id, w.PB, "not in the request"));
    }

    // ---- pictures of a work ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Pictures_go_on_a_work_once_approved_photos_and_videos_only()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (l, r, clear, burn) = await ApprovedAsync(w);
        var (media, storage) = Media(l.Clock);

        Assert.Equal("WORK_NO_PICTURES_YET", (await Assert.ThrowsAsync<WorkConflictException>(() => media.StartUploadAsync(r, burn, "a.jpg", "image/jpeg", 10, null))).Code);
        Assert.Equal("WORK_FILE_TYPE", (await Assert.ThrowsAsync<DomainValidationException>(() => media.StartUploadAsync(r, clear, "a.pdf", "application/pdf", 10, null))).Code);
        Assert.Equal("WORK_FILE_TYPE", (await Assert.ThrowsAsync<DomainValidationException>(() => media.StartUploadAsync(r, clear, "a.svg", "image/svg+xml", 10, null))).Code);
        Assert.Equal("FILE_TOO_LARGE", (await Assert.ThrowsAsync<DomainValidationException>(() => media.StartUploadAsync(r, clear, "a.mp4", "video/mp4", 2L * 1024 * 1024 * 1024, null))).Code);

        var id = await UploadAsync(media, storage, r, clear, "IMG 1.JPG");
        var f = (await l.Store.GetWorkFileAsync(l.Id, id))!;
        Assert.Equal(FileUploadStatus.Ready, f.UploadStatus);
        Assert.Matches($"^work/{l.Id}/{clear}/[0-9a-f]{{32}}\\.jpg$", f.StorageKey); // not the file's name
        Assert.Equal("IMG 1.JPG", f.OriginalName);
        Assert.Equal((38.57, 23.15, 4.5m), (f.UploadLatitude!.Value, f.UploadLongitude!.Value, f.UploadAccuracyM!.Value)); // the phone's position

        await media.SaveThumbnailAsync(r, id, Jpeg);
        Assert.Contains($"put {f.StorageKey}.thumb.jpg", storage.Log);
        await Assert.ThrowsAsync<DomainValidationException>(() => media.SaveThumbnailAsync(r, id, new byte[] { 1, 2, 3 }));
        await media.SaveMediaDataAsync(r, id, new MediaMetadata { TakenLocal = "2026-11-01 10:24:28", CameraModel = "iPhone 13 mini", Latitude = 0, Longitude = 0, Width = 4032 });
        var withData = (await l.Store.GetWorkFileAsync(l.Id, id))!;
        Assert.Equal(("2026-11-01 10:24:28", "iPhone 13 mini", 4032), (withData.TakenLocal, withData.CameraModel, withData.Width!.Value));
        Assert.Null(withData.Latitude); // 0, 0 is no place
        Assert.True(withData.HasThumbnail);

        Assert.Contains((await l.Store.ListEventsAsync(l.Id, 50)), e => e.Action == WorkActions.FileUploaded && e.Summary.Contains("IMG 1.JPG"));

        // Paid: no more pictures.
        await l.Links.StartAsync(r, clear);
        await l.Links.DoneAsync(r, clear);
        await UploadAsync(media, storage, r, clear, "after.jpg"); // done: still fine
        await l.Work.PaidAsync(w.DataPerson, l.Id, clear);
        await Assert.ThrowsAsync<WorkConflictException>(() => media.StartUploadAsync(r, clear, "late.jpg", "image/jpeg", 10, null));
    }

    [Fact]
    public async Task A_link_can_not_reach_another_requests_uploads_or_messages()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var a = await ApprovedAsync(w, "A");
        var b = await ApprovedAsync(w, "B");
        var (media, storage) = Media(a.L.Clock);
        var session = await media.StartUploadAsync(b.R, b.Clear, "b.jpg", "image/jpeg", 100, null);
        var readyOfB = await UploadAsync(media, storage, b.R, b.Clear, "b2.jpg");

        await Assert.ThrowsAsync<EntityNotFoundException>(() => media.PartUrlsAsync(a.R, session.FileAttachmentId, new[] { 1 }));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => media.CompleteAsync(a.R, session.FileAttachmentId, new[] { new UploadedPart(1, "x") }));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => media.AbortAsync(a.R, session.FileAttachmentId));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => media.SaveThumbnailAsync(a.R, readyOfB, Jpeg));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => media.SaveMediaDataAsync(a.R, readyOfB, new MediaMetadata()));
        await Assert.ThrowsAsync<WorkConflictException>(() => media.StartUploadAsync(a.R, b.Clear, "x.jpg", "image/jpeg", 10, null));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => media.CopyToParcelAsync(w.DataPerson, a.L.Id, readyOfB));
        Assert.Empty(await a.L.Store.ListWorkFilesAsync(a.L.Id, null));
        Assert.Single(await b.L.Store.ListWorkFilesAsync(b.L.Id, null));
    }

    [Fact]
    public async Task Copy_to_parcel_makes_a_parcel_photo_once_and_the_work_keeps_its_own()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (l, r, clear, _) = await ApprovedAsync(w);
        var (media, storage) = Media(l.Clock);
        var id = await UploadAsync(media, storage, r, clear, "Before.JPG");
        await media.SaveThumbnailAsync(r, id, Jpeg);
        await media.SaveMediaDataAsync(r, id, new MediaMetadata { TakenLocal = "2026-11-01 10:24:28", CameraMake = "Apple", Latitude = 38.57, Longitude = 23.15 });
        var work = (await l.Store.GetWorkFileAsync(l.Id, id))!;

        var sales = w.Limited with { Permissions = new HashSet<string> { Permissions.ManageWork, Permissions.ViewAllParcels } }; // may see, may not add files
        await Assert.ThrowsAsync<ForbiddenException>(() => media.CopyToParcelAsync(sales, l.Id, id));

        var fileId = await media.CopyToParcelAsync(w.DataPerson, l.Id, id);
        var parcelFile = (await new MySqlFileAttachmentStore(_db!).GetAsync(fileId))!;
        Assert.Equal((FileTargetTypes.Parcel, w.PA1, FileUploadStatus.Ready, "Before.JPG", true), (parcelFile.AttachedToType, parcelFile.AttachedToId,
            parcelFile.UploadStatus, parcelFile.OriginalFileName, parcelFile.HasThumbnail));
        Assert.Matches($"^parcel/{w.PA1}/[0-9a-f]{{32}}\\.jpg$", parcelFile.StorageKey);
        Assert.Contains($"copy {work.StorageKey} -> {parcelFile.StorageKey}", storage.Log);
        Assert.Contains($"copy {work.StorageKey}.thumb.jpg -> {parcelFile.StorageKey}.thumb.jpg", storage.Log);
        Assert.True(storage.Objects.ContainsKey(work.StorageKey)); // the work keeps its proof
        await using (var c = await _db!.OpenAsync())
        {
            Assert.Equal("PHOTO", await c.ExecuteScalarAsync<string>("SELECT t.code FROM file_attachment f JOIN file_type t ON t.file_type_id = f.file_type_id WHERE f.file_attachment_id = @fileId", new { fileId }));
            Assert.StartsWith("From work request #", await c.ExecuteScalarAsync<string>("SELECT notes FROM file_attachment WHERE file_attachment_id = @fileId", new { fileId }));
            Assert.Equal("2026-11-01 10:24:28", await c.ExecuteScalarAsync<string>("SELECT taken_local FROM file_media_metadata WHERE file_attachment_id = @fileId", new { fileId }));
        }

        Assert.Equal(fileId, (await l.Store.GetWorkFileAsync(l.Id, id))!.CopiedToFileAttachmentId);
        Assert.Equal("WORK_FILE_COPIED", (await Assert.ThrowsAsync<WorkConflictException>(() => media.CopyToParcelAsync(w.DataPerson, l.Id, id))).Code);
        Assert.Contains((await l.Store.ListEventsAsync(l.Id, 50)), e => e.Action == WorkActions.CopiedToParcel);

        // A video becomes a Video.
        var vid = await UploadAsync(media, storage, r, clear, "clip.mp4", "video/mp4", 5000);
        var videoFile = await media.CopyToParcelAsync(w.DataPerson, l.Id, vid);
        await using (var c = await _db!.OpenAsync())
        {
            Assert.Equal("VIDEO", await c.ExecuteScalarAsync<string>("SELECT t.code FROM file_attachment f JOIN file_type t ON t.file_type_id = f.file_type_id WHERE f.file_attachment_id = @videoFile", new { videoFile }));
        }
    }

    [Fact]
    public async Task The_contractor_sees_only_the_request_parcels_marketing_photos_and_videos()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var l = await LinkAsync(w);
        await using (var c = await _db!.OpenAsync())
        {
            async Task File(long parcel, string type, string mime, string name) => await c.ExecuteAsync("""
                INSERT INTO file_attachment (file_type_id, attached_to_type, attached_to_id, storage_key, original_file_name, mime_type, file_size, upload_status)
                SELECT file_type_id, 'Parcel', @parcel, CONCAT('parcel/', @parcel, '/', UUID(), '.x'), @name, @mime, 10, 'Ready' FROM file_type WHERE code = @type
                """, new { parcel, type, mime, name });
            await File(w.PA1, "PHOTO", "image/jpeg", "ok-photo");
            await File(w.PA2, "DRONE_VIDEO", "video/mp4", "ok-video");
            await File(w.PA1, "PHOTO", "image/heic", "heic");         // a browser can't show it
            await File(w.PA1, "PHOTO", "image/svg+xml", "svg");       // could run as a page
            await File(w.PB, "PHOTO", "image/jpeg", "other-parcel");  // not in the request
            var legal = await c.ExecuteScalarAsync<string?>("SELECT code FROM file_type WHERE category = 'Legal' LIMIT 1");
            await File(w.PA1, legal!, "image/jpeg", "title-deed");
        }

        var media = await l.Store.ListParcelMediaAsync(l.Id, WorkMediaService.CustomerCategory, WorkMediaService.CustomerMediaTypes);
        Assert.Equal(new[] { "ok-photo", "ok-video" }, media.Select(m => m.OriginalFileName).Order());
    }

    [Fact]
    public async Task The_daily_job_removes_abandoned_uploads_but_never_a_finished_one()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (l, r, clear, _) = await ApprovedAsync(w);
        var (media, storage) = Media(l.Clock);
        var abandoned = await media.StartUploadAsync(r, clear, "left.jpg", "image/jpeg", 100, null);
        var finishedInS3 = await media.StartUploadAsync(r, clear, "done-in-s3.jpg", "image/jpeg", 300, null);
        var ready = await UploadAsync(media, storage, r, clear, "ready.jpg");
        var fresh = await media.StartUploadAsync(r, clear, "now.jpg", "image/jpeg", 100, null);
        var s3Done = (await l.Store.GetWorkFileAsync(l.Id, finishedInS3.FileAttachmentId))!;
        storage.Objects[s3Done.StorageKey] = 300; // S3 has it whole; our Ready step never happened
        await using (var c = await _db!.OpenAsync())
        {
            await c.ExecuteAsync("UPDATE work_file SET uploaded_utc = UTC_TIMESTAMP(3) - INTERVAL 2 DAY WHERE work_file_id IN @ids",
                new { ids = new[] { abandoned.FileAttachmentId, finishedInS3.FileAttachmentId, ready } });
        }

        var (_, swept) = await media.SweepAsync();

        Assert.True(swept >= 1);
        Assert.Null(await l.Store.GetWorkFileAsync(l.Id, abandoned.FileAttachmentId));
        Assert.Contains(storage.Log, x => x.StartsWith("abort work/", StringComparison.Ordinal));
        Assert.Equal(FileUploadStatus.Ready, (await l.Store.GetWorkFileAsync(l.Id, finishedInS3.FileAttachmentId))!.UploadStatus); // recovered
        Assert.Equal(FileUploadStatus.Ready, (await l.Store.GetWorkFileAsync(l.Id, ready))!.UploadStatus);
        Assert.Equal(FileUploadStatus.Pending, (await l.Store.GetWorkFileAsync(l.Id, fresh.FileAttachmentId))!.UploadStatus); // under a day
    }

    [Fact]
    public async Task The_contractor_page_gets_each_works_pictures()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (l, r, clear, _) = await ApprovedAsync(w);
        var (media, storage) = Media(l.Clock);
        await UploadAsync(media, storage, r, clear, "Secret name Papadopoulos.jpg");

        var json = System.Text.Json.JsonSerializer.Serialize(await Nadlan.Host.Work.WorkLinkEndpoints.ViewAsync(r, l.Store,
            new Nadlan.Host.Configuration.NadlanOptions(), new TestUrls(), true, CancellationToken.None));
        Assert.Contains($"https://files.test/work/{l.Id}/{clear}/", json);
        Assert.DoesNotContain("Papadopoulos", json); // the link and the reply never carry the uploader's file name
        Assert.Contains("\"pictures\":true", json);
    }
}
