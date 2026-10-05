namespace Nadlan.Core.Editing;

/// <summary>The objects edited in forms, whose saves are checked against the version the form was opened with.</summary>
public static class EditTargets
{
    public const string Asset = "Asset";
    public const string Contact = "Contact";
    public const string Portfolio = "Portfolio";
    public const string Parcel = "Parcel";
}

/// <summary>
/// Optimistic edit check. A form gets the object's version with the object and sends it back on save; the save first
/// takes this object's edit lock (all app instances), compares the version and only then writes, so a stale tab can't
/// silently overwrite a newer save (e.g. put a "Sold" Asset back to "Available").
/// </summary>
public interface IEditVersionStore
{
    /// <summary>The object's current version (an opaque string), or null if it doesn't exist.</summary>
    Task<string?> GetAsync(string target, long id, CancellationToken ct = default);

    /// <summary>
    /// Locks edits of this object until disposed (dispose after the write). Throws <see cref="Validation.EditConflictException"/>
    /// when <paramref name="expectedVersion"/> is given and the object has changed since. Null = no check (API clients).
    /// </summary>
    Task<IAsyncDisposable> BeginEditAsync(string target, long id, string? expectedVersion, CancellationToken ct = default);
}
