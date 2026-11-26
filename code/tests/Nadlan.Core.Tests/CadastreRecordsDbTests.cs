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
using Nadlan.Core.Cadastre;
using Nadlan.Core.Security;
using Nadlan.Persistence.MySql;
using Nadlan.Persistence.MySql.Cadastre;
using static Nadlan.Core.Tests.CadastreRecordsServerTests;

namespace Nadlan.Core.Tests;

/// <summary>Test classes that start the whole app in this process: one at a time (Program reads the database from an environment variable).</summary>
[CollectionDefinition(Name)]
public sealed class InProcessAppCollection
{
    public const string Name = "App in process";
}

/// <summary>The SQL side of the Cadastre records (migration 031) on a throwaway MySQL (NADLAN_TEST_MYSQL). Made-up data only.</summary>
public class MySqlCadastreTests : IClassFixture<ScratchMySql>
{
    private readonly MySqlDatabase? _db;
    private static int _seq;

    public MySqlCadastreTests(ScratchMySql scratch)
    {
        _db = scratch.Db;
    }

    /// <summary>An area with three real KAEKs, one provisional, and an Admin; the KAEKs differ per test.</summary>
    private async Task<(int Area, long Admin, string[] Kaeks, long[] Parcels)> SeedAsync()
    {
        var n = Interlocked.Increment(ref _seq);
        await using var c = await _db!.OpenAsync();
        var gr = await c.ExecuteScalarAsync<int>("SELECT country_id FROM country WHERE code = 'GR'");
        var area = await c.ExecuteScalarAsync<int>("INSERT INTO geographic_area (country_id, code, name) VALUES (@gr, @code, @code); SELECT LAST_INSERT_ID();",
            new { gr, code = "CAD" + n + Guid.NewGuid().ToString("N")[..6] });
        var admin = await c.ExecuteScalarAsync<long>("""
            INSERT INTO app_user (email, display_name, security_role_id) SELECT @e, 'Admin Cad', security_role_id FROM security_role WHERE code = 'ADMIN';
            SELECT LAST_INSERT_ID();
            """, new { e = $"cad{n}-{Guid.NewGuid():N}@example.gr" });
        var kaeks = Enumerable.Range(1, 3).Select(i => $"98{n:D4}{i:D6}").ToArray();
        var parcels = new List<long>();
        foreach (var (k, i) in kaeks.Append($"TMP-CAD{n}-OT1-P1").Select((k, i) => (k, i)))
        {
            var lon = 21 + n * 0.01 + i * 0.001;
            parcels.Add(await c.ExecuteScalarAsync<long>("""
                INSERT INTO parcel (country_id, registry_id, registry_id_is_provisional, geographic_area_id, geometry)
                VALUES (@gr, @k, @tmp, @area, ST_GeomFromText(@wkt, 4326, 'axis-order=long-lat')); SELECT LAST_INSERT_ID();
                """, new { gr, k, tmp = k.StartsWith("TMP", StringComparison.Ordinal), area,
                wkt = FormattableString.Invariant($"POLYGON(({lon} 38, {lon + 0.0005} 38, {lon + 0.0005} 38.0005, {lon} 38.0005, {lon} 38))") }));
        }

        return (area, admin, kaeks, parcels.ToArray());
    }

    private static CadastreRecordInput For(string kaek, CadastreRecordInput r) => r with { Kaek = kaek };

    [Fact]
    public async Task A_record_is_saved_and_read_back_with_its_owners_acts_and_parties()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var service = new CadastreService(new MySqlCadastreStore(_db));
        var record = For(w.Kaeks[0], Record(
            Unit(0, 2052.50m, new[] { Company() }),
            Unit(1, 30m, new[] { Person(), new CadastreOwnerInput("ΑΓΝΩΣΤΟΣ", "1 / 1", "Unknown", null, null, null) },
                Sale(new DateOnly(2024, 3, 28), 21000, w.Kaeks[0] + "/0/1", Buyer()),
                new CadastreActInput("RecordTitle", null, null, null, "Other", "ΣΥΜΒΟΛΑΙΟΓΡΑΦΙΚΟ ΕΓΓΡΑΦΟ", new DateOnly(1898, 7, 25), "9009", null, null, null, null))));

