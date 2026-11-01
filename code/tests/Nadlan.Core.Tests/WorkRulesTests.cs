using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Core.Work;

namespace Nadlan.Core.Tests;

/// <summary>The work requests' rules without a database: statuses, the Parcel's own status, the link, passwords (N5).</summary>
public class WorkRulesTests
{
    private const string R = WorkStatuses.Requested, O = WorkStatuses.Offered, A = WorkStatuses.Approved, I = WorkStatuses.InWork,
        D = WorkStatuses.Done, P = WorkStatuses.Paid;

    // ---- moves --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(R, O, WorkActor.Contractor, WorkStamps.Price | WorkStamps.Offered)]
    [InlineData(O, O, WorkActor.Contractor, WorkStamps.Price | WorkStamps.Offered)] // a changed offer, while not approved
    [InlineData(O, A, WorkActor.Staff, WorkStamps.Approved)]
    [InlineData(A, I, WorkActor.Contractor, WorkStamps.Started)]
    [InlineData(I, D, WorkActor.Contractor, WorkStamps.Done)]
    [InlineData(D, P, WorkActor.Staff, WorkStamps.Paid)]
    public void Every_allowed_move_and_what_it_stamps(string from, string to, WorkActor actor, WorkStamps sets)
    {
        var step = WorkTransitions.Move(from, to, actor);

        Assert.Equal(sets, step.Sets);
        Assert.Equal(WorkStamps.None, step.Clears);
        Assert.False(step.IsCorrection);
    }

    [Theory]
    [InlineData(O, A)] // approve
    [InlineData(D, P)] // mark paid
    public void A_contractor_can_never_approve_or_mark_paid(string from, string to)
    {
        var ex = Assert.Throws<WorkConflictException>(() => WorkTransitions.Move(from, to, WorkActor.Contractor));
        Assert.Equal("WORK_STEP_NOT_YOURS", ex.Code);
    }

    [Theory]
    [InlineData(A)]
    [InlineData(I)]
    [InlineData(D)]
    [InlineData(P)]
    public void A_contractor_can_not_change_an_approved_price(string from)
    {
        var ex = Assert.Throws<WorkConflictException>(() => WorkTransitions.Move(from, O, WorkActor.Contractor));
        Assert.Equal("WORK_STEP_NOT_ALLOWED", ex.Code);
    }

    [Theory]
    [InlineData(R, O)] // staff don't offer for the contractor
    [InlineData(A, I)] // nor start
    [InlineData(I, D)] // nor set done (a wrong step is undone with Correct)
    public void Staff_do_not_take_the_contractors_steps(string from, string to)
    {
        var ex = Assert.Throws<WorkConflictException>(() => WorkTransitions.Move(from, to, WorkActor.Staff));
        Assert.Equal("WORK_STEP_NOT_YOURS", ex.Code);
    }

    [Theory]
    [InlineData(R, A)] // approve without an offer
    [InlineData(R, I)]
    [InlineData(O, I)] // start before approval
    [InlineData(A, D)] // done without starting
    [InlineData(I, P)] // paid before done
    [InlineData(P, P)]
    [InlineData(D, I)] // backwards only through Correct
    [InlineData(A, O)]
    public void Every_other_move_is_refused_for_both(string from, string to)
    {
        Assert.Throws<WorkConflictException>(() => WorkTransitions.Move(from, to, WorkActor.Contractor));
        Assert.Throws<WorkConflictException>(() => WorkTransitions.Move(from, to, WorkActor.Staff));
    }

    [Theory]
    [InlineData(O, R, WorkStamps.Price | WorkStamps.Offered)] // the contractor offers again
    [InlineData(A, O, WorkStamps.Approved)]                   // "reopen the price": the price stays
    [InlineData(I, A, WorkStamps.Started)]
    [InlineData(D, I, WorkStamps.Done)]
    [InlineData(P, D, WorkStamps.Paid)]
    public void A_correction_goes_one_step_back_and_clears_that_steps_stamps(string from, string to, WorkStamps clears)
    {
        var step = WorkTransitions.Correct(from, "Typed by mistake");

        Assert.Equal(to, step.To);
        Assert.Equal(clears, step.Clears);
        Assert.Equal(WorkStamps.None, step.Sets);
        Assert.True(step.IsCorrection);
        Assert.Equal(WorkActor.Staff, step.Actor);
    }

    [Fact]
    public void A_correction_needs_a_reason_and_something_to_undo()
    {
        Assert.Equal("WORK_REASON_REQUIRED", Assert.Throws<WorkConflictException>(() => WorkTransitions.Correct(A, "  ")).Code);
        Assert.Equal("WORK_REASON_REQUIRED", Assert.Throws<WorkConflictException>(() => WorkTransitions.Correct(A, null)).Code);
        Assert.Equal("WORK_NOTHING_TO_CORRECT", Assert.Throws<WorkConflictException>(() => WorkTransitions.Correct(R, "why")).Code);
    }

