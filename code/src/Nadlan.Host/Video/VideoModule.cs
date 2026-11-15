using Nadlan.Core.Files;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Core.Video;
using Nadlan.Host.Configuration;
using Nadlan.Persistence.MySql.Video;
using Nadlan.Storage.S3;

namespace Nadlan.Host.Video;

/// <summary>Large videos made light (migration 028): registration, the background job and the File details API.</summary>
public static class VideoModule
{
    public sealed record FramesDto(int[]? Indexes);

    public sealed record OffsetDto(decimal OffsetM);

    public static IServiceCollection AddNadlanVideo(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Nadlan:Video").Get<NadlanOptions.VideoOptions>() ?? new NadlanOptions.VideoOptions();
        var settings = options.ToSettings();
        services.AddSingleton(settings);
        services.AddSingleton<IVideoStore, MySqlVideoStore>();
        services.AddSingleton<IVideoTranscoder>(sp => new MediaConvertTranscoder(sp.GetRequiredService<StorageOptions>(), settings));
        services.AddSingleton<VideoService>();
        services.AddHostedService<VideoWorker>();
        return services;
    }

    public static void MapVideoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files/{fileAttachmentId:long}/video");

        // File details of a video: the conversion, the frames offered as photos, the heights correction.
        group.MapGet("", async (long fileAttachmentId, UserAccess me, AccessPolicy policy, IFileAttachmentStore files, FileService service,
            IVideoStore store, VideoService video, IFileUrlProvider urls, CancellationToken ct) =>
        {
            var (file, category) = await VisibleVideoAsync(fileAttachmentId, me, policy, files, service, ct);
            var r = await store.GetAsync(fileAttachmentId, ct);
            var canEdit = (await policy.FileTargetAsync(me, file.AttachedToType, file.AttachedToId, ct)).CanEdit;
            var framesThere = r is { Status: VideoStatuses.Ready, FramesDeletedUtc: null };
            return Results.Ok(new
            {
                active = video.IsActive,
                canEdit,
                status = r?.Status,
                error = r?.ErrorMessage,
                queuedUtc = r?.QueuedUtc,
                completedUtc = r?.CompletedUtc,
                smallSize = r?.SmallSize,
                largeSize = r?.LargeSize,
                originalSize = r?.OriginalSize,
                originalDeletedUtc = r?.OriginalDeletedUtc,
                originalDeleteDueUtc = r is { Status: VideoStatuses.Ready, OriginalDeletedUtc: null, CompletedUtc: { } done }
                    ? done.AddDays(video.Settings.OriginalRetentionDays) : (DateTime?)null,
                frames = framesThere
                    ? r!.OfferedFrames.Select(i => new { index = i, at = VideoService.FrameTimeText(i), thumbUrl = urls.GetUrl(VideoKeys.Frame(fileAttachmentId, i, small: true)) })
                    : null,
                posterIndex = r?.PosterIndex,
                offsetM = r?.TakeoffOffsetM ?? 0,
                hasHeights = !string.IsNullOrEmpty(r?.HeightsJson),
                category,
            });
        });

        // "Optimize" (a video uploaded before the feature) or "Try again" after a failure.
        group.MapPost("/optimize", async (long fileAttachmentId, UserAccess me, AccessPolicy policy, IFileAttachmentStore files, FileService service,
            VideoService video, CancellationToken ct) =>
        {
            var (file, category) = await VisibleVideoAsync(fileAttachmentId, me, policy, files, service, ct);
            await policy.RequireFileChangeAsync(me, file.AttachedToType, file.AttachedToId, file.UploadedByUserId, category, ct);
            return Results.Ok(new { queued = await video.QueueOneAsync(fileAttachmentId, ct) });
        });

