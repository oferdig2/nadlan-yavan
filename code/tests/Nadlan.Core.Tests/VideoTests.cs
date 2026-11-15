using Dapper;
using Nadlan.Core.Files;
using Nadlan.Core.Video;
using Nadlan.Persistence.MySql;
using Nadlan.Persistence.MySql.Files;
using Nadlan.Persistence.MySql.Video;
using Nadlan.Storage.S3;

namespace Nadlan.Core.Tests;

/// <summary>Large videos made light (migration 028): the rules, and the whole flow against the throwaway MySQL with a fake MediaConvert.</summary>
public class VideoTests
{
    private static readonly VideoSettings Settings = new() { Enabled = true, Subnets = "subnet-1", FrameCount = 10, OriginalRetentionDays = 7, QueueUploadedAfterUtc = DateTime.MinValue };

    // ---- rules ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Offered_frames_are_spread_between_5_and_95_percent()
    {
        Assert.Equal(new[] { 7, 20, 33, 46, 59, 72, 85, 98, 111, 124 }, VideoRules.OfferedFrames(132, 10)); // DJI_0948: 263 s
        Assert.Equal(new[] { 0, 1, 2 }, VideoRules.OfferedFrames(3, 10));
        Assert.Empty(VideoRules.OfferedFrames(0, 10));
        Assert.Empty(VideoRules.OfferedFrames(50, 0));
    }

    [Fact]
    public void The_poster_is_the_middle_of_the_longest_time_frame_on_its_parcel_else_the_middle()
    {
        Assert.Equal(66, VideoRules.PosterFrame(132, null));
        var segs = new[] { new FileSegment(1, 10_000, 20_000), new FileSegment(1, 100_000, 160_000) };
        Assert.Equal(65, VideoRules.PosterFrame(132, segs)); // 130 s -> frame 65
        Assert.Equal(0, VideoRules.PosterFrame(0, segs));
    }

    [Fact]
    public void Heights_keep_half_metre_changes_and_read_as_floors()
    {
        var heights = VideoRules.HeightsFromTrack("[[0,38.5,23.3,0.1],[500,38.5,23.3,0.3],[1000,38.5,23.3,3.2],[1500,38.5,23.3,3.4],[2000,38.5,23.3,18.0],[2500,1,1,null]]");
        Assert.Equal("[[0,0.1],[1000,3.2],[2000,18]]", heights);
        var cues = VideoRules.HeightCues(heights, 0m, Settings, 10_000);
        Assert.Equal(new object[] { 0, 1000, "0 m · ≈ ground floor view" }, cues[0]);
        Assert.Equal(new object[] { 1000, 2000, "3 m · ≈ 1st floor view" }, cues[1]); // nearer the 1st floor's eye height (4.6 m)
        Assert.Equal(new object[] { 2000, 10_000, "18 m · ≈ 5th floor view" }, cues[2]);
        Assert.Equal("23 m · ≈ 7th floor view", VideoRules.HeightCues(heights, 5m, Settings, null)[^1][2]); // took off 5 m above the plot
        Assert.Equal("52 m", VideoRules.HeightLabel(52, Settings));
        Assert.Equal("-4 m", VideoRules.HeightLabel(-4, Settings));
        Assert.Equal("6 m · ≈ 1st floor view", VideoRules.HeightLabel(6, Settings));
        Assert.Equal("37 m · ≈ 12th floor view", VideoRules.HeightLabel(37, Settings));
        Assert.Null(VideoRules.HeightsFromTrack(null));
        Assert.Empty(VideoRules.HeightCues(null, 0m, Settings, null));
    }

    [Fact]
    public void A_dji_srt_is_read_two_points_a_second()
    {
        var points = DjiSrt.Parse(Srt(6, 95.8));
        Assert.Equal(new[] { 0, 600 }, points.Select(p => p.Ms)); // cues every 200 ms thinned to at most 2 a second
        Assert.Equal((38.507449, 23.369184, 95.8), (points[0].Lat, points[0].Lng, points[0].RelAlt!.Value));
        Assert.Empty(DjiSrt.Parse("not a flight log"));
    }

