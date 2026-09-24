using Dapper;
using Nadlan.Core.Files;

namespace Nadlan.Persistence.MySql.Files;

public sealed class MySqlFileAttachmentStore : IFileAttachmentStore
{
    private readonly MySqlDatabase _db;

    public MySqlFileAttachmentStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<long> InsertPendingAsync(FileAttachment file, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO file_attachment (file_type_id, attached_to_type, attached_to_id, storage_key, original_file_name,
                                         mime_type, file_size, upload_status, s3_upload_id)
            VALUES (@FileTypeId, @AttachedToType, @AttachedToId, @StorageKey, @OriginalFileName,
                    @MimeType, @FileSize, @UploadStatus, @S3UploadId);
            SELECT LAST_INSERT_ID();
            """, file, cancellationToken: ct));
    }

    public async Task<FileAttachment?> GetAsync(long fileAttachmentId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<FileAttachment>(new CommandDefinition("""
            SELECT file_attachment_id, file_type_id, attached_to_type, attached_to_id, storage_key, original_file_name,
                   mime_type, file_size, caption, notes, sort_order, upload_status, s3_upload_id, uploaded_utc
            FROM file_attachment WHERE file_attachment_id = @fileAttachmentId
            """, new { fileAttachmentId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<FileListItem>> ListReadyAsync(string attachedToType, long attachedToId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<FileListItem>(new CommandDefinition("""
            SELECT f.file_attachment_id, f.file_type_id, t.code AS file_type_code, t.name AS file_type_name, t.category,
                   f.attached_to_type, f.attached_to_id, f.storage_key, f.original_file_name, f.mime_type, f.file_size,
                   f.caption, f.notes, f.sort_order, f.uploaded_utc
            FROM file_attachment f
            JOIN file_type t ON t.file_type_id = f.file_type_id
            WHERE f.attached_to_type = @attachedToType AND f.attached_to_id = @attachedToId AND f.upload_status = 'Ready'
            ORDER BY t.sort_order, COALESCE(f.sort_order, 2147483647), f.uploaded_utc
            """, new { attachedToType, attachedToId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task MarkReadyAsync(long fileAttachmentId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE file_attachment
            SET upload_status = 'Ready', s3_upload_id = NULL, completed_utc = UTC_TIMESTAMP(3), updated_utc = UTC_TIMESTAMP(3)
            WHERE file_attachment_id = @fileAttachmentId
            """, new { fileAttachmentId }, cancellationToken: ct));
    }

    public async Task UpdateMetadataAsync(long fileAttachmentId, int fileTypeId, string? caption, string? notes, int? sortOrder, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE file_attachment
            SET file_type_id = @fileTypeId, caption = @caption, notes = @notes, sort_order = @sortOrder, updated_utc = UTC_TIMESTAMP(3)
            WHERE file_attachment_id = @fileAttachmentId
            """, new { fileAttachmentId, fileTypeId, caption, notes, sortOrder }, cancellationToken: ct));
    }

    public async Task DeleteAsync(long fileAttachmentId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM file_attachment WHERE file_attachment_id = @fileAttachmentId", new { fileAttachmentId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<FileType>> ListTypesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<FileType>(new CommandDefinition("""
            SELECT file_type_id AS Id, code AS Code, name AS Name, category AS Category, is_active AS IsActive, sort_order AS SortOrder
            FROM file_type ORDER BY sort_order, name
            """, cancellationToken: ct));
        return rows.ToList();
    }
}

public sealed class MySqlFileTargetResolver : IFileTargetResolver
{
    // Fixed map: the table name never comes from input. User has no table yet (auth comes later).
    private static readonly Dictionary<string, (string Table, string Id)> Targets = new(StringComparer.Ordinal)
    {
        [FileTargetTypes.Parcel] = ("parcel", "parcel_id"),
        [FileTargetTypes.Asset] = ("asset", "asset_id"),
        [FileTargetTypes.Portfolio] = ("portfolio", "portfolio_id"),
        [FileTargetTypes.Contact] = ("contact", "contact_id"),
    };

    private readonly MySqlDatabase _db;

    public MySqlFileTargetResolver(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<bool> ExistsAsync(string attachedToType, long attachedToId, CancellationToken ct = default)
    {
        if (!Targets.TryGetValue(attachedToType, out var target))
        {
            return false;
        }

        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM {target.Table} WHERE {target.Id} = @attachedToId", new { attachedToId }, cancellationToken: ct)) > 0;
    }
}
