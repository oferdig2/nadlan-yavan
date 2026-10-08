using Nadlan.Core.Security;
using Nadlan.Host.Auth;

namespace Nadlan.Host.Machine;

/// <summary>
/// Who may use the server pages (workload and restarts, web files and hot patches, app_config): Admins on this fixed list
/// only. It is in the code on purpose: nothing in the database or app_config (which these pages edit) can extend it.
/// Everyone else, other Admins included, gets 404 - the pages don't exist for them.
/// </summary>
public static class MachineAdminAccess
{
    private static readonly HashSet<string> Emails = new(StringComparer.OrdinalIgnoreCase)
    {
        "oferdig2@gmail.com",
        "alon.schwarz@gmail.com",
    };

    public static bool IsMachineAdmin(UserAccess? user) => user is { IsAdmin: true } && Emails.Contains(user.Email.Trim());

    /// <summary>The route group for these pages. API tokens never get here (AuthRegistration blocks them on /api/admin).</summary>
    public static RouteGroupBuilder MapMachineGroup(this IEndpointRouteBuilder app) =>
        app.MapGroup("/api/admin/machine").AddEndpointFilter(async (context, next) =>
            IsMachineAdmin(context.HttpContext.GetUserAccess())
                ? await next(context)
                : Results.Json(new { error = "NOT_FOUND", message = "Not found." }, statusCode: StatusCodes.Status404NotFound));
}
