namespace Nadlan.Core.Security;

/// <summary>
/// The server admins (owner ruling 2026-10-07: Ofer and Alon only): Admins with one of these emails may use the server
/// pages - settings with their secrets, files every browser runs, restarts. Fixed in code on purpose: nothing in the
/// database or app_config can extend it. Their accounts are protected too (<see cref="UserAdminService"/>): another Admin
/// can't set their password, change their email or role, mint their tokens or links, and can't take one of these emails -
/// otherwise any Admin could make themselves a server admin.
/// </summary>
public static class MachineAdmins
{
    private static readonly HashSet<string> Emails = new(StringComparer.OrdinalIgnoreCase)
    {
        "oferdig2@gmail.com",
        "alon.schwarz@gmail.com",
    };

    public static bool IsListedEmail(string? email) => email is not null && Emails.Contains(email.Trim());

    public static bool Is(UserAccess? user) => user is { IsAdmin: true } && IsListedEmail(user.Email);
}
