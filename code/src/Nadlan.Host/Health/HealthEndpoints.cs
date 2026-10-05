using System.Reflection;
using MySqlConnector;
using Nadlan.Persistence.MySql;

namespace Nadlan.Host.Health;

public static class HealthEndpoints
{
    // The release id the server package was built with (code/release passes it as InformationalVersion); "dev" locally.
    private static readonly string Version =
        typeof(HealthEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    /// <summary>
    /// For the deploy script and uptime monitors: 200 with the running release when the database answers, 503 when it
    /// doesn't. No sign-in, and nothing in it but the release id.
    /// </summary>
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", async (MySqlDatabase db, CancellationToken ct) =>
        {
            try
            {
                await using var conn = await db.OpenAsync(ct);
                await using var cmd = new MySqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync(ct);
                return Results.Ok(new { status = "ok", version = Version });
            }
            catch (MySqlException)
            {
                return Results.Json(new { status = "database-unavailable", version = Version }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).AllowAnonymous();
    }
}
