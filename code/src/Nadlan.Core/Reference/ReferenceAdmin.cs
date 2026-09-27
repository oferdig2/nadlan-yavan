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
        if (!CodePattern().IsMatch(code))
        {
            throw new DomainValidationException("REFERENCE_CODE_INVALID", "Code: capital letters, digits and _ only (max 40), e.g. UNDER_OFFER.");
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
    /// </summary>
    public async Task UpdateAsync(ReferenceTable table, ReferenceRow input, CancellationToken ct = default)
    {
        var existing = await _store.GetAsync(table, input.Id, ct) ?? throw new EntityNotFoundException("ReferenceRow", input.Id);
        await _store.UpdateAsync(table, Validate(table, input with { Code = existing.Code }), ct);
    }

    private static ReferenceRow Validate(ReferenceTable table, ReferenceRow row)
    {
        var name = TextNormalize.NullIfBlank(row.Name) ?? throw new DomainValidationException("REFERENCE_NAME_REQUIRED", "Enter a name.");
        if (name.Length > 200)
        {
            throw new DomainValidationException("REFERENCE_NAME_TOO_LONG", "The name is too long.");
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

    [GeneratedRegex("^[A-Z0-9_]{1,40}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();
}
