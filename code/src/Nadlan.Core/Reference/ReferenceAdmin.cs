using System.Text.RegularExpressions;
using Nadlan.Core.Text;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Reference;

/// <summary>The small Admin-maintained lookup tables (Appendix 1 §9.1). One generic editor serves all of them.</summary>
public enum ReferenceTable
{
    AssetStatus,
    PropertyType,
    PortfolioType,
    ContactRole,
    FileType,
    GeographicArea,
}

/// <summary>A row of any lookup table. Color: statuses only; Category: file types only.</summary>
public sealed record ReferenceRow(int Id, string Code, string Name, bool IsActive, int SortOrder, string? Color, string? Category);

public interface IReferenceAdminStore
{
    Task<IReadOnlyList<ReferenceRow>> ListAsync(ReferenceTable table, CancellationToken ct = default);
    Task<ReferenceRow?> GetAsync(ReferenceTable table, int id, CancellationToken ct = default);
    Task<int> InsertAsync(ReferenceTable table, ReferenceRow row, CancellationToken ct = default);
    Task UpdateAsync(ReferenceTable table, ReferenceRow row, CancellationToken ct = default);
}

public sealed partial class ReferenceAdminService
{
    public static readonly IReadOnlyList<string> FileCategories = new[] { "Legal", "Engineering", "Marketing", "Cadastral", "Permission", "General" };

    private readonly IReferenceAdminStore _store;

    public ReferenceAdminService(IReferenceAdminStore store)
    {
        _store = store;
    }

    public async Task<int> CreateAsync(ReferenceTable table, ReferenceRow input, CancellationToken ct = default)
    {
        var code = (input.Code ?? "").Trim().ToUpperInvariant();
        var maxCode = MaxCodeLength(table);
        if (!CodePattern().IsMatch(code) || code.Length > maxCode)
        {
            throw new DomainValidationException("REFERENCE_CODE_INVALID", $"Code: capital letters, digits and _ only (max {maxCode}), e.g. UNDER_OFFER.");
        }

        try
        {
            return await _store.InsertAsync(table, Validate(table, input with { Code = code }), ct);
        }
        catch (DuplicateKeyException)
        {
            throw new DomainValidationException("REFERENCE_CODE_EXISTS", $"The code {code} is already used in this list.");
        }
    }

    /// <summary>
    /// Name, order, active, colour/category can change; the Code can't, because the app and imports refer to
    /// codes (e.g. FOR_SALE, LEGAL_OWNER, the file types behind the drop zones). Rows are never deleted.
    /// A file type's category decides who sees every file of that type (a Legal document moved to Marketing would show
    /// to everyone who sees Marketing files), so only an Admin may change it (<paramref name="mayRecategoriseFiles"/>).
    /// </summary>
    public async Task UpdateAsync(ReferenceTable table, ReferenceRow input, bool mayRecategoriseFiles, CancellationToken ct = default)
    {
        var existing = await _store.GetAsync(table, input.Id, ct) ?? throw new EntityNotFoundException("ReferenceRow", input.Id);
        if (table == ReferenceTable.FileType && !mayRecategoriseFiles && !string.Equals(input.Category, existing.Category, StringComparison.Ordinal))
        {
            throw new ForbiddenException("FILE_TYPE_CATEGORY_ADMIN_ONLY",
                "Only an Admin can move a file type to another category: it changes who can see every file of that type.");
        }

        await _store.UpdateAsync(table, Validate(table, input with { Code = existing.Code }), ct);
    }

    private static ReferenceRow Validate(ReferenceTable table, ReferenceRow row)
    {
        var name = TextNormalize.NullIfBlank(row.Name) ?? throw new DomainValidationException("REFERENCE_NAME_REQUIRED", "Enter a name.");
        var maxName = MaxNameLength(table);
        if (name.Length > maxName)
        {
            throw new DomainValidationException("REFERENCE_NAME_TOO_LONG", $"The name can have at most {maxName} characters.");
        }

        var color = row.Color;
        if (table == ReferenceTable.AssetStatus)
        {
            color = TextNormalize.NullIfBlank(color) ?? "#2563eb";
            if (!ColorPattern().IsMatch(color))
            {
                throw new DomainValidationException("REFERENCE_COLOR_INVALID", "Map colour must look like #16a34a.");
            }
        }

        var category = row.Category;
        if (table == ReferenceTable.FileType && !FileCategories.Contains(category ?? ""))
        {
            throw new DomainValidationException("REFERENCE_CATEGORY_INVALID", $"Category must be one of: {string.Join(", ", FileCategories)}.");
        }

        return row with { Name = name, Color = color, Category = category };
    }

    // Column sizes (Sql/002-004): geographic_area has code VARCHAR(16) / name VARCHAR(200), the others VARCHAR(40) / VARCHAR(100).
    private static int MaxCodeLength(ReferenceTable table) => table == ReferenceTable.GeographicArea ? 16 : 40;

    private static int MaxNameLength(ReferenceTable table) => table == ReferenceTable.GeographicArea ? 200 : 100;

    [GeneratedRegex("^[A-Z0-9_]{1,40}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();
}
