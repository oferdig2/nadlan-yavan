using Nadlan.KaekImporter.Api;
using Nadlan.KaekImporter.Geometry;
using Nadlan.KaekImporter.Ktimanet;

namespace Nadlan.KaekImporter.Import;

public enum ShapeOutcome { Saved, AlreadyHandled, Road, NadlanFailed }

/// <summary>Stops a run or click session, with a message for the user.</summary>
public sealed class StopImportException(string message) : Exception(message);

/// <summary>The GreekPlot server is older than update mode (no check call): the run goes on without checking.</summary>
public sealed class ChecksUnsupportedException(string message) : Exception(message);

/// <summary>
/// One import (a sweep or a click-to-import session): what to do with each shape the site returns, and the report.
/// Both modes go through <see cref="HandleAsync"/>, so they skip, save and report the same way.
/// With no Nadlan client (offline) everything runs the same, but parcels are only reported, never saved.
/// Every new parcel carries the Cadastre stage of the click request that found it (GETPST = in operation, GETPSTKG = survey
/// post, GETPSTKGPRO = pre-posting) and when it was seen.
/// </summary>
public sealed class ImportSession
{
    private const int MaxConsecutiveNadlanErrors = 3;

    private readonly IKtimanetSite _site;
    private readonly INadlanParcels? _nadlan;
    private readonly TextWriter _console;
    private readonly int? _geographicAreaId;
    private readonly string? _reportFolder;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly DateTime _started = DateTime.Now;
    private int _nadlanErrors;

    /// <param name="geographicAreaId">Required whenever parcels are saved (nadlan not null): a parcel without an area must
    /// never be created. Program checks it first and tells the user.</param>
    /// <param name="reportFolder">Where the CSV report goes; default Documents\Nadlan\KAEK imports.</param>
    public ImportSession(IKtimanetSite site, INadlanParcels? nadlan, int? geographicAreaId, TextWriter console, string? reportFolder = null)
    {
        if (nadlan is not null && geographicAreaId is null)
        {
            throw new ArgumentException("A geographic area is required to save parcels.", nameof(geographicAreaId));
        }

        _site = site;
        _nadlan = nadlan;
        _geographicAreaId = geographicAreaId;
        _console = console;
        _reportFolder = reportFolder;
        Report = new ImportReport { Offline = nadlan is null };
    }

    public ImportReport Report { get; }

    /// <summary>Parcels Nadlan already has; they count as handled without asking Nadlan again.</summary>
    /// <param name="listed">False in update mode: the check of the parcel makes its report row.</param>
    public void MarkAlreadyInNadlan(string kaek, bool listed = true)
    {
        if (_seen.Add(kaek) && listed)
        {
            Report.Add(new ReportRow(kaek, ParcelResult.AlreadyInNadlan, null, null, Array.Empty<string>(),
                "Already in GreekPlot before this run - not requested from the site."));
        }
    }

    /// <summary>A KAEK met while checking (another one at an existing parcel's point): never created by the sweep (§3.2 case 3).</summary>
    public void MarkSeen(string kaek) => _seen.Add(kaek);

    /// <param name="request">The click request that was asked, when the reply doesn't say (the sweep's view).</param>
    public async Task<ShapeOutcome> HandleAsync(KtimanetShape shape, IReadOnlyList<IReadOnlyList<LonLat>> rings, CancellationToken ct, string? request = null)
    {
        if (!_seen.Add(shape.Kaek))
        {
            return ShapeOutcome.AlreadyHandled;
        }

        if (!shape.IsLandParcel)
        {
            Report.AddRoad(shape.Kaek);
            await _site.AddShapeAsync(shape.Rings, "road");
            await _site.LogAsync($"{shape.Kaek}: road / special property - skipped");
            await _site.CountsAsync(Report.Counts);
            return ShapeOutcome.Road;
        }

        var stage = KtimanetClick.StageOf(shape.Request ?? request);
        if (_nadlan is null)
        {
            var area = PolygonMath.AreaSqm(shape.Rings);
            Report.Add(new ReportRow(shape.Kaek, ParcelResult.NotSaved, null, area, Array.Empty<string>(), "Offline - not saved", stage));
            await _site.AddShapeAsync(shape.Rings, "found");
            await _site.LogAsync($"{shape.Kaek}: found ({area:0} m², {KtimanetClick.StageLabel(stage)}) - not saved (offline)", "warn");
            await _site.CountsAsync(Report.Counts);
            return ShapeOutcome.Saved; // paced like a real save: same load on the site
        }

        var saved = await SaveAsync(shape, rings, stage, _nadlan, ct);
        await _site.CountsAsync(Report.Counts);
        CountNadlan(saved);
        return saved ? ShapeOutcome.Saved : ShapeOutcome.NadlanFailed;
    }

