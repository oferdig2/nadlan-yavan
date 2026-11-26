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

        var at = (await service.SaveAsync(record, w.Admin)).FetchedUtc;
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
        Assert.Equal("090000009", s.Units[0].Owners[0].TaxId);
        Assert.Equal(700m, view.LastSale!.PricePerSqm);
    }

    [Fact]
    public async Task Each_send_is_a_new_snapshot_and_the_history_is_kept()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var service = new CadastreService(new MySqlCadastreStore(_db));
        var first = (await service.SaveAsync(For(w.Kaeks[0], Record(Unit(1, 21, new[] { Person("ΠΡΩΤΟΣ ΙΔΙΟΚΤΗΤΗΣ") }))), w.Admin)).FetchedUtc;
        var second = (await service.SaveAsync(For(w.Kaeks[0], Record(Unit(1, 21, new[] { Person("ΔΕΥΤΕΡΟΣ ΙΔΙΟΚΤΗΤΗΣ") }))), w.Admin)).FetchedUtc;

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
    public async Task Every_V2_field_is_saved_and_read_back()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var service = new CadastreService(new MySqlCadastreStore(_db));
        var details = new CadastrePersonDetails
        {
            FatherName = "ΔΟΚΙΜΟΣ ΠΑΤΗΡ", MotherName = "ΔΟΚΙΜΗ ΜΗΤΗΡ", BirthDate = new DateOnly(1970, 1, 1), BirthPlace = "ΑΘΗΝΑ",
            IdCardNumber = "ΧΧ000000", IdCardDate = new DateOnly(2001, 2, 3), IdCardAuthority = "Τ.Α. ΔΟΚΙΜΩΝ", RightType = "ΨΙΛΗ", Source = "RecordPage",
        };
        var extractAct = new CadastreActInput("Extract", "5413", 2024, "ΚΓ ΔΟΚΙΜΩΝ", "Auction", "ΠΩΛΗΣΗ ΔΙΑ ΠΛΕΙΣΤΗΡΙΑΣΜΟΥ", new DateOnly(2024, 7, 10), "4876",
            null, null, null, new[] { Buyer(tax: "100000004") with { Details = details with { Source = "DescriptiveExtract", BirthDate = new DateOnly(1, 1, 1) } } })
        {
            RegDate = new DateOnly(2024, 7, 18), Section = "Later", RightText = "ΚΥΡΙΟΤΗΤΑ ΨΙΛΗ", DocumentText = "ΣΥΜΒΟΛΑΙΟΓΡΑΦΙΚΟ ΕΓΓΡΑΦΟ, 4876 - 10/7/2024",
            IssuerText = "ΔΟΚΙΜΑΣΤΗΣ ΣΥΜΒΟΛΑΙΟΓΡΑΦΟΣ, ΔΟΚΙΜΙ", RegStatusText = "ΟΡΙΣΤΙΚΗ ΚΑΤΑΧΩΡΙΣΗ",
        };
        var unit = Unit(0, 2052.45m, new[] { Person(tax: "100000003") with { Details = details } }, extractAct) with
        {
            MeasuredAreaSqm = 2048.10m, RemarksText = "Ο.Τ. 18", OtText = "18", ExtractStatus = "Read", PropertyKind = "ΓΕΩΤΕΜΑΧΙΟ",
            OfficeText = "ΚΓ ΔΟΚΙΜΩΝ", MapSheet = "6411/7", NeighbourKaeks = "990000000002 990000000003",
        };
        var over = new CadastreUnitInput(0, 1, "NotRead", "Not read: over the run's unit cap.", "1", 80m, null, "9/1000", null, null, null);
        await service.SaveAsync(For(w.Kaeks[0], Record(unit, over)), w.Admin);

        var view = (await service.ViewAsync(w.Kaeks[0]))!;
        var u = view.Latest!.Units[0];
        Assert.Equal((2048.10m, "Ο.Τ. 18", "18", "Read", "ΓΕΩΤΕΜΑΧΙΟ", "6411/7"), (u.MeasuredAreaSqm!.Value, u.RemarksText, u.OtText, u.ExtractStatus, u.PropertyKind, u.MapSheet));
        Assert.Equal("990000000002 990000000003", u.NeighbourKaeks);
        var o = u.Owners[0];
        Assert.Equal("100000003", o.TaxId);
        Assert.Equal(details, o.Details);
        var a = u.Acts[0];
        Assert.Equal(("Extract", new DateOnly(2024, 7, 18), "Later", "ΟΡΙΣΤΙΚΗ ΚΑΤΑΧΩΡΙΣΗ"), (a.Source, a.RegDate!.Value, a.Section, a.RegStatusText));
        var p = a.Parties[0];
        Assert.Equal(("100000004", (DateOnly?)null, "DescriptiveExtract"), (p.TaxId, p.Details.BirthDate, p.Details.Source)); // 01/01/0001 = NULL
        Assert.Equal("NotRead", view.Latest.Units[1].Status);
        // The extract's auction, without a summary: the card's last sale (no price).
        Assert.Equal(("Auction", "Extract", (decimal?)null), (view.LastSale!.ActTypeCode, view.LastSale.Source, view.LastSale.Price));
    }

    [Fact]
    public async Task Other_parcels_of_a_person_come_by_tax_number()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var service = new CadastreService(new MySqlCadastreStore(_db));
        var tax = "1" + (40_000_000 + _seq);
        await service.SaveAsync(For(w.Kaeks[0], Record(Unit(0, null, new[] { Person("ΔΟΚΙΜΟΠΟΥΛΟΣ ΓΙΩΡΓΟΣ", tax) }))), w.Admin);
        await service.SaveAsync(For(w.Kaeks[1], Record(Unit(0, null, new[] { Person("ΔΟΚΙΜΟΠΟΥΛΟΣ ΓΕΩΡΓΙΟΣ", tax) }))), w.Admin); // spelled otherwise: same ΑΦΜ

        var others = (await service.ViewAsync(w.Kaeks[0]))!.OtherParcels[tax];
        Assert.Equal(new[] { (w.Parcels[1], w.Kaeks[1]) }, others.Select(x => (x.ParcelId, x.Kaek)));
    }

    [Fact]
    public async Task The_parcels_stage_is_kept_in_its_own_table_and_the_newest_sighting_wins()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var service = new CadastreService(new MySqlCadastreStore(_db));
        await using var c = await _db.OpenAsync();
        async Task<string?> Stage(long parcel) =>
            await c.ExecuteScalarAsync<string?>("SELECT CONCAT(stage, '/', source) FROM parcel_cadastre_stage WHERE parcel_id = @parcel", new { parcel });

        Assert.Null(await Stage(w.Parcels[0])); // no row = unknown
        await service.SaveAsync(new CadastreRecordInput(w.Kaeks[0], "NotOperating", null, null), w.Admin);
        Assert.Equal("NotOperating/Lawyer", await Stage(w.Parcels[0]));
        await service.SaveAsync(For(w.Kaeks[0], Record(Unit(0, null, new[] { Person() }))), w.Admin);
        Assert.Equal("InOperation/Lawyer", await Stage(w.Parcels[0]));
        await service.SaveAsync(new CadastreRecordInput(w.Kaeks[0], "Error", "signed out", null), w.Admin); // an error says nothing
        Assert.Equal("InOperation/Lawyer", await Stage(w.Parcels[0]));
        Assert.Equal(new[] { "Cadastre stage: Not operating (lawyer service)", "Cadastre stage: Not operating → In operation (lawyer service)" },
            await c.QueryAsync<string>("SELECT summary FROM activity WHERE entity_type = 'Parcel' AND entity_id = @p AND action_type = 'CadastreStageChanged' ORDER BY activity_id",
                new { p = w.Parcels[0] }));

        // A newer sighting from the map (the polygon importer) is not overwritten by an older lawyer record.
        await c.ExecuteAsync("""
            INSERT INTO parcel_cadastre_stage (parcel_id, kaek, stage, seen_utc, source, request_seen)
            VALUES (@p, @k, 'SurveyPost', UTC_TIMESTAMP(3) + INTERVAL 1 DAY, 'Polygon', 'GETPSTKG')
            """, new { p = w.Parcels[1], k = w.Kaeks[1] });
        await service.SaveAsync(For(w.Kaeks[1], Record(Unit(0, null, new[] { Person() }))), w.Admin);
        Assert.Equal("SurveyPost/Polygon", await Stage(w.Parcels[1]));
        Assert.Null(await Stage(w.Parcels[3])); // the provisional parcel has no such KAEK
    }

    [Fact]
    public async Task The_database_keeps_a_persons_tax_number_now()
    {
        // V2 (owner, 2026-11-26): migration 035 dropped ck_cadastre_owner_tax / ck_cadastre_act_party_tax and renamed the column.
        if (_db is null) { return; }
        var w = await SeedAsync();
        await new CadastreService(new MySqlCadastreStore(_db)).SaveAsync(For(w.Kaeks[0], Record(Unit(0, null, new[] { Person() }))), w.Admin);
        await using var c = await _db.OpenAsync();
        var fetch = await c.ExecuteScalarAsync<long>("SELECT MAX(cadastre_fetch_id) FROM cadastre_fetch WHERE kaek = @k", new { k = w.Kaeks[0] });

        await c.ExecuteAsync("INSERT INTO cadastre_owner (cadastre_fetch_id, name, owner_kind, tax_id) VALUES (@fetch, 'ΔΟΚΙΜΟΣ', 'Person', '100000001')", new { fetch });
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cadastre_owner WHERE cadastre_fetch_id = @fetch AND tax_id = '100000001'", new { fetch }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM information_schema.check_constraints
            WHERE constraint_schema = DATABASE() AND constraint_name IN ('ck_cadastre_owner_tax', 'ck_cadastre_act_party_tax')
            """));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM information_schema.statistics
            WHERE table_schema = DATABASE() AND table_name = 'cadastre_owner' AND index_name = 'ix_cadastre_owner_tax' AND column_name = 'tax_id'
            """)); // the index followed the rename
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

            // V2 tests: KAEKs in another area, so the area counts above never change with them.
            var area2 = await c.ExecuteScalarAsync<int>("INSERT INTO geographic_area (country_id, code, name) VALUES (@gr, 'CADT2', 'Cadastre test 2'); SELECT LAST_INSERT_ID();", new { gr });
            var other = new List<long>();
            foreach (var (k, i) in OtherKaeks.Select((k, i) => (k, i)))
            {
                var lon = 22.1 + i * 0.001;
                other.Add(await c.ExecuteScalarAsync<long>("""
                    INSERT INTO parcel (country_id, registry_id, geographic_area_id, geometry)
                    VALUES (@gr, @k, @area2, ST_GeomFromText(@wkt, 4326, 'axis-order=long-lat')); SELECT LAST_INSERT_ID();
                    """, new { gr, k, area2, wkt = FormattableString.Invariant($"POLYGON(({lon} 38, {lon + 0.0005} 38, {lon + 0.0005} 38.0005, {lon} 38.0005, {lon} 38))") }));
            }

            OtherParcels = other.ToArray();
        }

        Environment.SetEnvironmentVariable(MySqlDatabase.ConnectionStringEnvVar, Db.ConnectionString);
        // The documents go to a storage in memory (never the real bucket); its links are made-up URLs.
        App = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production").ConfigureTestServices(s =>
        {
            s.AddSingleton<Nadlan.Core.Files.IObjectStorage>(Storage);
            s.AddSingleton<Nadlan.Core.Files.IFileUrlProvider>(new FakeUrls());
        }));
        _ = App.Server;
    }

    public static readonly string[] OtherKaeks = { "970000000101", "970000000102", "970000000103" };
    public long[] OtherParcels { get; private set; } = Array.Empty<long>();
    public MemoryStorage Storage { get; } = new();

    /// <summary>Object storage in memory: what was put, by key.</summary>
    public sealed class MemoryStorage : Nadlan.Core.Files.IObjectStorage
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Objects = new();
        public bool Fail;
        public bool IsConfigured => true;
        public Task<string> StartMultipartUploadAsync(string key, string contentType, string contentDisposition, CancellationToken ct = default) => throw new NotSupportedException();
        public string GetPartUploadUrl(string key, string uploadId, int partNumber, TimeSpan lifetime) => throw new NotSupportedException();
        public Task<IReadOnlyList<Nadlan.Core.Files.UploadedPart>?> ListUploadedPartsAsync(string key, string uploadId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CompleteMultipartUploadAsync(string key, string uploadId, IReadOnlyList<Nadlan.Core.Files.UploadedPart> parts, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long?> GetObjectSizeAsync(string key, CancellationToken ct = default) => Task.FromResult(Objects.TryGetValue(key, out var b) ? (long?)b.Length : null);
        public Task DeleteObjectAsync(string key, CancellationToken ct = default) { Objects.TryRemove(key, out _); return Task.CompletedTask; }

        public Task PutObjectAsync(string key, byte[] content, string contentType, CancellationToken ct = default)
        {
            if (Fail) throw new Nadlan.Core.Files.StorageUnavailableException("The test storage is down.");
            Objects[key] = content;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUrls : Nadlan.Core.Files.IFileUrlProvider
    {
        public string GetUrl(string storageKey) => "https://files.example.invalid/" + storageKey + "?sig=test";
        public string GetUrl(string storageKey, string downloadName) => GetUrl(storageKey) + "&name=" + Uri.EscapeDataString(downloadName);
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
        Assert.Equal("090000009", owner.GetProperty("taxId").GetString());
    }

    private async Task<(int Status, JsonElement Body, HttpContext Http)> UploadAsync(string path, byte[] bytes, string? cookie = null, string? bearer = null)
    {
        var http = await _f.Server.SendAsync(ctx =>
        {
            ctx.Request.Method = "POST";
            var q = path.IndexOf('?');
            ctx.Request.Path = path[..q];
            ctx.Request.QueryString = new QueryString(path[q..]);
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (cookie is not null) { ctx.Request.Headers.Cookie = cookie; ctx.Request.Headers["X-Nadlan-Request"] = "1"; }
            if (bearer is not null) { ctx.Request.Headers.Authorization = "Bearer " + bearer; }
            ctx.Request.ContentType = "application/pdf";
            ctx.Request.ContentLength = bytes.Length;
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody());
        });
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();
        var json = text.Length > 0 && text[0] == '{' ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return (http.Response.StatusCode, json, http);
    }

    private static readonly byte[] MadeUpPdf = Encoding.ASCII.GetBytes("%PDF-1.4 made-up extract for a test");

    [Fact]
    public async Task Documents_are_stored_for_Admins_only_and_every_open_is_logged()
    {
        if (Off) { return; }
        var token = await AdminTokenAsync();
        var kaek = CadastreAppFixture.OtherKaeks[1];
        var saved = await SendAsync("POST", "/api/cadastre/records", For(kaek), bearer: token);
        var fetchId = saved.Body.GetProperty("units")[0].GetProperty("cadastreFetchId").GetInt64();
        var sales = await SignInAsync(CadastreAppFixture.SalesEmail);
        var admin = await SignInAsync(CadastreAppFixture.AdminEmail);

        // The importer stores the descriptive extract; the same file again is not stored twice.
        var up = await UploadAsync($"/api/cadastre/documents?fetchId={fetchId}&kind=DescriptiveExtract&textReadable=true", MadeUpPdf, bearer: token);
        Assert.True(up.Status == 200, up.Body.ToString());
        var docId = up.Body.GetProperty("cadastreDocumentId").GetInt64();
        Assert.Equal($"{kaek}-0-0-KF.pdf", up.Body.GetProperty("fileName").GetString());
        Assert.Equal(docId, (await UploadAsync($"/api/cadastre/documents?fetchId={fetchId}&kind=DescriptiveExtract&textReadable=true", MadeUpPdf, bearer: token))
            .Body.GetProperty("cadastreDocumentId").GetInt64());
        var key = Assert.Single(_f.Storage.Objects.Keys, k => k.Contains(kaek, StringComparison.Ordinal));
        Assert.StartsWith($"cadastre/{kaek}/0-0/KF-", key);
        Assert.DoesNotContain("/_", "/" + key); // the CDN blocks every folder starting with "_"

        // Refused: not a PDF, a summary without its registration, anyone but an Admin.
        Assert.Equal(400, (await UploadAsync($"/api/cadastre/documents?fetchId={fetchId}&kind=DescriptiveExtract", Encoding.ASCII.GetBytes("<html>no</html>"), bearer: token)).Status);
        Assert.Equal(400, (await UploadAsync($"/api/cadastre/documents?fetchId={fetchId}&kind=Summary", MadeUpPdf, bearer: token)).Status);
        Assert.Equal(404, (await UploadAsync($"/api/cadastre/documents?fetchId={fetchId}&kind=SpatialExtract", MadeUpPdf, cookie: sales)).Status);
        Assert.Equal(404, (await UploadAsync($"/api/cadastre/documents?fetchId=999999999&kind=SpatialExtract", MadeUpPdf, bearer: token)).Status);

        // The card lists it.
        var card = (await SendAsync("GET", $"/api/parcels/{_f.OtherParcels[1]}/cadastre", cookie: admin)).Body;
        var listed = Assert.Single(card.GetProperty("units")[0].GetProperty("documents").EnumerateArray());
        Assert.Equal(("DescriptiveExtract", docId), (listed.GetProperty("kind").GetString(), listed.GetProperty("cadastreDocumentId").GetInt64()));
        Assert.False(listed.TryGetProperty("storageKey", out _)); // the storage key never leaves the server

        // Opening: 404 for anyone but an Admin (and nothing logged), 401 signed out; each Admin open is logged, then redirected.
        await using var c = await _f.Db!.OpenAsync();
        async Task<int> Opens() => await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM activity WHERE entity_type = 'CadastreDocument' AND entity_id = @docId AND action_type = 'Opened'", new { docId });
        Assert.Equal(404, (await SendAsync("GET", $"/api/cadastre/documents/{docId}/content", cookie: sales)).Status);
        Assert.Equal(401, (await SendAsync("GET", $"/api/cadastre/documents/{docId}/content")).Status);
        Assert.Equal(0, await Opens());

        var open = await SendAsync("GET", $"/api/cadastre/documents/{docId}/content", cookie: admin);
        Assert.Equal(302, open.Status);
        Assert.StartsWith("https://files.example.invalid/cadastre/", open.Http.Response.Headers.Location.ToString());
        await SendAsync("GET", $"/api/cadastre/documents/{docId}/content", cookie: admin);
        Assert.Equal(2, await Opens());
        Assert.Equal(_f.AdminId, await c.ExecuteScalarAsync<long>(
            "SELECT MAX(user_id) FROM activity WHERE entity_type = 'CadastreDocument' AND entity_id = @docId", new { docId }));
        Assert.Equal(404, (await SendAsync("GET", "/api/cadastre/documents/999999999/content", cookie: admin)).Status);
    }

    [Fact]
    public async Task A_failed_upload_to_the_storage_leaves_no_document_row()
    {
        if (Off) { return; }
        var token = await AdminTokenAsync();
        var kaek = CadastreAppFixture.OtherKaeks[2];
        var saved = await SendAsync("POST", "/api/cadastre/records", For(kaek), bearer: token);
        var fetchId = saved.Body.GetProperty("units")[0].GetProperty("cadastreFetchId").GetInt64();
        _f.Storage.Fail = true;
        try
        {
            Assert.Equal(503, (await UploadAsync($"/api/cadastre/documents?fetchId={fetchId}&kind=SpatialExtract", Encoding.ASCII.GetBytes("%PDF-1.4 diagram"), bearer: token)).Status);
        }
        finally
        {
            _f.Storage.Fail = false;
        }

        await using var c = await _f.Db!.OpenAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cadastre_document WHERE cadastre_fetch_id = @fetchId", new { fetchId }));

        // The stage the lawyer service showed was kept for the Parcel (an extension table, no column on parcel).
        Assert.Equal(("InOperation", "Lawyer"), await c.QuerySingleAsync<(string, string)>(
            "SELECT stage, source FROM parcel_cadastre_stage WHERE parcel_id = @p", new { p = _f.OtherParcels[2] }));
    }

    [Fact]
    public async Task A_persons_tax_number_is_saved_now()
    {
        if (Off) { return; }
        var token = await AdminTokenAsync();
        var kaek = CadastreAppFixture.OtherKaeks[0];

        var (status, body, _) = await SendAsync("POST", "/api/cadastre/records", For(kaek, Person(tax: "100000001")), bearer: token);

        Assert.True(status == 200, body.ToString());
        Assert.Single(body.GetProperty("units").EnumerateArray()); // each unit's row id, for the documents
        await using var c = await _f.Db!.OpenAsync();
        Assert.Equal("100000001", await c.ExecuteScalarAsync<string>("""
            SELECT o.tax_id FROM cadastre_owner o JOIN cadastre_fetch f ON f.cadastre_fetch_id = o.cadastre_fetch_id WHERE f.kaek = @kaek
            """, new { kaek }));
    }
}
