using Dapper;
using Nadlan.Core.Import;

namespace Nadlan.Persistence.MySql.Import;

public sealed class MySqlImportStore : IImportStore
{
    private readonly MySqlDatabase _db;

    public MySqlImportStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<long> StartBatchAsync(string source, string sourceFile, long? importedByUserId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO import_batch (source, source_file, status, imported_by_user_id)
            VALUES (@source, @sourceFile, @status, @importedByUserId);
            SELECT LAST_INSERT_ID();
            """, new { source, sourceFile, status = ImportBatchStatus.Running, importedByUserId }, cancellationToken: ct));
    }

    public async Task AddRecordAsync(ImportRecord record, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO import_record (import_batch_id, legacy_table, legacy_record_id, target_entity_type,
                                       target_entity_id, status, error_message, raw_data)
            VALUES (@ImportBatchId, @LegacyTable, @LegacyRecordId, @TargetEntityType,
                    @TargetEntityId, @Status, @ErrorMessage, @RawDataJson)
            """, record, cancellationToken: ct));
    }

    public async Task CompleteBatchAsync(long importBatchId, string status, string summary, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE import_batch SET status = @status, summary = @summary, completed_utc = UTC_TIMESTAMP(3)
            WHERE import_batch_id = @importBatchId
            """, new { importBatchId, status, summary }, cancellationToken: ct));
    }
}
