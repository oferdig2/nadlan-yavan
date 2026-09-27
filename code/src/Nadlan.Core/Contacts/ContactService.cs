using Nadlan.Core.Activity;
using Nadlan.Core.Reference;
using Nadlan.Core.Text;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Contacts;

public sealed class ContactService
{
    private readonly IContactStore _contacts;
    private readonly IActivityLog _activity;
    private readonly IReferenceDataStore? _reference;

    public ContactService(IContactStore contacts, IActivityLog? activity = null, IReferenceDataStore? reference = null)
    {
        _contacts = contacts;
        _activity = activity ?? NullActivityLog.Instance;
        _reference = reference;
    }

    /// <summary>Unknown role ids are rejected (not a DB error); inactive roles can't be newly given, kept ones stay.</summary>
    private async Task EnsureRolesAsync(IReadOnlyList<int> roleIds, IReadOnlyList<int> alreadyHas, CancellationToken ct)
    {
        if (_reference is null || roleIds.Count == 0)
        {
            return;
        }

        var roles = await _reference.ListAsync(ReferenceList.ContactRole, ct);
        var bad = roleIds.Where(id => !roles.Any(r => r.Id == id && (r.IsActive || alreadyHas.Contains(id)))).ToList();
        if (bad.Count > 0)
        {
            throw new DomainValidationException("CONTACT_ROLE_INVALID", $"Unknown or inactive contact role(s): {string.Join(", ", bad)}.");
        }
    }

    public async Task<Contact> CreateAsync(Contact input, CancellationToken ct = default)
    {
        var contact = Normalize(input);
        await EnsureRolesAsync(contact.RoleIds, Array.Empty<int>(), ct);
        var id = await _contacts.InsertAsync(contact, ct);
        await _activity.RecordAsync(new ActivityEntry("Contact", id, ActivityActions.ContactCreated, $"Contact {contact.DisplayName} created."), ct);
        return contact with { ContactId = id };
    }

    public async Task<Contact> UpdateAsync(Contact input, CancellationToken ct = default)
    {
        var existing = await _contacts.GetAsync(input.ContactId, ct) ?? throw new EntityNotFoundException("Contact", input.ContactId);
        var contact = Normalize(input);
        await EnsureRolesAsync(contact.RoleIds, existing.RoleIds, ct);
        await _contacts.UpdateAsync(contact, ct);

        var summary = existing.IsActive == contact.IsActive
            ? $"Contact {contact.DisplayName} edited."
            : $"Contact {contact.DisplayName} {(contact.IsActive ? "reactivated" : "deactivated")}.";
        await _activity.RecordAsync(new ActivityEntry("Contact", contact.ContactId, ActivityActions.ContactEdited, summary), ct);
        return contact;
    }

    /// <summary>Trims fields and derives DisplayName from the name/company when it is left empty.</summary>
    private static Contact Normalize(Contact c)
    {
        var first = TextNormalize.NullIfBlank(c.FirstName);
        var last = TextNormalize.NullIfBlank(c.LastName);
        var company = TextNormalize.NullIfBlank(c.CompanyName);
        var display = TextNormalize.NullIfBlank(c.DisplayName)
                      ?? TextNormalize.NullIfBlank(string.Join(' ', new[] { first, last }.Where(s => s is not null)))
                      ?? company
                      ?? throw new DomainValidationException("CONTACT_NAME_REQUIRED", "Enter a name or company for the contact.");

        var type = string.Equals(c.ContactType, ContactTypes.Organization, StringComparison.OrdinalIgnoreCase)
            ? ContactTypes.Organization : ContactTypes.Person;
        var email = TextNormalize.NullIfBlank(c.Email);
        if (email is not null && !email.Contains('@'))
        {
            throw new DomainValidationException("CONTACT_EMAIL_INVALID", "The email address does not look valid.");
        }

        return c with
        {
            ContactType = type,
            DisplayName = display,
            FirstName = first,
            LastName = last,
            CompanyName = company,
            Email = email,
            Phone = TextNormalize.NullIfBlank(c.Phone),
            CellPhone = TextNormalize.NullIfBlank(c.CellPhone),
            Notes = TextNormalize.NullIfBlank(c.Notes),
            RoleIds = c.RoleIds.Distinct().ToList(),
        };
    }
}
