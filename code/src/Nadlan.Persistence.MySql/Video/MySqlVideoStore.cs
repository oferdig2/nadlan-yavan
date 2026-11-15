using System.Text.Json;
using Dapper;
using Nadlan.Core.Video;

namespace Nadlan.Persistence.MySql.Video;

/// <summary>Video conversions (migration 028, video_rendition).</summary>
public sealed class MySqlVideoStore : IVideoStore
{
    private const string Columns = """
        file_attachment_id, status, job_id, attempts, with_audio, queued_utc, submitted_utc, completed_utc, error_message,
        small_key, small_size, large_key, large_size, duration_ms, frame_count, frames_json, poster_index, heights_json,
        takeoff_offset_m, original_key, original_size, original_mime, original_deleted_utc, frames_deleted_utc
        """;

    // A video on a Parcel or an Asset, with what the page read from inside it (size; a stored flight track).
    private const string CandidateSelect = """
        SELECT f.file_attachment_id, f.attached_to_type, f.attached_to_id, f.storage_key, f.original_file_name, f.mime_type, f.file_size,
               f.has_thumbnail, f.uploaded_utc, m.width, m.height, (m.track_json IS NOT NULL) AS has_track
        FROM file_attachment f
        LEFT JOIN file_media_metadata m ON m.file_attachment_id = f.file_attachment_id
        WHERE f.upload_status = 'Ready' AND f.mime_type LIKE 'video/%' AND f.attached_to_type IN ('Parcel', 'Asset')
        """;

    private readonly MySqlDatabase _db;

