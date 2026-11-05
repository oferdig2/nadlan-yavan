using Dapper;
using Nadlan.Core.Presentation;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Persistence.MySql.Assets;
using Nadlan.Persistence.MySql.Portfolios;
using Nadlan.Persistence.MySql.Presentation;
using Nadlan.Persistence.MySql.Security;

namespace Nadlan.Core.Tests;

/// <summary>Public presentation links (migration 025, owner 2026-11-05): no sign-in, optional password, 1 week by default.</summary>
public partial class WorkMySqlTests
{
    private sealed class PlainSealer : IPresentationLinkSealer
    {
        public string Seal(string token) => "sealed:" + new string(token.Reverse().ToArray());

        public string Unseal(string sealedToken) => new(sealedToken["sealed:".Length..].Reverse().ToArray());
    }

    private (PresentationLinkService Links, MySqlPresentationLinkStore Store, FixedClock Clock) MakeLinks()
    {
        var store = new MySqlPresentationLinkStore(_db!);
        var clock = new FixedClock(DateTime.UtcNow);
        var service = new PresentationLinkService(store, new MySqlPortfolioStore(_db!), new AccessPolicy(new MySqlAccessStore(_db!), new MySqlAssetStore(_db!)),
            new PlainSealer(), clock);
        return (service, store, clock);
    }

    [Fact]
    public async Task A_presentation_link_lasts_a_week_keeps_only_a_hash_and_is_for_Admin_and_Data_person()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (links, store, clock) = MakeLinks();

        var (link, token) = await links.CreateAsync(w.DataPerson with { Permissions = new HashSet<string>(w.DataPerson.Permissions) { Permissions.SharePresentation } },
            null, new long[] { 9, 5, 9, 3 }, "Sea-view plots", null);
        Assert.Equal("9,5,3", link.AssetIds); // order kept, repeats dropped
        Assert.Equal(new long[] { 9, 5, 3 }, link.AssetIdList);
        Assert.InRange(link.LinkExpiresUtc, clock.Now.AddDays(7).AddSeconds(-1), clock.Now.AddDays(7).AddSeconds(1));
        Assert.True(token.Length >= 40);
        await using (var c = await _db!.OpenAsync())
        {
            var row = await c.QuerySingleAsync<(string Hash, string Sealed)>(
                "SELECT link_token_hash, link_token_sealed FROM presentation_link WHERE presentation_link_id = @id", new { id = link.PresentationLinkId });
            Assert.DoesNotContain(token, row.Hash + row.Sealed);
        }

        Assert.Equal(token, await links.TokenAsync(w.Admin, link.PresentationLinkId));
        Assert.Equal(link.PresentationLinkId, (await links.ResolveAsync(token)).PresentationLinkId);

        // Without SHARE_PRESENTATION: no making, no managing.
        var sales = new UserAccess { UserId = w.Admin.UserId, RoleCode = "SALES", Permissions = new HashSet<string> { Permissions.ViewAllAssets } };
        await Assert.ThrowsAsync<ForbiddenException>(() => links.CreateAsync(sales, null, new long[] { 1 }, null, null));
        await Assert.ThrowsAsync<ForbiddenException>(() => links.TokenAsync(sales, link.PresentationLinkId));

