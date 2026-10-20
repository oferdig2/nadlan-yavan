using Dapper;
using Nadlan.Core.Files;

namespace Nadlan.Persistence.MySql.Files;

/// <summary>Map drop: unassigned files with what was read from them, and video time frames per Parcel (migration 016).</summary>
public sealed class MySqlMapDropStore : IMapDropStore
{
    private const string MetadataColumns = """
        m.taken_local, m.camera_make, m.camera_model, m.latitude, m.longitude, m.rel_altitude_m, m.abs_altitude_m,
        m.gimbal_yaw, m.gimbal_pitch, m.gps_status, m.duration_s, m.width, m.height, m.bitrate_kbps, m.video_codec,
        m.index_at_end, m.source_modified_utc, m.track_file_id, m.track_json, m.suggestions_json
        """;

    private const string FileColumns = """
        f.file_attachment_id, f.file_type_id, t.code AS file_type_code, t.name AS file_type_name, t.category,
        f.attached_to_type, f.attached_to_id, f.storage_key, f.original_file_name, f.mime_type, f.file_size,
        f.caption, f.notes, f.sort_order, f.uploaded_utc, f.has_thumbnail
        """;

    private readonly MySqlDatabase _db;

    public MySqlMapDropStore(MySqlDatabase db)
    {
        _db = db;
    }

    private sealed record ItemRow
    {
        public long FileAttachmentId { get; init; }
        public int FileTypeId { get; init; }
        public string FileTypeCode { get; init; } = "";
        public string FileTypeName { get; init; } = "";
        public string Category { get; init; } = "";
        public string AttachedToType { get; init; } = "";
        public long AttachedToId { get; init; }
        public string StorageKey { get; init; } = "";
        public string OriginalFileName { get; init; } = "";
        public string MimeType { get; init; } = "";
        public long FileSize { get; init; }
        public string? Caption { get; init; }
        public string? Notes { get; init; }
        public int? SortOrder { get; init; }
        public DateTime UploadedUtc { get; init; }
        public bool HasThumbnail { get; init; }
        public long? UploadedByUserId { get; init; }
        public string? UploadedByName { get; init; }
        public bool HasMetadata { get; init; }
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

    public async Task<IReadOnlyList<MapDropItem>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ItemRow>(new CommandDefinition($"""
            SELECT {FileColumns}, f.uploaded_by_user_id, u.display_name AS uploaded_by_name,
                   (m.file_attachment_id IS NOT NULL) AS has_metadata, {MetadataColumns}
            FROM file_attachment f
            JOIN file_type t ON t.file_type_id = f.file_type_id
            LEFT JOIN app_user u ON u.user_id = f.uploaded_by_user_id
            LEFT JOIN file_media_metadata m ON m.file_attachment_id = f.file_attachment_id
            WHERE f.attached_to_type = 'MapDrop' AND f.upload_status = 'Ready'
            ORDER BY f.uploaded_utc, f.file_attachment_id
            """, cancellationToken: ct));
        return rows.Select(r => new MapDropItem
        {
            File = new FileListItem
            {
                FileAttachmentId = r.FileAttachmentId, FileTypeId = r.FileTypeId, FileTypeCode = r.FileTypeCode, FileTypeName = r.FileTypeName,
                Category = r.Category, AttachedToType = r.AttachedToType, AttachedToId = r.AttachedToId, StorageKey = r.StorageKey,
                OriginalFileName = r.OriginalFileName, MimeType = r.MimeType, FileSize = r.FileSize, Caption = r.Caption, Notes = r.Notes,
                SortOrder = r.SortOrder, UploadedUtc = DateTime.SpecifyKind(r.UploadedUtc, DateTimeKind.Utc), HasThumbnail = r.HasThumbnail,
            },
            UploadedByUserId = r.UploadedByUserId,
            UploadedByName = r.UploadedByName,
            Metadata = r.HasMetadata ? ToMetadata(r) : null,
        }).ToList();
    }