    public MySqlVideoStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<VideoRendition?> GetAsync(long fileAttachmentId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<VideoRendition>(new CommandDefinition(
            $"SELECT {Columns} FROM video_rendition WHERE file_attachment_id = @fileAttachmentId", new { fileAttachmentId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<VideoCandidate>> ListUnqueuedAsync(DateTime? uploadedAfterUtc, int limit, CancellationToken ct = default)
    {
        if (uploadedAfterUtc is { } cutoff && cutoff >= new DateTime(9000, 1, 1))
        {
            return Array.Empty<VideoCandidate>(); // "never" (not set)
        }

        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<VideoCandidate>(new CommandDefinition(CandidateSelect + """
              AND NOT EXISTS (SELECT 1 FROM video_rendition r WHERE r.file_attachment_id = f.file_attachment_id)
              AND (@uploadedAfterUtc IS NULL OR f.uploaded_utc >= @uploadedAfterUtc)
            ORDER BY f.uploaded_utc
            LIMIT @limit
            """, new { uploadedAfterUtc, limit }, cancellationToken: ct))).ToList();
    }

    public async Task<VideoCandidate?> GetCandidateAsync(long fileAttachmentId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<VideoCandidate>(new CommandDefinition(CandidateSelect + " AND f.file_attachment_id = @fileAttachmentId",
            new { fileAttachmentId }, cancellationToken: ct));
    }

    public async Task<bool> QueueAsync(VideoCandidate file, string? heightsJson, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            INSERT IGNORE INTO video_rendition (file_attachment_id, status, heights_json, original_key, original_size, original_mime)
            VALUES (@FileAttachmentId, 'Queued', @heightsJson, @StorageKey, @FileSize, @MimeType)
            """, new { file.FileAttachmentId, heightsJson, file.StorageKey, file.FileSize, file.MimeType }, cancellationToken: ct)) == 1;
    }

    public async Task<IReadOnlyList<VideoRendition>> ListByStatusAsync(string status, int limit, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<VideoRendition>(new CommandDefinition(
            $"SELECT {Columns} FROM video_rendition WHERE status = @status ORDER BY queued_utc LIMIT @limit", new { status, limit }, cancellationToken: ct))).ToList();
    }

    public Task MarkSubmittedAsync(long fileAttachmentId, string jobId, bool withAudio, CancellationToken ct = default) => UpdateAsync(
        "status = 'Submitted', job_id = @jobId, with_audio = @withAudio, attempts = attempts + 1, submitted_utc = UTC_TIMESTAMP(3), error_message = NULL",
        new { fileAttachmentId, jobId, withAudio }, ct);

    public Task MarkFailedAsync(long fileAttachmentId, string message, CancellationToken ct = default) =>
        UpdateAsync("status = 'Failed', error_message = @message", new { fileAttachmentId, message = Cut(message) }, ct);

    public Task CountAttemptAsync(long fileAttachmentId, string message, CancellationToken ct = default) =>
        UpdateAsync("attempts = attempts + 1, error_message = @message", new { fileAttachmentId, message = Cut(message) }, ct);

    public Task RequeueAsync(long fileAttachmentId, bool withAudio, CancellationToken ct = default) =>
        UpdateAsync("status = 'Queued', job_id = NULL, with_audio = @withAudio, queued_utc = UTC_TIMESTAMP(3)", new { fileAttachmentId, withAudio }, ct);

    public Task MarkReadyAsync(long fileAttachmentId, VideoResult x, CancellationToken ct = default) => UpdateAsync("""
        status = 'Ready', completed_utc = UTC_TIMESTAMP(3), error_message = NULL, small_key = @SmallKey, small_size = @SmallSize,
        large_key = @LargeKey, large_size = @LargeSize, duration_ms = @DurationMs, frame_count = @FrameCount, frames_json = @frames,
        poster_index = @PosterIndex
        """, new { fileAttachmentId, x.SmallKey, x.SmallSize, x.LargeKey, x.LargeSize, x.DurationMs, x.FrameCount, frames = JsonSerializer.Serialize(x.OfferedFrames), x.PosterIndex }, ct);

    public Task SetOffsetAsync(long fileAttachmentId, decimal offsetM, CancellationToken ct = default) =>
        UpdateAsync("takeoff_offset_m = @offsetM", new { fileAttachmentId, offsetM }, ct);

    public Task SetHeightsAsync(long fileAttachmentId, string? heightsJson, CancellationToken ct = default) =>
        UpdateAsync("heights_json = @heightsJson", new { fileAttachmentId, heightsJson }, ct);

    public async Task<IReadOnlyList<VideoRendition>> ListOriginalsDueAsync(DateTime completedBeforeUtc, int limit, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<VideoRendition>(new CommandDefinition($"""
            SELECT {Columns} FROM video_rendition
            WHERE status = 'Ready' AND original_deleted_utc IS NULL AND completed_utc < @completedBeforeUtc
            ORDER BY completed_utc LIMIT @limit
            """, new { completedBeforeUtc, limit }, cancellationToken: ct))).ToList();
    }

    public async Task<bool> SwitchToLargeAsync(long fileAttachmentId, string originalKey, string largeKey, long largeSize, string fileName, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE file_attachment
            SET storage_key = @largeKey, file_size = @largeSize, mime_type = 'video/mp4', original_file_name = @fileName, updated_utc = UTC_TIMESTAMP(3)
            WHERE file_attachment_id = @fileAttachmentId AND storage_key = @originalKey
            """, new { fileAttachmentId, originalKey, largeKey, largeSize, fileName }, cancellationToken: ct)) == 1;
    }

    public Task MarkOriginalDeletedAsync(long fileAttachmentId, CancellationToken ct = default) =>
        UpdateAsync("original_deleted_utc = UTC_TIMESTAMP(3), frames_deleted_utc = UTC_TIMESTAMP(3)", new { fileAttachmentId }, ct);

    private const string RunColumns = """
        job_id, file_attachment_id, file_name, submitted_utc, started_utc, stopped_utc, cpu, memory_mb, storage_gb, status, stopped_by, cost_usd
        """;

    public async Task StartRunAsync(string jobId, long? fileAttachmentId, int cpu, int memoryMb, int storageGb, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT IGNORE INTO video_worker_run (job_id, file_attachment_id, file_name, cpu, memory_mb, storage_gb)
            VALUES (@jobId, (SELECT f.file_attachment_id FROM file_attachment f WHERE f.file_attachment_id = @fileAttachmentId),
                    (SELECT f.original_file_name FROM file_attachment f WHERE f.file_attachment_id = @fileAttachmentId), @cpu, @memoryMb, @storageGb)
            """, new { jobId, fileAttachmentId, cpu, memoryMb, storageGb }, cancellationToken: ct));
    }

    public async Task EndRunAsync(string jobId, DateTime? startedUtc, DateTime? stoppedUtc, string status, string stoppedBy, decimal? costUsd, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE video_worker_run
            SET started_utc = COALESCE(@startedUtc, started_utc), stopped_utc = COALESCE(@stoppedUtc, UTC_TIMESTAMP(3)), status = @status,
                stopped_by = @stoppedBy, cost_usd = @costUsd
            WHERE job_id = @jobId AND stopped_by IS NULL
            """, new { jobId, startedUtc, stoppedUtc, status, stoppedBy = Cut(stoppedBy, 200), costUsd }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<VideoWorkerRun>> ListRunsAsync(int limit, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<VideoWorkerRun>(new CommandDefinition(
            $"SELECT {RunColumns} FROM video_worker_run ORDER BY submitted_utc DESC LIMIT @limit", new { limit }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<VideoWorkerRun>> ListOpenRunsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<VideoWorkerRun>(new CommandDefinition(
            $"SELECT {RunColumns} FROM video_worker_run WHERE stopped_by IS NULL ORDER BY submitted_utc LIMIT 200", cancellationToken: ct))).ToList();
    }

    private static string Cut(string message, int max = 1000) => message.Length <= max ? message : message[..max];

    private async Task UpdateAsync(string set, object args, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE video_rendition SET {set}, updated_utc = UTC_TIMESTAMP(3) WHERE file_attachment_id = @fileAttachmentId", args, cancellationToken: ct));
    }
}