    /// <summary>
    /// Update mode: what the site answered at a point well inside a parcel GreekPlot already has, sent to GreekPlot, which
    /// decides (stage updated, unchanged, a change filed for an Admin, another KAEK there, not found). Nothing is created.
    /// </summary>
    /// <param name="shape">The site's shape at the point; null = nothing there.</param>
    public async Task CheckAsync(ExistingParcel parcel, KtimanetShape? shape, string request, DateTime seenUtc, CancellationToken ct)
    {
        if (_nadlan is null) return;
        var stage = KtimanetClick.StageOf(shape?.Request ?? request) ?? throw new StopImportException($"Unknown map-click request {request}.");
        var rings = shape is null ? null : Egsa87.ToWgs84(shape.Rings);
        double? area = shape is null ? null : PolygonMath.AreaSqm(shape.Rings);
        if (shape is not null && shape.Kaek != parcel.RegistryId)
        {
            MarkSeen(shape.Kaek); // not created by the sweep either: an Admin looks at it first
        }

        var result = await _nadlan.CheckParcelAsync(parcel.ParcelId, shape is not null, shape?.Kaek, stage, seenUtc, rings, area, ct);
        if (result.Outcome is CheckOutcome.Unsupported)
        {
            throw new ChecksUnsupportedException("Existing parcels are not checked: " + result.Message);
        }

        if (result.Outcome is CheckOutcome.Unauthorized)
        {
            throw new StopImportException($"GreekPlot refused the importer's sign-in ({result.Message}). Click \"Connect to GreekPlot\" in the panel, then start again.");
        }

        var label = KtimanetClick.StageLabel(stage);
        var (row, kind, text, logKind) = result.Outcome switch
        {
            CheckOutcome.Unchanged => (ParcelResult.CheckUnchanged, "checked", $"unchanged ({label})", "ok"),
            CheckOutcome.StageUpdated => (ParcelResult.CheckStageUpdated, "checked", $"stage updated: {label}", "ok"),
            CheckOutcome.ProposalFiled => (ParcelResult.CheckProposed, "proposed",
                $"the Cadastre's polygon differs by {result.DifferenceSqm:0.#} m² - filed for an Admin (Cadastre changes)", "warn"),
            CheckOutcome.KaekMismatch => (ParcelResult.CheckKaekMismatch, "proposed", $"the site answers another KAEK there ({shape?.Kaek}) - filed for an Admin", "warn"),
            CheckOutcome.NotFound => (ParcelResult.CheckNotFound, "proposed", $"nothing there in this layer ({label}) - filed for an Admin", "warn"),
            _ => (ParcelResult.CheckFailed, "rejected", "check failed - " + result.Message, "err"),
        };
        var note = result.Message is not null && row != ParcelResult.CheckFailed ? $" ({result.Message})" : "";
        Report.Add(new ReportRow(parcel.RegistryId, row, parcel.ParcelId, area, Array.Empty<string>(), text + note, stage));
        await _site.AddShapeAsync(shape?.Rings ?? Egsa87.FromWgs84(parcel.Rings), kind);
        await _site.LogAsync($"{parcel.RegistryId}: {text}{note}", logKind);
        await _site.CountsAsync(Report.Counts);
        _console.WriteLine($"{parcel.RegistryId}  check {result.Outcome}  {result.Message}");
        CountNadlan(result.Outcome != CheckOutcome.ServerError);
    }

