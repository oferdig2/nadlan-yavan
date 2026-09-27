using Microsoft.Playwright;
using Nadlan.KaekImporter.Geometry;
using Nadlan.KaekImporter.Ktimanet;
using Nadlan.KaekImporter.Api;

namespace Nadlan.KaekImporter.Import;

public sealed record ImportSettings(
    double GridStepMetres,
    TimeSpan MinParcelDelay, TimeSpan MaxParcelDelay,
    TimeSpan MinMissDelay, TimeSpan MaxMissDelay,
    double MaxViewMetres,
    int? MaxParcels);

/// <summary>
/// One "Acquire polygons" run over the view the user chose. Asks the site about one point at a time, at a human
/// pace: a random 10-30 s after each new parcel, a shorter pause after a point with nothing new (sea, road).
/// </summary>
public sealed class ImportRun
{
    private const int MaxConsecutiveErrors = 3;

    private readonly KtimanetPage _site;
    private readonly NadlanApiClient _nadlan;
    private readonly ImportSettings _settings;
    private readonly TextWriter _console;

    public ImportRun(KtimanetPage site, NadlanApiClient nadlan, ImportSettings settings, TextWriter console)
    {
        _site = site;
        _nadlan = nadlan;
        _settings = settings;
        _console = console;
    }

    /// <summary>Checks the view before anything starts; returns an error for the user, or null.</summary>
    public string? Validate(MapExtent extent) =>
        extent.Width > _settings.MaxViewMetres || extent.Height > _settings.MaxViewMetres
            ? $"Zoom in first: the view is {extent.Width / 1000:0.0} x {extent.Height / 1000:0.0} km; the limit is {_settings.MaxViewMetres / 1000:0.#} km."
            : null;

    public async Task<ImportReport> RunAsync(MapExtent extent, int? geographicAreaId, CancellationToken ct)
    {
        var started = DateTime.Now;
        var report = new ImportReport();
        var grid = new SweepGrid(extent, _settings.GridStepMetres);
        report.PointsTotal = grid.TotalPoints;
        var coverage = new Coverage();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var notes = $"Imported from gis.ktimanet.gr on {started:yyyy-MM-dd}.";

        await _site.SetSweepAsync(extent);
        try
        {
            await PreloadExistingAsync(extent, coverage, seen, report, ct);
            await _site.CountsAsync(report.Counts);

            var siteErrors = 0;
            var nadlanErrors = 0;
            foreach (var point in grid.Points())
            {
                ct.ThrowIfCancellationRequested();
                report.PointsDone++;
                if (_settings.MaxParcels is int max && report.Count(ParcelResult.Created) >= max)
                {
                    await _site.LogAsync($"Stopped after {max} new parcels (--max-parcels).", "warn");
                    break;
                }

                var position = Egsa87.ToWgs84(point.Position);
                if (coverage.Contains(position))
                {
                    continue; // inside a shape we already have: no request
                }

                await _site.SetProbeAsync(point.Position);
                await _site.StatusAsync($"Pass {point.Pass}: asking the site about point {report.PointsDone} of {report.PointsTotal}...");
                string reply;
                try
                {
                    report.Requests++;
                    reply = await _site.QueryAsync(point.Position);
                    siteErrors = 0;
                }
                catch (PlaywrightException ex) when (!ct.IsCancellationRequested)
                {
                    siteErrors++;
                    await _site.LogAsync($"Site error: {FirstLine(ex.Message)}", "err");
                    if (siteErrors >= MaxConsecutiveErrors)
                    {
                        throw new StopRunException(
                            "Ktimatologio stopped answering. Reload its page and start again - parcels already imported are skipped.");
                    }

                    await PauseAsync(_settings.MinParcelDelay, _settings.MaxParcelDelay, ct);
                    continue;
                }

                var shape = KtimanetReply.Parse(reply);
                if (shape is null)
                {
                    grid.MarkEmpty(point);
                    await PauseAsync(_settings.MinMissDelay, _settings.MaxMissDelay, ct);
                    continue;
                }

                var rings = shape.Rings.Select(r => (IReadOnlyList<LonLat>)r.Select(Egsa87.ToWgs84).ToList()).ToList();
                coverage.Add(rings);

                if (!seen.Add(shape.Kaek))
                {
                    await PauseAsync(_settings.MinMissDelay, _settings.MaxMissDelay, ct);
                    continue;
                }

                if (!shape.IsLandParcel)
                {
                    report.AddRoad(shape.Kaek);
                    await _site.AddShapeAsync(shape.Rings, "road");
                    await _site.LogAsync($"{shape.Kaek}: road / special property - skipped");
                    await _site.CountsAsync(report.Counts);
                    await PauseAsync(_settings.MinMissDelay, _settings.MaxMissDelay, ct);
                    continue;
                }

                var saved = await SaveAsync(shape, rings, geographicAreaId, notes, report, ct);
                nadlanErrors = saved ? 0 : nadlanErrors + 1;
                if (nadlanErrors >= MaxConsecutiveErrors)
                {
                    throw new StopRunException($"Nadlan failed {nadlanErrors} times in a row; stopped. Check the Nadlan server and start again.");
                }

                await _site.CountsAsync(report.Counts);
                await PauseAsync(_settings.MinParcelDelay, _settings.MaxParcelDelay, ct);
            }

            await FinishAsync(report, started, "Done: " + report.Summary);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(report, started, "Stopped: " + report.Summary);
        }
        catch (Exception ex) when (ex is StopRunException or HttpRequestException)
        {
            await FinishAsync(report, started, ex.Message + " So far: " + report.Summary);
        }

        return report;
    }

