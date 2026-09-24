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
