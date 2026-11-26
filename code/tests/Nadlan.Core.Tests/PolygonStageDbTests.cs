using System.Globalization;
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
using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;
using Nadlan.Core.Security;
using Nadlan.Persistence.MySql;
using Nadlan.Persistence.MySql.Activity;
using Nadlan.Persistence.MySql.Cadastre;
using Nadlan.Persistence.MySql.Editing;
using Nadlan.Persistence.MySql.GeographicAreas;
using Nadlan.Persistence.MySql.Parcels;

namespace Nadlan.Core.Tests;

/// <summary>
/// The SQL side of the Cadastre stage and the review of re-scraped polygons (migration 034: parcel_cadastre_stage, its own
/// table - no row = unknown - and parcel_geometry_proposal) on a throwaway MySQL (NADLAN_TEST_MYSQL). Made-up data only.
/// </summary>
public class MySqlPolygonStageTests : IClassFixture<ScratchMySql>
{
    private readonly MySqlDatabase? _db;
    private static int _seq;

    public MySqlPolygonStageTests(ScratchMySql scratch)
    {
        _db = scratch.Db;
    }

    private static string Wkt(double lon, double lat, double widthM = 20, double heightM = 20)
    {
        var dLon = widthM / (111_320 * Math.Cos(lat * Math.PI / 180));
        var dLat = heightM / 110_540;
        return FormattableString.Invariant($"POLYGON(({lon} {lat}, {lon + dLon} {lat}, {lon + dLon} {lat + dLat}, {lon} {lat + dLat}, {lon} {lat}))");
    }

    private sealed record World(int Area, long Admin, long Contact, double Lon, ParcelCadastreService Service, ParcelService Parcels, MySqlParcelStore Store);

    private async Task<World> SeedAsync()
    {
        var n = Interlocked.Increment(ref _seq);
        await using var c = await _db!.OpenAsync();
        var gr = await c.ExecuteScalarAsync<int>("SELECT country_id FROM country WHERE code = 'GR'");
        var area = await c.ExecuteScalarAsync<int>("INSERT INTO geographic_area (country_id, code, name) VALUES (@gr, @code, @code); SELECT LAST_INSERT_ID();",
            new { gr, code = "STG" + n + Guid.NewGuid().ToString("N")[..6] });
        var admin = await c.ExecuteScalarAsync<long>("""
            INSERT INTO app_user (email, display_name, security_role_id) SELECT @e, 'Admin Stage', security_role_id FROM security_role WHERE code = 'ADMIN';
            SELECT LAST_INSERT_ID();
            """, new { e = $"stage{n}-{Guid.NewGuid():N}@example.gr" });
        var contact = await c.ExecuteScalarAsync<long>("INSERT INTO contact (display_name) VALUES ('Stage contact'); SELECT LAST_INSERT_ID();");
        var store = new MySqlParcelStore(_db);
        var activity = new MySqlActivityLog(_db);
        var parcels = new ParcelService(store, new MySqlGeographicAreaStore(_db), new MySqlCountryStore(_db), activity);
        var service = new ParcelCadastreService(new MySqlParcelCadastreStore(_db), store, parcels, new MySqlEditVersionStore(_db), activity);
        return new World(area, admin, contact, 24 + n * 0.05, service, parcels, store);
    }

    private static GeoPolygon Polygon(string wkt) => GeoPolygon.FromWkt(wkt);

    private async Task<long> CreateAsync(World w, string kaek, double lon, string? stage = null, DateTime? seen = null, double heightM = 20, double widthM = 20)
    {
        var r = await w.Parcels.CreateAsync(new CreateParcelRequest
        {
            RegistryId = kaek, GeographicAreaId = w.Area, Geometry = Polygon(Wkt(lon, 38, widthM, heightM)), CadastralStage = stage, CadastralStageSeenUtc = seen,
            CreatedByUserId = w.Admin, AcceptOverlaps = true,
        });
        Assert.Equal(CreateParcelOutcome.Created, r.Outcome);
        return r.ParcelId!.Value;
    }

