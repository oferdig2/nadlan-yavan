using Nadlan.Core.Files;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Errors;

/// <summary>Turns domain exceptions into the JSON error shape the browser expects: { error, message }.</summary>
public sealed class ApiErrorMiddleware
{
    private readonly RequestDelegate _next;

    public ApiErrorMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (DomainValidationException ex)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, ex.Code, ex.Message);
        }
        catch (EntityNotFoundException ex)
        {
            await WriteAsync(context, StatusCodes.Status404NotFound, ex.Code, ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, "BAD_REQUEST", ex.Message);
        }
        catch (StorageUnavailableException ex)
        {
            await WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "STORAGE_ERROR", ex.Message);
        }
    }

    private static Task WriteAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = code, message });
    }
}