    private static MediaMetadata ToMetadata(ItemRow r) => new()
    {
        TakenLocal = r.TakenLocal, CameraMake = r.CameraMake, CameraModel = r.CameraModel, Latitude = r.Latitude, Longitude = r.Longitude,
        RelAltitudeM = r.RelAltitudeM, AbsAltitudeM = r.AbsAltitudeM, GimbalYaw = r.GimbalYaw, GimbalPitch = r.GimbalPitch, GpsStatus = r.GpsStatus,
        DurationS = r.DurationS, Width = r.Width, Height = r.Height, BitrateKbps = r.BitrateKbps, VideoCodec = r.VideoCodec, IndexAtEnd = r.IndexAtEnd,
        SourceModifiedUtc = r.SourceModifiedUtc is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null,
        TrackFileId = r.TrackFileId, TrackJson = r.TrackJson, SuggestionsJson = r.SuggestionsJson,
    };

    public async Task<MediaMetadata?> GetMetadataAsync(long fileAttachmentId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ItemRow>(new CommandDefinition($"""
            SELECT {MetadataColumns} FROM file_media_metadata m WHERE m.file_attachment_id = @fileAttachmentId
            """, new { fileAttachmentId }, cancellationToken: ct));
        return row is null ? null : ToMetadata(row);
    }