    private static string Kaek(int n, int i) => $"97{n:D4}{i:D6}";

    [Fact]
    public async Task The_stage_lives_in_its_own_table_and_no_row_means_unknown()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var seen = new DateTime(2026, 11, 26, 9, 15, 0, DateTimeKind.Utc);
        var imported = await CreateAsync(w, Kaek(_seq, 1), w.Lon, "SurveyPost", seen);
        var handMade = await CreateAsync(w, Kaek(_seq, 2), w.Lon + 0.001);

        var p = (await w.Store.GetAsync(imported))!;
        Assert.Equal(("SurveyPost", seen, "Polygon", "GETPSTKG"), (p.CadastralStage, p.CadastralStageSeenUtc!.Value, p.CadastralStageSource, p.CadastralStageRequest));
        var h = (await w.Store.GetAsync(handMade))!;
        Assert.Null(h.CadastralStage);
        await using var c = await _db.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM parcel_cadastre_stage WHERE parcel_id IN @ids", new { ids = new[] { imported, handMade } }));
        Assert.Equal(Kaek(_seq, 1), await c.ExecuteScalarAsync<string>("SELECT kaek FROM parcel_cadastre_stage WHERE parcel_id = @imported", new { imported }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'parcel' AND column_name LIKE '%stage%'")); // parcel unchanged

        // The map filter (LEFT JOIN): Unknown = no row.
        var scope = AccessScope.Everything;
        async Task<long[]> Ids(params string[] stages) =>
            (await w.Store.QueryAsync(new ParcelQuery { GeographicAreaIds = new[] { w.Area }, Stages = stages, Scope = scope })).Select(x => x.ParcelId).ToArray();
        Assert.Equal(new[] { handMade }, await Ids(CadastralStages.Unknown));
        Assert.Equal(new[] { imported }, await Ids("SurveyPost"));
        Assert.Equal(new[] { imported, handMade }, await Ids("SurveyPost", CadastralStages.Unknown));
        Assert.Empty(await Ids("InOperation"));
        Assert.Equal(1, await w.Store.CountAsync(new ParcelQuery { GeographicAreaIds = new[] { w.Area }, Stages = new[] { "Unknown" }, Scope = scope }));
        Assert.Equal(1, (await w.Store.CountByKindAsync(new ParcelQuery { GeographicAreaIds = new[] { w.Area }, Stages = new[] { "SurveyPost" }, Scope = scope })).Values.Sum());
        Assert.Single(await w.Store.ListAnchorsAsync(new ParcelQuery { GeographicAreaIds = new[] { w.Area }, Stages = new[] { "SurveyPost" }, Scope = scope }));

        // Deleting the Parcel takes its stage row with it.
        Assert.Equal(ParcelDeleteOutcome.Deleted, await w.Store.DeleteAsync(imported));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM parcel_cadastre_stage WHERE parcel_id = @imported", new { imported }));
    }

    [Fact]
    public async Task The_newest_sighting_wins_the_lawyer_run_sets_it_by_KAEK_and_the_parcel_row_is_not_touched()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var day1 = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);
        var id = await CreateAsync(w, Kaek(_seq, 1), w.Lon, "SurveyPost", day1);
        var other = await CreateAsync(w, Kaek(_seq, 2), w.Lon + 0.001);
        await using var c = await _db.OpenAsync();
        var updatedBefore = await c.ExecuteScalarAsync<DateTime>("SELECT updated_utc FROM parcel WHERE parcel_id = @id", new { id });

        Assert.False(await w.Service.RecordStageAsync(id, "InOperation", day1.AddDays(-1), CadastralStages.Polygon)); // older
        Assert.Equal("SurveyPost", (await w.Store.GetAsync(id))!.CadastralStage);

