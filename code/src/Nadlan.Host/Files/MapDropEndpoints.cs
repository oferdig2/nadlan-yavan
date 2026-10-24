using System.Text.Json;
using Nadlan.Core.Files;
using Nadlan.Core.Security;

namespace Nadlan.Host.Files;

/// <summary>
/// Map drop (migration 016): the files dropped on the map and not assigned yet, what was read from them, their Parcel
/// suggestions, assigning, and the time frames of a video split over several Parcels. Admin + Data person (MAP_DROP).
/// Uploading uses the normal file upload API with attachedToType "MapDrop" and attachedToId = the caller's user id.
/// </summary>
public static class MapDropEndpoints
{
    public sealed record InspectionDto(MediaMetadata? Metadata, double?[][]? Track);

    public sealed record SegmentDto(long ParcelId, int StartMs, int EndMs, string? Source);

    public sealed record AssignDto(long ParcelId, int FileTypeId, SegmentDto[]? Segments);

    public sealed record LinkDto(long VideoFileId);

    public sealed record ApplyDto(long VideoFileId, SegmentDto[]? Segments);

    public sealed record NameSizeDto(string? Name, long Size);

    public sealed record DuplicatesDto(NameSizeDto[]? Files);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapMapDropEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/map-drop");

        group.MapGet("/", async (UserAccess me, MapDropService service, IFileUrlProvider urls, CancellationToken ct) =>
        {
            var items = await service.ListAsync(me, ct);
            return Results.Ok(new
            {
                keepDays = (int)MapDropService.KeepFor.TotalDays,
                items = items.Where(i => me.CanSeeFileCategory(i.File.Category)).Select(i => ToDto(i, urls)),
            });
        });

        // Before uploading: files that are already stored under the same name and size (the user decides).
        group.MapPost("/duplicates", async (DuplicatesDto dto, UserAccess me, MapDropService service, CancellationToken ct) =>
        {
            var files = (dto.Files ?? Array.Empty<NameSizeDto>()).Where(f => !string.IsNullOrWhiteSpace(f.Name) && f.Size > 0)
                .Select(f => (f.Name!, f.Size)).ToList();
            var hits = await service.FindDuplicatesAsync(me, files, ct);
            return Results.Ok(hits.Select(h => new
            {
                fileName = h.FileName, fileSize = h.FileSize, fileAttachmentId = h.FileAttachmentId,
                attachedToType = h.AttachedToType, attachedToId = h.AttachedToId, registryId = h.RegistryId,
            }));
        });

        group.MapPut("/{fileAttachmentId:long}/inspection", async (long fileAttachmentId, InspectionDto dto, UserAccess me, MapDropService service,
            CancellationToken ct) =>
        {
            var track = dto.Track?.Where(p => p is { Length: >= 3 } && p[0] is not null && p[1] is not null && p[2] is not null)
                .Select(p => new TrackPoint((int)p[0]!.Value, p[1]!.Value, p[2]!.Value, p.Length > 3 ? p[3] : null)).ToList();
            var suggestions = await service.SaveInspectionAsync(me, fileAttachmentId, dto.Metadata ?? new MediaMetadata(), track, ct);
            return Results.Ok(new { suggestions });
        });

        group.MapPost("/{fileAttachmentId:long}/assign", async (long fileAttachmentId, AssignDto dto, UserAccess me, MapDropService service,
            CancellationToken ct) =>
        {
            await service.AssignAsync(me, fileAttachmentId, dto.ParcelId, dto.FileTypeId, ToSegments(dto.Segments), ct);
            return Results.Ok(new { fileAttachmentId, parcelId = dto.ParcelId });
        });

        // A .srt dropped on its own: the videos it may belong to (same name), on a Parcel or still in Map drop.
        group.MapGet("/{fileAttachmentId:long}/videos", async (long fileAttachmentId, UserAccess me, MapDropService service, IFileUrlProvider urls,
            CancellationToken ct) =>
            Results.Ok((await service.FindVideosForTrackAsync(me, fileAttachmentId, ct)).Select(v => new
            {
                fileAttachmentId = v.FileAttachmentId, fileName = v.FileName, attachedToType = v.AttachedToType, attachedToId = v.AttachedToId,
                registryId = v.RegistryId, hasTrack = v.HasTrack, url = urls.GetUrl(v.StorageKey),
            })));

