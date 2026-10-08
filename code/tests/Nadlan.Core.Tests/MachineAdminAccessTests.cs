using Nadlan.Core.Security;
using Nadlan.Host.Machine;

namespace Nadlan.Core.Tests;

/// <summary>
/// Owner's ruling (2026-10-07): the Server, Web files, Downloads and Settings tabs are for Ofer and Alon only - no Data person,
/// no other Admin, no role with every permission. Being Admin and being on the list are both required.
/// </summary>
public class MachineAdminAccessTests
{
    private static UserAccess User(string email, string role, params string[] permissions)
        => new() { UserId = 1, Email = email, RoleCode = role, Permissions = new HashSet<string>(permissions) };

    private static readonly string[] Everything = typeof(Permissions).GetFields()
        .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToArray();

    [Theory]
    [InlineData("oferdig2@gmail.com")]
    [InlineData("alon.schwarz@gmail.com")]
    [InlineData(" OFERDIG2@gmail.com ")]
    public void The_two_admins_on_the_list_get_in(string email) => Assert.True(MachineAdminAccess.IsMachineAdmin(User(email, SecurityRoles.Admin)));

    [Fact]
    public void Another_admin_does_not() => Assert.False(MachineAdminAccess.IsMachineAdmin(User("other.admin@example.com", SecurityRoles.Admin)));

    [Theory]
    [InlineData("DATA_PERSON")]
    [InlineData("SALES")]
    [InlineData("VIEWER")]
    public void A_listed_email_without_the_Admin_role_does_not(string role)
        // e.g. if Alon's account were moved to Data person: every permission in the book still doesn't open these pages
        => Assert.False(MachineAdminAccess.IsMachineAdmin(User("alon.schwarz@gmail.com", role, Everything)));

    [Fact]
    public void Nobody_signed_in_does_not() => Assert.False(MachineAdminAccess.IsMachineAdmin(null));
}
