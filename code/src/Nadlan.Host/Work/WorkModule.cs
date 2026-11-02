using Microsoft.AspNetCore.DataProtection;
using Nadlan.Core.Work;
using Nadlan.Persistence.MySql.Work;

namespace Nadlan.Host.Work;

/// <summary>
/// The work requests module (remarks N5 + N6): its own tables, endpoints, pages and services. The rest of the app knows
/// it only through <see cref="AddWorkModule"/> and <see cref="MapWorkEndpoints"/> - one line each.
/// </summary>
public static class WorkModule
{
    public static IServiceCollection AddWorkModule(this IServiceCollection services)
    {
        services.AddSingleton<IWorkStore, MySqlWorkStore>();
        services.AddSingleton<IWorkLinkSealer, DataProtectionWorkLinkSealer>();
        services.AddSingleton<WorkService>();
        services.AddSingleton<WorkLinkService>();
        services.AddSingleton<WorkMediaService>();
        services.AddSingleton<WorkTypeService>();
        services.AddHostedService<WorkSweeper>(); // daily: the 90-day track limit, abandoned picture uploads
        return services;
    }

    public static void MapWorkEndpoints(this IEndpointRouteBuilder app)
    {
        WorkEndpoints.Map(app);
        WorkLinkEndpoints.Map(app);
    }

    /// <summary>
    /// A move the status rules refuse (<see cref="WorkConflictException"/>) answers 409 { error, message } - caught here, so
    /// the shared error middleware stays as it is.
    /// </summary>
    internal static RouteGroupBuilder WithWorkConflicts(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (WorkConflictException ex)
            {
                return Results.Json(new { error = ex.Code, message = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });
        return group;
    }
}

/// <summary>The link token sealed with the app's data protection keys (kept in MySQL, so they survive restarts).</summary>
public sealed class DataProtectionWorkLinkSealer : IWorkLinkSealer
{
    private const string Purpose = "Nadlan.Work.LinkToken.v1";

    private readonly IDataProtector _protector;

    public DataProtectionWorkLinkSealer(IDataProtectionProvider protection)
    {
        _protector = protection.CreateProtector(Purpose);
    }

    public string Seal(string token) => _protector.Protect(token);

    public string Unseal(string sealedToken) => _protector.Unprotect(sealedToken);
}
