using Nadlan.Core.Text;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Contacts;

public sealed class ContactService
{
    private readonly IContactStore _contacts;

    public ContactService(IContactStore contacts)
    {
        _contacts = contacts;
    }

    public async Task<Contact> CreateAsync(Contact input, CancellationToken ct = default)
    {
        var contact = Normalize(input);
        var id = await _contacts.InsertAsync(contact, ct);
        return contact with { ContactId = id };
    }

    public async Task<Contact> UpdateAsync(Contact input, CancellationToken ct = default)
    {
        _ = await _contacts.GetAsync(input.ContactId, ct) ?? throw new EntityNotFoundException("Contact", input.ContactId);
        var contact = Normalize(input);
        await _contacts.UpdateAsync(contact, ct);
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

        var type = c.ContactType == ContactTypes.Organization ? ContactTypes.Organization : ContactTypes.Person;
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