        var at = await service.SaveAsync(record, w.Admin);
        var view = (await service.ViewAsync(w.Kaeks[0]))!;

        var s = view.Latest!;
        Assert.Equal(("Found", 2, "Admin Cad"), (s.Status, s.Units.Count, s.FetchedBy));
        Assert.Equal(at, s.FetchedUtc);
        var flat = s.Units[1];
        Assert.Equal((0, 1, "1", 30m, "9/1000"), (flat.UnitV!.Value, flat.UnitH!.Value, flat.FloorText, flat.TitleAreaSqm!.Value, flat.PlotShareText));
        Assert.Equal("ΔΟΚΙΜΟΠΟΥΛΟΣ ΝΙΚΟΣ", flat.Owners[0].Name);
        Assert.True(flat.Owners[1].IsUnknown);
        var sale = flat.Acts[0];
        Assert.Equal(("Sale", new DateOnly(2024, 3, 28), 21000m, 63000m), (sale.ActTypeCode, sale.ActDate!.Value, sale.TransactionValue!.Value, sale.ObjectiveValue!.Value));
        Assert.Equal("ΟΔΟΣ ΔΟΚΙΜΗΣ 5, ΑΘΗΝΑ", Assert.Single(sale.Parties).Address);
        Assert.Equal(new DateOnly(1898, 7, 25), flat.Acts[1].ActDate);
        Assert.Equal("090000009", s.Units[0].Owners[0].EntityTaxId);
        Assert.Equal(700m, view.LastSale!.PricePerSqm);
    }

    [Fact]
    public async Task Each_send_is_a_new_snapshot_and_the_history_is_kept()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var service = new CadastreService(new MySqlCadastreStore(_db));
        var first = await service.SaveAsync(For(w.Kaeks[0], Record(Unit(1, 21, new[] { Person("ΠΡΩΤΟΣ ΙΔΙΟΚΤΗΤΗΣ") }))), w.Admin);
        var second = await service.SaveAsync(For(w.Kaeks[0], Record(Unit(1, 21, new[] { Person("ΔΕΥΤΕΡΟΣ ΙΔΙΟΚΤΗΤΗΣ") }))), w.Admin);

        Assert.True(second > first);
        Assert.Equal("ΔΕΥΤΕΡΟΣ ΙΔΙΟΚΤΗΤΗΣ", (await service.ViewAsync(w.Kaeks[0]))!.Latest!.Units[0].Owners[0].Name);
        await using var c = await _db.OpenAsync();
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cadastre_fetch WHERE kaek = @k", new { k = w.Kaeks[0] }));
    }

    [Fact]
    public async Task A_failed_attempt_never_hides_the_last_good_snapshot_and_an_area_run_retries_it()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var service = new CadastreService(new MySqlCadastreStore(_db));
        await service.SaveAsync(For(w.Kaeks[0], Record(Unit(1, 21, new[] { Person() }))), w.Admin);
        await service.SaveAsync(new CadastreRecordInput(w.Kaeks[0], "Error", "The site answered HTTP 500.", null), w.Admin);
        await service.SaveAsync(new CadastreRecordInput(w.Kaeks[1], "NotOperating", null, null), w.Admin);
        await service.SaveAsync(new CadastreRecordInput(w.Kaeks[2], "Error", "signed out", null), w.Admin);

        var view = (await service.ViewAsync(w.Kaeks[0]))!;
        Assert.Equal("Found", view.Latest!.Status);
        Assert.Equal("The site answered HTTP 500.", view.FailedAttempt!.Error);
        Assert.Equal("NotOperating", (await service.ViewAsync(w.Kaeks[1]))!.Latest!.Status);
        var onlyError = (await service.ViewAsync(w.Kaeks[2]))!;
        Assert.Null(onlyError.Latest);
        Assert.NotNull(onlyError.FailedAttempt);

        var (kaeks, fetched) = await service.AreaKaeksAsync(w.Area);
        Assert.Equal(w.Kaeks, kaeks); // real KAEKs only (not the TMP- one), sorted
        Assert.Equal(new[] { w.Kaeks[0], w.Kaeks[1] }, fetched.Order()); // Error-only is not fetched
    }

    [Fact]
    public async Task Other_parcels_of_a_company_come_from_the_latest_snapshots_only()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var service = new CadastreService(new MySqlCadastreStore(_db));
        var tax = "09" + (7_000_000 + _seq);
        await service.SaveAsync(For(w.Kaeks[0], Record(Unit(0, null, new[] { Company(tax) }))), w.Admin);
        await service.SaveAsync(For(w.Kaeks[1], Record(Unit(0, null, new[] { Company(tax) }))), w.Admin);
        await service.SaveAsync(For(w.Kaeks[2], Record(Unit(0, null, new[] { Company(tax) }))), w.Admin);
        await service.SaveAsync(For(w.Kaeks[2], Record(Unit(0, null, new[] { Person() }))), w.Admin); // sold since

        var others = (await service.ViewAsync(w.Kaeks[0]))!.OtherParcels[tax];
        Assert.Equal(new[] { (w.Parcels[1], w.Kaeks[1]) }, others.Select(o => (o.ParcelId, o.Kaek)));
    }

    [Fact]
    public async Task The_database_itself_refuses_a_persons_tax_number()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        await new CadastreService(new MySqlCadastreStore(_db)).SaveAsync(For(w.Kaeks[0], Record(Unit(0, null, new[] { Person() }))), w.Admin);
        await using var c = await _db.OpenAsync();
        var fetch = await c.ExecuteScalarAsync<long>("SELECT MAX(cadastre_fetch_id) FROM cadastre_fetch WHERE kaek = @k", new { k = w.Kaeks[0] });

        var ex = await Assert.ThrowsAsync<MySqlException>(() => c.ExecuteAsync(
            "INSERT INTO cadastre_owner (cadastre_fetch_id, name, owner_kind, entity_tax_id) VALUES (@fetch, 'ΔΟΚΙΜΟΣ', 'Person', '100000001')", new { fetch }));
        Assert.Contains("ck_cadastre_owner_tax", ex.Message);
        await Assert.ThrowsAsync<MySqlException>(() => c.ExecuteAsync(
            "UPDATE cadastre_owner SET entity_tax_id = '100000001' WHERE cadastre_fetch_id = @fetch", new { fetch }));
    }
}

