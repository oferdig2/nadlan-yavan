using Nadlan.Core.Security;

namespace Nadlan.Host.Auth;

/// <summary>
/// "Connect to Nadlan" from the KAEK importer: the importer opens connect-importer.html in its own browser, the user signs
/// in and clicks Connect, and this issues a token for that user. The importer reads it from the page (it drives that
/// tab), so nobody copies or pastes a token.
/// </summary>
public static class ImporterEndpoints
{
    public sealed record ConnectDto(string? Device);

    public static void MapImporterEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/importer/token", async (ConnectDto dto, UserAccess me, HttpContext http, UserAdminService service, CancellationToken ct) =>
        {
            // Only a person signed in with the cookie; a token must not be able to mint more tokens.
            if (http.User.Identity?.AuthenticationType != AuthSchemes.Cookie)
            {
                return Results.Json(new { error = "IMPORTER_COOKIE_ONLY", message = "Sign in to GreekPlot in the browser to connect the importer." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var device = new string((dto.Device ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').Take(40).ToArray());
            var token = await service.CreateOwnApiTokenAsync(me, "KAEK importer" + (device.Length > 0 ? " - " + device : ""), ct);
            return Results.Ok(new { token, me.DisplayName });
        });
    }
}
