using Nadlan.Core.Editing;
using Nadlan.Core.Contacts;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Contacts;

/// <summary>Contacts: visible with VIEW_ALL_CONTACTS / MANAGE_CONTACTS, one's own Contact, or a grant. MANAGE_CONTACTS writes.</summary>
public static class ContactEndpoints
{
    public sealed record ContactDto(
        string? ContactType, string? DisplayName, string? FirstName, string? LastName, string? CompanyName,
        string? Email, string? Phone, string? CellPhone, string? Notes, bool? IsActive, int[]? RoleIds, string? Version = null);

    public static void MapContactEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contacts");

        // Autocomplete for EntitySelector: short, limited result set, only Contacts the caller may see.
        // includeInactive: the Admin Contacts list; pickers leave it off so inactive Contacts can't be chosen.
        group.MapGet("/", async (string? q, int? roleId, int? limit, bool? includeInactive, UserAccess me, IContactStore contacts, CancellationToken ct) =>
            Results.Ok(await contacts.SearchAsync(q, roleId, Math.Clamp(limit ?? 20, 1, 200), me.Scope, ct, includeInactive ?? false)));

        group.MapGet("/{contactId:long}", async (long contactId, UserAccess me, AccessPolicy policy, IContactStore contacts,
            IEditVersionStore versions, CancellationToken ct) =>
        {
            var rights = await policy.RequireContactViewAsync(me, contactId, ct);
            var version = await versions.GetAsync(EditTargets.Contact, contactId, ct); // before the data: a save in between = a 409, never a stale form
            var contact = await contacts.GetAsync(contactId, ct) ?? throw new EntityNotFoundException("Contact", contactId);
            return Results.Ok(new
            {
                contact.ContactId, contact.ContactType, contact.DisplayName, contact.FirstName, contact.LastName, contact.CompanyName,
                contact.Email, contact.Phone, contact.CellPhone, contact.Notes, contact.IsActive, contact.RoleIds,
                version, // sent back on save (edit check)
                rights = new { rights.CanEdit },
            });
        });

        group.MapPost("/", async (ContactDto dto, UserAccess me, ContactService service, CancellationToken ct) =>
        {
            if (!me.Has(Permissions.ManageContacts))
            {
                throw new ForbiddenException("CONTACT_CREATE_FORBIDDEN", "You may not create Contacts.");
            }

            return Results.Ok(await service.CreateAsync(ToContact(dto, 0), ct));
        });

        group.MapPut("/{contactId:long}", async (long contactId, ContactDto dto, UserAccess me, AccessPolicy policy,
            ContactService service, IContactStore contacts, IEditVersionStore versions, CancellationToken ct) =>
        {
            if (!(await policy.RequireContactViewAsync(me, contactId, ct)).CanEdit)
            {
                throw new ForbiddenException("CONTACT_EDIT_FORBIDDEN", "You may view this Contact but not change it.");
            }

            await using var edit = await versions.BeginEditAsync(EditTargets.Contact, contactId, dto.Version, ct);

            // A client that doesn't send contactType must not turn an Organization into a Person.
            var type = dto.ContactType ?? (await contacts.GetAsync(contactId, ct))?.ContactType;
            return Results.Ok(await service.UpdateAsync(ToContact(dto with { ContactType = type }, contactId), ct));
        });
    }

    private static Contact ToContact(ContactDto dto, long contactId) => new()
    {
        ContactId = contactId,
        ContactType = dto.ContactType ?? ContactTypes.Person,
        DisplayName = dto.DisplayName ?? "",
        FirstName = dto.FirstName,
        LastName = dto.LastName,
        CompanyName = dto.CompanyName,
        Email = dto.Email,
        Phone = dto.Phone,
        CellPhone = dto.CellPhone,
        Notes = dto.Notes,
        IsActive = dto.IsActive ?? true,
        RoleIds = dto.RoleIds ?? Array.Empty<int>(),
    };
}
