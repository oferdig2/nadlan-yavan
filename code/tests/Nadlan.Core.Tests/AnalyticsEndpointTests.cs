using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Nadlan.Core.Analytics;
using Nadlan.Core.Presentation;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Host.Analytics;
using Nadlan.Persistence.MySql;
using Nadlan.Persistence.MySql.Analytics;

namespace Nadlan.Core.Tests;

/// <summary>
/// The whole app in this process (WebApplicationFactory) on a throwaway MySQL schema (NADLAN_TEST_MYSQL), with MaxMind's
/// test databases as the geo files. Without the variable these tests do nothing.
/// </summary>
public sealed class AnalyticsAppFixture : IAsyncLifetime
{
    public const string Password = "Analytics-test-2026!";

    public MySqlDatabase? Db { get; private set; }
    private WebApplicationFactory<Program>? App { get; set; }
    public TestServer Server => App!.Server;
    public IServiceProvider Services => App!.Services;

    public long AdminId { get; private set; }
    public long SalesId { get; private set; }
    public string AdminEmail { get; private set; } = "";
    public string SalesEmail { get; private set; } = "";
    public long[] AssetIds { get; private set; } = Array.Empty<long>();
    public string AreaName { get; private set; } = "";

    public async Task InitializeAsync()
    {
        if (ScratchMySql.ServerConnectionString is null)
        {
            return;
        }

        Db = ScratchMySql.NewDatabase("nadlan_a_");
        await new SchemaMigrator(Db).MigrateAsync();
        await SeedAsync();
        // Read by Program.cs; environment variables come last there, after the settings in the database.
        Environment.SetEnvironmentVariable(MySqlDatabase.ConnectionStringEnvVar, Db.ConnectionString);
        Environment.SetEnvironmentVariable("Nadlan__Geo__CityDbPath", AnalyticsRulesTests.TestDb("GeoIP2-City-Test.mmdb"));
        Environment.SetEnvironmentVariable("Nadlan__Geo__AsnDbPath", AnalyticsRulesTests.TestDb("GeoLite2-ASN-Test.mmdb"));
        App = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production"));
        _ = App.Server; // start it
    }

    private async Task SeedAsync()
    {
        await using var c = await Db!.OpenAsync();
        async Task<long> Id(string sql, object? args = null) => await c.ExecuteScalarAsync<long>(sql + "; SELECT LAST_INSERT_ID();", args);
        var hash = PasswordHasher.Hash(Password);
        AdminEmail = "admin-an@example.gr";
        SalesEmail = "sales-an@example.gr";
        AdminId = await Id("INSERT INTO app_user (email, display_name, security_role_id, password_hash) SELECT @e, 'Admin A', security_role_id, @hash FROM security_role WHERE code = 'ADMIN'",
            new { e = AdminEmail, hash });
        SalesId = await Id("INSERT INTO app_user (email, display_name, security_role_id, password_hash) SELECT @e, 'Sales S', security_role_id, @hash FROM security_role WHERE code = 'SALES'",
            new { e = SalesEmail, hash });

        var gr = await c.ExecuteScalarAsync<int>("SELECT country_id FROM country WHERE code = 'GR'");
        AreaName = "Skroponeria AN";
        var area = await Id("INSERT INTO geographic_area (country_id, code, name) VALUES (@gr, 'SKRAN', @name)", new { gr, name = AreaName });
        var contact = await Id("INSERT INTO contact (display_name) VALUES ('Agent AN')");
        var ids = new List<long>();
        foreach (var (type, lon) in new[] { ("PLOT", 23.70), ("HOUSE", 23.72) })
        {
            var parcel = await Id("""
                INSERT INTO parcel (country_id, registry_id, geographic_area_id, geometry)
                VALUES (@gr, @kaek, @area, ST_GeomFromText(@wkt, 4326, 'axis-order=long-lat'))
                """, new { gr, kaek = "AN-" + type, area, wkt = $"POLYGON(({lon} 37.9, {lon + 0.001} 37.9, {lon + 0.001} 37.901, {lon} 37.901, {lon} 37.9))" });
            var asset = await Id("""
                INSERT INTO asset (managing_contact_id, property_type_id, asset_status_id)
                SELECT @contact, (SELECT property_type_id FROM property_type WHERE code = @type), (SELECT asset_status_id FROM asset_status WHERE code = 'FOR_SALE')
                """, new { contact, type });
            await c.ExecuteAsync("INSERT INTO asset_parcel (asset_id, parcel_id) VALUES (@asset, @parcel)", new { asset, parcel });
            ids.Add(asset);
        }

        AssetIds = ids.ToArray();
    }