    /// <summary>
    /// Parcels with a real KAEK that Nadlan already has need no request to the site; they are listed in the report.
    /// Provisional (TMP-) ones are not skipped - the real parcel is imported over them.
    /// </summary>
    private async Task PreloadExistingAsync(MapExtent extent, Coverage coverage, HashSet<string> seen, ImportReport report, CancellationToken ct)
    {
        var corners = new[]
        {
            Egsa87.ToWgs84(new EgsaPoint(extent.Left, extent.Top)), Egsa87.ToWgs84(new EgsaPoint(extent.Right, extent.Top)),
            Egsa87.ToWgs84(new EgsaPoint(extent.Left, extent.Bottom)), Egsa87.ToWgs84(new EgsaPoint(extent.Right, extent.Bottom)),
        };
        var existing = await _nadlan.ListParcelsInAsync(corners.Min(c => c.Lon), corners.Min(c => c.Lat),
            corners.Max(c => c.Lon), corners.Max(c => c.Lat), ct);

        foreach (var parcel in existing.Where(p => !p.IsProvisional))
        {
            coverage.Add(parcel.Rings);
            seen.Add(parcel.RegistryId);
            report.Add(new ReportRow(parcel.RegistryId, ParcelResult.AlreadyInNadlan, null, null, Array.Empty<string>(),
                "Already in Nadlan before this run - not requested from the site."));
        }

        if (report.Rows.Count > 0)
        {
            await _site.LogAsync($"{report.Rows.Count} parcels in this view are already in Nadlan; skipping them.");
        }
    }

    /// <summary>False when Nadlan itself failed (5xx), so the caller can stop if that keeps happening.</summary>
    private async Task<bool> SaveAsync(KtimanetShape shape, IReadOnlyList<IReadOnlyList<LonLat>> rings, int? geographicAreaId, string notes,
        ImportReport report, CancellationToken ct)
    {
        var area = PolygonMath.AreaSqm(shape.Rings);
        var result = await _nadlan.CreateParcelAsync(shape.Kaek, geographicAreaId, rings, area, notes, ct);
        switch (result.Outcome)
        {
            case CreateOutcome.Created:
                report.Add(new ReportRow(shape.Kaek, ParcelResult.Created, result.ParcelId, area, result.OverlapsWith, result.Message));
                await _site.AddShapeAsync(shape.Rings, "created");
                await _site.LogAsync($"{shape.Kaek}: created ({area:0} m²)" +
                    (result.OverlapsWith.Count > 0 ? $", overlaps {string.Join(", ", result.OverlapsWith)}" : "") +
                    (result.Message is null ? "" : $" - {result.Message}"), result.Message is null ? "ok" : "warn");
                break;
            case CreateOutcome.AlreadyExists:
                report.Add(new ReportRow(shape.Kaek, ParcelResult.AlreadyInNadlan, result.ParcelId, area, Array.Empty<string>(), result.Message));
                await _site.AddShapeAsync(shape.Rings, "exists");
                await _site.LogAsync($"{shape.Kaek}: already in Nadlan");
                break;
            case CreateOutcome.Rejected:
                report.Add(new ReportRow(shape.Kaek, ParcelResult.Rejected, null, area, Array.Empty<string>(), result.Message));
                await _site.AddShapeAsync(shape.Rings, "rejected");
                await _site.LogAsync($"{shape.Kaek}: rejected - {result.Message}", "err");
                break;
            case CreateOutcome.ServerError:
                report.Add(new ReportRow(shape.Kaek, ParcelResult.Rejected, null, area, Array.Empty<string>(), "Nadlan server error - " + result.Message));
                await _site.AddShapeAsync(shape.Rings, "rejected");
                await _site.LogAsync($"{shape.Kaek}: Nadlan server error - {result.Message}", "err");
                break;
        }

        _console.WriteLine($"{shape.Kaek}  {result.Outcome}  {result.Message}");
        return result.Outcome != CreateOutcome.ServerError;
    }

    private async Task FinishAsync(ImportReport report, DateTime started, string text)
    {
        string? csv = null;
        try
        {
            csv = await report.WriteCsvAsync(started);
        }
        catch (IOException ex)
        {
            await _site.LogAsync("Could not write the report file: " + ex.Message, "err");
        }

        await _site.CountsAsync(report.Counts);
        await _site.FinishedAsync(text + (csv is null ? "" : $" Report: {csv}"));
        _console.WriteLine(text);
        if (csv is not null) _console.WriteLine("Report: " + csv);
    }

    private async Task PauseAsync(TimeSpan min, TimeSpan max, CancellationToken ct)
    {
        var delay = min + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * (max - min).TotalMilliseconds);
        if (delay >= TimeSpan.FromSeconds(5))
        {
            await _site.StatusAsync($"Waiting {delay.TotalSeconds:0} s before the next request...");
        }

        await Task.Delay(delay, ct);
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    private sealed class StopRunException(string message) : Exception(message);
}
