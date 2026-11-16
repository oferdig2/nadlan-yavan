using System.Globalization;
using System.Text.Json;
using Nadlan.Core.Files;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Video;

/// <summary>
/// The conversion steps, run by a background job every 30 s (<see cref="TickAsync"/>) and once a day (<see cref="SweepAsync"/>).
/// Every step is safe to repeat: a crash between two steps leaves a row the next round picks up again.
/// </summary>
public sealed class VideoService
{
    public const int MaxAttempts = 3;

    /// <summary>A .srt is read whole on the server: DJI logs are ~1 MB per 4 minutes.</summary>
    public const int MaxSrtBytes = 16 * 1024 * 1024;

    private readonly IVideoStore _store;
    private readonly IVideoTranscoder _transcoder;
    private readonly IObjectStorage _storage;
    private readonly IFileAttachmentStore _files;
    private readonly IMapDropStore _media;
    private readonly VideoSettings _settings;
    private readonly TimeProvider _clock;

    public VideoService(IVideoStore store, IVideoTranscoder transcoder, IObjectStorage storage, IFileAttachmentStore files, IMapDropStore media,
        VideoSettings settings, TimeProvider? clock = null)
    {
        _store = store;
        _transcoder = transcoder;
        _storage = storage;
        _files = files;
        _media = media;
        _settings = settings;
        _clock = clock ?? TimeProvider.System;
    }

    public VideoSettings Settings => _settings;

    public bool IsActive => _settings.Enabled && _storage.IsConfigured && !string.IsNullOrWhiteSpace(_settings.Cluster)
                            && !string.IsNullOrWhiteSpace(_settings.TaskDefinition) && !string.IsNullOrWhiteSpace(_settings.Subnets);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>One round: queue new uploads, send queued jobs, collect finished ones. Returns what happened, for the log.</summary>
    public async Task<string?> TickAsync(CancellationToken ct = default)
    {
        if (!IsActive)
        {
            return null;
        }

        var queued = 0;
        foreach (var c in await _store.ListUnqueuedAsync(_settings.QueueUploadedAfterUtc, 20, ct))
        {
            queued += await QueueCandidateAsync(c, ct) ? 1 : 0;
        }

        // At most MaxWorkers at once (review 2026-11-16: 7 at once hit the account's vCPU limit); the rest wait their turn.
        // A frame being caught counts too.
        var sent = 0;
        var room = Math.Max(1, _settings.MaxWorkers) - await BusyWorkersAsync(ct);
        foreach (var r in room > 0 ? await _store.ListByStatusAsync(VideoStatuses.Queued, room, ct) : Array.Empty<VideoRendition>())
        {
            var result = await SubmitAsync(r, ct);
            if (result < 0)
            {
                break; // AWS has no room right now: try again next round
            }

            sent += result;
        }

        var done = 0;
        foreach (var r in await _store.ListByStatusAsync(VideoStatuses.Submitted, 50, ct))
        {
            done += await CollectAsync(r, ct) ? 1 : 0;
        }

        var caught = await CaptureRoundAsync(ct);
        return queued + sent + done + caught == 0 ? null : $"queued {queued}, sent {sent}, finished {done}" + (caught > 0 ? $", frames caught {caught}" : "");
    }

    /// <summary>Workers running now: conversions plus capture runs.</summary>
    private async Task<int> BusyWorkersAsync(CancellationToken ct) =>
        (await _store.ListByStatusAsync(VideoStatuses.Submitted, 500, ct)).Count
        + (await _store.ListCapturesByStatusAsync(VideoCaptureStatuses.Running, 500, ct)).Select(c => c.JobId).Distinct().Count();