    [Fact]
    public void A_worker_run_costs_its_seconds_at_the_list_prices_at_least_a_minute()
    {
        var t = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);
        // 4 vCPU + 8 GB + 30 GB disk above the 20 included, 5 minutes: (4*0.0356 + 8*0.00392 + 30*0.000111) / 12
        Assert.Equal(0.01476m, VideoRules.RunCost(t, t.AddMinutes(5), 4096, 8192, 50, Settings));
        Assert.Equal(VideoRules.RunCost(t, t.AddSeconds(60), 4096, 8192, 50, Settings), VideoRules.RunCost(t, t.AddSeconds(5), 4096, 8192, 50, Settings));
    }

    [Fact]
    public void The_worker_script_never_runs_unbounded_and_says_complete_last()
    {
        var script = FargateTranscoder.ShellScript;
        Assert.Contains("timeout \"$MAX_SECONDS\" ffmpeg", script);
        Assert.Contains("-movflags +faststart", script); // plays at once
        Assert.Contains("min(720,ih)", script); // never blown up
        Assert.Contains("-map \"0:a:0?\"", script); // sound when there is some, no failure when not
        Assert.EndsWith("say \"done in $(( $(date +%s) - t0 )) s\"", script.TrimEnd());
        Assert.True(script.IndexOf("result.json\" --content-type", StringComparison.Ordinal) > script.IndexOf("frames/\" --recursive", StringComparison.Ordinal));
        Assert.DoesNotContain("\r", script);
    }

    [Fact]
    public void A_job_without_sound_is_retried_without_audio()
    {
        Assert.True(VideoRules.IsNoAudioError("Input audio selector [Audio Selector 1] does not match any audio track"));
        Assert.False(VideoRules.IsNoAudioError("Access denied to s3"));
    }

    private static string Srt(int cues, double relAlt) => string.Join("\n\n", Enumerable.Range(0, cues).Select(i =>
        $"{i + 1}\n{T(i * 200)} --> {T(i * 200 + 200)}\n<font size=\"28\">SrtCnt : {i + 1}, DiffTime : 33ms\n2026-08-04 14:28:51.951\n" +
        $"[iso : 110] [shutter : 1/2500.0] [fnum : 170] [ev : 0] [ct : 4629] [color_md : default] [focal_len : 240] [dzoom_ratio: 10000, delta:0],[latitude: 38.507449] [longitude: 23.369184] [rel_alt: {relAlt:0.000} abs_alt: 138.549] </font>"));

    private static string T(int ms) => TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss\,fff", System.Globalization.CultureInfo.InvariantCulture);

    // ---- the flow, end to end (fake worker, recording storage) -------- ---------------------------------------------------

    private sealed class MemStorage : IObjectStorage
    {
        public Dictionary<string, long> Objects { get; } = new();
        public Dictionary<string, string> Texts { get; } = new();
        public bool IsConfigured => true;
        public Task<string> StartMultipartUploadAsync(string key, string contentType, string contentDisposition, CancellationToken ct = default) => Task.FromResult("u");
        public string GetPartUploadUrl(string key, string uploadId, int partNumber, TimeSpan lifetime) => "";
        public Task<IReadOnlyList<UploadedPart>?> ListUploadedPartsAsync(string key, string uploadId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UploadedPart>?>(null);
        public Task CompleteMultipartUploadAsync(string key, string uploadId, IReadOnlyList<UploadedPart> parts, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long?> GetObjectSizeAsync(string key, CancellationToken ct = default) => Task.FromResult(Objects.TryGetValue(key, out var s) ? s : (long?)null);
        public Task DeleteObjectAsync(string key, CancellationToken ct = default) { Objects.Remove(key); return Task.CompletedTask; }
        public Task CopyObjectAsync(string fromKey, string toKey, CancellationToken ct = default) { Objects[toKey] = Objects[fromKey]; return Task.CompletedTask; }
        public Task<IReadOnlyList<StoredObject>> ListAsync(string prefix, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredObject>>(Objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(o => new StoredObject(o.Key, o.Value, DateTimeOffset.UtcNow)).ToList());
        public Task<string?> ReadTextAsync(string key, int maxBytes, CancellationToken ct = default) => Task.FromResult(Texts.TryGetValue(key, out var t) ? t : null);
    }

    private sealed class FakeTranscoder : IVideoTranscoder
    {
        public List<VideoJobSpec> Jobs { get; } = new();
        public Dictionary<string, VideoJobState> States { get; } = new();
        public Task<string> SubmitAsync(VideoJobSpec spec, CancellationToken ct = default)
        {
            Jobs.Add(spec);
            var id = "job-" + Jobs.Count;
            States[id] = new VideoJobState("PROGRESSING", null, null);
            return Task.FromResult(id);
        }

        public Task<VideoJobState> GetAsync(string jobId, CancellationToken ct = default) =>
            Task.FromResult(States.TryGetValue(jobId, out var s) ? s : new VideoJobState("ERROR", "gone", null));

        public List<RunningWorker> Running { get; } = new();
        public List<string> Stopped { get; } = new();
        public Task<IReadOnlyList<RunningWorker>> ListRunningAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RunningWorker>>(Running.ToList());

        public Task StopAsync(string jobId, string reason, CancellationToken ct = default)
        {
            Stopped.Add(jobId);
            Running.RemoveAll(w => w.JobId == jobId);
            return Task.CompletedTask;
        }
    }

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    [Fact]
    public async Task A_video_is_converted_gets_a_poster_photos_and_heights_and_later_loses_its_original()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var db = ScratchMySql.NewDatabase("nadlan_v_");
        try
        {
            await new SchemaMigrator(db).MigrateAsync();
            var files = new MySqlFileAttachmentStore(db);
            var store = new MySqlVideoStore(db);
            var storage = new MemStorage();
            var mc = new FakeTranscoder();
            var clock = new Clock(new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc));
            var video = new VideoService(store, mc, storage, files, new MySqlMapDropStore(db), Settings, clock);
            var types = await files.ListTypesAsync();
            var droneVideo = types.First(t => t.Code == "DRONE_VIDEO");

            // A drone video and its .srt, both uploaded through the Files dialog of Parcel 7 (no track linked), and a PDF.
            async Task<long> AddAsync(string name, string mime, string key, long size, int typeId)
            {
                var id = await files.InsertPendingAsync(new FileAttachment { FileTypeId = typeId, AttachedToType = FileTargetTypes.Parcel, AttachedToId = 7, StorageKey = key,
                    OriginalFileName = name, MimeType = mime, FileSize = size, UploadedByUserId = 1 });
                await files.MarkReadyAsync(id);
                storage.Objects[key] = size;
                return id;
            }

            var vid = await AddAsync("DJI_0948.MP4", "video/mp4", "parcel/7/a.mp4", 1_190_124_045, droneVideo.Id);
            await AddAsync("DJI_0948.SRT", "application/x-subrip", "parcel/7/b.srt", 1000, types.First(t => t.Code == "DRONE_TRACK").Id);
            storage.Texts["parcel/7/b.srt"] = Srt(20, 18.0);
            await AddAsync("deed.pdf", "application/pdf", "parcel/7/c.pdf", 10, types.First().Id);

            // Round 1: queued (with the heights read from the .srt beside it) and sent.
            Assert.Equal("queued 1, sent 1, finished 0", await video.TickAsync());
            Assert.Equal(new VideoJobSpec("parcel/7/a.mp4", $"_renditions/{vid}/", true, null, null), mc.Jobs.Single());
            var r = (await store.GetAsync(vid))!;
            Assert.Equal((VideoStatuses.Submitted, 1, "parcel/7/a.mp4"), (r.Status, r.Attempts, r.OriginalKey));
            Assert.Equal("[[0,18]]", r.HeightsJson!.Replace(" ", "")); // MySQL writes JSON with spaces

            // No sound track: MediaConvert fails, and the next round sends it again without audio.
            mc.States["job-1"] = new VideoJobState("ERROR", "Input audio selector [Audio Selector 1] does not match any audio track", null);
            await video.TickAsync();
            await video.TickAsync();
            Assert.False(mc.Jobs[^1].WithAudio);

            // Done: two MP4s and a frame every 2 s (60 frames = 2 minutes), in two sizes.
            storage.Objects[VideoKeys.Small(vid)] = 100;
            storage.Objects[VideoKeys.Large(vid)] = 200;
            for (var i = 0; i < 60; i++)
            {
                storage.Objects[VideoKeys.Frame(vid, i, small: false)] = 5;
                storage.Objects[VideoKeys.Frame(vid, i, small: true)] = 1;
            }

            mc.States["job-2"] = new VideoJobState("COMPLETE", null, 120_000, clock.Now, clock.Now.AddMinutes(5));
            Assert.Equal("queued 0, sent 0, finished 1", await video.TickAsync());

            // Every worker run is kept for the Admin: the failed one and the finished one with its time and cost.
            var runs = await store.ListRunsAsync(10);
            Assert.Equal(new[] { ("job-1", "failed", (decimal?)null), ("job-2", "finished", (decimal?)0.01476m) },
                runs.OrderBy(x => x.JobId).Select(x => (x.JobId, x.StoppedBy!, x.CostUsd)));
            Assert.Equal(("DJI_0948.MP4", 4096, 8192, 50), (runs[0].FileName, runs[0].Cpu, runs[0].MemoryMb, runs[0].StorageGb));
            Assert.Empty(await store.ListOpenRunsAsync());
            r = (await store.GetAsync(vid))!;
            Assert.Equal((VideoStatuses.Ready, 60, 30, 120_000), (r.Status, r.FrameCount!.Value, r.PosterIndex!.Value, r.DurationMs!.Value));
            Assert.Equal(VideoRules.OfferedFrames(60, 10), r.OfferedFrames);
            Assert.True(storage.Objects.ContainsKey("parcel/7/a.mp4.thumb.jpg")); // the poster on every card
            var listed = (await files.ListReadyAsync(FileTargetTypes.Parcel, 7)).Single(f => f.FileAttachmentId == vid);
            Assert.Equal((VideoStatuses.Ready, VideoKeys.Small(vid), VideoKeys.Large(vid), true), (listed.VideoStatus, listed.VideoSmallKey, listed.VideoLargeKey, listed.HasThumbnail));

            // Two offered frames become drone photos of the same Parcel.
            var photo = types.First(t => t.Code == "DRONE_PHOTO");
            var added = await video.AddFramesAsync(vid, new[] { r.OfferedFrames[0], r.OfferedFrames[1] }, 1, photo);
            Assert.Equal(2, added.Count);
            var p1 = (await files.GetAsync(added[0]))!;
            Assert.Equal(("DJI_0948 00-06.jpg", photo.Id, FileUploadStatus.Ready, true), (p1.OriginalFileName, p1.FileTypeId, p1.UploadStatus, p1.HasThumbnail));
            await Assert.ThrowsAsync<Nadlan.Core.Validation.DomainValidationException>(() => video.AddFramesAsync(vid, new[] { 59 }, 1, photo)); // not offered

            // The take-off correction moves every height.
            await video.SetOffsetAsync(vid, 5m);
            Assert.Equal("23 m · ≈ 7th floor view", video.Cues(await store.GetAsync(vid))[0][2]);

            // Before the waiting period nothing is deleted; after it the file IS the 1080p and the original and frames are gone.
            Assert.Equal(0, await video.SweepAsync());
            clock.Now = clock.Now.AddDays(8);
            await using (var c = await db.OpenAsync())
            {
                await c.ExecuteAsync("UPDATE video_rendition SET completed_utc = @t WHERE file_attachment_id = @vid", new { t = clock.Now.AddDays(-8), vid });
            }

            Assert.Equal(1, await video.SweepAsync());
            var after = (await files.GetAsync(vid))!;
            Assert.Equal((VideoKeys.Large(vid), 200L, "video/mp4", "DJI_0948.mp4"), (after.StorageKey, after.FileSize, after.MimeType, after.OriginalFileName));
            Assert.False(storage.Objects.ContainsKey("parcel/7/a.mp4"));
            Assert.True(storage.Objects.ContainsKey(VideoKeys.Large(vid) + ".thumb.jpg"));
            Assert.DoesNotContain(storage.Objects.Keys, k => k.Contains("/frames/"));
            Assert.True(storage.Objects.ContainsKey((await files.GetAsync(added[0]))!.StorageKey)); // the kept photos stay
            Assert.NotNull((await store.GetAsync(vid))!.OriginalDeletedUtc);
            Assert.Equal(0, await video.SweepAsync()); // once

            // Deleting the file takes its light versions along (and the row, by the foreign key).
            await new FileService(files, new AlwaysExists(), storage, new FileStorageSettings()).DeleteAsync(vid);
            Assert.DoesNotContain(storage.Objects.Keys, k => k.StartsWith($"_renditions/{vid}/", StringComparison.Ordinal));
            Assert.Null(await store.GetAsync(vid));
        }
        finally
        {
            await ScratchMySql.DropAsync(db);
        }
    }

    [Fact]
    public async Task Nothing_happens_while_off_and_older_videos_wait_for_their_date()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var db = ScratchMySql.NewDatabase("nadlan_v_");
        try
        {
            await new SchemaMigrator(db).MigrateAsync();
            var files = new MySqlFileAttachmentStore(db);
            var storage = new MemStorage();
            var mc = new FakeTranscoder();
            var id = await files.InsertPendingAsync(new FileAttachment { FileTypeId = (await files.ListTypesAsync()).First().Id, AttachedToType = FileTargetTypes.Asset,
                AttachedToId = 3, StorageKey = "asset/3/x.mov", OriginalFileName = "x.MOV", MimeType = "video/quicktime", FileSize = 9 });
            await files.MarkReadyAsync(id);

            var off = new VideoService(new MySqlVideoStore(db), mc, storage, files, new MySqlMapDropStore(db), new VideoSettings { Enabled = false, Subnets = "subnet-1" });
            Assert.Null(await off.TickAsync());
            Assert.Equal(0, await off.SweepAsync());

            var later = new VideoService(new MySqlVideoStore(db), mc, storage, files, new MySqlMapDropStore(db),
                new VideoSettings { Enabled = true, Subnets = "subnet-1", QueueUploadedAfterUtc = DateTime.UtcNow.AddDays(1) });
            Assert.Null(await later.TickAsync()); // uploaded before the date: not by itself...
            Assert.True(await later.QueueOneAsync(id)); // ...but "Optimize" does
            Assert.False(await later.QueueOneAsync(id));
            Assert.Equal("queued 0, sent 1, finished 0", await later.TickAsync());
            Assert.Empty(mc.Jobs.Where(j => !j.WithAudio)); // a phone video: with sound
        }
        finally
        {
            await ScratchMySql.DropAsync(db);
        }
    }

    [Fact]
    public async Task No_worker_is_left_running()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var db = ScratchMySql.NewDatabase("nadlan_v_");
        try
        {
            await new SchemaMigrator(db).MigrateAsync();
            var files = new MySqlFileAttachmentStore(db);
            var store = new MySqlVideoStore(db);
            var mc = new FakeTranscoder();
            var clock = new Clock(DateTime.UtcNow);
            var video = new VideoService(store, mc, new MemStorage(), files, new MySqlMapDropStore(db), Settings with { MaxRunMinutes = 90 }, clock);
            async Task<long> VideoAsync(string key)
            {
                var id = await files.InsertPendingAsync(new FileAttachment { FileTypeId = (await files.ListTypesAsync()).First().Id, AttachedToType = FileTargetTypes.Parcel,
                    AttachedToId = 9, StorageKey = key, OriginalFileName = key, MimeType = "video/mp4", FileSize = 5 });
                await files.MarkReadyAsync(id);
                return id;
            }

            var quick = await VideoAsync("a.mp4");
            var stuck = await VideoAsync("b.mp4");
            Assert.Equal("queued 2, sent 2, finished 0", await video.TickAsync());
            mc.Running.Add(new RunningWorker("job-1", clock.Now.AddMinutes(-3)));    // working, fine
            mc.Running.Add(new RunningWorker("job-2", clock.Now.AddMinutes(-120)));  // running for 2 hours
            mc.Running.Add(new RunningWorker("job-x", clock.Now.AddMinutes(-1)));    // no video waits for it

            Assert.Equal(2, await video.CheckWorkersAsync());
            Assert.Equal(new[] { "job-2", "job-x" }, mc.Stopped.Order());
            Assert.Equal(VideoStatuses.Submitted, (await store.GetAsync(quick))!.Status);
            var failed = (await store.GetAsync(stuck))!;
            Assert.Equal((VideoStatuses.Failed, "checker: ran over 90 minutes"), (failed.Status, failed.ErrorMessage));
            var run = (await store.ListRunsAsync(10)).Single(r => r.JobId == "job-2");
            Assert.Equal("checker: ran over 90 minutes", run.StoppedBy);
            Assert.True(run.CostUsd > 0.05m); // two hours of a 4 vCPU worker

            // A run whose video went away (file deleted) is closed by the next check, never left open.
            mc.Running.Clear();
            await using (var c = await db.OpenAsync())
            {
                await c.ExecuteAsync("DELETE FROM file_attachment WHERE file_attachment_id = @quick", new { quick });
            }

            Assert.Equal(0, await video.CheckWorkersAsync());
            Assert.Empty(await store.ListOpenRunsAsync());
        }
        finally
        {
            await ScratchMySql.DropAsync(db);
        }
    }

    private sealed class AlwaysExists : IFileTargetResolver
    {
        public Task<bool> ExistsAsync(string attachedToType, long attachedToId, CancellationToken ct = default) => Task.FromResult(true);
    }
}
