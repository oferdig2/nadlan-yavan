using Nadlan.Core.Reference;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Admin;

/// <summary>
/// Admin-only maintenance of lookup tables (Appendix 1 §9.1).
/// TODO(auth slice): RequireAuthorization(Admin) on the whole group.
/// </summary>
public static class AdminEndpoints
{
    // URL name → table. The page asks for e.g. /api/admin/reference/asset-statuses.
    private static readonly Dictionary<string, ReferenceTable> TableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["asset-statuses"] = ReferenceTable.AssetStatus,
        ["property-types"] = ReferenceTable.PropertyType,
        ["portfolio-types"] = ReferenceTable.PortfolioType,
        ["contact-roles"] = ReferenceTable.ContactRole,
        ["file-types"] = ReferenceTable.FileType,
        ["geographic-areas"] = ReferenceTable.GeographicArea,
    };

    public sealed record ReferenceRowDto(string? Code, string? Name, bool? IsActive, int? SortOrder, string? Color, string? Category);

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/reference");

        group.MapGet("/{table}", async (string table, IReferenceAdminStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(Resolve(table), ct)));

        group.MapPost("/{table}", async (string table, ReferenceRowDto dto, ReferenceAdminService service, CancellationToken ct) =>
            Results.Ok(new { id = await service.CreateAsync(Resolve(table), ToRow(0, dto), ct) }));

        group.MapPut("/{table}/{id:int}", async (string table, int id, ReferenceRowDto dto, ReferenceAdminService service, CancellationToken ct) =>
        {
            await service.UpdateAsync(Resolve(table), ToRow(id, dto), ct);
            return Results.NoContent();
        });
    }

    private static ReferenceTable Resolve(string table)
        => TableNames.TryGetValue(table, out var t)
            ? t
            : throw new DomainValidationException("REFERENCE_TABLE_UNKNOWN", $"Unknown list '{table}'. Use one of: {string.Join(", ", TableNames.Keys)}.");

    private static ReferenceRow ToRow(int id, ReferenceRowDto dto)
        => new(id, dto.Code ?? "", dto.Name ?? "", dto.IsActive ?? true, dto.SortOrder ?? 0, dto.Color, dto.Category);
}
