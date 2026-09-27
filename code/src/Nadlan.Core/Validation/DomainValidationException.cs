namespace Nadlan.Core.Validation;

/// <summary>A business rule was violated. The API returns it as 400 { error = Code, message = Message }.</summary>
public sealed class DomainValidationException : Exception
{
    public string Code { get; }

    public DomainValidationException(string code, string message) : base(message)
    {
        Code = code;
    }
}

/// <summary>A unique key (e.g. Country + KAEK) was already taken when the row was written - usually a race.</summary>
public sealed class DuplicateKeyException : Exception
{
    public DuplicateKeyException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>The target entity does not exist. The API returns it as 404.</summary>
public sealed class EntityNotFoundException : Exception
{
    public string Code { get; }

    public EntityNotFoundException(string entity, object id) : base($"{entity} {id} was not found.")
    {
        Code = $"{entity.ToUpperInvariant()}_NOT_FOUND";
    }
}

/// <summary>
/// The database could not compute overlaps for a polygon (a GIS edge case, e.g. neighbours touching along a border).
/// Not the user's fault and not a reason to lose the save: the Parcel is saved and the user is warned.
/// </summary>
public sealed class OverlapCheckFailedException : Exception
{
    public OverlapCheckFailedException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// The user may see the entity but not do this with it. The API returns it as 403.
/// (An entity the user may not even see is reported as <see cref="EntityNotFoundException"/>, so it can't be discovered.)
/// </summary>
public sealed class ForbiddenException : Exception
{
    public string Code { get; }

    public ForbiddenException(string code, string message) : base(message)
    {
        Code = code;
    }
}