        var cadastre = new CadastreService(new MySqlCadastreStore(_db), w.Service);
        await cadastre.SaveAsync(new CadastreRecordInput(Kaek(_seq, 1), "Found", null,
            new[] { new CadastreUnitInput(0, 0, "Found", null, null, null, null, null, null, Array.Empty<CadastreOwnerInput>(), Array.Empty<CadastreActInput>()) }), w.Admin);
        await cadastre.SaveAsync(new CadastreRecordInput(Kaek(_seq, 2), "NotOperating", null, null), w.Admin);
        await cadastre.SaveAsync(new CadastreRecordInput(Kaek(_seq, 2), "Error", "signed out", null), w.Admin); // says nothing

        var p = (await w.Store.GetAsync(id))!;
        Assert.Equal(("InOperation", "Lawyer", (string?)null), (p.CadastralStage, p.CadastralStageSource, p.CadastralStageRequest));
        Assert.Equal("NotOperating", (await w.Store.GetAsync(other))!.CadastralStage);
        Assert.Equal(updatedBefore, await c.ExecuteScalarAsync<DateTime>("SELECT updated_utc FROM parcel WHERE parcel_id = @id", new { id }));
        var history = (await c.QueryAsync<(string Summary, long? UserId)>(
            "SELECT summary, user_id FROM activity WHERE entity_type = 'Parcel' AND entity_id = @id ORDER BY activity_id", new { id })).ToList();
        Assert.Contains(history, h => h.Summary.StartsWith("Cadastre stage: Survey post → In operation (lawyer, seen ", StringComparison.Ordinal) && h.UserId == w.Admin);
        Assert.Contains(history, h => h.Summary.Contains("created, Cadastre stage: Survey post (map)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Proposals_are_filed_superseded_one_pending_per_parcel_accepted_as_an_edit_and_rejected()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var id = await CreateAsync(w, Kaek(_seq, 1), w.Lon, "InOperation", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        var store = new MySqlParcelCadastreStore(_db);
        CadastreCheckInput Found(double heightM) =>
            new(Kaek(_seq, 1), "InOperation", new DateTime(2026, 11, 26, 11, 0, 0, DateTimeKind.Utc), Polygon(Wkt(w.Lon, 38, 20, heightM)), 440.5m, "Found");

        var first = await w.Service.CheckAsync(id, Found(22), w.Admin);
        var second = await w.Service.CheckAsync(id, Found(24), w.Admin);
        Assert.Equal((CadastreCheckOutcome.ProposalFiled, CadastreCheckOutcome.ProposalFiled), (first.Outcome, second.Outcome));
        Assert.Equal(ProposalStatuses.Superseded, (await store.GetAsync(first.ProposalId!.Value))!.Status);

        await using var c = await _db.OpenAsync();
        var dup = await Assert.ThrowsAsync<MySqlException>(() => c.ExecuteAsync("""
            INSERT INTO parcel_geometry_proposal (parcel_id, kind, parcel_updated_utc, seen_utc) VALUES (@id, 'NotFound', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3))
            """, new { id }));
        Assert.Contains("uq_parcel_geometry_proposal_pending", dup.Message); // the database keeps one pending per Parcel

        var listed = Assert.Single(await store.ListAsync(w.Area, ProposalStatuses.Pending, 100));
        Assert.Equal((second.ProposalId!.Value, Kaek(_seq, 1), false, false, false), (listed.ProposalId, listed.RegistryId, listed.HasAssets, listed.InWorkRequest, listed.ParcelEditedSince));
        Assert.Null(listed.Geometry); // lists carry no polygon
        Assert.InRange((double)listed.DifferenceSqm!.Value, 75, 85);
        Assert.NotNull((await store.GetAsync(second.ProposalId!.Value))!.Geometry);

        var before = (await w.Store.GetAsync(id))!.Geometry.ToWkt();
        Assert.Equal(CreateParcelOutcome.Updated, (await w.Service.AcceptAsync(second.ProposalId!.Value, w.Admin, false)).Outcome);
        var after = (await w.Store.GetAsync(id))!;
        Assert.Equal(440.5m, after.OfficialAreaSqm);
        Assert.InRange(GeoDifference.SymmetricDifferenceSqm(after.Geometry, Polygon(Wkt(w.Lon, 38, 20, 24))), 0, 0.01);
        var history = await c.ExecuteScalarAsync<string>("""
            SELECT JSON_UNQUOTE(JSON_EXTRACT(metadata_json, '$.oldWkt')) FROM activity
            WHERE entity_type = 'Parcel' AND entity_id = @id AND action_type = 'ParcelGeometryChanged' ORDER BY activity_id DESC LIMIT 1
            """, new { id });
        Assert.InRange(GeoDifference.SymmetricDifferenceSqm(Polygon(history!), Polygon(before)), 0, 0.01); // the old polygon kept
        var decided = (await store.GetAsync(second.ProposalId!.Value))!;
        Assert.Equal((ProposalStatuses.Accepted, w.Admin, "Admin Stage"), (decided.Status, decided.DecidedByUserId!.Value, decided.DecidedByName));

        // Now ours is the Cadastre's: the same again is unchanged. Another KAEK there: filed, then kept as is.
        Assert.Equal(CadastreCheckOutcome.Unchanged, (await w.Service.CheckAsync(id, Found(24), w.Admin)).Outcome);
        var mismatch = await w.Service.CheckAsync(id, Found(24) with { KaekSeen = Kaek(_seq, 9) }, w.Admin);
        await w.Service.RejectAsync(mismatch.ProposalId!.Value, w.Admin);
        Assert.Equal(ProposalStatuses.Rejected, (await store.GetAsync(mismatch.ProposalId!.Value))!.Status);
        Assert.Equal(mismatch.ProposalId, (await w.Service.CheckAsync(id, Found(24) with { KaekSeen = Kaek(_seq, 9) }, w.Admin)).ProposalId); // not filed again

        // Deleting the Parcel takes its proposals with it.
        Assert.Equal(ParcelDeleteOutcome.Deleted, await w.Store.DeleteAsync(id));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM parcel_geometry_proposal WHERE parcel_id = @id", new { id }));
    }

    [Fact]
    public async Task Accept_small_leaves_parcels_with_Assets_or_in_a_work_request_and_applies_the_overlap_rule()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var seen = new DateTime(2026, 11, 26, 11, 0, 0, DateTimeKind.Utc);
        var ids = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await CreateAsync(w, Kaek(_seq, i + 1), w.Lon + i * 0.005, "InOperation", seen.AddDays(-10), 100, 100));
        }

        await using var c = await _db.OpenAsync();
        var asset = await c.ExecuteScalarAsync<long>("""
            INSERT INTO asset (managing_contact_id, asset_status_id) SELECT @contact, asset_status_id FROM asset_status WHERE code = 'FOR_SALE';
            SELECT LAST_INSERT_ID();
            """, new { contact = w.Contact });
        await c.ExecuteAsync("INSERT INTO asset_parcel (asset_id, parcel_id) VALUES (@asset, @p)", new { asset, p = ids[1] });
        var work = await c.ExecuteScalarAsync<long>("""
            INSERT INTO work_request (name, contractor_contact_id, link_token_hash, link_token_sealed, link_expires_utc, created_by_user_id)
            VALUES ('Stage test', @contact, @hash, 'sealed', UTC_TIMESTAMP(3) + INTERVAL 1 DAY, @admin); SELECT LAST_INSERT_ID();
            """, new { contact = w.Contact, hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), admin = w.Admin });
        await c.ExecuteAsync("INSERT INTO work_request_parcel (work_request_id, parcel_id, status) VALUES (@work, @p, 'OPEN_OFFERS')", new { work, p = ids[2] });
        // The fourth grows into a neighbour drawn by hand: accepting it needs the overlap confirmation.
        await w.Parcels.CreateAsync(new CreateParcelRequest
        {
            RegistryId = Kaek(_seq, 8), GeographicAreaId = w.Area, Geometry = Polygon(Wkt(w.Lon + 3 * 0.005, 38 + 100.5 / 110_540, 20, 5)), AcceptOverlaps = true,
        });

        foreach (var (pid, i) in ids.Select((p, i) => (p, i)))
        {
            var r = await w.Service.CheckAsync(pid, new CadastreCheckInput(Kaek(_seq, i + 1), "InOperation", seen,
                Polygon(Wkt(w.Lon + i * 0.005, 38, 100, 101)), 10_100m, "Found"), w.Admin); // 100 m² of 10,000 = 1 %
            Assert.Equal(CadastreCheckOutcome.ProposalFiled, r.Outcome);
        }

        var result = await w.Service.AcceptSmallAsync(w.Area, w.Admin);

        Assert.True(result.Accepted == 1, string.Join("; ", result.Skipped.Select(s => s.ParcelId + ": " + s.Reason)));
        Assert.Equal(10_100m, (await w.Store.GetAsync(ids[0]))!.OfficialAreaSqm);
        Assert.Equal(new[] { (ids[1], "has Assets"), (ids[2], "in a work request"), (ids[3], "overlaps other Parcels") },
            result.Skipped.Select(s => (s.ParcelId, s.Reason)).OrderBy(s => s.ParcelId));
        Assert.Equal(3, (await new MySqlParcelCadastreStore(_db).ListAsync(w.Area, ProposalStatuses.Pending, 100)).Count);
    }
}