/// <summary>The whole app in this process on a throwaway schema (NADLAN_TEST_MYSQL): the Cadastre API's permissions and 400s.</summary>
public sealed class CadastreAppFixture : IAsyncLifetime
{
    public const string Password = "Cadastre-test-2026!";
    public MySqlDatabase? Db { get; private set; }
    private WebApplicationFactory<Program>? App { get; set; }
    public TestServer Server => App!.Server;
    public IServiceProvider Services => App!.Services;
    public long AdminId { get; private set; }
    public int Area { get; private set; }
    public long[] Parcels { get; private set; } = Array.Empty<long>();
    public const string AdminEmail = "admin-cad@example.gr";
    public const string SalesEmail = "sales-cad@example.gr";
    public static readonly string[] Kaeks = { "970000000001", "970000000002" };

    public async Task InitializeAsync()
    {
        if (ScratchMySql.ServerConnectionString is null) return;
        Db = ScratchMySql.NewDatabase("nadlan_c_");
        await new SchemaMigrator(Db).MigrateAsync();
        await using (var c = await Db.OpenAsync())
        {
            var hash = PasswordHasher.Hash(Password);
            AdminId = await c.ExecuteScalarAsync<long>("INSERT INTO app_user (email, display_name, security_role_id, password_hash) SELECT @e, 'Admin C', security_role_id, @hash FROM security_role WHERE code = 'ADMIN'; SELECT LAST_INSERT_ID();",
                new { e = AdminEmail, hash });
            await c.ExecuteAsync("INSERT INTO app_user (email, display_name, security_role_id, password_hash) SELECT @e, 'Sales C', security_role_id, @hash FROM security_role WHERE code = 'SALES'",
                new { e = SalesEmail, hash });
            var gr = await c.ExecuteScalarAsync<int>("SELECT country_id FROM country WHERE code = 'GR'");
            Area = await c.ExecuteScalarAsync<int>("INSERT INTO geographic_area (country_id, code, name) VALUES (@gr, 'CADT', 'Cadastre test'); SELECT LAST_INSERT_ID();", new { gr });
            var ids = new List<long>();
            foreach (var (k, i) in Kaeks.Select((k, i) => (k, i)))
            {
                var lon = 22.0 + i * 0.001;
                ids.Add(await c.ExecuteScalarAsync<long>("""
                    INSERT INTO parcel (country_id, registry_id, geographic_area_id, geometry)
                    VALUES (@gr, @k, @Area, ST_GeomFromText(@wkt, 4326, 'axis-order=long-lat')); SELECT LAST_INSERT_ID();
                    """, new { gr, k, Area, wkt = FormattableString.Invariant($"POLYGON(({lon} 38, {lon + 0.0005} 38, {lon + 0.0005} 38.0005, {lon} 38.0005, {lon} 38))") }));
            }

            Parcels = ids.ToArray();
        }

        Environment.SetEnvironmentVariable(MySqlDatabase.ConnectionStringEnvVar, Db.ConnectionString);
        App = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production"));
        _ = App.Server;
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
        if (Db is not null) await ScratchMySql.DropAsync(Db);
    }
}

