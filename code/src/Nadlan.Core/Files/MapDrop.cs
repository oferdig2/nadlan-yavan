using System.Globalization;
using System.Text.Json;
using Nadlan.Core.Activity;
using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Files;

/// <summary>What was read from inside a file (EXIF / DJI XMP / MP4 header / the .srt beside it). Null = not found.</summary>
public sealed record MediaMetadata
{
    public string? TakenLocal { get; init; }
    public string? CameraMake { get; init; }
    public string? CameraModel { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public decimal? RelAltitudeM { get; init; }
    public decimal? AbsAltitudeM { get; init; }
    public decimal? GimbalYaw { get; init; }
    public decimal? GimbalPitch { get; init; }
    public string? GpsStatus { get; init; }
    public decimal? DurationS { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? BitrateKbps { get; init; }
    public string? VideoCodec { get; init; }
    public bool? IndexAtEnd { get; init; }
    public DateTime? SourceModifiedUtc { get; init; }
    public long? TrackFileId { get; init; }
    public string? TrackJson { get; init; }
    public string? SuggestionsJson { get; init; }
}

/// <summary>One point of a flight track: milliseconds into the video, position, height above take-off.</summary>
public readonly record struct TrackPoint(int Ms, double Lat, double Lng, double? RelAlt);

/// <summary>
/// A Parcel found for a file. Photos: no times. Videos with a track: one entry per stretch over the Parcel (a Parcel
/// flown over twice has two). LongLook = at least <see cref="TrackSegmenter.LongLookMs"/> - ticked by default in the UI.
/// </summary>
public sealed record ParcelSuggestion(long ParcelId, string? RegistryId, int? StartMs = null, int? EndMs = null, bool LongLook = true);

/// <summary>A video a Map drop .srt may belong to (same name): where it is, and whether it already has a flight track.</summary>
public sealed record TrackVideoCandidate(long FileAttachmentId, string FileName, string AttachedToType, long AttachedToId, string? RegistryId,
    bool HasTrack, string StorageKey);

/// <summary>A time frame of a video on one Parcel.</summary>
public sealed record FileSegment(long ParcelId, int StartMs, int EndMs, string Source = FileSegmentSources.Manual, string? RegistryId = null);

public static class FileSegmentSources
{
    public const string Track = "Track";
    public const string Manual = "Manual";
}

/// <summary>A Map drop file for the review strip: the file, who dropped it, and what was found in it.</summary>
public sealed record MapDropItem
{
    public required FileListItem File { get; init; }
    public long? UploadedByUserId { get; init; }
    public string? UploadedByName { get; init; }
    public MediaMetadata? Metadata { get; init; }
}

/// <summary>One clean-up round: files deleted, and what failed (e.g. an S3 object that couldn't be removed).</summary>
public sealed record MapDropSweepResult(int Deleted, IReadOnlyList<string> Failures);

/// <summary>An already-stored file with the same name and size as one being dropped (the user decides).</summary>
public sealed record DuplicateHit(string FileName, long FileSize, long FileAttachmentId, string AttachedToType, long AttachedToId, string? RegistryId,
    string? Category = null);

public interface IMapDropStore
{
    Task<IReadOnlyList<MapDropItem>> ListAsync(CancellationToken ct = default);
    Task<MediaMetadata?> GetMetadataAsync(long fileAttachmentId, CancellationToken ct = default);
    Task UpsertMetadataAsync(long fileAttachmentId, MediaMetadata metadata, CancellationToken ct = default);

    /// <summary>MapDrop → Parcel with its document type. False if the file is no longer in Map drop (assigned meanwhile).</summary>
    Task<bool> AssignAsync(long fileAttachmentId, long parcelId, int fileTypeId, CancellationToken ct = default);

    Task<IReadOnlyList<FileSegment>> ListSegmentsAsync(long fileAttachmentId, CancellationToken ct = default);
    Task ReplaceSegmentsAsync(long fileAttachmentId, IReadOnlyList<FileSegment> segments, long? userId, CancellationToken ct = default);

    /// <summary>Time frames on one Parcel, per video (the Parcel's own videos and videos of other Parcels linked here).</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyList<FileSegment>>> ListSegmentsOnParcelAsync(long parcelId, CancellationToken ct = default);

    /// <summary>Ready videos whose home is elsewhere but which have a time frame on this Parcel.</summary>
    Task<IReadOnlyList<FileListItem>> ListLinkedFilesAsync(long parcelId, CancellationToken ct = default);

    Task<IReadOnlyList<DuplicateHit>> FindDuplicatesAsync(IReadOnlyList<(string Name, long Size)> files, CancellationToken ct = default);

    /// <summary>Map drop files (Ready or Pending) uploaded before the cutoff.</summary>
    Task<IReadOnlyList<long>> ListExpiredAsync(DateTime uploadedBeforeUtc, int limit, CancellationToken ct = default);

    /// <summary>Ready videos whose file name without its extension is <paramref name="baseName"/> (any case), on any target.</summary>
    Task<IReadOnlyList<long>> FindVideoIdsByBaseNameAsync(string baseName, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>
/// Flight track → Parcels with time frames: the Parcel(s) under each point, runs of the same Parcel(s) joined, GPS
/// flicker at a boundary (a run shorter than <see cref="FlickerMs"/>) folded into the run before it, and each Parcel's
/// touching stretches merged. Pure: no database (unit-tested on a real DJI track).
/// </summary>
public static class TrackSegmenter
{
    public const int FlickerMs = 1000;
    public const int LongLookMs = 5000;

    public static IReadOnlyList<ParcelSuggestion> Segment(IReadOnlyList<TrackPoint> points, IReadOnlyList<(long Id, string? RegistryId, GeoPolygon Geometry)> parcels)
    {
        if (points.Count == 0)
        {
            return Array.Empty<ParcelSuggestion>();
        }

        var boxes = parcels.Select(p => (p.Id, p.RegistryId, p.Geometry, Box: p.Geometry.Bounds())).ToList();
        string KeyOf(TrackPoint t)
        {
            var hits = boxes.Where(b => t.Lng >= b.Box.West && t.Lng <= b.Box.East && t.Lat >= b.Box.South && t.Lat <= b.Box.North
                                         && b.Geometry.Contains(new GeoPoint(t.Lng, t.Lat)))
                .Select(b => b.Id).OrderBy(id => id);
            return string.Join("+", hits);
        }

        // Runs of the same Parcel set; a run lasts until the next run starts (the last one until its last point).
        var runs = new List<(string Key, int Start, int End)>();
        foreach (var t in points)
        {
            var key = KeyOf(t);
            if (runs.Count > 0 && runs[^1].Key == key)
            {
                runs[^1] = (key, runs[^1].Start, t.Ms);
            }
            else
            {
                if (runs.Count > 0)
                {
                    runs[^1] = (runs[^1].Key, runs[^1].Start, t.Ms);
                }

                runs.Add((key, t.Ms, t.Ms));
            }
        }

        var merged = new List<(string Key, int Start, int End)>();
        foreach (var r in runs)
        {
            if (merged.Count > 0 && (r.End - r.Start < FlickerMs || merged[^1].Key == r.Key))
            {
                merged[^1] = (merged[^1].Key, merged[^1].Start, r.End);
            }
            else
            {
                merged.Add(r);
            }
        }

        // The first run can itself be flicker (a blip at take-off): fold it into the second.
        if (merged.Count > 1 && merged[0].End - merged[0].Start < FlickerMs)
        {
            merged[1] = (merged[1].Key, merged[0].Start, merged[1].End);
            merged.RemoveAt(0);
        }

        var names = parcels.ToDictionary(p => p.Id, p => p.RegistryId);
        var result = new List<ParcelSuggestion>();
        foreach (var group in merged.Where(m => m.Key.Length > 0)
                     .SelectMany(m => m.Key.Split('+').Select(id => (Id: long.Parse(id, CultureInfo.InvariantCulture), m.Start, m.End)))
                     .GroupBy(x => x.Id))
        {
            var spans = group.OrderBy(x => x.Start).Select(x => (x.Start, x.End)).ToList();
            var joined = new List<(int Start, int End)>();
            foreach (var s in spans)
            {
                if (joined.Count > 0 && s.Start <= joined[^1].End)
                {
                    joined[^1] = (joined[^1].Start, Math.Max(joined[^1].End, s.End));
                }
                else
                {
                    joined.Add(s);
                }
            }

            result.AddRange(joined.Where(j => j.End > j.Start)
                .Select(j => new ParcelSuggestion(group.Key, names[group.Key], j.Start, j.End, j.End - j.Start >= LongLookMs)));
        }

        return result.OrderBy(s => s.StartMs).ThenBy(s => s.ParcelId).ToList();
    }
}

/// <summary>
/// Map drop (owner, 2026-10-20): media dropped on the map are uploaded unassigned, inspected in the browser, matched to
/// Parcels here, then assigned by an Admin or Data person - or deleted (5 days after upload at the latest).
/// Only a video with a flight track can cover several Parcels (time frames); everything else goes to one Parcel.
/// </summary>
public sealed class MapDropService
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(5);
    public const int MaxTrackPoints = 20_000;   // 2 a second = 2.7 hours of flight
    public const int MaxSegments = 200;
    public const int MaxSuggestions = 500;   // a card with more is unusable; the longest stretches are kept

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IMapDropStore _store;
    private readonly IFileAttachmentStore _files;
    private readonly IParcelStore _parcels;
    private readonly AccessPolicy _policy;
    private readonly FileService _fileService;
    private readonly IActivityLog _activity;

    public MapDropService(IMapDropStore store, IFileAttachmentStore files, IParcelStore parcels, AccessPolicy policy, FileService fileService,
        IActivityLog? activity = null)
    {
        _store = store;
        _files = files;
        _parcels = parcels;
        _policy = policy;
        _fileService = fileService;
        _activity = activity ?? NullActivityLog.Instance;
    }

    public static void RequireMapDrop(UserAccess me)
    {
        if (!me.Has(Permissions.MapDrop))
        {
            throw new ForbiddenException("MAP_DROP_FORBIDDEN", "Map drop is for Admins and Data persons.");
        }
    }

    public Task<IReadOnlyList<MapDropItem>> ListAsync(UserAccess me, CancellationToken ct = default)
    {
        RequireMapDrop(me);
        return _store.ListAsync(ct);
    }

    public async Task<IReadOnlyList<DuplicateHit>> FindDuplicatesAsync(UserAccess me, IReadOnlyList<(string Name, long Size)> files, CancellationToken ct = default)
    {
        RequireMapDrop(me);
        if (files.Count > 2000)
        {
            throw new DomainValidationException("MAP_DROP_TOO_MANY", "Drop at most 2,000 files at once.");
        }

        // Only files the caller could see anyway: the check must not reveal e.g. an ID scan's name on a hidden Contact.
        var visible = new List<DuplicateHit>();
        foreach (var hit in await _store.FindDuplicatesAsync(files.Select(f => (FileService.CleanFileName(f.Name), f.Size)).ToList(), ct))
        {
            if (me.CanSeeFileCategory(hit.Category) && (await _policy.FileTargetAsync(me, hit.AttachedToType, hit.AttachedToId, ct)).CanView)
            {
                visible.Add(hit);
            }
        }

        return visible;
    }

    /// <summary>
    /// Stores what the browser read from the file and finds its Parcels: a photo's position → the Parcel(s) it is in;
    /// a video's flight track → Parcels with time frames. Returns the suggestions (also stored).
    /// </summary>
    public async Task<IReadOnlyList<ParcelSuggestion>> SaveInspectionAsync(UserAccess me, long fileAttachmentId, MediaMetadata metadata,
        IReadOnlyList<TrackPoint>? track, CancellationToken ct = default)
    {
        RequireMapDrop(me);
        var file = await GetMapDropFileAsync(fileAttachmentId, ct);
        var clean = Validate(metadata);
        var isVideo = file.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        if (clean.TrackFileId is { } trackFileId)
        {
            // The .srt the track was read from: a file this user can see, else not recorded.
            var trackFile = await _files.GetAsync(trackFileId, ct);
            var visible = trackFile is { UploadStatus: FileUploadStatus.Ready }
                          && (await _policy.FileTargetAsync(me, trackFile.AttachedToType, trackFile.AttachedToId, ct)).CanView;
            clean = clean with { TrackFileId = visible ? trackFileId : null };
        }
        IReadOnlyList<ParcelSuggestion> suggestions = Array.Empty<ParcelSuggestion>();
        string? trackJson = null;

        // A video and its .srt upload side by side: the video's own reading may arrive after its track was stored. Then the
        // stored track (and the .srt it came from) is kept and used again, not wiped.
        var existing = await _store.GetMetadataAsync(fileAttachmentId, ct);
        if (isVideo && track is not { Count: > 0 } && existing?.TrackJson is { Length: > 0 } storedJson)
        {
            track = ReadTrackJson(storedJson);
            clean = clean with { TrackFileId = clean.TrackFileId ?? existing.TrackFileId };
        }

        if (track is { Count: > 0 } && isVideo)
        {
            var points = ValidateTrack(track);
            trackJson = SerializeTrack(points);
            suggestions = await SegmentAsync(me, points, ct);
            clean = clean with { Latitude = clean.Latitude ?? points[0].Lat, Longitude = clean.Longitude ?? points[0].Lng };
        }
        else if (track is { Count: > 0 } && IsTrackFile(file))
        {
            // A .srt keeps its own flight, so it can be linked to its video later - also one uploaded long before
            // (LinkTrackToVideoAsync). No Parcel of its own: it belongs to its video.
            var points = ValidateTrack(track);
            trackJson = SerializeTrack(points);
            clean = clean with { Latitude = clean.Latitude ?? points[0].Lat, Longitude = clean.Longitude ?? points[0].Lng };
        }
        else if (clean is { Latitude: { } lat, Longitude: { } lng } && (isVideo || file.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
        {
            // A photo's (or phone video's) own position. A lone .srt keeps its first point for the card, but gets no
            // Parcel of its own: it belongs to its video.
            var parcels = await ParcelsAroundAsync(me, new[] { (lat, lng) }, ct);
            suggestions = parcels.Where(p => p.Geometry.Contains(new GeoPoint(lng, lat)))
                .Select(p => new ParcelSuggestion(p.Id, p.RegistryId)).ToList();
        }

        await _store.UpsertMetadataAsync(fileAttachmentId, clean with
        {
            TrackJson = trackJson ?? existing?.TrackJson,
            SuggestionsJson = JsonSerializer.Serialize(suggestions, Json),
        }, ct);
        return suggestions;
    }

    /// <summary>
    /// Assigns a Map drop file to its home Parcel with a document type. A video may also get time frames on several
    /// Parcels (its home included). The .srt it was read from goes along to the same Parcel.
    /// </summary>
    public async Task AssignAsync(UserAccess me, long fileAttachmentId, long parcelId, int fileTypeId, IReadOnlyList<FileSegment>? segments,
        CancellationToken ct = default)
    {
        RequireMapDrop(me);
        var file = await GetMapDropFileAsync(fileAttachmentId, ct);
        var category = await _fileService.CategoryOfAsync(fileTypeId, ct)
                       ?? throw new DomainValidationException("FILE_TYPE_REQUIRED", "Select a document type.");
        await _policy.RequireFileUploadAsync(me, FileTargetTypes.Parcel, parcelId, category, ct);
        var types = await _files.ListTypesAsync(ct);
        if (!types.Any(t => t.Id == fileTypeId && t.IsActive))
        {
            throw new DomainValidationException("FILE_TYPE_REQUIRED", "Select a document type.");
        }

        var frames = await ValidateSegmentsAsync(me, file, segments ?? Array.Empty<FileSegment>(), ct);
        if (!await _store.AssignAsync(fileAttachmentId, parcelId, fileTypeId, ct))
        {
            throw new DomainValidationException("MAP_DROP_ALREADY_ASSIGNED", "Someone assigned this file a moment ago.");
        }

        if (frames.Count > 0)
        {
            await _store.ReplaceSegmentsAsync(fileAttachmentId, frames, me.UserId, ct);
        }

        var parcelName = await RegistryIdAsync(parcelId, ct);
        await _activity.RecordAsync(new ActivityEntry(FileTargetTypes.Parcel, parcelId, ActivityActions.FileUploaded,
            $"Added {file.OriginalFileName} from Map drop.", new { file.FileAttachmentId, file.FileSize, category }), ct);
        foreach (var other in frames.Where(f => f.ParcelId != parcelId).GroupBy(f => f.ParcelId))
        {
            await _activity.RecordAsync(new ActivityEntry(FileTargetTypes.Parcel, other.Key, ActivityActions.FileLinked,
                $"Video {file.OriginalFileName} (on Parcel {parcelName}) shown here: {FormatFrames(other)}.",
                new { file.FileAttachmentId, category }), ct);
        }

        // The flight track travels with its video.
        var metadata = await _store.GetMetadataAsync(fileAttachmentId, ct);
        if (metadata?.TrackFileId is { } trackId && await _files.GetAsync(trackId, ct) is { AttachedToType: FileTargetTypes.MapDrop } track)
        {
            var trackType = types.FirstOrDefault(t => t.Code == "DRONE_TRACK" && t.IsActive)?.Id ?? fileTypeId;
            if (await _store.AssignAsync(trackId, parcelId, trackType, ct))
            {
                await _activity.RecordAsync(new ActivityEntry(FileTargetTypes.Parcel, parcelId, ActivityActions.FileUploaded,
                    $"Added {track.OriginalFileName} from Map drop.", new { track.FileAttachmentId, track.FileSize, category }), ct);
            }
        }
    }

    /// <summary>
    /// Videos that a Map drop .srt may belong to: same name without the extension (DJI_0949.SRT - DJI_0949.MP4), already on a
    /// Parcel or still in Map drop, that the caller can see. Newest first.
    /// </summary>
    public async Task<IReadOnlyList<TrackVideoCandidate>> FindVideosForTrackAsync(UserAccess me, long trackFileId, CancellationToken ct = default)
    {
        RequireMapDrop(me);
        var srt = await GetMapDropFileAsync(trackFileId, ct);
        RequireTrackFile(srt);
        var result = new List<TrackVideoCandidate>();
        foreach (var id in await _store.FindVideoIdsByBaseNameAsync(Path.GetFileNameWithoutExtension(srt.OriginalFileName), ct))
        {
            FileAttachment video;
            try { video = await GetVisibleFileAsync(me, id, ct); }
            catch (EntityNotFoundException) { continue; }

            var registryId = video.AttachedToType == FileTargetTypes.Parcel ? await RegistryIdAsync(video.AttachedToId, ct) : null;
            var hasTrack = (await _store.GetMetadataAsync(id, ct))?.TrackJson is { Length: > 0 };
            result.Add(new TrackVideoCandidate(video.FileAttachmentId, video.OriginalFileName, video.AttachedToType, video.AttachedToId, registryId,
                hasTrack, video.StorageKey));
        }

        return result;
    }

    /// <summary>
    /// A Map drop .srt joined to its video, found by name (FindVideosForTrackAsync): the video's flight track and time-frame
    /// suggestions come from the .srt. A video still in Map drop gets them on its own card (assigned as usual, the .srt
    /// goes along). A video already on a Parcel: the frames are returned for the .srt's card, applied by ApplyTrackToVideoAsync.
    /// </summary>
    public async Task<IReadOnlyList<ParcelSuggestion>> LinkTrackToVideoAsync(UserAccess me, long trackFileId, long videoFileId, CancellationToken ct = default)
    {
        RequireMapDrop(me);
        var srt = await GetMapDropFileAsync(trackFileId, ct);
        RequireTrackFile(srt);
        var stored = (await _store.GetMetadataAsync(trackFileId, ct))?.TrackJson;
        var points = stored is { Length: > 0 } ? ReadTrackJson(stored) : Array.Empty<TrackPoint>();
        if (points.Count == 0)
        {
            throw new DomainValidationException("MAP_DROP_TRACK_MISSING",
                $"{srt.OriginalFileName} was uploaded before flight tracks were kept: delete it here and drop it again.");
        }

        var video = await GetVisibleFileAsync(me, videoFileId, ct);
        if (!video.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException("FILE_SEGMENTS_VIDEO_ONLY", "Only a video can be split over several Parcels.");
        }

        var existing = await _store.GetMetadataAsync(videoFileId, ct) ?? new MediaMetadata();
        if (video.AttachedToType == FileTargetTypes.MapDrop)
        {
            return await SaveInspectionAsync(me, videoFileId, existing with { TrackFileId = trackFileId, TrackJson = null, SuggestionsJson = null }, points, ct);
        }

        if (video.AttachedToType != FileTargetTypes.Parcel)
        {
            throw new EntityNotFoundException("File", videoFileId);
        }

        // Changing a video on a Parcel: that Parcel's file rights (the frames' own Parcels are checked when applied).
        await _policy.RequireFileChangeAsync(me, video.AttachedToType, video.AttachedToId, video.UploadedByUserId,
            await _fileService.CategoryOfAsync(video.FileTypeId, ct), ct);
        var suggestions = await SegmentAsync(me, points, ct);
        await _store.UpsertMetadataAsync(videoFileId, existing with
        {
            TrackFileId = trackFileId,
            TrackJson = SerializeTrack(points),
            SuggestionsJson = JsonSerializer.Serialize(suggestions, Json),
            Latitude = existing.Latitude ?? points[0].Lat,
            Longitude = existing.Longitude ?? points[0].Lng,
        }, ct);
        return suggestions;
    }

    /// <summary>
    /// The ticked time frames of a linked .srt applied to its video on a Parcel (frames replaced, with the same rights and
    /// history as a data-entry correction), and the .srt moved to the video's Parcel.
    /// </summary>
    public async Task ApplyTrackToVideoAsync(UserAccess me, long trackFileId, long videoFileId, IReadOnlyList<FileSegment> segments, CancellationToken ct = default)
    {
        RequireMapDrop(me);
        var srt = await GetMapDropFileAsync(trackFileId, ct);
        RequireTrackFile(srt);
        var video = await GetVisibleFileAsync(me, videoFileId, ct);
        if (video.AttachedToType != FileTargetTypes.Parcel)
        {
            throw new DomainValidationException("MAP_DROP_VIDEO_NOT_ON_PARCEL", "That video is still in Map drop: assign it from its own card.");
        }

        if ((await _store.GetMetadataAsync(videoFileId, ct))?.TrackFileId != trackFileId)
        {
            throw new DomainValidationException("MAP_DROP_NOT_LINKED", "Link the flight track to the video first.");
        }

        var types = await _files.ListTypesAsync(ct);
        var trackType = types.FirstOrDefault(t => t.Code == "DRONE_TRACK" && t.IsActive)?.Id ?? srt.FileTypeId;
        var trackCategory = await _fileService.CategoryOfAsync(trackType, ct);
        await _policy.RequireFileUploadAsync(me, FileTargetTypes.Parcel, video.AttachedToId, trackCategory, ct); // before anything changes

        await ReplaceSegmentsAsync(me, videoFileId, segments, ct);
        if (await _store.AssignAsync(trackFileId, video.AttachedToId, trackType, ct))
        {
            await _activity.RecordAsync(new ActivityEntry(FileTargetTypes.Parcel, video.AttachedToId, ActivityActions.FileUploaded,
                $"Added {srt.OriginalFileName} from Map drop (flight track of {video.OriginalFileName}).",
                new { srt.FileAttachmentId, srt.FileSize, category = trackCategory }), ct);
        }
    }

    private static bool IsTrackFile(FileAttachment file) => file.OriginalFileName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase);

    private static void RequireTrackFile(FileAttachment file)
    {
        if (!IsTrackFile(file))
        {
            throw new DomainValidationException("MAP_DROP_NOT_A_TRACK", "Only a .srt flight track can be linked to a video.");
        }
    }

    private static string SerializeTrack(IReadOnlyList<TrackPoint> points) =>
        JsonSerializer.Serialize(points.Select(p => new object?[] { p.Ms, Math.Round(p.Lat, 7), Math.Round(p.Lng, 7), p.RelAlt is { } a ? Math.Round(a, 1) : null }), Json);

    /// <summary>A flight track's Parcels with time frames (at most <see cref="MaxSuggestions"/>, the longest stretches).</summary>
    private async Task<IReadOnlyList<ParcelSuggestion>> SegmentAsync(UserAccess me, IReadOnlyList<TrackPoint> points, CancellationToken ct)
    {
        var parcels = await ParcelsAroundAsync(me, points.Select(p => (p.Lat, p.Lng)).ToList(), ct);
        IReadOnlyList<ParcelSuggestion> suggestions = TrackSegmenter.Segment(points, parcels);
        if (suggestions.Count > MaxSuggestions)
        {
            suggestions = suggestions.OrderByDescending(x => x.EndMs - x.StartMs).Take(MaxSuggestions).OrderBy(x => x.StartMs).ThenBy(x => x.ParcelId).ToList();
        }

        return suggestions;
    }

    public async Task<IReadOnlyList<FileSegment>> ListSegmentsAsync(UserAccess me, long fileAttachmentId, CancellationToken ct = default)
    {
        await GetVisibleFileAsync(me, fileAttachmentId, ct);
        // Frames on Parcels the caller can't see are left out (their KAEK isn't shown either).
        var visible = new List<FileSegment>();
        foreach (var s in await _store.ListSegmentsAsync(fileAttachmentId, ct))
        {
            if ((await _policy.ParcelAsync(me, s.ParcelId, ct)).CanView)
            {
                visible.Add(s);
            }
        }

        return visible;
    }

    /// <summary>A data-entry correction of a video's time frames (all of them, replaced).</summary>
    public async Task ReplaceSegmentsAsync(UserAccess me, long fileAttachmentId, IReadOnlyList<FileSegment> segments, CancellationToken ct = default)
    {
        RequireMapDrop(me);
        var file = await GetVisibleFileAsync(me, fileAttachmentId, ct);
        if (file.AttachedToType != FileTargetTypes.Parcel)
        {
            throw new DomainValidationException("FILE_SEGMENTS_PARCEL_ONLY", "Time frames are for videos on a Parcel.");
        }

        // The video itself is changed (its own Parcel's file rights), and so is every Parcel that loses a frame.
        await _policy.RequireFileChangeAsync(me, file.AttachedToType, file.AttachedToId, file.UploadedByUserId,
            await _fileService.CategoryOfAsync(file.FileTypeId, ct), ct);
        var frames = await ValidateSegmentsAsync(me, file, segments, ct);
        var before = await _store.ListSegmentsAsync(fileAttachmentId, ct);
        foreach (var removed in before.Select(b => b.ParcelId).Distinct().Where(id => frames.All(f => f.ParcelId != id)))
        {
            await _policy.RequireParcelEditAsync(me, removed, ct);
        }

        await _store.ReplaceSegmentsAsync(fileAttachmentId, frames, me.UserId, ct);
        var summary = frames.Count == 0 ? "none" : string.Join("; ", frames.GroupBy(f => f.ParcelId).Select(g => $"{g.First().RegistryId ?? "#" + g.Key} {FormatFrames(g)}"));
        await _activity.RecordAsync(new ActivityEntry(FileTargetTypes.Parcel, file.AttachedToId, ActivityActions.FileLinked,
            $"Time frames of {file.OriginalFileName} set: {summary}.", new { file.FileAttachmentId, before = before.Count, after = frames.Count }), ct);
    }

    /// <summary>
    /// Deletes Map drop files uploaded more than <see cref="KeepFor"/> ago. One file failing (S3 down) doesn't stop the
    /// others; each failure comes back for the log.
    /// </summary>
    public async Task<MapDropSweepResult> SweepExpiredAsync(CancellationToken ct = default)
    {
        var count = 0;
        var failures = new List<string>();
        var cutoff = DateTime.UtcNow - KeepFor;
        foreach (var id in await _store.ListExpiredAsync(cutoff, limit: 500, ct))
        {
            try
            {
                // Re-checked at the moment of deleting: a file assigned since the list was read stays.
                if (await _fileService.DeleteExpiredMapDropAsync(id, cutoff, ct))
                {
                    count++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex.Message);
            }
        }

        return new MapDropSweepResult(count, failures);
    }

    public static string FormatTime(int ms)
    {
        var s = ms / 1000;
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    private static string FormatFrames(IEnumerable<FileSegment> frames)
        => string.Join(", ", frames.OrderBy(f => f.StartMs).Select(f => $"{FormatTime(f.StartMs)}-{FormatTime(f.EndMs)}"));

    private async Task<string> RegistryIdAsync(long parcelId, CancellationToken ct)
        => (await _parcels.GetAsync(parcelId, ct))?.RegistryId ?? "#" + parcelId;

    private async Task<FileAttachment> GetMapDropFileAsync(long fileAttachmentId, CancellationToken ct)
    {
        var file = await _files.GetAsync(fileAttachmentId, ct);
        if (file is null || file.AttachedToType != FileTargetTypes.MapDrop)
        {
            throw new EntityNotFoundException("File", fileAttachmentId);
        }

        if (file.UploadStatus != FileUploadStatus.Ready)
        {
            throw new DomainValidationException("FILE_NOT_READY", "The file is still uploading.");
        }

        return file;
    }

    private async Task<FileAttachment> GetVisibleFileAsync(UserAccess me, long fileAttachmentId, CancellationToken ct)
    {
        var file = await _files.GetAsync(fileAttachmentId, ct);
        if (file is not { UploadStatus: FileUploadStatus.Ready }
            || !(await _policy.FileTargetAsync(me, file.AttachedToType, file.AttachedToId, ct)).CanView
            || !me.CanSeeFileCategory(await _fileService.CategoryOfAsync(file.FileTypeId, ct)))
        {
            throw new EntityNotFoundException("File", fileAttachmentId);
        }

        return file;
    }

    private async Task<IReadOnlyList<FileSegment>> ValidateSegmentsAsync(UserAccess me, FileAttachment file, IReadOnlyList<FileSegment> segments, CancellationToken ct)
    {
        if (segments.Count == 0)
        {
            return Array.Empty<FileSegment>();
        }

        if (!file.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException("FILE_SEGMENTS_VIDEO_ONLY", "Only a video can be split over several Parcels.");
        }

        if (segments.Count > MaxSegments)
        {
            throw new DomainValidationException("FILE_SEGMENTS_TOO_MANY", $"At most {MaxSegments} time frames per video.");
        }

        var duration = (await _store.GetMetadataAsync(file.FileAttachmentId, ct))?.DurationS;
        var result = new List<FileSegment>();
        var names = new Dictionary<long, string?>();
        foreach (var s in segments)
        {
            if (s.StartMs < 0 || s.EndMs <= s.StartMs)
            {
                throw new DomainValidationException("FILE_SEGMENT_TIMES", $"A time frame must end after it starts ({FormatTime(Math.Max(0, s.StartMs))}-{FormatTime(Math.Max(0, s.EndMs))}).");
            }

            if (duration is { } d && s.EndMs > (int)Math.Ceiling(d * 1000m) + 1000)
            {
                throw new DomainValidationException("FILE_SEGMENT_TIMES", $"The video is only {FormatTime((int)(d * 1000m))} long.");
            }

            if (!names.ContainsKey(s.ParcelId))
            {
                // Putting a video on a Parcel changes that Parcel: edit rights there ("not found" if it can't even be seen).
                await _policy.RequireParcelEditAsync(me, s.ParcelId, ct);
                names[s.ParcelId] = await RegistryIdAsync(s.ParcelId, ct);
            }

            var source = s.Source == FileSegmentSources.Track ? FileSegmentSources.Track : FileSegmentSources.Manual;
            result.Add(new FileSegment(s.ParcelId, s.StartMs, s.EndMs, source, names[s.ParcelId]));
        }

        return result.OrderBy(s => s.StartMs).ThenBy(s => s.ParcelId).ToList();
    }

    private async Task<IReadOnlyList<(long Id, string? RegistryId, GeoPolygon Geometry)>> ParcelsAroundAsync(UserAccess me,
        IReadOnlyList<(double Lat, double Lng)> points, CancellationToken ct)
    {
        const double margin = 0.0005; // ~50 m
        var area = new GeoBounds(points.Min(p => p.Lng) - margin, points.Min(p => p.Lat) - margin, points.Max(p => p.Lng) + margin, points.Max(p => p.Lat) + margin);
        if (area.East - area.West > 0.5 || area.North - area.South > 0.5)
        {
            return Array.Empty<(long, string?, GeoPolygon)>(); // a broken track spanning tens of km: find nothing rather than everything
        }

        var found = await _parcels.QueryAsync(new ParcelQuery { Area = area, Scope = me.Scope, Limit = 5000, ExcludeBeaches = true }, ct); // no beach suggestions
        return found.Select(p => (p.ParcelId, p.RegistryId, p.Geometry)).ToList();
    }

    /// <summary>The stored track: [[ms, lat, lng, relAlt], ...].</summary>
    internal static IReadOnlyList<TrackPoint> ReadTrackJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<double?[][]>(json, Json)!
                .Where(p => p is { Length: >= 3 } && p[0] is not null && p[1] is not null && p[2] is not null)
                .Select(p => new TrackPoint((int)p[0]!.Value, p[1]!.Value, p[2]!.Value, p.Length > 3 ? p[3] : null)).ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<TrackPoint>();
        }
    }

    private static MediaMetadata Validate(MediaMetadata m)
    {
        static string? Text(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : (s.Trim().Length <= max ? s.Trim() : s.Trim()[..max]);
        static decimal? Range(decimal? v, decimal min, decimal max) => v is { } x && x >= min && x <= max ? Math.Round(x, 3) : null;
        var hasPosition = m.Latitude is { } lat && m.Longitude is { } lng && Math.Abs(lat) <= 90 && Math.Abs(lng) <= 180 && !(lat == 0 && lng == 0);
        var taken = Text(m.TakenLocal, 19);
        if (taken is not null && !DateTime.TryParseExact(taken, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            taken = null;
        }

        return new MediaMetadata
        {
            TakenLocal = taken,
            CameraMake = Text(m.CameraMake, 60),
            CameraModel = Text(m.CameraModel, 80),
            Latitude = hasPosition ? m.Latitude : null,
            Longitude = hasPosition ? m.Longitude : null,
            RelAltitudeM = Range(m.RelAltitudeM, -10000, 100000),
            AbsAltitudeM = Range(m.AbsAltitudeM, -10000, 100000),
            GimbalYaw = Range(m.GimbalYaw, -360, 360),
            GimbalPitch = Range(m.GimbalPitch, -180, 180),
            GpsStatus = Text(m.GpsStatus, 20),
            DurationS = Range(m.DurationS, 0, 1_000_000),
            Width = m.Width is > 0 and < 100_000 ? m.Width : null,
            Height = m.Height is > 0 and < 100_000 ? m.Height : null,
            BitrateKbps = m.BitrateKbps is > 0 and < 10_000_000 ? m.BitrateKbps : null,
            VideoCodec = Text(m.VideoCodec, 20),
            IndexAtEnd = m.IndexAtEnd,
            SourceModifiedUtc = m.SourceModifiedUtc is { } t && t.Year is > 1990 and < 2200 ? DateTime.SpecifyKind(t.ToUniversalTime(), DateTimeKind.Utc) : null,
            TrackFileId = m.TrackFileId,
        };
    }

    private static IReadOnlyList<TrackPoint> ValidateTrack(IReadOnlyList<TrackPoint> track)
    {
        if (track.Count > MaxTrackPoints)
        {
            throw new DomainValidationException("TRACK_TOO_LONG", $"A flight track can have at most {MaxTrackPoints:N0} points (2 a second).");
        }

        var points = track.Where(p => p.Ms >= 0 && Math.Abs(p.Lat) <= 90 && Math.Abs(p.Lng) <= 180 && !(p.Lat == 0 && p.Lng == 0))
            .OrderBy(p => p.Ms).ToList();
        if (points.Count == 0)
        {
            throw new DomainValidationException("TRACK_EMPTY", "The flight track has no usable positions.");
        }

        return points;
    }
}
