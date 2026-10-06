using Nadlan.KaekImporter.Api;
using Nadlan.KaekImporter.Geometry;
using Nadlan.KaekImporter.Ktimanet;

namespace Nadlan.KaekImporter.Import;

public enum ShapeOutcome { Saved, AlreadyHandled, Road, NadlanFailed }

/// <summary>Stops a run or click session, with a message for the user.</summary>
public sealed class StopImportException(string message) : Exception(message);

/// <summary>
/// One import (a sweep or a click-to-import session): what to do with each shape the site returns, and the report.
/// Both modes go through <see cref="HandleAsync"/>, so they skip, save and report the same way.
/// With no Nadlan client (offline) everything runs the same, but parcels are only reported, never saved.
/// </summary>
public sealed class ImportSession
{
    private const int MaxConsecutiveNadlanErrors = 3;

    private readonly KtimanetPage _site;
    private readonly NadlanApiClient? _nadlan;
    private readonly TextWriter _console;
    private readonly int? _geographicAreaId;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly DateTime _started = DateTime.Now;
    private int _nadlanErrors;

    public ImportSession(KtimanetPage site, NadlanApiClient? nadlan, int? geographicAreaId, TextWriter console)
    {
        _site = site;
        _nadlan = nadlan;
        _geographicAreaId = geographicAreaId;
        _console = console;
    }

    public ImportReport Report { get; } = new();

    /// <summary>Parcels Nadlan already has; they count as handled without asking Nadlan again.</summary>
    public void MarkAlreadyInNadlan(string kaek)
    {
        if (_seen.Add(kaek))
        {
            Report.Add(new ReportRow(kaek, ParcelResult.AlreadyInNadlan, null, null, Array.Empty<string>(),
                "Already in GreekPlot before this run - not requested from the site."));
        }
    }

    public async Task<ShapeOutcome> HandleAsync(KtimanetShape shape, IReadOnlyList<IReadOnlyList<LonLat>> rings, CancellationToken ct)
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

        if (_nadlan is null)
        {
            var area = PolygonMath.AreaSqm(shape.Rings);
            Report.Add(new ReportRow(shape.Kaek, ParcelResult.NotSaved, null, area, Array.Empty<string>(), "Offline - not saved"));
            await _site.AddShapeAsync(shape.Rings, "found");
            await _site.LogAsync($"{shape.Kaek}: found ({area:0} m²) - not saved (offline)", "warn");
            await _site.CountsAsync(Report.Counts);
            return ShapeOutcome.Saved; // paced like a real save: same load on the site
        }

        var saved = await SaveAsync(shape, rings, _nadlan, ct);
        await _site.CountsAsync(Report.Counts);
        _nadlanErrors = saved ? 0 : _nadlanErrors + 1;
        if (_nadlanErrors >= MaxConsecutiveNadlanErrors)
        {
            throw new StopImportException($"GreekPlot failed {_nadlanErrors} times in a row; stopped. Check the GreekPlot server and start again.");
        }

        return saved ? ShapeOutcome.Saved : ShapeOutcome.NadlanFailed;
    }

    /// <summary>Writes the CSV report and tells the user; <paramref name="text"/> is the headline.</summary>
    public async Task FinishAsync(string text)
    {
        string? csv = null;
        try
        {
            csv = await Report.WriteCsvAsync(_started);
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
    private async Task<bool> SaveAsync(KtimanetShape shape, IReadOnlyList<IReadOnlyList<LonLat>> rings, NadlanApiClient nadlan, CancellationToken ct)
    {
        var area = PolygonMath.AreaSqm(shape.Rings);
        var notes = $"Imported from gis.ktimanet.gr on {_started:yyyy-MM-dd}.";
        var result = await nadlan.CreateParcelAsync(shape.Kaek, _geographicAreaId, rings, area, notes, ct);
        switch (result.Outcome)
        {
            case CreateOutcome.Created:
                Report.Add(new ReportRow(shape.Kaek, ParcelResult.Created, result.ParcelId, area, result.OverlapsWith, result.Message));
                await _site.AddShapeAsync(shape.Rings, "created");
                await _site.LogAsync($"{shape.Kaek}: created ({area:0} m²)" +
                    (result.OverlapsWith.Count > 0 ? $", overlaps {string.Join(", ", result.OverlapsWith)}" : "") +
                    (result.Message is null ? "" : $" - {result.Message}"), result.Message is null ? "ok" : "warn");
                break;
            case CreateOutcome.AlreadyExists:
                Report.Add(new ReportRow(shape.Kaek, ParcelResult.AlreadyInNadlan, result.ParcelId, area, Array.Empty<string>(), result.Message));
                await _site.AddShapeAsync(shape.Rings, "exists");
                await _site.LogAsync($"{shape.Kaek}: already in GreekPlot");
                break;
            case CreateOutcome.Rejected:
                Report.Add(new ReportRow(shape.Kaek, ParcelResult.Rejected, null, area, Array.Empty<string>(), result.Message));
                await _site.AddShapeAsync(shape.Rings, "rejected");
                await _site.LogAsync($"{shape.Kaek}: rejected - {result.Message}", "err");
                break;
            case CreateOutcome.Unauthorized:
                // Every further parcel would be refused too: stop, and the panel offers "Connect to Nadlan".
                throw new StopImportException($"GreekPlot refused the importer's sign-in ({result.Message}). Click \"Connect to GreekPlot\" in the panel, then start again.");
            case CreateOutcome.ServerError:
                Report.Add(new ReportRow(shape.Kaek, ParcelResult.Rejected, null, area, Array.Empty<string>(), "GreekPlot server error - " + result.Message));
                await _site.AddShapeAsync(shape.Rings, "rejected");
                await _site.LogAsync($"{shape.Kaek}: GreekPlot server error - {result.Message}", "err");
                break;
        }

        _console.WriteLine($"{shape.Kaek}  {result.Outcome}  {result.Message}");
        return result.Outcome != CreateOutcome.ServerError;
    }
}