        // Ticked frames become photos of the same Parcel / Asset (drone video -> drone photos).
        group.MapPost("/frames", async (long fileAttachmentId, FramesDto dto, UserAccess me, AccessPolicy policy, IFileAttachmentStore files,
            FileService service, VideoService video, CancellationToken ct) =>
        {
            var (file, _) = await VisibleVideoAsync(fileAttachmentId, me, policy, files, service, ct);
            if (dto.Indexes is not { Length: > 0 })
            {
                throw new DomainValidationException("VIDEO_FRAMES_NONE", "Tick at least one frame.");
            }

            var types = await files.ListTypesAsync(ct);
            var videoType = types.FirstOrDefault(t => t.Id == file.FileTypeId);
            var code = videoType?.Code.StartsWith("DRONE", StringComparison.OrdinalIgnoreCase) == true ? "DRONE_PHOTO" : "PHOTO";
            var photo = types.FirstOrDefault(t => t.Code == code && t.IsActive) ?? types.FirstOrDefault(t => t.Code == "PHOTO" && t.IsActive)
                        ?? throw new DomainValidationException("FILE_TYPE_REQUIRED", "The file type Photo isn't on the list (any more).");
            await policy.RequireFileUploadAsync(me, file.AttachedToType, file.AttachedToId, photo.Category, ct);
            var ids = await video.AddFramesAsync(fileAttachmentId, dto.Indexes, me.UserId, photo, ct);
            return Results.Ok(new { added = ids.Count, fileAttachmentIds = ids });
        });

        // "Take-off was 5 m above the plot": added to every height shown.
        group.MapPut("/offset", async (long fileAttachmentId, OffsetDto dto, UserAccess me, AccessPolicy policy, IFileAttachmentStore files,
            FileService service, VideoService video, CancellationToken ct) =>
        {
            var (file, category) = await VisibleVideoAsync(fileAttachmentId, me, policy, files, service, ct);
            await policy.RequireFileChangeAsync(me, file.AttachedToType, file.AttachedToId, file.UploadedByUserId, category, ct);
            await video.SetOffsetAsync(fileAttachmentId, dto.OffsetM, ct);
            return Results.NoContent();
        });

        // Admin: convert every video not converted yet (those uploaded before the feature).
        app.MapPost("/api/video/optimize-all", async (UserAccess me, VideoService video, CancellationToken ct) =>
        {
            if (!me.IsAdmin)
            {
                throw new ForbiddenException("VIDEO_ADMIN_ONLY", "Only an Admin can convert all videos.");
            }

            return Results.Ok(new { queued = await video.QueueAllAsync(ct) });
        });
    }

    private static async Task<(FileAttachment File, string Category)> VisibleVideoAsync(long fileAttachmentId, UserAccess me, AccessPolicy policy,
        IFileAttachmentStore files, FileService service, CancellationToken ct)
    {
        var file = await files.GetAsync(fileAttachmentId, ct);
        var category = file is null ? "" : await service.CategoryOfAsync(file.FileTypeId, ct);
        if (file is not { UploadStatus: FileUploadStatus.Ready } || !file.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || !(await policy.FileTargetAsync(me, file.AttachedToType, file.AttachedToId, ct)).CanView || !me.CanSeeFileCategory(category))
        {
            throw new EntityNotFoundException("File", fileAttachmentId);
        }

        return (file, category);
    }
}

/// <summary>Every 30 s: queue, send and collect conversions. Once a day: delete originals whose waiting period is over.</summary>
public sealed class VideoWorker : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SweepEvery = TimeSpan.FromHours(24);

    private readonly VideoService _video;
    private readonly ILogger<VideoWorker> _log;

    public VideoWorker(VideoService video, ILogger<VideoWorker> log)
    {
        _video = video;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_video.Settings.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(FirstRunDelay, stoppingToken);
            var nextSweep = DateTime.UtcNow.AddMinutes(10);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    var did = await _video.TickAsync(stoppingToken);
                    if (did is not null)
                    {
                        _log.LogInformation("Videos: {What}.", did);
                    }

                    if (DateTime.UtcNow >= nextSweep)
                    {
                        nextSweep = DateTime.UtcNow.Add(SweepEvery);
                        var deleted = await _video.SweepAsync(stoppingToken);
                        if (deleted > 0)
                        {
                            _log.LogInformation("Videos: deleted {Count} original(s) after their waiting period.", deleted);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Video round failed; will retry in {Interval}.", Interval); // never takes the app down
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