    /// <summary>"Optimize" one video now (also one uploaded before the feature). False if it is already converted or on its way.</summary>
    public async Task<bool> QueueOneAsync(long fileAttachmentId, CancellationToken ct = default)
    {
        RequireActive();
        var c = await _store.GetCandidateAsync(fileAttachmentId, ct)
                ?? throw new DomainValidationException("VIDEO_NOT_CONVERTIBLE", "Only a video on a parcel or an asset can be optimized.");
        var existing = await _store.GetAsync(fileAttachmentId, ct);
        if (existing is { Status: VideoStatuses.Failed })
        {
            await _store.RequeueAsync(fileAttachmentId, existing.WithAudio, ct); // "Try again" after a failure
            return true;
        }

        return existing is null && await QueueCandidateAsync(c, ct);
    }

    /// <summary>Every video on a Parcel or Asset not converted yet (the ones uploaded before the feature). Returns how many were queued.</summary>
    public async Task<int> QueueAllAsync(CancellationToken ct = default)
    {
        RequireActive();
        var n = 0;
        foreach (var c in await _store.ListUnqueuedAsync(null, 1000, ct))
        {
            n += await QueueCandidateAsync(c, ct) ? 1 : 0;
        }

        return n;
    }

    private void RequireActive()
    {
        if (!IsActive)
        {
            throw new DomainValidationException("VIDEO_OFF", "Video optimization is off (Admin > Settings > Videos: on, cluster, subnets).");
        }
    }

    private async Task<bool> QueueCandidateAsync(VideoCandidate c, CancellationToken ct)
    {
        var track = await TrackOfAsync(c, ct);
        return await _store.QueueAsync(c, VideoRules.HeightsFromTrack(track), ct);
    }

    /// <summary>
    /// The video's flight track: the one stored for it, else read from a DJI .srt of the same name on the same Parcel / Asset
    /// (DJI_0948.SRT beside DJI_0948.MP4, both uploaded through the Files dialog) - and then stored for it.
    /// </summary>
    internal async Task<string?> TrackOfAsync(VideoCandidate c, CancellationToken ct)
    {
        var meta = await _media.GetMetadataAsync(c.FileAttachmentId, ct);
        if (!string.IsNullOrEmpty(meta?.TrackJson))
        {
            return meta.TrackJson;
        }

        var stem = Path.GetFileNameWithoutExtension(c.OriginalFileName);
        var srt = (await _files.ListReadyAsync(c.AttachedToType, c.AttachedToId, ct))
            .FirstOrDefault(f => f.OriginalFileName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(Path.GetFileNameWithoutExtension(f.OriginalFileName), stem, StringComparison.OrdinalIgnoreCase));
        if (srt is null)
        {
            return null;
        }

        var text = await _storage.ReadTextAsync(srt.StorageKey, MaxSrtBytes, ct);
        var points = text is null ? Array.Empty<TrackPoint>() : DjiSrt.Parse(text);
        if (points.Count < 2)
        {
            return null;
        }

        var json = DjiSrt.Serialize(points);
        await _media.UpsertMetadataAsync(c.FileAttachmentId, (meta ?? new MediaMetadata()) with { TrackFileId = srt.FileAttachmentId, TrackJson = json }, ct);
        return json;
    }

    /// <summary>1 = a worker started, 0 = not (failed or given up), -1 = AWS has no room right now (the video stays queued).</summary>
    private async Task<int> SubmitAsync(VideoRendition r, CancellationToken ct)
    {
        var c = await _store.GetCandidateAsync(r.FileAttachmentId, ct);
        if (c is null)
        {
            await _store.MarkFailedAsync(r.FileAttachmentId, "The file is no longer a video on a parcel or an asset.", ct);
            return 0;
        }

        // No worker (and no cost) for a file whose bytes are gone (review 2026-11-16: three old test uploads with no object).
        if (await _storage.GetObjectSizeAsync(c.StorageKey, ct) is not > 0)
        {
            await _store.MarkFailedAsync(r.FileAttachmentId, "The original file is missing in storage: nothing to convert.", ct);
            return 0;
        }

        string jobId;
        try
        {
            jobId = await _transcoder.SubmitAsync(new VideoJobSpec(c.StorageKey, VideoKeys.Prefix(c.FileAttachmentId), r.WithAudio, c.Width, c.Height), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (VideoRules.IsCapacityError(ex.Message))
            {
                return -1; // not the video's fault: no attempt counted
            }

            if (r.Attempts + 1 >= MaxAttempts)
            {
                await _store.MarkFailedAsync(r.FileAttachmentId, "Could not start: " + ex.Message, ct);
            }
            else
            {
                await _store.CountAttemptAsync(r.FileAttachmentId, "Could not start: " + ex.Message, ct); // tried again next round
            }

            return 0;
        }

        // A worker runs (and costs) from here on: record it first; if that fails, stop it at once and give up on the video - never
        // start the same video again while a worker we lost track of goes on (review 2026-11-15: three workers per video).
        try
        {
            await _store.StartRunAsync(jobId, r.FileAttachmentId, _settings.Cpu, _settings.MemoryMb, _settings.StorageGb, ct);
            await _store.MarkSubmittedAsync(r.FileAttachmentId, jobId, r.WithAudio, ct);
            return 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                await _transcoder.StopAsync(jobId, "the app could not record this job", ct);
            }
            catch (Exception)
            {
                // the checker stops it within 5 minutes
            }

            await _store.MarkFailedAsync(r.FileAttachmentId, "Could not record the job: " + ex.Message, ct);
            return 0;
        }
    }

