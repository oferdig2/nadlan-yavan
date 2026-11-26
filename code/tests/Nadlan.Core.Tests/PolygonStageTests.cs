using System.Globalization;
using System.Text;
using Nadlan.Core.Activity;
using Nadlan.Core.Editing;
using Nadlan.Core.Geo;
using Nadlan.Core.GeographicAreas;
using Nadlan.Core.Parcels;
using Nadlan.Core.Validation;
using Nadlan.KaekImporter.Api;
using Nadlan.KaekImporter.Geometry;
using Nadlan.KaekImporter.Import;
using Nadlan.KaekImporter.Ktimanet;

namespace Nadlan.Core.Tests;

/// <summary>
/// The Cadastre stage of each Parcel and the check of Parcels already in GreekPlot (brief
/// AI/Remarks/CodingAgent-PolygonStageAndUpdate-2026-11-26.md, migration 034), without a database. Made-up KAEKs and shapes only.
/// </summary>
public class PolygonStageTests
{
    // A made-up 20 x 20 m plot (about 400 m²) somewhere in Greece, in lon/lat.
    private static GeoPolygon Box(double lon, double lat, double widthM = 20, double heightM = 20)
    {
        var dLon = widthM / (111_320 * Math.Cos(lat * Math.PI / 180));
        var dLat = heightM / 110_540;
        return new GeoPolygon(new[]
        {
            (IReadOnlyList<GeoPoint>)new[] { new GeoPoint(lon, lat), new GeoPoint(lon + dLon, lat), new GeoPoint(lon + dLon, lat + dLat), new GeoPoint(lon, lat + dLat), new GeoPoint(lon, lat) },
        });
    }

    private const double Lon = 23.5, Lat = 38.0;
    private static readonly GeoPolygon Ours = Box(Lon, Lat);

    // ---- stage values -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GETPST", "InOperation")]
    [InlineData("GETPSTKG", "SurveyPost")]
    [InlineData("GETPSTKGPRO", "PrePosting")]
    [InlineData("GETSOMETHING", null)]
    public void Each_click_request_says_its_stage_on_both_sides(string request, string? stage)
    {
        Assert.Equal(stage, CadastralStages.FromClickRequest(request));
        Assert.Equal(stage, KtimanetClick.StageOf(request));
        if (stage is not null)
        {
            Assert.Equal(request, CadastralStages.RequestOf(stage));
            Assert.Equal(request, KtimanetClick.RequestOf(stage));
            Assert.Equal(request, CadastralStages.RequestFor(stage, null));
        }
    }

