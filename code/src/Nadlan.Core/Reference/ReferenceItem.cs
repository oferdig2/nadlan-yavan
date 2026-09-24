namespace Nadlan.Core.Reference;

/// <summary>A row of a small Admin-maintained lookup table (status, property type, ...).</summary>
public sealed record ReferenceItem(int Id, string Code, string Name, bool IsActive, int SortOrder, string? Color = null);

public enum ReferenceList
{
    AssetStatus,
    PropertyType,
    PortfolioType,
    ContactRole,
}

public interface IReferenceDataStore
{
    Task<IReadOnlyList<ReferenceItem>> ListAsync(ReferenceList list, CancellationToken ct = default);
}
