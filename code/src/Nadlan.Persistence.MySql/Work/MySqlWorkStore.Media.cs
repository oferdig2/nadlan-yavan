using Dapper;
using Nadlan.Core.Work;

namespace Nadlan.Persistence.MySql.Work;

/// <summary>Piece 3: work pictures, the conversations, the contractor's track and the Parcels' photos for the contractor.</summary>
public sealed partial class MySqlWorkStore
{
    private const string WorkFileSelect = """
        SELECT f.work_file_id, f.work_item_id, wi.work_request_id, wi.parcel_id, p.registry_id, wt.name AS work_type_name,
               f.storage_key, f.original_name, f.mime_type, f.file_size, f.upload_status, f.s3_upload_id, f.has_thumbnail,
               f.taken_local, f.camera_make, f.camera_model, f.latitude, f.longitude, f.width, f.height, f.duration_s,
               f.upload_latitude, f.upload_longitude, f.upload_accuracy_m, f.uploaded_utc, f.copied_to_file_attachment_id
        FROM work_file f
        JOIN work_item wi ON wi.work_item_id = f.work_item_id
        JOIN work_type wt ON wt.work_type_id = wi.work_type_id
        JOIN parcel p ON p.parcel_id = wi.parcel_id
        """;

    public async Task<long> InsertWorkFileAsync(WorkFile file, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO work_file (work_item_id, storage_key, original_name, mime_type, file_size, upload_status, s3_upload_id,
                                   upload_latitude, upload_longitude, upload_accuracy_m)
            VALUES (@WorkItemId, @StorageKey, @OriginalName, @MimeType, @FileSize, 'Pending', @S3UploadId,
                    @UploadLatitude, @UploadLongitude, @UploadAccuracyM);
            SELECT LAST_INSERT_ID();
            """, file, cancellationToken: ct));
    }

    public async Task<WorkFile?> GetWorkFileAsync(long workRequestId, long workFileId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<WorkFile>(new CommandDefinition(
            WorkFileSelect + " WHERE f.work_file_id = @workFileId AND wi.work_request_id = @workRequestId",
            new { workRequestId, workFileId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<WorkFile>> ListWorkFilesAsync(long workRequestId, long? parcelId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<WorkFile>(new CommandDefinition(
            WorkFileSelect + """
             WHERE wi.work_request_id = @workRequestId AND f.upload_status = 'Ready' AND (@parcelId IS NULL OR wi.parcel_id = @parcelId)
            ORDER BY f.uploaded_utc DESC, f.work_file_id DESC
            """, new { workRequestId, parcelId }, cancellationToken: ct))).ToList();
    }

    public async Task<bool> MarkWorkFileReadyAsync(long workFileId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE work_file SET upload_status = 'Ready', s3_upload_id = NULL WHERE work_file_id = @workFileId AND upload_status = 'Pending'",
            new { workFileId }, cancellationToken: ct)) == 1;
    }

    public async Task<bool> DeletePendingWorkFileAsync(long workFileId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM work_file WHERE work_file_id = @workFileId AND upload_status = 'Pending'", new { workFileId }, cancellationToken: ct)) == 1;
    }

    public async Task SetWorkFileThumbnailAsync(long workFileId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE work_file SET has_thumbnail = 1 WHERE work_file_id = @workFileId", new { workFileId }, cancellationToken: ct));
    }

    public async Task SetWorkFileMediaAsync(long workFileId, WorkFile media, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE work_file SET taken_local = @TakenLocal, camera_make = @CameraMake, camera_model = @CameraModel, latitude = @Latitude,
                                 longitude = @Longitude, width = @Width, height = @Height, duration_s = @DurationS
            WHERE work_file_id = @workFileId
            """, new { workFileId, media.TakenLocal, media.CameraMake, media.CameraModel, media.Latitude, media.Longitude, media.Width, media.Height, media.DurationS },
            cancellationToken: ct));
    }

    public async Task<bool> MarkWorkFileCopiedAsync(long workFileId, long fileAttachmentId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE work_file SET copied_to_file_attachment_id = @fileAttachmentId WHERE work_file_id = @workFileId AND copied_to_file_attachment_id IS NULL",
            new { workFileId, fileAttachmentId }, cancellationToken: ct)) == 1;
    }

    public async Task<IReadOnlyList<WorkFile>> ListStalePendingWorkFilesAsync(DateTime startedBeforeUtc, int limit, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<WorkFile>(new CommandDefinition(
            WorkFileSelect + " WHERE f.upload_status = 'Pending' AND f.uploaded_utc < @startedBeforeUtc ORDER BY f.work_file_id LIMIT @limit",
            new { startedBeforeUtc, limit }, cancellationToken: ct))).ToList();
    }

    // ---- conversation -------------------------------------------------------------------------------------------

    public async Task<long> AddMessageAsync(long workRequestId, long parcelId, string authorKind, long? authorUserId, string body, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO work_message (work_request_id, parcel_id, author_kind, author_user_id, body)
            VALUES (@workRequestId, @parcelId, @authorKind, @authorUserId, @body);
            SELECT LAST_INSERT_ID();
            """, new { workRequestId, parcelId, authorKind, authorUserId, body }, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE work_request SET updated_utc = UTC_TIMESTAMP(3) WHERE work_request_id = @workRequestId", new { workRequestId }, cancellationToken: ct));
        return id;
    }

    public async Task<IReadOnlyList<WorkMessage>> ListMessagesAsync(long workRequestId, long? parcelId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<WorkMessage>(new CommandDefinition("""
            SELECT m.work_message_id, m.parcel_id, m.author_kind, u.display_name AS author_name, m.body, m.created_utc
            FROM work_message m
            LEFT JOIN app_user u ON u.user_id = m.author_user_id
            WHERE m.work_request_id = @workRequestId AND (@parcelId IS NULL OR m.parcel_id = @parcelId)
            ORDER BY m.created_utc, m.work_message_id
            """, new { workRequestId, parcelId }, cancellationToken: ct))).ToList();
    }

    public async Task<bool> HasParcelAsync(long workRequestId, long parcelId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM work_request_parcel WHERE work_request_id = @workRequestId AND parcel_id = @parcelId)",
            new { workRequestId, parcelId }, cancellationToken: ct));
    }

    // ---- the contractor's track ---------------------------------------------------------------------------------

    public async Task<bool> AppendTrackAsync(long workRequestId, string batchId, IReadOnlyList<WorkTrackAppend> parts, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // The same batch again (the phone got no answer the first time): added once.
        if (await conn.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO work_track_batch (work_request_id, batch_id) VALUES (@workRequestId, @batchId)", new { workRequestId, batchId }, tx, cancellationToken: ct)) == 0)
        {
            return false;
        }

        foreach (var part in parts)
        {
            // The parcel's one path: made by its first batch, every later one added to its end (the JSON array stays whole).
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO work_track (work_request_id, parcel_id, path, first_utc, last_utc, entry_count)
                VALUES (@workRequestId, @parcelId, CONCAT('[', @items, ']'), @firstUtc, @lastUtc, @count)
                ON DUPLICATE KEY UPDATE path = CONCAT(LEFT(path, CHAR_LENGTH(path) - 1), ',', @items, ']'),
                  first_utc = LEAST(first_utc, @firstUtc), last_utc = GREATEST(last_utc, @lastUtc), entry_count = entry_count + @count,
                  updated_utc = UTC_TIMESTAMP(3)
                """, new { workRequestId, part.ParcelId, items = part.Items, part.FirstUtc, part.LastUtc, part.Count }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<WorkTrackPath>> ListTracksAsync(long workRequestId, long? parcelId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<WorkTrackPath>(new CommandDefinition("""
            SELECT parcel_id, first_utc, last_utc, path FROM work_track
            WHERE work_request_id = @workRequestId AND (@parcelId IS NULL OR parcel_id = @parcelId) ORDER BY last_utc DESC
            """, new { workRequestId, parcelId }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<(long WorkRequestId, long ParcelId)>> ListTracksStartingBeforeAsync(DateTime beforeUtc, int limit,
        CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<(long, long)>(new CommandDefinition("""
            SELECT work_request_id, parcel_id FROM work_track WHERE first_utc < @beforeUtc AND last_utc >= @beforeUtc ORDER BY first_utc LIMIT @limit
            """, new { beforeUtc, limit }, cancellationToken: ct))).ToList();
    }

    public async Task RewriteTrackAsync(long workRequestId, long parcelId, Func<string, WorkTrackRewrite?> rewrite, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var path = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT path FROM work_track WHERE work_request_id = @workRequestId AND parcel_id = @parcelId FOR UPDATE",
            new { workRequestId, parcelId }, tx, cancellationToken: ct));
        if (path is null)
        {
            return;
        }

        var next = rewrite(path);
        await conn.ExecuteAsync(new CommandDefinition(next is null
            ? "DELETE FROM work_track WHERE work_request_id = @workRequestId AND parcel_id = @parcelId"
            : """
              UPDATE work_track SET path = @Path, first_utc = @FirstUtc, last_utc = @LastUtc, entry_count = @Count, updated_utc = UTC_TIMESTAMP(3)
              WHERE work_request_id = @workRequestId AND parcel_id = @parcelId
              """,
            new { workRequestId, parcelId, next?.Path, next?.FirstUtc, next?.LastUtc, next?.Count }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<int> PurgeLocationsAsync(DateTime recordedBeforeUtc, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        // In slices, so a big first purge never holds a long lock.
        var total = 0;
        int n;
        do
        {
            n = await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM work_track WHERE last_utc < @recordedBeforeUtc ORDER BY last_utc LIMIT 500", new { recordedBeforeUtc }, cancellationToken: ct));
            total += n;
        }
        while (n == 500);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM work_track_batch WHERE received_utc < @recordedBeforeUtc", new { recordedBeforeUtc }, cancellationToken: ct));
        return total;
    }

    // ---- the Parcels' photos for the contractor -----------------------------------------------------------------

    public async Task<IReadOnlyList<WorkParcelMedia>> ListParcelMediaAsync(long workRequestId, string category, IReadOnlyCollection<string> mimeTypes,
        CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<WorkParcelMedia>(new CommandDefinition("""
            SELECT fa.file_attachment_id, fa.attached_to_id AS parcel_id, fa.storage_key, fa.original_file_name, fa.mime_type, fa.has_thumbnail,
                   m.taken_local
            FROM work_request_parcel wrp
            JOIN file_attachment fa ON fa.attached_to_type = 'Parcel' AND fa.attached_to_id = wrp.parcel_id AND fa.upload_status = 'Ready'
            JOIN file_type ft ON ft.file_type_id = fa.file_type_id AND ft.category = @category
            LEFT JOIN file_media_metadata m ON m.file_attachment_id = fa.file_attachment_id
            WHERE wrp.work_request_id = @workRequestId AND LOWER(TRIM(SUBSTRING_INDEX(fa.mime_type, ';', 1))) IN @mimeTypes
            ORDER BY wrp.sort_order, COALESCE(fa.sort_order, 2147483647), fa.uploaded_utc
            """, new { workRequestId, category, mimeTypes = mimeTypes.Select(t => t.ToLowerInvariant()).ToArray() }, cancellationToken: ct))).ToList();
    }
}
