namespace Nadlan.Core.Contacts;

/// <summary>A business person or organisation (agent, engineer, lawyer, legal owner, ...). Needs no login.</summary>
public sealed record Contact
{
    public long ContactId { get; init; }
    public string ContactType { get; init; } = ContactTypes.Person;
    public string DisplayName { get; init; } = "";
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? CompanyName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? CellPhone { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; } = true;
    public IReadOnlyList<int> RoleIds { get; init; } = Array.Empty<int>();
}

public static class ContactTypes
{
    public const string Person = "Person";
    public const string Organization = "Organization";
}

/// <summary>What selectors and summaries show; never the full record.</summary>
public sealed record ContactSummary(long ContactId, string DisplayName, string? Email, string? Phone, bool IsActive);

public interface IContactStore
{
    Task<Contact?> GetAsync(long contactId, CancellationToken ct = default);
    Task<IReadOnlyList<ContactSummary>> SearchAsync(string? text, int? roleId, int limit, CancellationToken ct = default);
    Task<long> InsertAsync(Contact contact, CancellationToken ct = default);
    Task UpdateAsync(Contact contact, CancellationToken ct = default);
}