    // ---- the Parcel's own status --------------------------------------------------------------------------------

    [Theory]
    [InlineData(ParcelWorkStatuses.OpenOffers)]               // no works
    [InlineData(ParcelWorkStatuses.OpenOffers, R)]
    [InlineData(ParcelWorkStatuses.OpenOffers, R, O, O)]      // offers, none approved
    [InlineData(ParcelWorkStatuses.Approved, A)]
    [InlineData(ParcelWorkStatuses.Approved, A, O, R)]        // the others are ignored once one is approved
    [InlineData(ParcelWorkStatuses.Approved, A, A)]
    [InlineData(ParcelWorkStatuses.InWork, I)]
    [InlineData(ParcelWorkStatuses.InWork, I, A)]
    [InlineData(ParcelWorkStatuses.InWork, D, A)]             // one done, one approved not started: not all done
    [InlineData(ParcelWorkStatuses.InWork, P, A)]             // the truck paid, the burning not started
    [InlineData(ParcelWorkStatuses.InWork, D, I, O)]
    [InlineData(ParcelWorkStatuses.Done, D)]
    [InlineData(ParcelWorkStatuses.Done, D, P)]               // all done or paid, not all paid
    [InlineData(ParcelWorkStatuses.Done, D, D, O, R)]         // never-approved works ignored
    [InlineData(ParcelWorkStatuses.Paid, P)]
    [InlineData(ParcelWorkStatuses.Paid, P, P, O)]
    public void The_parcel_status_follows_its_approved_works(string expected, params string[] works)
        => Assert.Equal(expected, ParcelWorkStatuses.From(works));

    // ---- the link -----------------------------------------------------------------------------------------------

    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_link_state_disabled_wins_then_expired_then_locked()
    {
        Assert.Equal(WorkLinkStates.Active, WorkLinkStates.Of(true, Now.AddDays(1), null, Now));
        Assert.Equal(WorkLinkStates.Active, WorkLinkStates.Of(true, Now.AddDays(1), Now.AddMinutes(-1), Now)); // the lock is over
        Assert.Equal(WorkLinkStates.Locked, WorkLinkStates.Of(true, Now.AddDays(1), Now.AddMinutes(5), Now));
        Assert.Equal(WorkLinkStates.Expired, WorkLinkStates.Of(true, Now, Now.AddMinutes(5), Now));
        Assert.Equal(WorkLinkStates.Disabled, WorkLinkStates.Of(false, Now.AddDays(-1), Now.AddMinutes(5), Now));
    }

    [Fact]
    public void A_link_token_is_long_url_safe_and_stored_only_as_its_hash()
    {
        var (token, hash) = PasswordHasher.NewToken();

        Assert.Matches("^[A-Za-z0-9_-]{43}$", token); // 32 random bytes, base64url
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, PasswordHasher.HashToken(token));
        Assert.NotEqual(hash, PasswordHasher.NewToken().TokenHash);
    }

    [Fact]
    public void A_generated_password_is_six_digits()
    {
        for (var i = 0; i < 200; i++)
        {
            Assert.Matches("^[0-9]{6}$", WorkService.GeneratePassword());
        }
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("   ")]
    public void A_typed_password_needs_four_characters(string password)
        => Assert.Equal("WORK_PASSWORD_LENGTH", Assert.Throws<DomainValidationException>(() => WorkService.CleanPassword(password)).Code);

    [Fact]
    public void A_request_needs_a_name_of_at_most_200_characters()
    {
        Assert.Equal("Skroponeria", WorkService.CleanName("  Skroponeria "));
        Assert.Equal("WORK_NAME_REQUIRED", Assert.Throws<DomainValidationException>(() => WorkService.CleanName(" ")).Code);
        Assert.Equal("WORK_NAME_TOO_LONG", Assert.Throws<DomainValidationException>(() => WorkService.CleanName(new string('x', 201))).Code);
    }

    [Fact]
    public void Without_MANAGE_WORK_nothing_is_allowed()
    {
        var sales = new UserAccess { UserId = 1, RoleCode = "SALES", Permissions = new HashSet<string> { Permissions.UseMap, Permissions.ViewAllParcels } };
        var dataPerson = new UserAccess { UserId = 2, RoleCode = "DATA_PERSON", Permissions = new HashSet<string> { Permissions.ManageWork } };
        var admin = new UserAccess { UserId = 3, RoleCode = SecurityRoles.Admin };

        Assert.Equal("WORK_FORBIDDEN", Assert.Throws<ForbiddenException>(() => WorkService.Require(sales)).Code);
        WorkService.Require(dataPerson);
        WorkService.Require(admin);
    }
}