    public async Task DisposeAsync()
    {
        if (App is not null)
        {
            await App.DisposeAsync();
        }

        if (Db is not null)
        {
            await ScratchMySql.DropAsync(Db);
        }
    }
}

public class AnalyticsEndpointTests : IClassFixture<AnalyticsAppFixture>
{
    private const string IPhone = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1";
    private const string Edge = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0";
    private const string London = "81.2.69.142"; // in MaxMind's City test database

    private readonly AnalyticsAppFixture _f;
    private static int _ip = 10;

    public AnalyticsEndpointTests(AnalyticsAppFixture fixture)
    {
        _f = fixture;
    }

    private static bool Off => ScratchMySql.ServerConnectionString is null;

    private static UserAccess Admin(AnalyticsAppFixture f) => new() { UserId = f.AdminId, DisplayName = "Admin A", RoleCode = SecurityRoles.Admin };

    /// <summary>A public link of the seeded Assets: (id, token).</summary>
    private async Task<(long Id, string Token)> LinkAsync(string? password = null)
    {
        var links = _f.Services.GetRequiredService<PresentationLinkService>();
        var (link, token) = await links.CreateAsync(Admin(_f), null, _f.AssetIds, "For the Smiths", null);
        if (password is not null)
        {
            await links.SetPasswordAsync(Admin(_f), link.PresentationLinkId, password);
        }

        return (link.PresentationLinkId, token);
    }

    private static long Ms(DateTime utc) => AnalyticsService.ToMs(utc);

    private static string Id(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..20];

    private static object Batch(string session, string visitor, IEnumerable<object> events, object? context = null, long? sentAt = null, string? batchId = null,
        string source = "present_public")
        => new { batchId = batchId ?? Id("b"), sentAt = sentAt ?? Ms(DateTime.UtcNow), source, sessionId = session, visitorId = visitor, context, events };

    private static object E(string name, DateTime? at = null, object? s = null, object? p = null) => new { e = name, t = Ms(at ?? DateTime.UtcNow), s, p };