        // ... joined to one of them: the video's time frames from the .srt (see MapDropService.LinkTrackToVideoAsync).
        group.MapPost("/{fileAttachmentId:long}/link", async (long fileAttachmentId, LinkDto dto, UserAccess me, MapDropService service, CancellationToken ct) =>
            Results.Ok(new { suggestions = await service.LinkTrackToVideoAsync(me, fileAttachmentId, dto.VideoFileId, ct) }));

        // ... and the ticked frames applied to that video on its Parcel; the .srt goes to the same Parcel.
        group.MapPost("/{fileAttachmentId:long}/apply", async (long fileAttachmentId, ApplyDto dto, UserAccess me, MapDropService service, CancellationToken ct) =>
        {
            await service.ApplyTrackToVideoAsync(me, fileAttachmentId, dto.VideoFileId, ToSegments(dto.Segments), ct);
            return Results.Ok(new { fileAttachmentId, videoFileId = dto.VideoFileId });
        });

        // A video's time frames on Parcels: read by anyone who sees the file, corrected by Map drop users (data entry).
        app.MapGet("/api/files/{fileAttachmentId:long}/segments", async (long fileAttachmentId, UserAccess me, MapDropService service, CancellationToken ct) =>
            Results.Ok(await service.ListSegmentsAsync(me, fileAttachmentId, ct)));

        app.MapPut("/api/files/{fileAttachmentId:long}/segments", async (long fileAttachmentId, SegmentDto[]? segments, UserAccess me,
            MapDropService service, CancellationToken ct) =>
        {
            await service.ReplaceSegmentsAsync(me, fileAttachmentId, ToSegments(segments), ct);
            return Results.Ok(await service.ListSegmentsAsync(me, fileAttachmentId, ct));
        });
    }

    private static IReadOnlyList<FileSegment> ToSegments(SegmentDto[]? segments)
        => (segments ?? Array.Empty<SegmentDto>()).Select(s => new FileSegment(s.ParcelId, s.StartMs, s.EndMs,
            s.Source == FileSegmentSources.Track ? FileSegmentSources.Track : FileSegmentSources.Manual)).ToList();

    private static object ToDto(MapDropItem i, IFileUrlProvider urls)
    {
        var m = i.Metadata;
        var suggestions = m?.SuggestionsJson is { Length: > 0 } s ? JsonSerializer.Deserialize<List<ParcelSuggestion>>(s, Json) : null;
        return new
        {
            file = FileEndpoints.ToDto(i.File, urls),
            uploadedByUserId = i.UploadedByUserId,
            uploadedByName = i.UploadedByName,
            expiresUtc = DateTime.SpecifyKind(i.File.UploadedUtc, DateTimeKind.Utc) + MapDropService.KeepFor,
            inspected = m is not null,
            metadata = m is null ? null : new
            {
                m.TakenLocal, m.CameraMake, m.CameraModel, m.Latitude, m.Longitude, m.RelAltitudeM, m.AbsAltitudeM, m.GimbalYaw, m.GimbalPitch,
                m.GpsStatus, m.DurationS, m.Width, m.Height, m.BitrateKbps, m.VideoCodec, m.IndexAtEnd, m.SourceModifiedUtc, m.TrackFileId,
                hasTrack = !string.IsNullOrEmpty(m.TrackJson),
            },
            suggestions = suggestions ?? new List<ParcelSuggestion>(),
        };
    }
}

/// <summary>Background job: a few minutes after startup and then every 6 hours, deletes Map drop files older than 5 days.</summary>
public sealed class MapDropSweeper : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly MapDropService _mapDrop;
    private readonly ILogger<MapDropSweeper> _log;

    public MapDropSweeper(MapDropService mapDrop, ILogger<MapDropSweeper> log)
    {
        _mapDrop = mapDrop;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    var result = await _mapDrop.SweepExpiredAsync(stoppingToken);
                    if (result.Deleted > 0)
                    {
                        _log.LogInformation("Map drop: deleted {Count} file(s) not assigned within {Days} days.", result.Deleted, MapDropService.KeepFor.TotalDays);
                    }

                    foreach (var failure in result.Failures)
                    {
                        _log.LogWarning("Map drop clean-up: {Failure}", failure);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Map drop clean-up failed; will retry in {Interval}.", Interval);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