/// <summary>The whole app in this process on a throwaway schema (NADLAN_TEST_MYSQL): who may call what, and staff only.</summary>
public sealed class PolygonStageAppFixture : IAsyncLifetime
{
    public const string Password = "Stage-test-2026!";
    public const string AdminEmail = "admin-stage@example.gr";
    public const string SalesEmail = "sales-stage@example.gr";
    public MySqlDatabase? Db { get; private set; }
    private WebApplicationFactory<Program>? App { get; set; }
    public TestServer Server => App!.Server;
    public IServiceProvider Services => App!.Services;
    public long AdminId { get; private set; }
    public int Area { get; private set; }
    public long StagedParcel { get; private set; }
    public long Asset { get; private set; }
    public const string StagedKaek = "960000000001";

    public async Task InitializeAsync()
    {
        if (ScratchMySql.ServerConnectionString is null) return;
        Db = ScratchMySql.NewDatabase("nadlan_s_");
        await new SchemaMigrator(Db).MigrateAsync();
        await using (var c = await Db.OpenAsync())
        {
            var hash = PasswordHasher.Hash(Password);
            AdminId = await c.ExecuteScalarAsync<long>("INSERT INTO app_user (email, display_name, security_role_id, password_hash) SELECT @e, 'Admin S', security_role_id, @hash FROM security_role WHERE code = 'ADMIN'; SELECT LAST_INSERT_ID();",
                new { e = AdminEmail, hash });
            await c.ExecuteAsync("INSERT INTO app_user (email, display_name, security_role_id, password_hash) SELECT @e, 'Sales S', security_role_id, @hash FROM security_role WHERE code = 'SALES'",
                new { e = SalesEmail, hash });
            var gr = await c.ExecuteScalarAsync<int>("SELECT country_id FROM country WHERE code = 'GR'");
            Area = await c.ExecuteScalarAsync<int>("INSERT INTO geographic_area (country_id, code, name) VALUES (@gr, 'STGT', 'Stage test'); SELECT LAST_INSERT_ID();", new { gr });
            StagedParcel = await c.ExecuteScalarAsync<long>("""
                INSERT INTO parcel (country_id, registry_id, geographic_area_id, geometry, official_area_sqm)
                VALUES (@gr, @k, @Area, ST_GeomFromText('POLYGON((22.5 38, 22.5005 38, 22.5005 38.0005, 22.5 38.0005, 22.5 38))', 4326, 'axis-order=long-lat'), 2400);
                SELECT LAST_INSERT_ID();
                """, new { gr, k = StagedKaek, Area });
            await c.ExecuteAsync("""
                INSERT INTO parcel_cadastre_stage (parcel_id, kaek, stage, seen_utc, source, request_seen)
                VALUES (@StagedParcel, @k, 'InOperation', '2026-11-26 08:00:00', 'Polygon', 'GETPST')
                """, new { StagedParcel, k = StagedKaek });
            var contact = await c.ExecuteScalarAsync<long>("INSERT INTO contact (display_name) VALUES ('Agent S'); SELECT LAST_INSERT_ID();");
            Asset = await c.ExecuteScalarAsync<long>("""
                INSERT INTO asset (managing_contact_id, asset_status_id, ask_price, currency_code)
                SELECT @contact, asset_status_id, 150000, 'EUR' FROM asset_status WHERE code = 'FOR_SALE'; SELECT LAST_INSERT_ID();
                """, new { contact });
            await c.ExecuteAsync("INSERT INTO asset_parcel (asset_id, parcel_id) VALUES (@Asset, @StagedParcel)", new { Asset, StagedParcel });
            await c.ExecuteAsync("""
                INSERT INTO parcel_geometry_proposal (parcel_id, kind, kaek_seen, stage_seen, parcel_updated_utc, seen_utc)
                VALUES (@StagedParcel, 'KaekMismatch', '960000000009', 'InOperation', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3))
                """, new { StagedParcel });
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
public class PolygonStageEndpointTests : IClassFixture<PolygonStageAppFixture>
{
    private readonly PolygonStageAppFixture _f;

    public PolygonStageEndpointTests(PolygonStageAppFixture f)
    {
        _f = f;
    }

    private static bool Off => ScratchMySql.ServerConnectionString is null;

    private async Task<(int Status, string Text, JsonElement Body, HttpContext Http)> SendAsync(string method, string path, object? body = null, string? cookie = null, string? bearer = null)
    {
        var http = await _f.Server.SendAsync(ctx =>
        {
            ctx.Request.Method = method;
            var q = path.IndexOf('?');
            ctx.Request.Path = q < 0 ? path : path[..q];
            ctx.Request.QueryString = q < 0 ? QueryString.Empty : new QueryString(path[q..]);
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (cookie is not null) { ctx.Request.Headers.Cookie = cookie; }
            if (bearer is null) { ctx.Request.Headers["X-Nadlan-Request"] = "1"; }
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
        return (http.Response.StatusCode, text, json, http);
    }

    private sealed class HasBody : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private async Task<string> SignInAsync(string email)
    {
        var (status, text, _, http) = await SendAsync("POST", "/api/auth/login", new { email, password = PolygonStageAppFixture.Password });
        Assert.True(status == 200, text);
        return string.Join("; ", http.Response.Headers.SetCookie.Select(c => c!.Split(';')[0]));
    }

    private async Task<string> AdminTokenAsync() =>
        await _f.Services.GetRequiredService<UserAdminService>().CreateOwnApiTokenAsync(
            new UserAccess { UserId = _f.AdminId, DisplayName = "Admin S", RoleCode = SecurityRoles.Admin }, "KAEK importer - test");

    private static double[][][] Ring(double lon, double lat, double size) => new[]
    {
        new[] { new[] { lon, lat }, new[] { lon + size, lat }, new[] { lon + size, lat + size }, new[] { lon, lat + size }, new[] { lon, lat } },
    };

    [Fact]
    public async Task Checks_and_the_review_are_404_for_anyone_but_an_Admin_and_the_importer_saves_the_stage()
    {
        if (Off) { return; }
        var sales = await SignInAsync(PolygonStageAppFixture.SalesEmail);
        var check = new { outcome = "NotFound", cadastralStage = "InOperation", seenUtc = DateTime.UtcNow };
        Assert.Equal(404, (await SendAsync("PUT", $"/api/parcels/{_f.StagedParcel}/cadastre-check", check, cookie: sales)).Status);
        Assert.Equal(404, (await SendAsync("GET", "/api/parcel-proposals", cookie: sales)).Status);
        Assert.Equal(404, (await SendAsync("POST", "/api/parcel-proposals/1/reject", cookie: sales)).Status);
        Assert.Equal(401, (await SendAsync("GET", "/api/parcel-proposals")).Status);

        // The importer (an Admin's token): a new parcel with its stage, then a check of it.
        var token = await AdminTokenAsync();
        var created = await SendAsync("POST", "/api/parcels", new
        {
            registryId = "960000000002", geographicAreaId = _f.Area, coordinates = Ring(22.6, 38, 0.0005), officialAreaSqm = 2400, acceptOverlaps = true,
            cadastralStage = "SurveyPost", cadastralStageSeenUtc = "2026-11-26T09:00:00Z", cadastralStageRequest = "GETPSTKG",
        }, bearer: token);
        Assert.True(created.Status == 200, created.Text);
        var id = created.Body.GetProperty("parcelId").GetInt64();
        var checkedNow = await SendAsync("PUT", $"/api/parcels/{id}/cadastre-check", new
        {
            outcome = "Found", kaekSeen = "960000000002", cadastralStage = "InOperation", request = "GETPST", seenUtc = "2026-11-26T10:00:00Z",
            rings = Ring(22.6, 38, 0.0005), areaSqm = 2400,
        }, bearer: token);
        Assert.True(checkedNow.Status == 200, checkedNow.Text);
        Assert.Equal("StageUpdated", checkedNow.Body.GetProperty("outcome").GetString());

        var admin = await SignInAsync(PolygonStageAppFixture.AdminEmail);
        var card = (await SendAsync("GET", $"/api/parcels/{id}", cookie: admin)).Body.GetProperty("cadastralStage");
        Assert.Equal(("InOperation", "Polygon", "GETPST"), (card.GetProperty("stage").GetString(), card.GetProperty("source").GetString(), card.GetProperty("request").GetString()));
        var list = (await SendAsync("GET", $"/api/parcel-proposals?areaId={_f.Area}", cookie: admin)).Body;
        Assert.Equal("KaekMismatch", list.GetProperty("items")[0].GetProperty("kind").GetString());

        // The map's stage filter.
        var unknown = (await SendAsync("GET", $"/api/parcels?areaIds={_f.Area}&stages=Unknown", cookie: admin)).Body;
        Assert.Equal(0, unknown.GetProperty("count").GetInt32());
        var operating = (await SendAsync("GET", $"/api/parcels?areaIds={_f.Area}&stages=InOperation", cookie: admin)).Body;
        Assert.Equal(2, operating.GetProperty("count").GetInt32());
        Assert.Equal(400, (await SendAsync("GET", $"/api/parcels?stages=Orange", cookie: admin)).Status);
    }

    [Fact]
    public async Task The_stage_and_proposals_never_appear_in_presentations_or_public_links()
    {
        if (Off) { return; }
        var admin = await SignInAsync(PolygonStageAppFixture.AdminEmail);
        var presentation = await SendAsync("GET", $"/api/presentation?assetIds={_f.Asset}", cookie: admin);
        Assert.Equal(200, presentation.Status);
        Assert.Equal(1, presentation.Body.GetProperty("items").GetArrayLength());

        // A public link's data is built the same way, as its creator, For sale only and without numbers.
        var me = new UserAccess { UserId = _f.AdminId, DisplayName = "Admin S", RoleCode = SecurityRoles.Admin };
        var (items, _, _) = await Nadlan.Host.Presentation.PresentationEndpoints.BuildAsync(_f.Services, me, null, new[] { _f.Asset }, forSaleOnly: true,
            CancellationToken.None, hideNumbers: true);
        var linkJson = JsonSerializer.Serialize(items, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Single(items);

        foreach (var text in new[] { presentation.Text, linkJson })
        {
            foreach (var word in new[] { "cadastral", "InOperation", "GETPST", "proposal", "960000000009" })
            {
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
