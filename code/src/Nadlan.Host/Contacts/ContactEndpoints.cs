using Nadlan.Core.Contacts;

namespace Nadlan.Host.Contacts;

/// <summary>TODO(auth slice): open for now; MANAGE_CONTACTS gates writes.</summary>
public static class ContactEndpoints
{
    public sealed record ContactDto(
        string? ContactType, string? DisplayName, string? FirstName, string? LastName, string? CompanyName,
        string? Email, string? Phone, string? CellPhone, string? Notes, bool? IsActive, int[]? RoleIds);

    public static void MapContactEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contacts");

        // Autocomplete for EntitySelector: short, limited result set.
        group.MapGet("/", async (string? q, int? roleId, int? limit, IContactStore contacts, CancellationToken ct) =>
            Results.Ok(await contacts.SearchAsync(q, roleId, Math.Clamp(limit ?? 20, 1, 50), ct)));

        group.MapGet("/{contactId:long}", async (long contactId, IContactStore contacts, CancellationToken ct) =>
            await contacts.GetAsync(contactId, ct) is { } c
                ? Results.Ok(c)
                : Results.NotFound(new { error = "CONTACT_NOT_FOUND", message = $"Contact {contactId} was not found." }));

        group.MapPost("/", async (ContactDto dto, ContactService service, CancellationToken ct) =>
            Results.Ok(await service.CreateAsync(ToContact(dto, 0), ct)));

        group.MapPut("/{contactId:long}", async (long contactId, ContactDto dto, ContactService service, IContactStore contacts, CancellationToken ct) =>
        {
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