    [Fact]
    public void A_request_that_does_not_answer_for_the_stage_is_refused_and_a_future_date_becomes_now()
    {
        Assert.Equal("PARCEL_STAGE_REQUEST", Assert.Throws<DomainValidationException>(() => CadastralStages.RequestFor("InOperation", "GETPSTKG")).Code);
        Assert.Equal("PARCEL_STAGE_INVALID", Assert.Throws<DomainValidationException>(() => CadastralStages.ParseFromMap("NotOperating")).Code); // the lawyer's only
        var now = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now, CadastralStages.SeenUtc(now.AddDays(3), now));
        Assert.Equal(now, CadastralStages.SeenUtc(null, now));
        Assert.Equal(now.AddHours(-1), CadastralStages.SeenUtc(now.AddHours(-1), now));
        Assert.Empty(CadastralStages.ParseFilter(CadastralStages.FilterValues)); // all ticked = no filter
        Assert.Equal(new[] { "Unknown", "InOperation" }, CadastralStages.ParseFilter(new[] { "unknown", "InOperation", "inoperation" }));
        Assert.Throws<DomainValidationException>(() => CadastralStages.ParseFilter(new[] { "Yellow" }));
    }

    // ---- new parcels ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_parcel_keeps_the_stage_of_its_click_request_and_a_hand_made_one_none()
    {
        var w = new World();
        var seen = new DateTime(2026, 11, 26, 9, 30, 0, DateTimeKind.Utc);

        await w.ParcelService.CreateAsync(new CreateParcelRequest { RegistryId = "990000000001", Geometry = Ours, CadastralStage = "SurveyPost", CadastralStageSeenUtc = seen });
        await w.ParcelService.CreateAsync(new CreateParcelRequest { RegistryId = "990000000002", Geometry = Box(Lon + 0.01, Lat) }); // hand-made

        var imported = w.Parcels.Items[0];
        Assert.Equal(("SurveyPost", seen, "Polygon", "GETPSTKG"),
            (imported.CadastralStage, imported.CadastralStageSeenUtc!.Value, imported.CadastralStageSource, imported.CadastralStageRequest));
        Assert.Contains("Cadastre stage: Survey post (map)", w.Activity.Entries[0].Summary);
        var handMade = w.Parcels.Items[1];
        Assert.Null(handMade.CadastralStage);
        Assert.Null(handMade.CadastralStageSeenUtc);
        Assert.Null(handMade.CadastralStageSource);
        await Assert.ThrowsAsync<DomainValidationException>(() => w.ParcelService.CreateAsync(
            new CreateParcelRequest { RegistryId = "990000000003", Geometry = Box(Lon + 0.02, Lat), CadastralStage = "Orange" }));
    }

    // ---- the newest sighting wins, every change in the history -------------------------------------------------------

    [Fact]
    public async Task The_newest_sighting_wins_whatever_the_source_and_each_change_is_in_the_history()
    {
        var w = new World();
        var p = w.Add("990000000011");
        var day1 = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);

        Assert.True(await w.Service.RecordStageAsync(p.ParcelId, "SurveyPost", day1, CadastralStages.Polygon));
        Assert.True(await w.Service.RecordStageAsync(p.ParcelId, "InOperation", day1.AddDays(2), CadastralStages.Lawyer));
        Assert.False(await w.Service.RecordStageAsync(p.ParcelId, "SurveyPost", day1.AddDays(1), CadastralStages.Polygon)); // older: ignored
        Assert.False(await w.Service.RecordStageAsync(p.ParcelId, "InOperation", day1.AddDays(3), CadastralStages.Polygon)); // same stage: date only

        var row = w.Stages.Rows[p.ParcelId];
        Assert.Equal(("InOperation", day1.AddDays(3), "Polygon", "GETPST"), (row.Stage, row.Seen, row.Source, row.Request));
        Assert.Equal(new[]
        {
            "Cadastre stage: Unknown → Survey post (map, seen 20 Nov 2026).",
            "Cadastre stage: Survey post → In operation (lawyer, seen 22 Nov 2026).",
        }, w.Activity.Entries.Select(e => e.Summary));
    }

    [Fact]
    public async Task The_lawyer_run_sets_in_operation_or_not_operating_by_KAEK_and_an_error_nothing()
    {
        var w = new World();
        var found = w.Add("990000000021");
        var notThere = w.Add("990000000022");
        var at = new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);

        await w.Service.RecordLawyerAsync("990000000021", "Found", at, 7);
        await w.Service.RecordLawyerAsync("990000000022", "NotOperating", at, 7);
        await w.Service.RecordLawyerAsync("990000000023", "Found", at, 7); // no such Parcel: nothing
        await w.Service.RecordLawyerAsync("990000000022", "Error", at.AddHours(1), 7);

        Assert.Equal(("InOperation", "Lawyer", (string?)null), (w.Stages.Rows[found.ParcelId].Stage, w.Stages.Rows[found.ParcelId].Source, w.Stages.Rows[found.ParcelId].Request));
        Assert.Equal(("NotOperating", at), (w.Stages.Rows[notThere.ParcelId].Stage, w.Stages.Rows[notThere.ParcelId].Seen));
        Assert.Equal(2, w.Stages.Rows.Count);
        Assert.All(w.Activity.Entries, e => Assert.Equal(7, e.UserId));
    }

    // ---- check outcomes ---------------------------------------------------------------------------------------------

    private static CadastreCheckInput Found(string kaek, GeoPolygon polygon, string stage = "InOperation", DateTime? seen = null) =>
        new(kaek, stage, seen ?? new DateTime(2026, 11, 26, 11, 0, 0, DateTimeKind.Utc), polygon, 400.12m, "Found");

    [Fact]
    public async Task The_same_polygon_under_the_threshold_is_unchanged_and_only_the_date_moves()
    {
        var w = new World();
        var p = w.Add("990000000031", Ours);
        await w.Service.RecordStageAsync(p.ParcelId, "InOperation", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), CadastralStages.Polygon);
        var nudged = Box(Lon + 0.02 / 111_320, Lat); // 2 cm east: 0.8 m² of difference, under max(1 m², 0.5 % of 400 m²) = 2 m²

        var result = await w.Service.CheckAsync(p.ParcelId, Found("990000000031", nudged), 1);

        Assert.Equal(CadastreCheckOutcome.Unchanged, result.Outcome);
        Assert.InRange(result.DifferenceSqm!.Value, 0.1, 2);
        Assert.Empty(w.Stages.Proposals);
        Assert.Equal(new DateTime(2026, 11, 26, 11, 0, 0, DateTimeKind.Utc), w.Stages.Rows[p.ParcelId].Seen);
        Assert.Single(w.Activity.Entries); // only the first stage
    }

    [Fact]
    public async Task A_stage_change_on_its_own_is_StageUpdated()
    {
        var w = new World();
        var p = w.Add("990000000032", Ours);
        await w.Service.RecordStageAsync(p.ParcelId, "SurveyPost", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), CadastralStages.Polygon);

        var result = await w.Service.CheckAsync(p.ParcelId, Found("990000000032", Ours, "InOperation"), 1);

        Assert.Equal((CadastreCheckOutcome.StageUpdated, true), (result.Outcome, result.StageChanged));
        Assert.Equal("InOperation", w.Stages.Rows[p.ParcelId].Stage);
        Assert.Contains("Survey post → In operation", w.Activity.Entries[^1].Summary);
        Assert.Empty(w.Stages.Proposals);
    }

    [Fact]
    public async Task A_polygon_over_the_threshold_files_a_proposal_and_updates_the_stage_now()
    {
        var w = new World();
        var p = w.Add("990000000033", Ours);
        var bigger = Box(Lon, Lat, 20, 22); // 2 m more to the north: about 40 m²

        var result = await w.Service.CheckAsync(p.ParcelId, Found("990000000033", bigger, "SurveyPost"), 5);

        Assert.Equal(CadastreCheckOutcome.ProposalFiled, result.Outcome);
        Assert.InRange(result.DifferenceSqm!.Value, 35, 45);
        var filed = Assert.Single(w.Stages.Proposals);
        Assert.Equal((ProposalKinds.GeometryChanged, ProposalStatuses.Pending, "990000000033", 400.12m, 5L),
            (filed.Kind, filed.Status, filed.KaekSeen, filed.AreaSqm!.Value, filed.CreatedByUserId!.Value));
        Assert.Equal("SurveyPost", w.Stages.Rows[p.ParcelId].Stage); // the stage now, the polygon waits
        Assert.Same(Ours, w.Parcels.Items.Single().Geometry); // ours untouched
    }

    [Fact]
    public async Task Another_KAEK_or_nothing_at_the_point_changes_nothing_and_files_for_an_Admin()
    {
        var w = new World();
        var p = w.Add("990000000034", Ours);

        var mismatch = await w.Service.CheckAsync(p.ParcelId, Found("990000000099", Box(Lon, Lat, 10, 20)), 1);
        var notFound = await w.Service.CheckAsync(p.ParcelId, new CadastreCheckInput(null, "InOperation", null, null, null, "NotFound"), 1);

        Assert.Equal(CadastreCheckOutcome.KaekMismatch, mismatch.Outcome);
        Assert.Equal(CadastreCheckOutcome.NotFound, notFound.Outcome);
        Assert.False(w.Stages.Rows.ContainsKey(p.ParcelId)); // no stage written
        Assert.Equal(new[] { (ProposalKinds.KaekMismatch, ProposalStatuses.Superseded), (ProposalKinds.NotFound, ProposalStatuses.Pending) },
            w.Stages.Proposals.Select(x => (x.Kind, x.Status)));
        Assert.Equal("990000000099", w.Stages.Proposals[0].KaekSeen);
        Assert.Null(w.Stages.Proposals[1].Geometry);
        Assert.Empty(w.Activity.Entries);
    }

    [Fact]
    public async Task A_newer_proposal_supersedes_the_pending_one_the_same_one_is_not_filed_twice_and_a_rejected_one_stays_rejected()
    {
        var w = new World();
        var p = w.Add("990000000035", Ours);
        var a = Box(Lon, Lat, 20, 22);
        var b = Box(Lon, Lat, 20, 25);

        var first = await w.Service.CheckAsync(p.ParcelId, Found("990000000035", a), 1);
        var again = await w.Service.CheckAsync(p.ParcelId, Found("990000000035", a), 1);
        Assert.Equal(first.ProposalId, again.ProposalId); // still waiting: not filed twice
        var other = await w.Service.CheckAsync(p.ParcelId, Found("990000000035", b), 1);
        Assert.NotEqual(first.ProposalId, other.ProposalId);
        Assert.Equal(new[] { ProposalStatuses.Superseded, ProposalStatuses.Pending }, w.Stages.Proposals.Select(x => x.Status));

        await w.Service.RejectAsync(other.ProposalId!.Value, 9);
        var later = await w.Service.CheckAsync(p.ParcelId, Found("990000000035", b), 1);
        Assert.Equal(other.ProposalId, later.ProposalId);
        Assert.Contains("rejected before", later.Note);
        Assert.Equal(2, w.Stages.Proposals.Count);
        await Assert.ThrowsAsync<DomainValidationException>(() => w.Service.RejectAsync(other.ProposalId!.Value, 9)); // decided

        // The Cadastre's polygon is ours again: nothing waits any more.
        var c = await w.Service.CheckAsync(p.ParcelId, Found("990000000035", a), 1);
        await w.Service.CheckAsync(p.ParcelId, Found("990000000035", Ours), 1);
        Assert.Equal(ProposalStatuses.Superseded, w.Stages.Proposals.Single(x => x.ProposalId == c.ProposalId).Status);
    }

    [Fact]
    public async Task Only_real_KAEKs_are_checked_and_bad_input_is_refused()
    {
        var w = new World();
        var tmp = w.Add("TMP-SKR-OT1-P1", Ours, provisional: true);
        var real = w.Add("990000000036", Box(Lon + 0.01, Lat));

        Assert.Equal("PARCEL_CHECK_PROVISIONAL", (await Assert.ThrowsAsync<DomainValidationException>(() => w.Service.CheckAsync(tmp.ParcelId, Found("TMP-SKR-OT1-P1", Ours), 1))).Code);
        Assert.Equal("PARCEL_CHECK_OUTCOME", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            w.Service.CheckAsync(real.ParcelId, Found("990000000036", Ours) with { Outcome = "Maybe" }, 1))).Code);
        Assert.Equal("PARCEL_STAGE_REQUIRED", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            w.Service.CheckAsync(real.ParcelId, Found("990000000036", Ours) with { CadastralStage = null }, 1))).Code);
        Assert.Equal("PARCEL_CHECK_POLYGON", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            w.Service.CheckAsync(real.ParcelId, Found("990000000036", Ours) with { Polygon = null }, 1))).Code);
        Assert.Equal("PARCEL_STAGE_REQUEST", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            w.Service.CheckAsync(real.ParcelId, Found("990000000036", Ours) with { Request = "GETPSTKG" }, 1))).Code);
        await Assert.ThrowsAsync<EntityNotFoundException>(() => w.Service.CheckAsync(999, Found("990000000036", Ours), 1));
    }

    // ---- accept / reject ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Accept_is_the_normal_polygon_edit_keeping_the_old_polygon_in_the_history()
    {
        var w = new World();
        var p = w.Add("990000000041", Ours, ot: "47", plot: "12");
        var bigger = Box(Lon, Lat, 20, 22);
        var filed = await w.Service.CheckAsync(p.ParcelId, Found("990000000041", bigger), 1);

        var result = await w.Service.AcceptAsync(filed.ProposalId!.Value, 3, acceptOverlaps: false);

        Assert.Equal(CreateParcelOutcome.Updated, result.Outcome);
        var saved = w.Parcels.Updated.Single();
        Assert.Same(w.Stages.Proposals[0].Geometry, saved.Geometry);
        Assert.Equal((400.12m, "47", "12", "990000000041"), (saved.OfficialAreaSqm!.Value, saved.OT, saved.PlotNumber, saved.RegistryId)); // the rest stays
        var history = w.Activity.Entries.Single(e => e.ActionType == ActivityActions.ParcelGeometryChanged);
        Assert.StartsWith($"Polygon replaced by the Cadastre's (change #{filed.ProposalId} accepted", history.Summary);
        Assert.Contains(Ours.ToWkt(), System.Text.Json.JsonSerializer.Serialize(history.Metadata)); // the old polygon kept
        Assert.Equal((ProposalStatuses.Accepted, 3L), (w.Stages.Proposals[0].Status, w.Stages.Proposals[0].DecidedByUserId!.Value));
        Assert.Equal(1, w.Versions.Locks); // under the Parcel's edit lock, as an edit
        await Assert.ThrowsAsync<DomainValidationException>(() => w.Service.AcceptAsync(filed.ProposalId!.Value, 3, false)); // decided
    }

    [Fact]
    public async Task Accept_applies_the_overlap_rule_of_an_edit_and_a_mismatch_cannot_be_accepted()
    {
        var w = new World();
        var p = w.Add("990000000042", Ours);
        w.Parcels.OverlapWith = new ParcelOverlapHit(77, "990000000077", 12.5);
        var filed = await w.Service.CheckAsync(p.ParcelId, Found("990000000042", Box(Lon, Lat, 20, 22)), 1);

        var first = await w.Service.AcceptAsync(filed.ProposalId!.Value, 3, acceptOverlaps: false);
        Assert.Equal(CreateParcelOutcome.NeedsOverlapConfirmation, first.Outcome);
        Assert.Empty(w.Parcels.Updated);
        Assert.Equal(ProposalStatuses.Pending, w.Stages.Proposals[0].Status);
        Assert.Equal(CreateParcelOutcome.Updated, (await w.Service.AcceptAsync(filed.ProposalId!.Value, 3, acceptOverlaps: true)).Outcome);

        var q = w.Add("990000000043", Box(Lon + 0.01, Lat));
        var mismatch = await w.Service.CheckAsync(q.ParcelId, Found("990000000098", Box(Lon + 0.01, Lat)), 1);
        Assert.Equal("PROPOSAL_NOT_GEOMETRY", (await Assert.ThrowsAsync<DomainValidationException>(() => w.Service.AcceptAsync(mismatch.ProposalId!.Value, 3, false))).Code);
    }

    [Fact]
    public async Task Accept_small_takes_only_small_changes_of_parcels_without_Assets_or_work()
    {
        var w = new World();
        var small = w.Add("990000000051", Box(Lon, Lat, 100, 100));                 // 10,000 m²
        var withAsset = w.Add("990000000052", Box(Lon + 0.01, Lat, 100, 100));
        var inWork = w.Add("990000000053", Box(Lon + 0.02, Lat, 100, 100));
        var large = w.Add("990000000054", Box(Lon + 0.03, Lat, 100, 100));
        foreach (var x in new[] { small, withAsset, inWork })
        {
            var lon = x.Geometry.Exterior[0].Lon;
            await w.Service.CheckAsync(x.ParcelId, Found(x.RegistryId!, Box(lon, Lat, 100, 101)), 1); // 100 m² = 1 %
        }

        await w.Service.CheckAsync(large.ParcelId, Found(large.RegistryId!, Box(Lon + 0.03, Lat, 100, 105)), 1); // 5 %
        w.Stages.WithAssets.Add(withAsset.ParcelId);
        w.Stages.InWork.Add(inWork.ParcelId);

        var result = await w.Service.AcceptSmallAsync(0, 3);

        Assert.Equal(1, result.Accepted);
        Assert.Equal(small.ParcelId, w.Parcels.Updated.Single().ParcelId);
        Assert.Equal(new[] { ("990000000052", "has Assets"), ("990000000053", "in a work request") },
            result.Skipped.Where(s => !s.Reason.StartsWith("not a small", StringComparison.Ordinal)).Select(s => (s.RegistryId!, s.Reason)).OrderBy(s => s.Item1));
        Assert.Contains(result.Skipped, s => s.RegistryId == "990000000054" && s.Reason.StartsWith("not a small change", StringComparison.Ordinal));
    }

    [Fact]
    public void The_difference_is_the_symmetric_difference_in_square_metres()
    {
        Assert.InRange(GeoDifference.AreaSqm(Ours), 399, 401);
        Assert.InRange(GeoDifference.SymmetricDifferenceSqm(Ours, Ours), 0, 1e-6);
        Assert.InRange(GeoDifference.SymmetricDifferenceSqm(Ours, Box(Lon, Lat, 20, 22)), 39, 41);
        Assert.InRange(GeoDifference.SymmetricDifferenceSqm(Ours, Box(Lon + 0.01, Lat)), 799, 801); // apart: both areas
        Assert.Equal(1.0, GeoDifference.SameThresholdSqm(100));
        Assert.Equal(5.0, GeoDifference.SameThresholdSqm(1000));
    }

    // ---- the importer ------------------------------------------------------------------------------------------------

    // A made-up reply in the site's layout: request|x,y|type@[box]@@KAEK@X~X~..=~~~~Y~Y~..=
    internal static string Reply(string request, string kaek, IReadOnlyList<EgsaPoint> ring)
    {
        string Join(Func<EgsaPoint, double> pick) => string.Join("~", ring.Select(p => pick(p).ToString("0.###", CultureInfo.InvariantCulture))) + "=";
        return $"{request}|1.000,2.000|2@[0,0,1,1]@@{kaek}@{Join(p => p.X)}~~~~{Join(p => p.Y)}";
    }

    private static IReadOnlyList<EgsaPoint> Square(double x, double y, double size = 20) =>
        new[] { new EgsaPoint(x, y), new EgsaPoint(x + size, y), new EgsaPoint(x + size, y + size), new EgsaPoint(x, y + size), new EgsaPoint(x, y) };

    [Fact]
    public void A_reply_says_which_request_answered_it()
    {
        var shape = KtimanetReply.Parse(Reply("GETPSTKGPRO", "990000000061", Square(480000, 4200000)))!;
        Assert.Equal(("990000000061", "GETPSTKGPRO", "PrePosting"), (shape.Kaek, shape.Request, KtimanetClick.StageOf(shape.Request)));
        Assert.Null(KtimanetReply.Parse("GETPST|1,2|"));
    }

    [Fact]
    public void The_check_clicks_well_inside_even_a_U_shaped_parcel()
    {
        // A U: 30 x 30 m with a 10 x 20 m bite from the top middle; its centroid lies in the bite (outside).
        var u = new[]
        {
            new EgsaPoint(0, 0), new EgsaPoint(30, 0), new EgsaPoint(30, 30), new EgsaPoint(20, 30), new EgsaPoint(20, 10),
            new EgsaPoint(10, 10), new EgsaPoint(10, 30), new EgsaPoint(0, 30), new EgsaPoint(0, 0),
        }.Select(p => new EgsaPoint(p.X + 480000, p.Y + 4200000)).ToList();

        var (point, clearance) = PolygonMath.InsidePoint(new[] { u })!.Value;

        Assert.True(PolygonMath.Contains(Egsa87.ToWgs84(new[] { (IReadOnlyList<EgsaPoint>)u }), Egsa87.ToWgs84(point)));
        Assert.InRange(clearance, 4.5, 6.5); // arms and base 10 m wide: best where an arm meets the base, ~5.9 m from any edge
        Assert.Null(PolygonMath.InsidePoint(new[] { (IReadOnlyList<EgsaPoint>)new[] { new EgsaPoint(1, 1), new EgsaPoint(2, 2), new EgsaPoint(3, 3), new EgsaPoint(1, 1) } }));
    }

    private static readonly ImportSettings Pace = new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), 2000, null);

    private static MapAreas View(params MapArea[] areas) =>
        new(new MapExtent(479900, 480300, 4200300, 4199900), 0.5, areas, KtimanetClick.Operating, areas.Length == 0 ? 0 : 1000);

    private static ExistingParcel Existing(long id, string kaek, IReadOnlyList<EgsaPoint> ring) =>
        new(kaek, false, Egsa87.ToWgs84(new[] { ring }), id);

    [Fact]
    public async Task Update_mode_off_asks_nothing_about_parcels_GreekPlot_has()
    {
        var site = new FakeSite();
        var nadlan = new FakeNadlan { Existing = { Existing(1, "990000000071", Square(480000, 4200000)), Existing(2, "990000000072", Square(480100, 4200000)) } };
        var pauses = new List<TimeSpan>();

        await new ImportRun(site, nadlan, Pace, TextWriter.Null, (d, _) => { pauses.Add(d); return Task.CompletedTask; }, TempFolder())
            .RunAsync(View(), 1, CancellationToken.None);

        Assert.Empty(site.Queries);
        Assert.Empty(nadlan.Checks);
        Assert.Empty(pauses);
        Assert.Contains(site.Logs, l => l.Contains("already in GreekPlot; skipping them", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_mode_on_asks_once_per_existing_parcel_well_inside_it_at_the_normal_pace()
    {
        var a = Square(480000, 4200000);
        var b = Square(480100, 4200000);
        var site = new FakeSite
        {
            Answer = (p, request) => p.X < 480050
                ? Reply(request, "990000000071", a)                                                   // the same
                : Reply(request, "990000000079", Square(480100, 4200000, 10)),                       // another KAEK there
        };
        var nadlan = new FakeNadlan { Existing = { Existing(1, "990000000071", a), Existing(2, "990000000072", b) } };
        var pauses = new List<TimeSpan>();

        await new ImportRun(site, nadlan, Pace, TextWriter.Null, (d, _) => { pauses.Add(d); return Task.CompletedTask; }, TempFolder())
            .RunAsync(View(), 1, CancellationToken.None, checkExisting: true);

        Assert.Equal(2, site.Queries.Count);
        Assert.All(site.Queries, q => Assert.Equal(KtimanetClick.Operating, q.Request));
        var atA = site.Queries.Single(q => q.Point.X < 480050).Point;
        Assert.InRange(atA.X, 480009.5, 480010.5); // the middle of the 20 m square
        Assert.InRange(atA.Y, 4200009.5, 4200010.5);
        Assert.Equal(2, pauses.Count);
        Assert.All(pauses, d => Assert.InRange(d, Pace.MinParcelDelay, Pace.MaxParcelDelay)); // the pace of a new parcel
        Assert.Equal(new[] { (1L, true, "990000000071", "InOperation"), (2L, true, "990000000079", "InOperation") },
            nadlan.Checks.Select(c => (c.ParcelId, c.Found, c.Kaek!, c.Stage)).OrderBy(c => c.Item1));
        Assert.Empty(nadlan.Created); // the other KAEK is for an Admin, never created by the sweep
    }

    [Fact]
    public async Task New_parcels_carry_the_stage_of_the_reply_and_offline_runs_list_it()
    {
        var area = new MapArea(400, new AreaPoint(new EgsaPoint(480210, 4200010), 9), new[] { new AreaPoint(new EgsaPoint(480210, 4200010), 9) });
        var site = new FakeSite { Answer = (_, _) => Reply("GETPSTKG", "990000000081", Square(480200, 4200000)) };
        var nadlan = new FakeNadlan();

        await new ImportRun(site, nadlan, Pace, TextWriter.Null, (_, _) => Task.CompletedTask, TempFolder()).RunAsync(View(area) with { ClickRequest = "GETPSTKG" }, 1, CancellationToken.None);
        Assert.Equal(("990000000081", "SurveyPost"), (nadlan.Created.Single().Kaek, nadlan.Created.Single().Stage));

        var offline = new FakeSite { Answer = site.Answer };
        var folder = TempFolder();
        await new ImportRun(offline, null, Pace, TextWriter.Null, (_, _) => Task.CompletedTask, folder).RunAsync(View(area) with { ClickRequest = "GETPSTKG" }, null, CancellationToken.None);
        Assert.Contains(offline.Logs, l => l.Contains("990000000081: found (400 m², survey post) - not saved (offline)", StringComparison.Ordinal));
        var csv = await File.ReadAllTextAsync(Directory.GetFiles(folder).Single(), Encoding.UTF8);
        Assert.Contains(",SurveyPost\r\n", csv);
    }

    [Fact]
    public async Task An_older_GreekPlot_without_checks_still_gets_its_sweep()
    {
        var area = new MapArea(400, new AreaPoint(new EgsaPoint(480210, 4200010), 9), new[] { new AreaPoint(new EgsaPoint(480210, 4200010), 9) });
        var site = new FakeSite { Answer = (p, r) => p.X > 480150 ? Reply(r, "990000000091", Square(480200, 4200000)) : Reply(r, "990000000071", Square(480000, 4200000)) };
        var nadlan = new FakeNadlan { Existing = { Existing(1, "990000000071", Square(480000, 4200000)), Existing(2, "990000000072", Square(480100, 4200000)) }, Unsupported = true };

        await new ImportRun(site, nadlan, Pace, TextWriter.Null, (_, _) => Task.CompletedTask, TempFolder()).RunAsync(View(area), 1, CancellationToken.None, checkExisting: true);

        Assert.Single(nadlan.Checks); // stopped checking after the first refusal
        Assert.Contains(site.Logs, l => l.Contains("database change 034", StringComparison.Ordinal));
        Assert.Equal("990000000091", nadlan.Created.Single().Kaek);
    }

    private static string TempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "nadlan-stage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    // ---- fakes ------------------------------------------------------------------------------------------------------

    private sealed class FakeSite : IKtimanetSite
    {
        public Func<EgsaPoint, string, string> Answer { get; init; } = (_, r) => r + "|1,2|";
        public List<(EgsaPoint Point, string Request)> Queries { get; } = new();
        public List<string> Logs { get; } = new();

        public Task<string> QueryAsync(EgsaPoint p, string request)
        {
            Queries.Add((p, request));
            return Task.FromResult(Answer(p, request));
        }

        public Task<(MapAreas? Areas, string? Error)> ReadAreasAsync(double sampleStepMetres) => throw new NotSupportedException();
        public Task StatusAsync(string text) => Task.CompletedTask;
        public Task LogAsync(string text, string kind = "") { Logs.Add(text); return Task.CompletedTask; }
        public Task CountsAsync(object counts) => Task.CompletedTask;
        public Task SetSweepAsync(MapExtent e) => Task.CompletedTask;
        public Task SetProbeAsync(EgsaPoint p) => Task.CompletedTask;
        public Task FinishedAsync(string text) { Logs.Add(text); return Task.CompletedTask; }
        public Task RefreshStateAsync() => Task.CompletedTask;
        public Task AddShapeAsync(IReadOnlyList<IReadOnlyList<EgsaPoint>> rings, string kind) => Task.CompletedTask;
    }

    private sealed class FakeNadlan : INadlanParcels
    {
        public List<ExistingParcel> Existing { get; } = new();
        public List<(string Kaek, string? Stage)> Created { get; } = new();
        public List<(long ParcelId, bool Found, string? Kaek, string Stage)> Checks { get; } = new();
        public bool Unsupported { get; init; }

        public Task<IReadOnlyList<ExistingParcel>> ListParcelsInAsync(double west, double south, double east, double north, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExistingParcel>>(Existing);

        public Task<CreateResult> CreateParcelAsync(string kaek, int? geographicAreaId, IReadOnlyList<IReadOnlyList<LonLat>> rings, double areaSqm, string notes,
            CancellationToken ct, string? cadastralStage = null, DateTime? seenUtc = null)
        {
            Created.Add((kaek, cadastralStage));
            return Task.FromResult(new CreateResult(CreateOutcome.Created, 100 + Created.Count, Array.Empty<string>(), null));
        }

        public Task<CheckResult> CheckParcelAsync(long parcelId, bool found, string? kaekSeen, string cadastralStage, DateTime seenUtc,
            IReadOnlyList<IReadOnlyList<LonLat>>? rings, double? areaSqm, CancellationToken ct)
        {
            Checks.Add((parcelId, found, kaekSeen, cadastralStage));
            return Task.FromResult(Unsupported
                ? new CheckResult(CheckOutcome.Unsupported, null, "this GreekPlot server can't check existing parcels yet (it needs the update with database change 034).")
                : new CheckResult(CheckOutcome.Unchanged, 0, null));
        }
    }

    /// <summary>The services with in-memory stores.</summary>
    private sealed class World
    {
        public Parcels Parcels { get; } = new();
        public StageStore Stages { get; } = new();
        public Log Activity { get; } = new();
        public Versions Versions { get; } = new();
        public ParcelService ParcelService { get; }
        public ParcelCadastreService Service { get; }

        public World()
        {
            ParcelService = new ParcelService(Parcels, new NoAreas(), new Greece(), Activity);
            Service = new ParcelCadastreService(Stages, Parcels, ParcelService, Versions, Activity,
                () => new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc));
            Stages.Parcels = Parcels;
        }

        public Parcel Add(string kaek, GeoPolygon? geometry = null, bool provisional = false, string? ot = null, string? plot = null)
        {
            var p = new Parcel
            {
                ParcelId = Parcels.Items.Count + 1, CountryId = 1, RegistryId = kaek, RegistryIdIsProvisional = provisional, Geometry = geometry ?? Ours,
                OT = ot, PlotNumber = plot, UpdatedUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            Parcels.Items.Add(p);
            return p;
        }
    }

    private sealed class StageStore : IParcelCadastreStore
    {
        public Parcels? Parcels { get; set; }
        public Dictionary<long, (string Stage, DateTime Seen, string Source, string? Request)> Rows { get; } = new();
        public List<ParcelProposal> Proposals { get; } = new();
        public HashSet<long> WithAssets { get; } = new();
        public HashSet<long> InWork { get; } = new();

        public Task<StageWrite> SetStageIfNewerAsync(long parcelId, string stage, DateTime seenUtc, string source, string? request, CancellationToken ct = default)
        {
            if (Rows.TryGetValue(parcelId, out var old) && seenUtc < old.Seen) return Task.FromResult(new StageWrite(false, old.Stage, old.Seen));
            Rows[parcelId] = (stage, seenUtc, source, request);
            return Task.FromResult(new StageWrite(true, old.Stage, old.Stage is null ? null : old.Seen));
        }

        public Task<long?> FindParcelIdByKaekAsync(string kaek, CancellationToken ct = default) =>
            Task.FromResult(Parcels!.Items.FirstOrDefault(p => p.RegistryId == kaek && !p.RegistryIdIsProvisional)?.ParcelId);

        public Task<long> FileProposalAsync(NewParcelProposal n, CancellationToken ct = default)
        {
            SupersedeCore(n.ParcelId);
            var p = Parcels!.Items.First(x => x.ParcelId == n.ParcelId);
            var id = Proposals.Count + 1L;
            Proposals.Add(new ParcelProposal
            {
                ProposalId = id, ParcelId = n.ParcelId, RegistryId = p.RegistryId, Kind = n.Kind, Status = ProposalStatuses.Pending, KaekSeen = n.KaekSeen,
                StageSeen = n.StageSeen, Geometry = n.Geometry, AreaSqm = n.AreaSqm, DifferenceSqm = n.DifferenceSqm, CurrentAreaSqm = n.CurrentAreaSqm,
                SeenUtc = n.SeenUtc, CreatedByUserId = n.CreatedByUserId,
            });
            return Task.FromResult(id);
        }

        private int SupersedeCore(long parcelId)
        {
            var n = 0;
            for (var i = 0; i < Proposals.Count; i++)
            {
                if (Proposals[i].ParcelId == parcelId && Proposals[i].Status == ProposalStatuses.Pending)
                {
                    Proposals[i] = Proposals[i] with { Status = ProposalStatuses.Superseded };
                    n++;
                }
            }

            return n;
        }

        public Task<int> SupersedePendingAsync(long parcelId, CancellationToken ct = default) => Task.FromResult(SupersedeCore(parcelId));

        public Task<ParcelProposal?> GetLatestAsync(long parcelId, CancellationToken ct = default) =>
            Task.FromResult(Proposals.LastOrDefault(p => p.ParcelId == parcelId && p.Status != ProposalStatuses.Superseded));

        private ParcelProposal Live(ParcelProposal p) => p with { HasAssets = WithAssets.Contains(p.ParcelId), InWorkRequest = InWork.Contains(p.ParcelId) };

        public Task<IReadOnlyList<ParcelProposal>> ListAsync(int? areaId, string? status, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ParcelProposal>>(Proposals.Where(p => status is null || p.Status == status).Select(Live).Reverse().Take(limit).ToList());

        public Task<ParcelProposal?> GetAsync(long proposalId, CancellationToken ct = default) =>
            Task.FromResult(Proposals.Where(p => p.ProposalId == proposalId).Select(Live).FirstOrDefault());

        public Task<bool> DecideAsync(long proposalId, string status, long? userId, CancellationToken ct = default)
        {
            var i = Proposals.FindIndex(p => p.ProposalId == proposalId && p.Status == ProposalStatuses.Pending);
            if (i < 0) return Task.FromResult(false);
            Proposals[i] = Proposals[i] with { Status = status, DecidedByUserId = userId };
            return Task.FromResult(true);
        }
    }

    private sealed class Parcels : IParcelStore
    {
        public List<Parcel> Items { get; } = new();
        public List<Parcel> Updated { get; } = new();
        public ParcelOverlapHit? OverlapWith { get; set; }

        public Task<Parcel?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(p => p.ParcelId == id));
        public Task<Parcel?> GetByRegistryIdAsync(int c, string r, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(p => p.RegistryId == r));
        public Task<bool> IsValidGeometryAsync(GeoPolygon p, CancellationToken ct = default) => Task.FromResult(true);

        public Task<long> InsertAsync(Parcel p, CancellationToken ct = default)
        {
            var id = Items.Count + 1L;
            Items.Add(p with { ParcelId = id });
            return Task.FromResult(id);
        }

        public Task UpdateAsync(Parcel p, CancellationToken ct = default)
        {
            Updated.Add(p);
            Items[Items.FindIndex(x => x.ParcelId == p.ParcelId)] = p;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ParcelOverlapHit>> FindOverlappingAsync(GeoPolygon c, double m, CancellationToken ct = default, long? exclude = null) =>
            Task.FromResult<IReadOnlyList<ParcelOverlapHit>>(OverlapWith is { } o ? new[] { o } : Array.Empty<ParcelOverlapHit>());

        public Task<IReadOnlyList<Parcel>> QueryAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ParcelNumbersResult> SetNumbersAsync(IReadOnlyList<ParcelNumbersWrite> writes, long? userId, Func<ParcelNumbers, ParcelNumbers, ActivityEntry> describe,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ParcelOverlap>> FindOverlapsAsync(double m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ParcelFingerprint> GetFingerprintAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<(string Kind, GeoPolygon Geometry)>> ListAllGeometriesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GeoPoint>> ListAnchorsAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> CountAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, long>> CountByKindAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ParcelDeleteOutcome> DeleteAsync(long parcelId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Versions : IEditVersionStore
    {
        public int Locks { get; private set; }
        public Task<string?> GetAsync(string target, long id, CancellationToken ct = default) => Task.FromResult<string?>("v1");

        public Task<IAsyncDisposable> BeginEditAsync(string target, long id, string? expectedVersion, CancellationToken ct = default)
        {
            Locks++;
            return Task.FromResult<IAsyncDisposable>(new Done());
        }

        private sealed class Done : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Log : IActivityLog
    {
        public List<ActivityEntry> Entries { get; } = new();
        public Task RecordAsync(ActivityEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task<IReadOnlyList<ActivityItem>> ListAsync(string entityType, long entityId, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ActivityItem>> ListByUserAsync(long userId, string? entityType, DateTime? fromUtc, DateTime? toUtc, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoAreas : IGeographicAreaStore
    {
        public Task<IReadOnlyList<GeographicArea>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GeographicArea>>(Array.Empty<GeographicArea>());
        public Task<GeographicArea?> GetByCodeAsync(int c, string code, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> InsertAsync(int c, string code, string name, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Greece : ICountryStore
    {
        public Task<int?> GetIdByCodeAsync(string code, CancellationToken ct = default) => Task.FromResult<int?>(1);
    }
}
