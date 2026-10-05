using MySqlConnector;
using Nadlan.Core.Files;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Errors;

/// <summary>
/// Turns exceptions into the JSON error shape the browser expects: { error, message }.
/// Anything unexpected is logged and returned as a JSON 500 - never an HTML page or an empty body.
/// </summary>
public sealed class ApiErrorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiErrorMiddleware> _log;

    public ApiErrorMiddleware(RequestDelegate next, ILogger<ApiErrorMiddleware> log)
    {
        _next = next;
        _log = log;
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
        catch (ForbiddenException ex)
        {
            await WriteAsync(context, StatusCodes.Status403Forbidden, ex.Code, ex.Message);
        }
        catch (EditConflictException ex)
        {
            await WriteAsync(context, StatusCodes.Status409Conflict, ex.Code, ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            // Includes what the endpoint couldn't bind (RouteHandlerOptions.ThrowOnBadRequest): malformed JSON, "abc" for a number.
            var message = ex.InnerException is System.Text.Json.JsonException
                ? "The request could not be read: it is not valid JSON, or a value has the wrong type."
                : ex.Message;
            await WriteAsync(context, ex.StatusCode, ex.StatusCode == StatusCodes.Status413PayloadTooLarge ? "PAYLOAD_TOO_LARGE" : "BAD_REQUEST", message);
        }
        catch (StorageUnavailableException ex)
        {
            await WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "STORAGE_ERROR", ex.Message);
        }
        catch (MySqlException ex) when (ex.ErrorCode is MySqlErrorCode.DataTooLong or MySqlErrorCode.WarningDataOutOfRange)
        {
            // Strict-mode column limits (e.g. caption VARCHAR(300)): a user input problem, not a server crash.
            await WriteAsync(context, StatusCodes.Status400BadRequest, "VALUE_TOO_LONG",
                "A value is too long or too large for its field. Shorten it and try again.");
        }
        catch (MySqlException ex) when (ex.ErrorCode is MySqlErrorCode.NoReferencedRow or MySqlErrorCode.NoReferencedRow2)
        {
            // A referenced row doesn't exist (stale id from another tab, deleted meanwhile).
            await WriteAsync(context, StatusCodes.Status400BadRequest, "REFERENCE_NOT_FOUND",
                "Something this refers to no longer exists. Reload and try again.");
        }
        catch (MySqlException ex) when (ex.ErrorCode is MySqlErrorCode.RowIsReferenced or MySqlErrorCode.RowIsReferenced2)
        {
            // Deleting something another row was just linked to (a race the services check for, caught by the database).
            await WriteAsync(context, StatusCodes.Status409Conflict, "IN_USE",
                "Something else was just linked to this, so it can't be removed. Reload and try again.");
        }
        catch (Exception ex) when (!context.Response.HasStarted && ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Unhandled error on {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteAsync(context, StatusCodes.Status500InternalServerError, "SERVER_ERROR",
                "Something went wrong on the server. It has been logged.");
        }
    }

    private static Task WriteAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = code, message });
    }
}