    private async Task<bool> CollectAsync(VideoRendition r, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(r.JobId))
        {
            await _store.RequeueAsync(r.FileAttachmentId, r.WithAudio, ct);
            return false;
        }

        var state = await _transcoder.GetAsync(r.JobId, ct);
        if (state.Status is "COMPLETE" or "ERROR" or "CANCELED")
        {
            await EndRunAsync(r.JobId, state, state.Status == "COMPLETE" ? "finished" : "failed", ct);
        }

        switch (state.Status)
        {
            case "COMPLETE":
                return await FinishAsync(r, state, ct);
            case "ERROR" or "CANCELED":
                if (r.WithAudio && VideoRules.IsNoAudioError(state.ErrorMessage))
                {
                    await _store.RequeueAsync(r.FileAttachmentId, withAudio: false, ct); // a drone video has no sound track
                }
                else if (r.Attempts < MaxAttempts && state.Status == "ERROR")
                {
                    await _store.RequeueAsync(r.FileAttachmentId, r.WithAudio, ct);
                }
                else
                {
                    await _store.MarkFailedAsync(r.FileAttachmentId, state.ErrorMessage ?? state.Status, ct);
                }

                return false;
            default:
                return false; // still working
        }
    }

    private Task EndRunAsync(string jobId, VideoJobState state, string stoppedBy, CancellationToken ct) =>
        EndRunAsync(jobId, state, stoppedBy, _settings.Cpu, _settings.MemoryMb, _settings.StorageGb, ct);

    /// <summary>Closes a run with its cost, at the size that run had (a capture worker is a quarter of a conversion worker).</summary>
    private Task EndRunAsync(string jobId, VideoJobState state, string stoppedBy, int cpu, int memoryMb, int storageGb, CancellationToken ct)
    {
        decimal? cost = state.StartedUtc is { } a && state.StoppedUtc is { } b
            ? VideoRules.RunCost(a, b, cpu, memoryMb, storageGb, _settings) : null;
        return _store.EndRunAsync(jobId, state.StartedUtc, state.StoppedUtc, state.Status, stoppedBy, cost, ct);
    }

    /// <summary>
    /// Every few minutes: no worker may be left running (owner, 2026-11-15). A worker no job waits for, or one running longer than
    /// <see cref="VideoSettings.MaxRunMinutes"/>, is stopped (its video is marked failed); a run whose worker is gone is closed.
    /// Returns how many workers were stopped.
    /// </summary>
    public async Task<int> CheckWorkersAsync(CancellationToken ct = default)
    {
        if (!IsActive)
        {
            return 0;
        }

        var waiting = (await _store.ListByStatusAsync(VideoStatuses.Submitted, 500, ct)).Where(r => !string.IsNullOrEmpty(r.JobId))
            .ToDictionary(r => r.JobId!, r => r);
        var catching = (await _store.ListCapturesByStatusAsync(VideoCaptureStatuses.Running, 500, ct)).Where(c => !string.IsNullOrEmpty(c.JobId))
            .GroupBy(c => c.JobId!).ToDictionary(g => g.Key, g => g.ToList());
        var open = (await _store.ListOpenRunsAsync(ct)).ToDictionary(r => r.JobId);
        var running = await _transcoder.ListRunningAsync(ct);
        var stopped = 0;
        foreach (var w in running)
        {
            var capture = catching.ContainsKey(w.JobId);
            var limit = capture ? CaptureMaxRunMinutes : Math.Max(5, _settings.MaxRunMinutes);
            var tooLong = w.StartedUtc is { } s && Now - s > TimeSpan.FromMinutes(limit);
            var orphan = !waiting.ContainsKey(w.JobId) && !capture;
            if (!tooLong && !orphan)
            {
                continue;
            }

            var reason = orphan ? "checker: no video waits for this worker" : $"checker: ran over {limit} minutes";
            await _transcoder.StopAsync(w.JobId, reason, ct);
            await _store.StartRunAsync(w.JobId, null, _settings.Cpu, _settings.MemoryMb, _settings.StorageGb, ct); // a worker we had no record of: its cost shows too
            var size = open.TryGetValue(w.JobId, out var run) ? (run.Cpu, run.MemoryMb, run.StorageGb) : (_settings.Cpu, _settings.MemoryMb, _settings.StorageGb);
            await EndRunAsync(w.JobId, new VideoJobState("STOPPED", reason, null, w.StartedUtc, Now), reason, size.Item1, size.Item2, size.Item3, ct);
            if (waiting.TryGetValue(w.JobId, out var r))
            {
                await _store.MarkFailedAsync(r.FileAttachmentId, reason, ct);
            }

            foreach (var c in catching.GetValueOrDefault(w.JobId) ?? new List<VideoCapture>())
            {
                await _store.MarkCaptureFailedAsync(c.CaptureId, reason, ct);
            }

            stopped++;
        }

        // Runs still open whose worker is no longer running and no video or frame waits for (e.g. the file was deleted meanwhile).
        var live = running.Select(w => w.JobId).ToHashSet();
        foreach (var run in open.Values)
        {
            if (!live.Contains(run.JobId) && !waiting.ContainsKey(run.JobId) && !catching.ContainsKey(run.JobId))
            {
                await EndRunAsync(run.JobId, await _transcoder.GetAsync(run.JobId, ct), "closed by the checker", run.Cpu, run.MemoryMb, run.StorageGb, ct);
            }
        }

        return stopped;
    }

    /// <summary>Verifies the outputs, picks the poster and the offered photos, and puts the poster on the file's card.</summary>
    private async Task<bool> FinishAsync(VideoRendition r, VideoJobState state, CancellationToken ct)
    {
        var id = r.FileAttachmentId;
        var small = await _storage.GetObjectSizeAsync(VideoKeys.Small(id), ct);
        var large = await _storage.GetObjectSizeAsync(VideoKeys.Large(id), ct);
        if (small is not > 0 || large is not > 0)
        {
            await _store.MarkFailedAsync(id, "The job finished but its MP4 files are missing or empty.", ct);
            return false;
        }

        var frames = VideoKeys.FullFrames((await _storage.ListAsync(VideoKeys.Prefix(id) + "frames/", ct)).Select(o => o.Key));
        var count = frames.Count == 0 ? 0 : frames[^1] + 1;
        var offered = VideoRules.OfferedFrames(count, Math.Clamp(_settings.FrameCount, 0, 30));
        int? poster = null;
        var file = await _files.GetAsync(id, ct);
        if (count > 0 && file is not null)
        {
            IReadOnlyList<FileSegment>? home = null;
            if (file.AttachedToType == FileTargetTypes.Parcel
                && (await _media.ListSegmentsOnParcelAsync(file.AttachedToId, ct)).TryGetValue(id, out var segs))
            {
                home = segs;
            }

            poster = VideoRules.PosterFrame(count, home);
            var posterKey = VideoKeys.Frame(id, poster.Value, small: true);
            if (await _storage.GetObjectSizeAsync(posterKey, ct) is > 0)
            {
                // The poster replaces the phone-made preview on every card (it lives next to whatever key the file has).
                await _storage.CopyObjectAsync(posterKey, FileAttachment.ThumbnailKey(file.StorageKey), ct);
                await _files.SetHasThumbnailAsync(id, ct);
            }
        }

        if (poster is int pi && !offered.Contains(pi))
        {
            offered = offered.Append(pi).Order().ToList(); // the poster is always among the photos offered (review 2026-11-15)
        }

        // An original that already plays everywhere and is not bigger stays the file; the copies that add nothing are removed.
        string largeKey = VideoKeys.Large(id), smallKey = VideoKeys.Small(id);
        long largeSize = large.Value, smallSize = small.Value;
        if (r.OriginalKey is not null && r.OriginalSize is long os && VideoRules.KeepOriginal(state.SourceCodec, large.Value, os))
        {
            await _storage.DeleteObjectAsync(largeKey, ct);
            (largeKey, largeSize) = (r.OriginalKey, os);
            if (VideoRules.KeepOriginal(state.SourceCodec, small.Value, os))
            {
                await _storage.DeleteObjectAsync(smallKey, ct);
                (smallKey, smallSize) = (r.OriginalKey, os);
            }
        }

        await _store.MarkReadyAsync(id, new VideoResult(smallKey, smallSize, largeKey, largeSize, state.DurationMs, count, offered, poster), ct);
        return true;
    }

    /// <summary>
    /// Once a day: originals of conversions verified more than <see cref="VideoSettings.OriginalRetentionDays"/> ago are deleted
    /// (owner, 2026-11-11: "customer doesn't want to pay tons for storage"); the file then IS its 1080p MP4. The frames go too
    /// (the photos someone added are copies). Returns how many originals were deleted.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        if (!_settings.Enabled || !_storage.IsConfigured)
        {
            return 0;
        }

        var n = 0;
        foreach (var r in await _store.ListOriginalsDueAsync(Now.AddDays(-Math.Max(1, _settings.OriginalRetentionDays)), 50, ct))
        {
            var file = await _files.GetAsync(r.FileAttachmentId, ct);
            var large = r.LargeKey is null ? null : await _storage.GetObjectSizeAsync(r.LargeKey, ct);
            if (file is null || r.LargeKey is null || large is not > 0 || r.OriginalKey is null)
            {
                continue; // never delete an original without its verified replacement
            }

            // The original IS the kept file (it played everywhere already), or its copy is not smaller (format unknown: the copy
            // stays for playing, the original for keeping): only the frames go.
            if (r.LargeKey == r.OriginalKey || (r.OriginalSize is long os && large.Value >= os))
            {
                foreach (var o in await _storage.ListAsync(VideoKeys.Prefix(r.FileAttachmentId) + "frames/", ct))
                {
                    await _storage.DeleteObjectAsync(o.Key, ct);
                }

                await _store.MarkFramesDeletedAsync(r.FileAttachmentId, ct);
                continue;
            }

            if (file.StorageKey == r.OriginalKey)
            {
                if (file.HasThumbnail)
                {
                    await _storage.CopyObjectAsync(FileAttachment.ThumbnailKey(r.OriginalKey), FileAttachment.ThumbnailKey(r.LargeKey), ct);
                }

                var name = Path.GetFileNameWithoutExtension(file.OriginalFileName) + ".mp4";
                if (!await _store.SwitchToLargeAsync(r.FileAttachmentId, r.OriginalKey, r.LargeKey, large.Value, name, ct))
                {
                    continue;
                }
            }

            await _storage.DeleteObjectAsync(r.OriginalKey, ct);
            await _storage.DeleteObjectAsync(FileAttachment.ThumbnailKey(r.OriginalKey), ct);
            foreach (var o in await _storage.ListAsync(VideoKeys.Prefix(r.FileAttachmentId) + "frames/", ct))
            {
                await _storage.DeleteObjectAsync(o.Key, ct);
            }

            await _store.MarkOriginalDeletedAsync(r.FileAttachmentId, ct);
            n++;
        }

        return n;
    }

    /// <summary>
    /// Offered frames become photos of the video's Parcel / Asset (copies: they stay when the frames are swept). Returns the new
    /// file ids. The caller checked the right to upload there.
    /// </summary>
    public async Task<IReadOnlyList<long>> AddFramesAsync(long fileAttachmentId, IReadOnlyList<int> indexes, long userId, FileType photoType, CancellationToken ct = default)
    {
        var r = await _store.GetAsync(fileAttachmentId, ct);
        var file = await _files.GetAsync(fileAttachmentId, ct);
        if (r is not { Status: VideoStatuses.Ready } || file is null || r.FramesDeletedUtc is not null)
        {
            throw new DomainValidationException("VIDEO_FRAMES_GONE", "This video's frames are not available (not converted yet, or removed after the waiting period).");
        }

        var offered = r.OfferedFrames;
        var ids = new List<long>();
        foreach (var i in indexes.Distinct())
        {
            if (!offered.Contains(i))
            {
                throw new DomainValidationException("VIDEO_FRAME_UNKNOWN", "Choose among the frames shown under the video.");
            }

            var full = VideoKeys.Frame(fileAttachmentId, i, small: false);
            var size = await _storage.GetObjectSizeAsync(full, ct);
            if (size is not > 0)
            {
                continue;
            }

            var at = TimeSpan.FromMilliseconds((long)i * VideoSettings.FrameIntervalMs);
            ids.Add(await AddPhotoAsync(file, full, VideoKeys.Frame(fileAttachmentId, i, small: true), size.Value, at, photoType.Id, userId,
                $"Frame at {(int)at.TotalMinutes}:{at.Seconds:00} of the video {file.OriginalFileName}.", ct));
        }

        return ids;
    }

    /// <summary>A frame (copied, so it outlives the frames and the original) becomes a photo of the video's Parcel / Asset.</summary>
    private async Task<long> AddPhotoAsync(FileAttachment video, string fullKey, string smallKey, long size, TimeSpan at, int photoTypeId, long? userId,
        string description, CancellationToken ct)
    {
        var name = $"{Path.GetFileNameWithoutExtension(video.OriginalFileName)} {(int)at.TotalMinutes:00}-{at.Seconds:00}.jpg";
        var key = $"{video.AttachedToType.ToLowerInvariant()}/{video.AttachedToId}/{Guid.NewGuid():N}.jpg";
        await _storage.CopyObjectAsync(fullKey, key, ct);
        await _storage.CopyObjectAsync(smallKey, FileAttachment.ThumbnailKey(key), ct);
        var id = await _files.InsertPendingAsync(new FileAttachment
        {
            FileTypeId = photoTypeId,
            AttachedToType = video.AttachedToType,
            AttachedToId = video.AttachedToId,
            StorageKey = key,
            OriginalFileName = name,
            MimeType = "image/jpeg",
            FileSize = size,
            UploadStatus = FileUploadStatus.Pending,
            UploadedByUserId = userId,
        }, ct);
        await _files.MarkReadyAsync(id, ct);
        await _files.SetHasThumbnailAsync(id, ct);
        await _files.UpdateMetadataAsync(id, photoTypeId, null, description, null, ct);
        return id;
    }

    // ---- Catch image (owner, 2026-11-16) ----

    /// <summary>A capture worker that runs longer than this is stopped (one frame takes seconds; 40 take a few minutes).</summary>
    public const int CaptureMaxRunMinutes = 20;

    /// <summary>The original of this video is still in storage: Hi-res and Catch image are offered (for Admin / Data person).</summary>
    public static bool HasOriginal(VideoRendition? r) => r is { OriginalKey: not null, OriginalDeletedUtc: null } && r.Status != VideoStatuses.Failed;

    /// <summary>
    /// "Catch image": records the moment; a small worker cuts that exact frame from the original at full size within about a minute
    /// and it becomes a photo of the video's Parcel / Asset. The caller checked the right to add that photo there.
    /// </summary>
    public async Task<long> RequestCaptureAsync(long fileAttachmentId, int atMs, FileType photoType, long userId, CancellationToken ct = default)
    {
        RequireActive();
        var r = await _store.GetAsync(fileAttachmentId, ct);
        if (!HasOriginal(r) || await _files.GetAsync(fileAttachmentId, ct) is null)
        {
            throw new DomainValidationException("VIDEO_ORIGINAL_GONE",
                $"The original of this video is no longer kept (only {_settings.OriginalRetentionDays} days after it is converted). Add one of the photos under the video instead.");
        }

        if (atMs < 0 || (r!.DurationMs is int d && d > 0 && atMs > d))
        {
            throw new DomainValidationException("VIDEO_CAPTURE_TIME", "That moment is outside the video.");
        }

        var waiting = (await _store.ListCapturesAsync(fileAttachmentId, 200, ct)).Count(c => c.Status is VideoCaptureStatuses.Pending or VideoCaptureStatuses.Running);
        if (waiting >= VideoCaptureWorker.MaxPerRun)
        {
            throw new DomainValidationException("VIDEO_CAPTURE_BUSY", $"{waiting} images of this video are still being caught. Wait a minute and try again.");
        }

        return await _store.AddCaptureAsync(fileAttachmentId, atMs, photoType.Id, userId, ct);
    }

    /// <summary>Collects finished capture runs, then starts one for each video with frames waiting. Returns the photos added.</summary>
    private async Task<int> CaptureRoundAsync(CancellationToken ct)
    {
        var added = 0;
        foreach (var job in (await _store.ListCapturesByStatusAsync(VideoCaptureStatuses.Running, 500, ct)).GroupBy(c => c.JobId ?? ""))
        {
            added += await CollectCapturesAsync(job.Key, job.ToList(), ct);
        }

        // A frame is waited for by a person: captures may use one worker more than conversions (a quarter of the size of one).
        var busy = await BusyWorkersAsync(ct);
        foreach (var video in (await _store.ListCapturesByStatusAsync(VideoCaptureStatuses.Pending, 500, ct)).GroupBy(c => c.FileAttachmentId))
        {
            if (busy >= Math.Max(1, _settings.MaxWorkers) + 1)
            {
                break;
            }

            var result = await SubmitCapturesAsync(video.Key, video.Take(VideoCaptureWorker.MaxPerRun).ToList(), ct);
            if (result < 0)
            {
                break; // AWS has no room right now: next round
            }

            busy += result;
        }

        return added;
    }

    /// <summary>1 = a worker started, 0 = not (the captures failed), -1 = AWS has no room right now (they stay pending).</summary>
    private async Task<int> SubmitCapturesAsync(long fileAttachmentId, IReadOnlyList<VideoCapture> captures, CancellationToken ct)
    {
        async Task FailAllAsync(string message)
        {
            foreach (var c in captures)
            {
                await _store.MarkCaptureFailedAsync(c.CaptureId, message, ct);
            }
        }

        var r = await _store.GetAsync(fileAttachmentId, ct);
        if (!HasOriginal(r) || await _storage.GetObjectSizeAsync(r!.OriginalKey!, ct) is not > 0)
        {
            await FailAllAsync("The original of this video is no longer in storage.");
            return 0;
        }

        var ids = captures.Select(c => c.CaptureId).ToList();
        string jobId;
        try
        {
            jobId = await _transcoder.SubmitCaptureAsync(r.OriginalKey!, VideoCaptureKeys.RunPrefix(fileAttachmentId, ids.Min()),
                captures.Select(c => c.AtMs).Distinct().Order().ToList(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (VideoRules.IsCapacityError(ex.Message))
            {
                return -1;
            }

            await FailAllAsync("Could not start: " + ex.Message);
            return 0;
        }

        try
        {
            await _store.StartRunAsync(jobId, fileAttachmentId, VideoCaptureWorker.Cpu, VideoCaptureWorker.MemoryMb, VideoCaptureWorker.StorageGb, ct);
            await _store.MarkCapturesRunningAsync(ids, jobId, ct);
            return 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                await _transcoder.StopAsync(jobId, "the app could not record this job", ct);
            }
            catch (Exception)
            {
                // the checker stops it within 5 minutes
            }

            await FailAllAsync("Could not record the job: " + ex.Message);
            return 0;
        }
    }

    private async Task<int> CollectCapturesAsync(string jobId, IReadOnlyList<VideoCapture> captures, CancellationToken ct)
    {
        if (jobId.Length == 0)
        {
            foreach (var c in captures)
            {
                await _store.MarkCaptureFailedAsync(c.CaptureId, "The worker was not recorded.", ct);
            }

            return 0;
        }

        var state = await _transcoder.GetAsync(jobId, ct);
        if (state.Status is not ("COMPLETE" or "ERROR" or "CANCELED"))
        {
            return 0; // still working
        }

        await EndRunAsync(jobId, state, state.Status == "COMPLETE" ? "finished" : "failed", VideoCaptureWorker.Cpu, VideoCaptureWorker.MemoryMb,
            VideoCaptureWorker.StorageGb, ct);
        var fileId = captures[0].FileAttachmentId;
        var prefix = VideoCaptureKeys.RunPrefix(fileId, captures.Min(c => c.CaptureId));
        var file = await _files.GetAsync(fileId, ct);
        var added = 0;
        foreach (var c in captures)
        {
            var at = TimeSpan.FromMilliseconds(c.AtMs);
            var when = $"{(int)at.TotalMinutes}:{at.Seconds:00}.{at.Milliseconds / 100}";
            var size = state.Status == "COMPLETE" ? await _storage.GetObjectSizeAsync(VideoCaptureKeys.Full(prefix, c.AtMs), ct) : null;
            if (state.Status != "COMPLETE" || file is null)
            {
                await _store.MarkCaptureFailedAsync(c.CaptureId, file is null ? "The video was deleted." : state.ErrorMessage ?? state.Status, ct);
            }
            else if (size is not > 0)
            {
                await _store.MarkCaptureFailedAsync(c.CaptureId, $"No frame at {when} (past the end of the video?).", ct);
            }
            else
            {
                var id = await AddPhotoAsync(file, VideoCaptureKeys.Full(prefix, c.AtMs), VideoCaptureKeys.Small(prefix, c.AtMs), size.Value, at,
                    c.FileTypeId, c.RequestedByUserId, $"Caught at {when} from the original of the video {file.OriginalFileName} (full size).", ct);
                await _store.MarkCaptureDoneAsync(c.CaptureId, id, ct);
                added++;
            }
        }

        // The photos are copies: the run's folder goes.
        foreach (var o in await _storage.ListAsync(prefix, ct))
        {
            await _storage.DeleteObjectAsync(o.Key, ct);
        }

        return added;
    }

    public async Task SetOffsetAsync(long fileAttachmentId, decimal offsetM, CancellationToken ct = default)
    {
        if (offsetM is < -500 or > 500)
        {
            throw new DomainValidationException("VIDEO_OFFSET", "The take-off correction must be between -500 and 500 metres.");
        }

        if (await _store.GetAsync(fileAttachmentId, ct) is null)
        {
            throw new DomainValidationException("VIDEO_NOT_QUEUED", "Optimize the video first.");
        }

        await _store.SetOffsetAsync(fileAttachmentId, Math.Round(offsetM, 1), ct);
    }

    /// <summary>The subtitle cues of a video (empty without a flight track).</summary>
    public IReadOnlyList<object[]> Cues(VideoRendition? r) =>
        r is null ? Array.Empty<object[]>() : VideoRules.HeightCues(r.HeightsJson, r.TakeoffOffsetM, _settings, r.DurationMs);

    public static string FrameTimeText(int index) =>
        TimeSpan.FromMilliseconds((long)index * VideoSettings.FrameIntervalMs).ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
