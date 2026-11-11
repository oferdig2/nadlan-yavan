using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Nadlan.Core.Presentation;

namespace Nadlan.Core.Tests;

/// <summary>
/// The analytics page (migration 027): Admin only; soft-deleted links keep their visits; the customer of a link is an
/// Admin-only field that never reaches anyone else. The whole app in-process on a throwaway schema (NADLAN_TEST_MYSQL).
/// </summary>
public partial class AnalyticsEndpointTests
{
    private const string Boxford = "2.125.160.216"; // in MaxMind's City test database (GB)

    private async Task<long> ContactAsync(string name, string? email = null)
    {
        await using var c = await _f.Db!.OpenAsync();
        return await c.ExecuteScalarAsync<long>("INSERT INTO contact (display_name, email) VALUES (@name, @email); SELECT LAST_INSERT_ID();", new { name, email });
    }

    private async Task<(long Id, string Token)> TitledLinkAsync(string title)
    {
        var (link, token) = await _f.Services.GetRequiredService<PresentationLinkService>().CreateAsync(Admin(_f), null, _f.AssetIds, title, null);
        return (link.PresentationLinkId, token);
    }

    private async Task VisitAsync(string token, string ip, params object[] events)
        => Assert.Equal(200, (await SendAsync("POST", "/api/present-link/events", Batch(Id("s"), Id("v"), events), token, ip: ip)).Status);