[Collection(InProcessAppCollection.Name)]
public class CadastreEndpointTests : IClassFixture<CadastreAppFixture>
{
    private readonly CadastreAppFixture _f;

    public CadastreEndpointTests(CadastreAppFixture f)
    {
        _f = f;
    }

    private static bool Off => ScratchMySql.ServerConnectionString is null;

    private async Task<(int Status, JsonElement Body, HttpContext Http)> SendAsync(string method, string path, object? body = null, string? cookie = null, string? bearer = null)
    {
        var http = await _f.Server.SendAsync(ctx =>
        {
            ctx.Request.Method = method;
            var q = path.IndexOf('?');
            ctx.Request.Path = q < 0 ? path : path[..q];
            ctx.Request.QueryString = q < 0 ? QueryString.Empty : new QueryString(path[q..]);
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (cookie is not null) { ctx.Request.Headers.Cookie = cookie; }
            if (bearer is null) { ctx.Request.Headers["X-Nadlan-Request"] = "1"; } // the browser's CSRF header; the importer (Bearer) sends none
            if (bearer is not null) { ctx.Request.Headers.Authorization = "Bearer " + bearer; }
            if (body is not null)
            {
                ctx.Request.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                ctx.Request.ContentLength = bytes.Length;
                ctx.Request.Body = new MemoryStream(bytes);
                ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody());
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

    private async Task<string> SignInAsync(string email)
    {
        var (status, body, http) = await SendAsync("POST", "/api/auth/login", new { email, password = CadastreAppFixture.Password });
        Assert.True(status == 200, body.ToString());
        return string.Join("; ", http.Response.Headers.SetCookie.Select(c => c!.Split(';')[0]));
    }

    /// <summary>What the importer holds after "Connect to GreekPlot": an API token of the Admin.</summary>
    private async Task<string> AdminTokenAsync() =>
        await _f.Services.GetRequiredService<UserAdminService>().CreateOwnApiTokenAsync(
            new UserAccess { UserId = _f.AdminId, DisplayName = "Admin C", RoleCode = SecurityRoles.Admin }, "KAEK importer - test");

    [Fact]
    public async Task Everything_is_404_for_anyone_but_an_Admin()
    {
        if (Off) { return; }
        var sales = await SignInAsync(CadastreAppFixture.SalesEmail);

        Assert.Equal(404, (await SendAsync("GET", $"/api/cadastre/kaeks?areaId={_f.Area}", cookie: sales)).Status);
        Assert.Equal(404, (await SendAsync("POST", "/api/cadastre/records", For(CadastreAppFixture.Kaeks[0]), cookie: sales)).Status);
        Assert.Equal(404, (await SendAsync("GET", $"/api/parcels/{_f.Parcels[0]}/cadastre", cookie: sales)).Status);
        Assert.Equal(401, (await SendAsync("GET", $"/api/parcels/{_f.Parcels[0]}/cadastre")).Status);

        var admin = await SignInAsync(CadastreAppFixture.AdminEmail);
        var (status, body, _) = await SendAsync("GET", $"/api/parcels/{_f.Parcels[1]}/cadastre", cookie: admin);
        Assert.Equal(200, status);
        Assert.False(body.GetProperty("researched").GetBoolean()); // this KAEK is never saved by these tests
    }

    private static CadastreRecordInput For(string kaek, CadastreOwnerInput? owner = null) =>
        Record(Unit(0, 120.5m, new[] { owner ?? Company() },
            Sale(new DateOnly(2024, 3, 28), 21000, kaek + "/0/0", Buyer()))) with { Kaek = kaek };

    [Fact]
    public async Task The_importer_saves_a_record_and_the_card_shows_it()
    {
        if (Off) { return; }
        var token = await AdminTokenAsync();
        var kaek = CadastreAppFixture.Kaeks[0];

        var (status, body, _) = await SendAsync("POST", "/api/cadastre/records", For(kaek), bearer: token);
        Assert.True(status == 200, body.ToString());

        var area = (await SendAsync("GET", $"/api/cadastre/kaeks?areaId={_f.Area}", bearer: token)).Body;
        Assert.Equal(2, area.GetProperty("total").GetInt32());
        Assert.Equal(1, area.GetProperty("alreadyFetched").GetInt32());
        Assert.Equal(CadastreAppFixture.Kaeks[1], Assert.Single(area.GetProperty("kaeks").EnumerateArray()).GetString());

        var admin = await SignInAsync(CadastreAppFixture.AdminEmail);
        var card = (await SendAsync("GET", $"/api/parcels/{_f.Parcels[0]}/cadastre", cookie: admin)).Body;
        Assert.True(card.GetProperty("researched").GetBoolean());
        Assert.Equal(21000m, card.GetProperty("lastSale").GetProperty("price").GetDecimal());
        Assert.Equal("2024-03-28", card.GetProperty("lastSale").GetProperty("date").GetString());
        var owner = card.GetProperty("units")[0].GetProperty("owners")[0];
        Assert.Equal("090000009", owner.GetProperty("entityTaxId").GetString());
    }

    [Fact]
    public async Task A_persons_tax_number_gets_400_and_nothing_is_saved()
    {
        if (Off) { return; }
        var token = await AdminTokenAsync();
        var kaek = CadastreAppFixture.Kaeks[1];

        var (status, body, _) = await SendAsync("POST", "/api/cadastre/records", For(kaek, Person(tax: "100000001")), bearer: token);

        Assert.Equal(400, status);
        Assert.Equal("CADASTRE_PERSON_TAX_ID", body.GetProperty("error").GetString());
        await using var c = await _f.Db!.OpenAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cadastre_fetch WHERE kaek = @kaek", new { kaek }));
    }
}