    private void CountNadlan(bool ok)
    {
        _nadlanErrors = ok ? 0 : _nadlanErrors + 1;
        if (_nadlanErrors >= MaxConsecutiveNadlanErrors)
        {
            throw new StopImportException($"GreekPlot failed {_nadlanErrors} times in a row; stopped. Check the GreekPlot server and start again.");
        }
    }

    /// <summary>Writes the CSV report and tells the user; <paramref name="text"/> is the headline.</summary>
    public async Task FinishAsync(string text)
    {
        string? csv = null;
        try
        {
            csv = await Report.WriteCsvAsync(_started, _reportFolder);
        }
        catch (IOException ex)
        {
            await _site.LogAsync("Could not write the report file: " + ex.Message, "err");
        }

        await _site.CountsAsync(Report.Counts);
        await _site.FinishedAsync(text + (csv is null ? "" : $" Report: {csv}"));
        _console.WriteLine(text);
        if (csv is not null) _console.WriteLine("Report: " + csv);
    }

    /// <summary>False when Nadlan itself failed (5xx), so the caller can stop if that keeps happening.</summary>
    private async Task<bool> SaveAsync(KtimanetShape shape, IReadOnlyList<IReadOnlyList<LonLat>> rings, string? stage, INadlanParcels nadlan, CancellationToken ct)
    {
        var area = PolygonMath.AreaSqm(shape.Rings);
        var notes = $"Imported from gis.ktimanet.gr on {_started:yyyy-MM-dd}.";
        var result = await nadlan.CreateParcelAsync(shape.Kaek, _geographicAreaId, rings, area, notes, ct, stage, stage is null ? null : DateTime.UtcNow);
        switch (result.Outcome)
        {
            case CreateOutcome.Created:
                Report.Add(new ReportRow(shape.Kaek, ParcelResult.Created, result.ParcelId, area, result.OverlapsWith, result.Message, stage));
                await _site.AddShapeAsync(shape.Rings, "created");
                await _site.LogAsync($"{shape.Kaek}: created ({area:0} m², {KtimanetClick.StageLabel(stage)})" +
                    (result.OverlapsWith.Count > 0 ? $", overlaps {string.Join(", ", result.OverlapsWith)}" : "") +
                    (result.Message is null ? "" : $" - {result.Message}"), result.Message is null ? "ok" : "warn");
                break;
            case CreateOutcome.AlreadyExists:
                Report.Add(new ReportRow(shape.Kaek, ParcelResult.AlreadyInNadlan, result.ParcelId, area, Array.Empty<string>(), result.Message, stage));
                await _site.AddShapeAsync(shape.Rings, "exists");
                await _site.LogAsync($"{shape.Kaek}: already in GreekPlot");
                break;
            case CreateOutcome.Rejected:
                Report.Add(new ReportRow(shape.Kaek, ParcelResult.Rejected, null, area, Array.Empty<string>(), result.Message, stage));
                await _site.AddShapeAsync(shape.Rings, "rejected");
                await _site.LogAsync($"{shape.Kaek}: rejected - {result.Message}", "err");
                break;
            case CreateOutcome.Unauthorized:
                // Every further parcel would be refused too: stop, and the panel offers "Connect to Nadlan".
                throw new StopImportException($"GreekPlot refused the importer's sign-in ({result.Message}). Click \"Connect to GreekPlot\" in the panel, then start again.");
            case CreateOutcome.ServerError:
                Report.Add(new ReportRow(shape.Kaek, ParcelResult.Rejected, null, area, Array.Empty<string>(), "GreekPlot server error - " + result.Message, stage));
                await _site.AddShapeAsync(shape.Rings, "rejected");
                await _site.LogAsync($"{shape.Kaek}: GreekPlot server error - {result.Message}", "err");
                break;
        }

        _console.WriteLine($"{shape.Kaek}  {result.Outcome}  {result.Message}");
        return result.Outcome != CreateOutcome.ServerError;
    }
}
