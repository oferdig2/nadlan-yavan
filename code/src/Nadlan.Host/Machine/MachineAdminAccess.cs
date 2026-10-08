using Nadlan.Core.Security;
using Nadlan.Host.Auth;

namespace Nadlan.Host.Machine;

/// <summary>
/// Who may use the server pages (workload and restarts, web files and hot patches, app_config): the server admins of
/// <see cref="MachineAdmins"/> only (fixed in code; their accounts can't be taken over by another Admin). Everyone else,
/// other Admins included, gets 404 - the pages don't exist for them.
/// </summary>
public static class MachineAdminAccess
{
    public static bool IsMachineAdmin(UserAccess? user) => MachineAdmins.Is(user);

    /// <summary>The route group for these pages. API tokens never get here (AuthRegistration blocks them on /api/admin).</summary>
    public static RouteGroupBuilder MapMachineGroup(this IEndpointRouteBuilder app) =>
        app.MapGroup("/api/admin/machine").AddEndpointFilter(async (context, next) =>
            IsMachineAdmin(context.HttpContext.GetUserAccess())
                ? await next(context)
                : Results.Json(new { error = "NOT_FOUND", message = "Not found." }, statusCode: StatusCodes.Status404NotFound));
}