    private static string AthensToday() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Nadlan.Core.Analytics.AnalyticsPageService.Athens).ToString("yyyy-MM-dd");

    // ---- who may use the page -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_endpoints_are_for_Admins_only_and_Data_person_keeps_the_Present_dialog_reports()
    {
        if (Off) { return; }

        var (link, token) = await TitledLinkAsync("Rights " + Id("t"));
        await VisitAsync(token, London, E("presentation_opened"));
        var chart = new { linkIds = new[] { link }, view = "metrics", split = "none" };
        var who = new { linkIds = new[] { link }, view = "metrics", split = "none", metric = "visits" };
        foreach (var email in new[] { _f.DataPersonEmail, _f.SalesEmail })
        {
            var cookie = await SignInAsync(email);
            Assert.Equal(403, (await SendAsync("GET", "/api/analytics/links/search", cookie: cookie, csrf: false)).Status);
            Assert.Equal(403, (await SendAsync("POST", "/api/analytics/chart", chart, cookie: cookie)).Status);
            Assert.Equal(403, (await SendAsync("POST", "/api/analytics/who", who, cookie: cookie)).Status);
        }

        Assert.Equal(401, (await SendAsync("GET", "/api/analytics/links/search", csrf: false)).Status);
        Assert.Equal(401, (await SendAsync("POST", "/api/analytics/chart", chart)).Status);
        var data = await SignInAsync(_f.DataPersonEmail);
        Assert.Equal(200, (await SendAsync("GET", $"/api/analytics/links?ids={link}", cookie: data, csrf: false)).Status);
        Assert.Equal(200, (await SendAsync("GET", $"/api/analytics/links/{link}", cookie: data, csrf: false)).Status);
        var admin = await SignInAsync(_f.AdminEmail);
        Assert.Equal(200, (await SendAsync("POST", "/api/analytics/chart", chart, cookie: admin)).Status);
    }

    // ---- the finder -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_finder_searches_text_customer_dates_kind_and_state_deleted_links_too()
    {
        if (Off) { return; }

        var tag = Id("f")[..10];
        var nira = await ContactAsync("Nira " + tag, tag + "@family.gr");
        var (sea, seaToken) = await TitledLinkAsync("Sea plots " + tag);
        var (hill, _) = await TitledLinkAsync("Hill plots " + tag);
        var (old, _) = await TitledLinkAsync("Old plots " + tag);
        var (gone, _) = await TitledLinkAsync("Gone plots " + tag);
        var links = _f.Services.GetRequiredService<PresentationLinkService>();
        await links.SetContactAsync(Admin(_f), sea, nira);
        await links.SetEnabledAsync(Admin(_f), hill, false);
        await links.DeleteAsync(Admin(_f), gone);
        await using (var c = await _f.Db!.OpenAsync())
        {
            await c.ExecuteAsync("UPDATE presentation_link SET created_utc = '2026-01-15 10:00:00', link_expires_utc = '2026-01-22 10:00:00' WHERE presentation_link_id = @old", new { old });
        }

        await VisitAsync(seaToken, London, E("presentation_opened"));
        var admin = await SignInAsync(_f.AdminEmail);

        async Task<long[]> Find(string query)
        {
            var (status, body, _) = await SendAsync("GET", "/api/analytics/links/search?" + query, cookie: admin, csrf: false);
            Assert.True(status == 200, body.ToString());
            return body.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).ToArray();
        }

        Assert.Equal(new[] { gone, old, hill, sea }.Order(), (await Find("q=" + tag)).Order());                    // All includes deleted
        Assert.Equal(new[] { sea }, await Find("q=" + Uri.EscapeDataString("nira " + tag)));                       // the customer's name, any case
        Assert.Equal(new[] { sea }, await Find("q=" + Uri.EscapeDataString(tag + "@family")));                     // ... and email
        Assert.Equal(new[] { sea }, await Find($"q={tag}&contactId={nira}"));
        Assert.Equal(new[] { gone }, await Find($"q={tag}&state=deleted"));
        Assert.Equal(new[] { hill }, await Find($"q={tag}&state=off"));
        Assert.Equal(new[] { old }, await Find($"q={tag}&state=expired"));
        Assert.Equal(new[] { sea }, await Find($"q={tag}&state=active"));
        Assert.Equal(new[] { old }, await Find($"q={tag}&createdTo=2026-01-31"));
        Assert.Equal(new[] { sea }, await Find($"q={tag}&visitedFrom={AthensToday()}"));
        Assert.Empty(await Find($"q={tag}&kind=portfolio"));
        Assert.Equal(4, (await Find($"q={tag}&kind=assets")).Length);
        Assert.Equal(new[] { old, sea }.Order(), (await Find($"ids={old}&ids={sea}")).Order());

        var (_, body, _) = await SendAsync("GET", $"/api/analytics/links/search?q={tag}&state=deleted", cookie: admin, csrf: false);
        var row = body.GetProperty("rows")[0];
        Assert.Equal(("deleted", "Gone plots " + tag, 2), (row.GetProperty("state").GetString(), row.GetProperty("name").GetString(), row.GetProperty("assetCount").GetInt32()));
    }

    // ---- the bars and their places ----------------------------------------------------------------------------------

    [Fact]
    public async Task Bars_aggregated_by_link_and_by_customer_agree_and_a_bars_places_add_up_deleted_links_counting()
    {
        if (Off) { return; }

        var tag = Id("c")[..10];
        var cohen = await ContactAsync("Cohen " + tag);
        var (a, tokenA) = await TitledLinkAsync("A " + tag);
        var (b, tokenB) = await TitledLinkAsync("B " + tag);
        await _f.Services.GetRequiredService<PresentationLinkService>().SetContactAsync(Admin(_f), a, cohen);
        var (plot, house) = (_f.AssetIds[0], _f.AssetIds[1]);
        await VisitAsync(tokenA, London, E("presentation_opened"), E("asset_viewed", s: new object[] { "Asset", plot }, p: new { via = "chip" }),
            E("photo_viewed", s: new object[] { "Asset", plot }, p: new { via = "open", where = "viewer" }), E("presentation_left", p: new { seconds = 150 }));
        await VisitAsync(tokenA, Boxford, E("presentation_opened"), E("asset_viewed", s: new object[] { "Asset", house }, p: new { via = "pin" }));
        await VisitAsync(tokenB, "203.0.113.77", E("presentation_opened"), E("asset_viewed", s: new object[] { "Asset", plot }, p: new { via = "plot" }),
            E("view_switched", p: new { to = "3d" }));
        await _f.Services.GetRequiredService<PresentationLinkService>().DeleteAsync(Admin(_f), b); // deleted: its visits still count here

        var admin = await SignInAsync(_f.AdminEmail);
        async Task<JsonElement> Chart(string view, string split, string? metric = null)
        {
            var (status, body, _) = await SendAsync("POST", "/api/analytics/chart", new { linkIds = new[] { a, b }, view, split, metric }, cookie: admin);
            Assert.True(status == 200, body.ToString());
            return body;
        }

        static double V(JsonElement chart, string x, string series, string metric) => chart.GetProperty("rows").EnumerateArray()
            .Where(r => r.GetProperty("x").GetString() == x && r.GetProperty("series").GetString() == series && r.GetProperty("metric").GetString() == metric)
            .Sum(r => r.GetProperty("value").GetDouble());

        var none = await Chart("metrics", "none");
        var byLink = await Chart("metrics", "link");
        var byCustomer = await Chart("metrics", "customer");
        Assert.Equal((3.0, 3.0, 1.0, 1.0), (V(none, "visits", "all", "visits"), V(none, "asset_clicks", "all", "asset_clicks"), V(none, "photo_opens", "all", "photo_opens"),
            V(none, "views_3d", "all", "views_3d")));
        foreach (var m in new[] { "visits", "asset_clicks", "photo_opens", "views_3d", "minutes" })
        {
            Assert.Equal(V(none, m, "all", m), V(byLink, m, a.ToString(), m) + V(byLink, m, b.ToString(), m), 6);
            Assert.Equal(V(none, m, "all", m), V(byCustomer, m, cohen.ToString(), m) + V(byCustomer, m, "none", m), 6);
        }

        Assert.Contains(byCustomer.GetProperty("series").EnumerateArray(), s => s.GetProperty("label").GetString() == "Cohen " + tag);

        var assets = await Chart("assets", "link", "asset_clicks");
        Assert.Equal(plot.ToString(), assets.GetProperty("xs")[0].GetProperty("key").GetString()); // the hottest first
        Assert.Equal(("Plot", 2.0), (assets.GetProperty("xs")[0].GetProperty("label").GetString()![..4], V(assets, plot.ToString(), "all", "asset_devices")));
        Assert.True(assets.GetProperty("xs")[0].GetProperty("parcelId").GetInt64() > 0);

        var path = await Chart("path", "none");
        Assert.Equal((3.0, 3.0, 1.0, 1.0, 1.0), (V(path, "visits", "all", "visits"), V(path, "path_property", "all", "path_property"), V(path, "path_photo", "all", "path_photo"),
            V(path, "path_tools", "all", "path_tools"), V(path, "path_2min", "all", "path_2min")));
        Assert.Equal(3.0, (await Chart("time", "none")).GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("metric").GetString() == "visits").Sum(r => r.GetProperty("value").GetDouble()));

        // One bar: visits on link A - its places add up to it, at city level, with the link's customer.
        var (ws, who, _) = await SendAsync("POST", "/api/analytics/who", new { linkIds = new[] { a, b }, view = "metrics", split = "link", metric = "visits", series = a.ToString() }, cookie: admin);
        Assert.True(ws == 200, who.ToString());
        var places = who.GetProperty("places").EnumerateArray().ToList();
        Assert.Equal(2.0, who.GetProperty("total").GetDouble());
        Assert.Equal(2.0, places.Sum(p => p.GetProperty("value").GetDouble()));
        Assert.Contains(places, p => p.GetProperty("label").GetString() == "London, United Kingdom");
        Assert.All(places, p => Assert.Equal("Cohen " + tag, p.GetProperty("visitList")[0].GetProperty("customer").GetString()));
        Assert.DoesNotContain(London, who.ToString()); // the IP only in a visit's own view

        // The unknown place: the deleted link's visit.
        var (_, unknown, _) = await SendAsync("POST", "/api/analytics/who", new { linkIds = new[] { a, b }, view = "assets", split = "none", metric = "asset_clicks", x = plot.ToString() }, cookie: admin);
        Assert.Equal(2.0, unknown.GetProperty("total").GetDouble());
        Assert.Contains(unknown.GetProperty("places").EnumerateArray(), p => p.GetProperty("label").GetString() == "unknown place");

        Assert.Equal(400, (await SendAsync("POST", "/api/analytics/chart", new { linkIds = Enumerable.Range(1, 51).ToArray() }, cookie: admin)).Status);
        Assert.Equal(400, (await SendAsync("POST", "/api/analytics/chart", new { linkIds = new[] { a }, from = "2024-01-01", to = "2026-11-10" }, cookie: admin)).Status);
    }

    // ---- the customer: Admin only -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_customer_is_set_changed_and_cleared_by_an_Admin_and_never_reaches_anyone_else()
    {
        if (Off) { return; }

        var tag = Id("k")[..10];
        var nira = await ContactAsync("Nira " + tag);
        var dan = await ContactAsync("Dan " + tag);
        var admin = await SignInAsync(_f.AdminEmail);
        var data = await SignInAsync(_f.DataPersonEmail);
        var sales = await SignInAsync(_f.SalesEmail);
        var target = string.Join("&", _f.AssetIds.Select(i => "assetIds=" + i));

        // An Admin makes a link for Nira.
        var (status, made, _) = await SendAsync("POST", "/api/presentation/links", new { assetIds = _f.AssetIds, title = "Sea-view plots " + tag, contactId = nira }, cookie: admin);
        Assert.True(status == 200, made.ToString());
        var link = made.GetProperty("link");
        var id = link.GetProperty("id").GetInt64();
        var token = made.GetProperty("token").GetString()!;
        Assert.Equal((nira, "Nira " + tag), (link.GetProperty("contactId").GetInt64(), link.GetProperty("contactName").GetString()));

        // Data person: the list has no customer field at all, even on the Admin's tagged link; sending one is refused.
        var (ls, list, _) = await SendAsync("GET", "/api/presentation/links?" + target, cookie: data, csrf: false);
        Assert.Equal(200, ls);
        Assert.Contains(list.EnumerateArray(), l => l.GetProperty("id").GetInt64() == id);
        Assert.All(list.EnumerateArray(), l => Assert.False(l.TryGetProperty("contactId", out _) || l.TryGetProperty("contactName", out _)));
        Assert.DoesNotContain("Nira " + tag, list.ToString());
        var before = await ScalarAsync<int>("SELECT COUNT(*) FROM presentation_link");
        var (ds, denied, _) = await SendAsync("POST", "/api/presentation/links", new { assetIds = _f.AssetIds, title = "Data " + tag, contactId = dan }, cookie: data);
        Assert.Equal((403, "PRESENTATION_LINK_CUSTOMER_FORBIDDEN"), (ds, denied.GetProperty("error").GetString()));
        Assert.Equal(before, await ScalarAsync<int>("SELECT COUNT(*) FROM presentation_link")); // refused before a link was made
        var (dm, dataMade, _) = await SendAsync("POST", "/api/presentation/links", new { assetIds = _f.AssetIds, title = "Data " + tag }, cookie: data);
        Assert.Equal(200, dm); // Data person still makes links as today
        var dataLink = dataMade.GetProperty("link").GetProperty("id").GetInt64();
        Assert.False(dataMade.GetProperty("link").TryGetProperty("contactId", out _));
        Assert.Equal(403, (await SendAsync("PATCH", $"/api/presentation/links/{id}/contact", new { contactId = dan }, cookie: data)).Status);
        Assert.Equal(403, (await SendAsync("PATCH", $"/api/presentation/links/{id}/contact", new { contactId = (long?)null }, cookie: data)).Status);
        Assert.Equal(403, (await SendAsync("PATCH", $"/api/presentation/links/{id}/contact", new { contactId = dan }, cookie: sales)).Status);
        Assert.Equal(403, (await SendAsync("POST", "/api/presentation/links", new { assetIds = _f.AssetIds, contactId = dan }, cookie: sales)).Status);
        Assert.Equal(nira, await ScalarAsync<long>("SELECT contact_id FROM presentation_link WHERE presentation_link_id = @id", new { id }));

        // An Admin changes it - on a Data person's link too - and clears it.
        var (cs, changed, _) = await SendAsync("PATCH", $"/api/presentation/links/{dataLink}/contact", new { contactId = dan }, cookie: admin);
        Assert.True(cs == 200, changed.ToString());
        Assert.Equal("Dan " + tag, changed.GetProperty("contactName").GetString());
        var (_, again, _) = await SendAsync("GET", "/api/presentation/links?" + target, cookie: data, csrf: false);
        Assert.DoesNotContain("Dan " + tag, again.ToString());
        Assert.Equal(200, (await SendAsync("PATCH", $"/api/presentation/links/{dataLink}/contact", new { contactId = (long?)null }, cookie: admin)).Status);
        Assert.Null(await ScalarAsync<long?>("SELECT contact_id FROM presentation_link WHERE presentation_link_id = @dataLink", new { dataLink }));

        // The visitor never sees it: not in the link's answers, nor in what the page records.
        foreach (var path in new[] { "/api/present-link/state", "/api/present-link/data" })
        {
            var (ps, page, _) = await SendAsync("GET", path, token: token, csrf: false);
            Assert.True(ps == 200, page.ToString());
            Assert.DoesNotContain("Nira", page.ToString());
            Assert.False(page.TryGetProperty("contactId", out _));
        }

        // A deactivated customer: still shown (greyed) on the old link, but can't be chosen again.
        await using (var c = await _f.Db!.OpenAsync())
        {
            await c.ExecuteAsync("UPDATE contact SET is_active = 0 WHERE contact_id = @nira", new { nira });
        }

        var (_, adminList, _) = await SendAsync("GET", "/api/presentation/links?" + target, cookie: admin, csrf: false);
        var tagged = adminList.EnumerateArray().Single(l => l.GetProperty("id").GetInt64() == id);
        Assert.Equal(("Nira " + tag, false), (tagged.GetProperty("contactName").GetString(), tagged.GetProperty("contactIsActive").GetBoolean()));
        Assert.Equal(400, (await SendAsync("PATCH", $"/api/presentation/links/{dataLink}/contact", new { contactId = nira }, cookie: admin)).Status);
        Assert.Equal(404, (await SendAsync("PATCH", $"/api/presentation/links/{dataLink}/contact", new { contactId = 987654321 }, cookie: admin)).Status);
    }

    [Fact]
    public async Task A_deleted_link_leaves_the_Present_dialog_and_keeps_counting_on_the_page()
    {
        if (Off) { return; }

        var tag = Id("d")[..10];
        var (link, token) = await TitledLinkAsync("Deleted " + tag);
        await VisitAsync(token, London, E("presentation_opened"));
        var admin = await SignInAsync(_f.AdminEmail);
        Assert.Equal(200, (await SendAsync("DELETE", $"/api/presentation/links/{link}", cookie: admin)).Status);
        var target = string.Join("&", _f.AssetIds.Select(i => "assetIds=" + i));
        var (_, list, _) = await SendAsync("GET", "/api/presentation/links?" + target, cookie: admin, csrf: false);
        Assert.DoesNotContain(list.EnumerateArray(), l => l.GetProperty("id").GetInt64() == link);
        var (_, chart, _) = await SendAsync("POST", "/api/analytics/chart", new { linkIds = new[] { link }, view = "metrics", split = "none" }, cookie: admin);
        Assert.Equal(1.0, chart.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("metric").GetString() == "visits").GetProperty("value").GetDouble());
    }
}
