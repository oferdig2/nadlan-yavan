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

    public async Task AddLocationAsync(long workRequestId, double latitude, double longitude, decimal? accuracyM, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO work_location (work_request_id, latitude, longitude, accuracy_m) VALUES (@workRequestId, @latitude, @longitude, @accuracyM)",
            new { workRequestId, latitude, longitude, accuracyM }, cancellationToken: ct));
    }

    public async Task<WorkLocationPoint?> LastLocationAsync(long workRequestId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<WorkLocationPoint>(new CommandDefinition("""
            SELECT latitude, longitude, accuracy_m, recorded_utc FROM work_location
            WHERE work_request_id = @workRequestId ORDER BY recorded_utc DESC, work_location_id DESC LIMIT 1
            """, new { workRequestId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<WorkLocationPoint>> ListLocationsAsync(long workRequestId, DateTime fromUtc, DateTime toUtc, int limit, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<WorkLocationPoint>(new CommandDefinition("""
            SELECT latitude, longitude, accuracy_m, recorded_utc FROM work_location
            WHERE work_request_id = @workRequestId AND recorded_utc >= @fromUtc AND recorded_utc < @toUtc
            ORDER BY recorded_utc, work_location_id LIMIT @limit
            """, new { workRequestId, fromUtc, toUtc, limit }, cancellationToken: ct))).ToList();
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
                "DELETE FROM work_location WHERE recorded_utc < @recordedBeforeUtc ORDER BY recorded_utc LIMIT 5000", new { recordedBeforeUtc }, cancellationToken: ct));
            total += n;
        }
        while (n == 5000);
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