    public async Task UpsertMetadataAsync(long fileAttachmentId, MediaMetadata m, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO file_media_metadata (file_attachment_id, taken_local, camera_make, camera_model, latitude, longitude, rel_altitude_m,
                abs_altitude_m, gimbal_yaw, gimbal_pitch, gps_status, duration_s, width, height, bitrate_kbps, video_codec, index_at_end,
                source_modified_utc, track_file_id, track_json, suggestions_json)
            VALUES (@fileAttachmentId, @TakenLocal, @CameraMake, @CameraModel, @Latitude, @Longitude, @RelAltitudeM,
                @AbsAltitudeM, @GimbalYaw, @GimbalPitch, @GpsStatus, @DurationS, @Width, @Height, @BitrateKbps, @VideoCodec, @IndexAtEnd,
                @SourceModifiedUtc, @TrackFileId, @TrackJson, @SuggestionsJson)
            ON DUPLICATE KEY UPDATE
                taken_local = VALUES(taken_local), camera_make = VALUES(camera_make), camera_model = VALUES(camera_model), latitude = VALUES(latitude),
                longitude = VALUES(longitude), rel_altitude_m = VALUES(rel_altitude_m), abs_altitude_m = VALUES(abs_altitude_m), gimbal_yaw = VALUES(gimbal_yaw),
                gimbal_pitch = VALUES(gimbal_pitch), gps_status = VALUES(gps_status), duration_s = VALUES(duration_s), width = VALUES(width), height = VALUES(height),
                bitrate_kbps = VALUES(bitrate_kbps), video_codec = VALUES(video_codec), index_at_end = VALUES(index_at_end),
                source_modified_utc = VALUES(source_modified_utc), track_file_id = VALUES(track_file_id), track_json = VALUES(track_json),
                suggestions_json = VALUES(suggestions_json), read_utc = UTC_TIMESTAMP(3)
            """, new
        {
            fileAttachmentId, m.TakenLocal, m.CameraMake, m.CameraModel, m.Latitude, m.Longitude, m.RelAltitudeM, m.AbsAltitudeM,
            m.GimbalYaw, m.GimbalPitch, m.GpsStatus, m.DurationS, m.Width, m.Height, m.BitrateKbps, m.VideoCodec, m.IndexAtEnd,
            m.SourceModifiedUtc, m.TrackFileId, m.TrackJson, m.SuggestionsJson,
        }, cancellationToken: ct));
    }

    public async Task<bool> AssignAsync(long fileAttachmentId, long parcelId, int fileTypeId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE file_attachment
            SET attached_to_type = 'Parcel', attached_to_id = @parcelId, file_type_id = @fileTypeId, updated_utc = UTC_TIMESTAMP(3)
            WHERE file_attachment_id = @fileAttachmentId AND attached_to_type = 'MapDrop' AND upload_status = 'Ready'
            """, new { fileAttachmentId, parcelId, fileTypeId }, cancellationToken: ct)) == 1;
    }

    public async Task<IReadOnlyList<FileSegment>> ListSegmentsAsync(long fileAttachmentId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<FileSegment>(new CommandDefinition("""
            SELECT s.parcel_id AS ParcelId, s.start_ms AS StartMs, s.end_ms AS EndMs, s.source AS Source, p.registry_id AS RegistryId
            FROM file_segment s JOIN parcel p ON p.parcel_id = s.parcel_id
            WHERE s.file_attachment_id = @fileAttachmentId
            ORDER BY s.start_ms, s.parcel_id
            """, new { fileAttachmentId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task ReplaceSegmentsAsync(long fileAttachmentId, IReadOnlyList<FileSegment> segments, long? userId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM file_segment WHERE file_attachment_id = @fileAttachmentId",
            new { fileAttachmentId }, tx, cancellationToken: ct));
        if (segments.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO file_segment (file_attachment_id, parcel_id, start_ms, end_ms, source, updated_by_user_id)
                VALUES (@FileAttachmentId, @ParcelId, @StartMs, @EndMs, @Source, @UserId)
                """, segments.Select(s => new { FileAttachmentId = fileAttachmentId, s.ParcelId, s.StartMs, s.EndMs, s.Source, UserId = userId }),
                tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<FileSegment>>> ListSegmentsOnParcelAsync(long parcelId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long FileAttachmentId, int StartMs, int EndMs, string Source)>(new CommandDefinition("""
            SELECT file_attachment_id, start_ms, end_ms, source FROM file_segment WHERE parcel_id = @parcelId ORDER BY start_ms
            """, new { parcelId }, cancellationToken: ct));
        return rows.GroupBy(r => r.FileAttachmentId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<FileSegment>)g.Select(r => new FileSegment(parcelId, r.StartMs, r.EndMs, r.Source)).ToList());
    }

    public async Task<IReadOnlyList<FileListItem>> ListLinkedFilesAsync(long parcelId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<FileListItem>(new CommandDefinition($"""
            SELECT {FileColumns}
            FROM file_attachment f
            JOIN file_type t ON t.file_type_id = f.file_type_id
            WHERE f.upload_status = 'Ready'
              AND NOT (f.attached_to_type = 'Parcel' AND f.attached_to_id = @parcelId)
              AND f.attached_to_type <> 'MapDrop'
              AND EXISTS (SELECT 1 FROM file_segment s WHERE s.file_attachment_id = f.file_attachment_id AND s.parcel_id = @parcelId)
            ORDER BY t.sort_order, f.uploaded_utc
            """, new { parcelId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<DuplicateHit>> FindDuplicatesAsync(IReadOnlyList<(string Name, long Size)> files, CancellationToken ct = default)
    {
        if (files.Count == 0)
        {
            return Array.Empty<DuplicateHit>();
        }

        await using var conn = await _db.OpenAsync(ct);
        var sizes = files.Select(f => f.Size).Distinct().ToArray();
        var rows = await conn.QueryAsync<DuplicateHit>(new CommandDefinition("""
            SELECT f.original_file_name AS FileName, f.file_size AS FileSize, f.file_attachment_id AS FileAttachmentId,
                   f.attached_to_type AS AttachedToType, f.attached_to_id AS AttachedToId,
                   CASE WHEN f.attached_to_type = 'Parcel' THEN p.registry_id END AS RegistryId
            FROM file_attachment f
            LEFT JOIN parcel p ON f.attached_to_type = 'Parcel' AND p.parcel_id = f.attached_to_id
            WHERE f.upload_status = 'Ready' AND f.file_size IN @sizes
            """, new { sizes }, cancellationToken: ct));
        var wanted = files.Select(f => (f.Name.ToLowerInvariant(), f.Size)).ToHashSet();
        return rows.Where(r => wanted.Contains((r.FileName.ToLowerInvariant(), r.FileSize))).ToList();
    }

    public async Task<IReadOnlyList<long>> ListExpiredAsync(DateTime uploadedBeforeUtc, int limit, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<long>(new CommandDefinition("""
            SELECT file_attachment_id FROM file_attachment
            WHERE attached_to_type = 'MapDrop' AND uploaded_utc < @uploadedBeforeUtc
            ORDER BY uploaded_utc LIMIT @limit
            """, new { uploadedBeforeUtc, limit }, cancellationToken: ct));
        return rows.ToList();
    }
}