        // Targets: a Portfolio or Assets, never both or nothing.
        await Assert.ThrowsAsync<DomainValidationException>(() => links.CreateAsync(w.Admin, null, Array.Empty<long>(), null, null));
        await Assert.ThrowsAsync<DomainValidationException>(() => links.CreateAsync(w.Admin, 1, new long[] { 1 }, null, null));
        await Assert.ThrowsAsync<DomainValidationException>(() => links.CreateAsync(w.Admin, null, Enumerable.Range(1, 101).Select(i => (long)i).ToArray(), null, null));
    }

    [Fact]
    public async Task A_presentation_link_expires_can_be_extended_up_to_a_year_disabled_and_replaced()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (links, _, clock) = MakeLinks();
        var (link, token) = await links.CreateAsync(w.Admin, null, new long[] { 1, 2 }, null, null);
        var id = link.PresentationLinkId;

        // Extend: a week more each time, never past a year.
        var e1 = await links.ExtendAsync(w.Admin, id);
        Assert.InRange(e1, clock.Now.AddDays(14).AddSeconds(-1), clock.Now.AddDays(14).AddSeconds(1));
        var lastDay = clock.Now.AddDays(300).ToString("yyyy-MM-dd");
        await links.SetLastDayAsync(w.Admin, id, lastDay);
        for (var i = 0; i < 12; i++) { await links.ExtendAsync(w.Admin, id); }
        Assert.True((await links.GetAsync(w.Admin, id)).LinkExpiresUtc <= clock.Now.AddDays(366).AddSeconds(1));
        await Assert.ThrowsAsync<DomainValidationException>(() => links.SetLastDayAsync(w.Admin, id, clock.Now.AddDays(400).ToString("yyyy-MM-dd")));
        await Assert.ThrowsAsync<DomainValidationException>(() => links.SetLastDayAsync(w.Admin, id, clock.Now.AddDays(-1).ToString("yyyy-MM-dd")));

        // Expired, disabled, unknown: the same refusal.
        await links.SetLastDayAsync(w.Admin, id, clock.Now.ToString("yyyy-MM-dd"));
        clock.Now = clock.Now.Date.AddDays(1).AddMinutes(1);
        Assert.Equal(PresentationLinkRefusal.Inactive, (await Assert.ThrowsAsync<PresentationLinkException>(() => links.ResolveAsync(token))).Refusal);
        await links.ExtendAsync(w.Admin, id);
        Assert.Equal(id, (await links.ResolveAsync(token)).PresentationLinkId);
        await links.SetEnabledAsync(w.Admin, id, false);
        Assert.Equal(PresentationLinkRefusal.Inactive, (await Assert.ThrowsAsync<PresentationLinkException>(() => links.ResolveAsync(token))).Refusal);
        await links.SetEnabledAsync(w.Admin, id, true);
        Assert.Equal(PresentationLinkRefusal.Inactive, (await Assert.ThrowsAsync<PresentationLinkException>(() => links.ResolveAsync("not-a-token"))).Refusal);

        // A new link: the old address stops at once.
        var fresh = await links.RenewAsync(w.Admin, id);
        await Assert.ThrowsAsync<PresentationLinkException>(() => links.ResolveAsync(token));
        Assert.Equal(id, (await links.ResolveAsync(fresh)).PresentationLinkId);
    }

    [Fact]
    public async Task A_presentation_link_password_locks_after_ten_wrong_tries_and_a_new_password_forgets_the_phones()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (links, _, clock) = MakeLinks();
        var (link, token) = await links.CreateAsync(w.Admin, null, new long[] { 1 }, null, null);
        var id = link.PresentationLinkId;
        Assert.True(links.MayEnter(link, null)); // no password: open

        var password = await links.SetPasswordAsync(w.Admin, id, null);
        Assert.Matches("^[0-9]{6}$", password);
        await Assert.ThrowsAsync<DomainValidationException>(() => links.SetPasswordAsync(w.Admin, id, "ab"));
        link = await links.ResolveAsync(token);
        Assert.False(links.MayEnter(link, null));
        Assert.Equal(PresentationLinkRefusal.PasswordNeeded,
            Assert.Throws<PresentationLinkException>(() => links.RequireEntry(link, null)).Refusal);

        var pass = await links.UnlockAsync(link, password);
        Assert.True(links.MayEnter(link, pass));
        Assert.True(pass.ExpiresUtc <= link.LinkExpiresUtc); // never outlives the link

        for (var i = 1; i < PresentationLinkService.MaxWrongPasswords; i++)
        {
            Assert.Equal(PresentationLinkRefusal.WrongPassword,
                (await Assert.ThrowsAsync<PresentationLinkException>(() => links.UnlockAsync(link, "999999x"))).Refusal);
        }

        Assert.Equal(PresentationLinkRefusal.Locked, (await Assert.ThrowsAsync<PresentationLinkException>(() => links.UnlockAsync(link, "999999x"))).Refusal);
        link = await links.ResolveAsync(token);
        Assert.Equal(PresentationLinkRefusal.Locked, (await Assert.ThrowsAsync<PresentationLinkException>(() => links.UnlockAsync(link, password))).Refusal);
        clock.Now = clock.Now.AddMinutes(16);
        Assert.True(links.MayEnter(link, await links.UnlockAsync(link, password)));

        // A new password: phones that gave the old one must give the new one.
        await links.SetPasswordAsync(w.Admin, id, "new-pass");
        link = await links.ResolveAsync(token);
        Assert.False(links.MayEnter(link, pass));
        await links.RemovePasswordAsync(w.Admin, id);
        Assert.True(links.MayEnter(await links.ResolveAsync(token), null));
    }
}