    /// <summary>A request straight into the app; ip = the client's address (as nginx would hand it over).</summary>
    private async Task<(int Status, JsonElement Body, HttpContext Http)> SendAsync(string method, string path, object? body = null, string? token = null, string? ip = null,
        string? cookie = null, bool csrf = true, string ua = IPhone, string? rawBody = null)
    {
        var http = await _f.Server.SendAsync(ctx =>
        {
            ctx.Request.Method = method;
            var q = path.IndexOf('?');
            ctx.Request.Path = q < 0 ? path : path[..q];
            ctx.Request.QueryString = q < 0 ? QueryString.Empty : new QueryString(path[q..]);
            ctx.Request.Headers.UserAgent = ua;
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip ?? London);
            if (token is not null) { ctx.Request.Headers["X-Present-Link"] = token; }
            if (csrf) { ctx.Request.Headers["X-Nadlan-Request"] = "1"; }
            if (cookie is not null) { ctx.Request.Headers.Cookie = cookie; }
            if (body is not null || rawBody is not null)
            {
                ctx.Request.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(rawBody ?? JsonSerializer.Serialize(body));
                ctx.Request.ContentLength = bytes.Length;
                ctx.Request.Body = new MemoryStream(bytes);
                ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody()); // else minimal APIs see no body
            }
        });
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();
        var json = text.Length > 0 && (text[0] == '{' || text[0] == '[') ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return (http.Response.StatusCode, json, http);
    }

    private sealed class HasBody : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private static string Cookies(HttpContext http)
        => string.Join("; ", http.Response.Headers.SetCookie.Select(c => c!.Split(';')[0]));

    private async Task<string> SignInAsync(string email)
    {
        var (status, body, http) = await SendAsync("POST", "/api/auth/login", new { email, password = AnalyticsAppFixture.Password });
        Assert.True(status == 200, body.ToString());
        return Cookies(http);
    }

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var c = await _f.Db!.OpenAsync();
        return (await c.ExecuteScalarAsync<T>(sql, args))!;
    }

    // ---- POST /api/present-link/events ----------------------------------------------------------------------------

    [Fact]
    public async Task A_batch_is_stored_once_and_a_first_batch_makes_the_visit_with_IP_device_place_screen_language_referrer()
    {
        if (Off) { return; }

        var (link, token) = await LinkAsync();
        var (session, visitor) = (Id("s"), Id("v"));
        var batch = Batch(session, visitor, new[] { E("presentation_opened", p: new { properties = 2 }), E("asset_viewed", s: new object[] { "Asset", _f.AssetIds[0] }, p: new { via = "chip" }) },
            new { screen = new[] { 390, 844 }, viewport = new[] { 390, 760 }, dpr = 3, orientation = "portrait", touch = true, lang = "el-GR", langs = new[] { "el-GR", "en" },
                tz = "Europe/Athens", referrer = "https://l.facebook.com/l.php?u=x", utm = new { source = "whatsapp" } }, batchId: Id("b"));

        var (status, body, _) = await SendAsync("POST", "/api/present-link/events", batch, token);
        Assert.Equal(200, status);
        Assert.Equal(2, body.GetProperty("stored").GetInt32());
        (status, body, _) = await SendAsync("POST", "/api/present-link/events", batch, token); // sent again: no answer reached the phone
        Assert.Equal(200, status);
        Assert.True(body.GetProperty("duplicate").GetBoolean());
        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM app_event WHERE session_id = @session", new { session }));

        await using var c = await _f.Db!.OpenAsync();
        var row = await c.QuerySingleAsync("SELECT * FROM app_event_context WHERE session_id = @session", new { session });
        Assert.Equal((London, "GB", "London"), ((string)row.ip, (string)row.geo_country, (string)row.geo_city));
        Assert.Equal(("Mobile Safari", "iOS", "phone"), ((string)row.browser, (string)row.os, (string)row.device_type));
        Assert.Equal((390, 844, 390, 760, 3.00m, "portrait"), ((int)row.screen_w, (int)row.screen_h, (int)row.viewport_w, (int)row.viewport_h, (decimal)row.pixel_ratio, (string)row.orientation));
        Assert.Equal(("el-GR", "el-GR,en", "Europe/Athens"), ((string)row.language, (string)row.languages, (string)row.time_zone));
        Assert.Equal(("l.facebook.com", "whatsapp", link), ((string)row.referrer_host, (string)row.utm_source, (long)row.presentation_link_id));
        Assert.Equal(visitor, (string)row.visitor_id);
        DateTime firstLast = row.last_utc;

        // A later batch: another context and browser - only the last time moves.
        var later = Batch(session, visitor, new[] { E("all_pressed", DateTime.UtcNow.AddSeconds(30)) }, new { screen = new[] { 1920, 1080 }, lang = "en-US", referrer = "https://mail.google.com/" });
        Assert.Equal(200, (await SendAsync("POST", "/api/present-link/events", later, token, ip: "2.125.160.216", ua: Edge)).Status);
        var after = await c.QuerySingleAsync("SELECT * FROM app_event_context WHERE session_id = @session", new { session });
        Assert.Equal((London, "Mobile Safari", 390, "el-GR", "l.facebook.com"), ((string)after.ip, (string)after.browser, (int)after.screen_w, (string)after.language, (string)after.referrer_host));
        Assert.True((DateTime)after.last_utc > firstLast);
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM app_event_context WHERE session_id = @session", new { session }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM app_event WHERE session_id = @session AND user_id IS NOT NULL", new { session }));
    }

    [Fact]
    public async Task The_link_comes_from_the_token_and_a_session_of_another_link_is_refused()
    {
        if (Off) { return; }

        var (a, tokenA) = await LinkAsync();
        var (b, tokenB) = await LinkAsync();
        var (session, visitor) = (Id("s"), Id("v"));
        // The body can't name a link: there is no such field, the token decides.
        Assert.Equal(200, (await SendAsync("POST", "/api/present-link/events", Batch(session, visitor, new[] { E("presentation_opened") }), tokenB)).Status);
        Assert.Equal(b, await ScalarAsync<long>("SELECT presentation_link_id FROM app_event WHERE session_id = @session", new { session }));

        // Link A's token with link B's session: refused, nothing changes.
        var (status, body, _) = await SendAsync("POST", "/api/present-link/events", Batch(session, visitor, new[] { E("asset_viewed", s: new object[] { "Asset", 1 }) }), tokenA);
        Assert.Equal(400, status);
        Assert.Equal("ANALYTICS_SESSION", body.GetProperty("error").GetString());
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM app_event WHERE presentation_link_id = @a", new { a }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM app_event WHERE session_id = @session", new { session }));
    }

    [Fact]
    public async Task A_disabled_expired_or_unknown_link_is_404_and_a_password_link_without_its_cookie_is_401()
    {
        if (Off) { return; }

        var links = _f.Services.GetRequiredService<PresentationLinkService>();
        object One() => Batch(Id("s"), Id("v"), new[] { E("presentation_opened") });

        var (disabled, tokenD) = await LinkAsync();
        await links.SetEnabledAsync(Admin(_f), disabled, false);
        Assert.Equal(404, (await SendAsync("POST", "/api/present-link/events", One(), tokenD)).Status);

        var (expired, tokenE) = await LinkAsync();
        await using (var c = await _f.Db!.OpenAsync())
        {
            await c.ExecuteAsync("UPDATE presentation_link SET link_expires_utc = UTC_TIMESTAMP(3) - INTERVAL 1 MINUTE WHERE presentation_link_id = @expired", new { expired });
        }

        Assert.Equal(404, (await SendAsync("POST", "/api/present-link/events", One(), tokenE)).Status);
        Assert.Equal(404, (await SendAsync("POST", "/api/present-link/events", One(), "no-such-token")).Status);

        var (_, tokenP) = await LinkAsync(password: "1234-pass");
        var (status, body, _) = await SendAsync("POST", "/api/present-link/events", One(), tokenP);
        Assert.Equal((401, "PRESENT_LINK_PASSWORD"), (status, body.GetProperty("error").GetString()));
        var unlock = await SendAsync("POST", "/api/present-link/unlock", new { password = "1234-pass" }, tokenP);
        Assert.Equal(200, unlock.Status);
        Assert.Equal(200, (await SendAsync("POST", "/api/present-link/events", One(), tokenP, cookie: Cookies(unlock.Http))).Status);
    }

    [Fact]
    public async Task A_signed_in_staff_cookie_is_no_link_and_is_not_recorded_as_a_user()
    {
        if (Off) { return; }

        var cookie = await SignInAsync(_f.AdminEmail);
        var (_, token) = await LinkAsync();
        var session = Id("s");
        Assert.Equal(404, (await SendAsync("POST", "/api/present-link/events", Batch(session, Id("v"), new[] { E("presentation_opened") }), cookie: cookie)).Status);
        Assert.Equal(200, (await SendAsync("POST", "/api/present-link/events", Batch(session, Id("v"), new[] { E("presentation_opened") }), token, cookie: cookie)).Status);
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM app_event WHERE session_id = @session AND user_id IS NOT NULL", new { session }));
        // and like every write, the app's request header is required
        Assert.Equal(403, (await SendAsync("POST", "/api/present-link/events", Batch(Id("s"), Id("v"), new[] { E("presentation_opened") }), token, csrf: false)).Status);
    }

    [Fact]
    public async Task Too_many_events_is_400_bad_ones_are_dropped_and_the_rest_kept_and_a_clock_an_hour_off_is_corrected()
    {
        if (Off) { return; }

        var (_, token) = await LinkAsync();
        var many = Enumerable.Range(0, 201).Select(_ => E("presentation_opened")).ToArray();
        var (status, body, _) = await SendAsync("POST", "/api/present-link/events", Batch(Id("s"), Id("v"), many), token);
        Assert.Equal((400, "ANALYTICS_TOO_BIG"), (status, body.GetProperty("error").GetString()));
        Assert.Equal(400, (await SendAsync("POST", "/api/present-link/events", Batch(Id("s"), Id("v"), new[] { E("presentation_opened") }, source: "map"), token)).Status);
        Assert.Equal(400, (await SendAsync("POST", "/api/present-link/events", null, token, rawBody: "{ not json")).Status);
        Assert.Equal(413, (await SendAsync("POST", "/api/present-link/events", null, token, rawBody: "{\"x\":\"" + new string('a', 70_000) + "\"}")).Status);

        var session = Id("s");
        var mixed = new[]
        {
            E("presentation_opened"),
            E("Bad Name"),
            E("photo_viewed", p: new { big = new string('x', 190), more = new string('y', 190), a = new string('z', 190), b = new string('q', 190), c = new string('w', 190),
                d = new string('e', 190), f = new string('r', 190), g = new string('t', 190), h = new string('u', 190), i = new string('i', 190), j = new string('o', 190) }),
            E("asset_viewed", s: new object[] { "Contact", 3 }),
            E("asset_viewed", s: new object[] { "Asset", _f.AssetIds[1] }, p: new { via = "pin" }),
        };
        (status, body, _) = await SendAsync("POST", "/api/present-link/events", Batch(session, Id("v"), mixed), token);
        Assert.Equal(200, status);
        Assert.Equal(2, body.GetProperty("stored").GetInt32());
        Assert.Equal(new[] { "asset_viewed", "presentation_opened" },
            (await (await _f.Db!.OpenAsync()).QueryAsync<string>("SELECT event FROM app_event WHERE session_id = @session ORDER BY event", new { session })).ToArray());

        // The phone's clock is an hour behind: its times are moved by the difference.
        var behind = DateTime.UtcNow.AddHours(-1);
        var late = Id("s");
        Assert.Equal(200, (await SendAsync("POST", "/api/present-link/events", Batch(late, Id("v"), new[] { E("presentation_opened", behind.AddSeconds(-5)) }, sentAt: Ms(behind)), token)).Status);
        var stored = await ScalarAsync<DateTime>("SELECT occurred_utc FROM app_event WHERE session_id = @late", new { late });
        Assert.InRange(stored, DateTime.UtcNow.AddSeconds(-30), DateTime.UtcNow);
    }

    [Fact]
    public async Task A_visitor_sending_too_fast_gets_429()
    {
        if (Off) { return; }

        var (_, token) = await LinkAsync();
        var ip = "203.0.113." + Interlocked.Increment(ref _ip);
        var statuses = new List<int>();
        for (var i = 0; i <= EventRateLimiter.PerMinute; i++)
        {
            statuses.Add((await SendAsync("POST", "/api/present-link/events", Batch(Id("s"), Id("v"), Array.Empty<object>()), token, ip: ip)).Status);
        }

        Assert.All(statuses.Take(EventRateLimiter.PerMinute), s => Assert.Equal(200, s));
        Assert.Equal(429, statuses[^1]);
    }

    // ---- geo: no file, and the daily backfill ---------------------------------------------------------------------

    [Fact]
    public async Task Without_geo_files_the_visit_keeps_its_IP_and_the_daily_job_places_it_later()
    {
        if (Off) { return; }

        var (link, _) = await LinkAsync();
        var store = new MySqlEventStore(_f.Db!);
        var noGeo = new AnalyticsService(store, new MaxMindGeoLookup(Path.Combine(Path.GetTempPath(), "none-" + Guid.NewGuid() + ".mmdb"), null), new UaParserAgentParser());
        var session = Id("s");
        var body = JsonDocument.Parse(JsonSerializer.Serialize(Batch(session, Id("v"), new[] { E("presentation_opened") }))).RootElement;
        Assert.Equal((1, false), await noGeo.RecordAsync(link, body, RequestInfo.Of(London, IPhone)));
        Assert.Null(await ScalarAsync<string?>("SELECT geo_country FROM app_event_context WHERE session_id = @session", new { session }));

        using var geo = new MaxMindGeoLookup(AnalyticsRulesTests.TestDb("GeoIP2-City-Test.mmdb"), AnalyticsRulesTests.TestDb("GeoLite2-ASN-Test.mmdb"));
        var filled = await new GeoBackfill(store, geo, Microsoft.Extensions.Logging.Abstractions.NullLogger<GeoBackfill>.Instance).RunOnceAsync(CancellationToken.None);
        Assert.True(filled >= 1);
        Assert.Equal("GB", await ScalarAsync<string?>("SELECT geo_country FROM app_event_context WHERE session_id = @session", new { session }));
    }

    // ---- the reports ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reports_count_a_links_opens_devices_and_property_time_and_Sales_gets_403()
    {
        if (Off) { return; }

        var (link, token) = await LinkAsync();
        var (plot, house) = (_f.AssetIds[0], _f.AssetIds[1]);
        var t = DateTime.UtcNow.AddMinutes(-10);
        var phone = Id("v");
        await SendAsync("POST", "/api/present-link/events", Batch(Id("s"), phone, new[]
        {
            E("presentation_opened", t), E("asset_viewed", t.AddSeconds(5), new object[] { "Asset", plot }, new { via = "chip" }),
            E("photo_viewed", t.AddSeconds(8), new object[] { "Asset", plot }, new { index = 2, of = 3, kind = "image", where = "card", via = "arrow" }),
            E("asset_time", t.AddSeconds(45), new object[] { "Asset", plot }, new { seconds = 40 }),
            E("measure_used", t.AddSeconds(50), null, new { metres = 150, points = 2 }),
            E("presentation_left", t.AddSeconds(60), null, new { seconds = 60 }),
        }, new { screen = new[] { 390, 844 } }), token);
        await SendAsync("POST", "/api/present-link/events", Batch(Id("s"), phone, new[]
        {
            E("presentation_opened", t.AddMinutes(5)), E("asset_viewed", t.AddMinutes(5).AddSeconds(2), new object[] { "Asset", house }, new { via = "pin" }),
            E("asset_time", t.AddMinutes(5).AddSeconds(22), new object[] { "Asset", house }, new { seconds = 20 }),
        }), token);
        await SendAsync("POST", "/api/present-link/events", Batch(Id("s"), Id("v"), new[] { E("presentation_opened", t.AddMinutes(8)) }), token, ip: "2.125.160.216", ua: Edge);

        var admin = await SignInAsync(_f.AdminEmail);
        var (status, list, _) = await SendAsync("GET", $"/api/analytics/links?ids={link}", cookie: admin, csrf: false);
        Assert.Equal(200, status);
        var line = list.EnumerateArray().Single();
        Assert.Equal((3, 3, 2), (line.GetProperty("opens").GetInt32(), line.GetProperty("visits").GetInt32(), line.GetProperty("devices").GetInt32()));
        Assert.Equal("desktop", line.GetProperty("lastDeviceType").GetString());

        var (s2, report, _) = await SendAsync("GET", $"/api/analytics/links/{link}", cookie: admin, csrf: false);
        Assert.Equal(200, s2);
        var props = report.GetProperty("properties").EnumerateArray().ToDictionary(p => p.GetProperty("assetId").GetInt64());
        Assert.Equal((1, 40.0, 1), (props[plot].GetProperty("views").GetInt32(), props[plot].GetProperty("seconds").GetDouble(), props[plot].GetProperty("photos").GetInt32()));
        Assert.Equal((1, 20.0), (props[house].GetProperty("views").GetInt32(), props[house].GetProperty("seconds").GetDouble()));
        Assert.Equal("Plot · " + _f.AreaName, props[plot].GetProperty("label").GetString());
        var visits = report.GetProperty("visits").EnumerateArray().ToList();
        Assert.Equal(3, visits.Count);
        Assert.DoesNotContain(visits, v => v.TryGetProperty("ip", out _)); // never the IP in the list

        var first = visits.Single(v => v.GetProperty("seconds").GetDouble() == 60);
        var (s3, visit, _) = await SendAsync("GET", $"/api/analytics/visits/{first.GetProperty("contextId").GetInt64()}", cookie: admin, csrf: false);
        Assert.Equal(200, s3);
        Assert.Equal(London, visit.GetProperty("context").GetProperty("ip").GetString());
        Assert.Equal($"Opened → Plot · {_f.AreaName} (40 s, 1 photo) → measured 150 m → left (1 min on screen)",
            string.Join(" → ", visit.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("text").GetString())));

        var sales = await SignInAsync(_f.SalesEmail);
        Assert.Equal(403, (await SendAsync("GET", $"/api/analytics/links?ids={link}", cookie: sales, csrf: false)).Status);
        Assert.Equal(403, (await SendAsync("GET", $"/api/analytics/links/{link}", cookie: sales, csrf: false)).Status);
        Assert.Equal(401, (await SendAsync("GET", $"/api/analytics/links/{link}", csrf: false)).Status); // not signed in
    }

    [Fact]
    public async Task Deleting_a_link_keeps_its_events()
    {
        if (Off) { return; }

        var (link, token) = await LinkAsync();
        var session = Id("s");
        await SendAsync("POST", "/api/present-link/events", Batch(session, Id("v"), new[] { E("presentation_opened") }), token);
        await _f.Services.GetRequiredService<PresentationLinkService>().DeleteAsync(Admin(_f), link);
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM app_event WHERE session_id = @session AND presentation_link_id IS NULL", new { session }));
    }
}
