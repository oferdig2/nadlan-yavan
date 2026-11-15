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

    public bool IsActive => _settings.Enabled && _storage.IsConfigured && !string.IsNullOrWhiteSpace(_settings.RoleArn);

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

        var sent = 0;
        foreach (var r in await _store.ListByStatusAsync(VideoStatuses.Queued, 5, ct))
        {
            sent += await SubmitAsync(r, ct) ? 1 : 0;
        }

        var done = 0;
        foreach (var r in await _store.ListByStatusAsync(VideoStatuses.Submitted, 50, ct))
        {
            done += await CollectAsync(r, ct) ? 1 : 0;
        }

        return queued + sent + done == 0 ? null : $"queued {queued}, sent {sent}, finished {done}";
    }

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
            throw new DomainValidationException("VIDEO_OFF", "Video optimization is off (Admin > Settings > Videos: on, role, region).");
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

    private async Task<bool> SubmitAsync(VideoRendition r, CancellationToken ct)
    {
        var c = await _store.GetCandidateAsync(r.FileAttachmentId, ct);
        if (c is null)
        {
            await _store.MarkFailedAsync(r.FileAttachmentId, "The file is no longer a video on a parcel or an asset.", ct);
            return false;
        }

        try
        {
            var jobId = await _transcoder.SubmitAsync(new VideoJobSpec(c.StorageKey, VideoKeys.Prefix(c.FileAttachmentId), r.WithAudio, c.Width, c.Height), ct);
            await _store.MarkSubmittedAsync(r.FileAttachmentId, jobId, r.WithAudio, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (r.Attempts + 1 >= MaxAttempts)
            {
                await _store.MarkFailedAsync(r.FileAttachmentId, "Could not start: " + ex.Message, ct);
            }
            else
            {
                await _store.CountAttemptAsync(r.FileAttachmentId, "Could not start: " + ex.Message, ct); // tried again next round
            }

            return false;
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

        await _store.MarkReadyAsync(id, new VideoResult(VideoKeys.Small(id), small.Value, VideoKeys.Large(id), large.Value, state.DurationMs, count, offered, poster), ct);
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
            var name = $"{Path.GetFileNameWithoutExtension(file.OriginalFileName)} {(int)at.TotalMinutes:00}-{at.Seconds:00}.jpg";
            var key = $"{file.AttachedToType.ToLowerInvariant()}/{file.AttachedToId}/{Guid.NewGuid():N}.jpg";
            await _storage.CopyObjectAsync(full, key, ct);
            await _storage.CopyObjectAsync(VideoKeys.Frame(fileAttachmentId, i, small: true), FileAttachment.ThumbnailKey(key), ct);
            var id = await _files.InsertPendingAsync(new FileAttachment
            {
                FileTypeId = photoType.Id,
                AttachedToType = file.AttachedToType,
                AttachedToId = file.AttachedToId,
                StorageKey = key,
                OriginalFileName = name,
                MimeType = "image/jpeg",
                FileSize = size.Value,
                UploadStatus = FileUploadStatus.Pending,
                UploadedByUserId = userId,
            }, ct);
            await _files.MarkReadyAsync(id, ct);
            await _files.SetHasThumbnailAsync(id, ct);
            await _files.UpdateMetadataAsync(id, photoType.Id, null,
                $"Frame at {(int)at.TotalMinutes}:{at.Seconds:00} of the video {file.OriginalFileName}.", null, ct);
            ids.Add(id);
        }

        return ids;
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
