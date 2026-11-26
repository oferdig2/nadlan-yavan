using Microsoft.Playwright;
using Nadlan.KaekImporter.Api;
using Nadlan.KaekImporter.Geometry;
using Nadlan.KaekImporter.Ktimanet;

namespace Nadlan.KaekImporter.Import;

public sealed record ImportSettings(
    TimeSpan MinParcelDelay, TimeSpan MaxParcelDelay,
    TimeSpan MinMissDelay, TimeSpan MaxMissDelay,
    double MaxViewMetres,
    int? MaxParcels);

/// <summary>
/// One "Acquire polygons" run over the view the user chose. The enclosed areas between the parcel lines (yellow, or
/// orange in Land Parcels areas) are found in the browser; each is clicked once in its middle (see
/// <see cref="AreaProbePlanner"/>) with the site's own click request for those lines, nearest the centre of the view
/// first. Requests go at a human pace: a random pause after each new parcel, a shorter one otherwise.
/// Parcels GreekPlot already has are skipped (no request) - unless update mode is on ("Also check parcels already in
/// GreekPlot", Admin only): then each is clicked once, well inside it, at the same pace as a new parcel, and GreekPlot
/// refreshes its Cadastre stage or files what changed for an Admin (brief PolygonStageAndUpdate §3).
/// </summary>
public sealed class ImportRun
{
    private const int MaxConsecutiveSiteErrors = 3;
    private const double SampleStepMetres = 5.0;

    private readonly IKtimanetSite _site;
    private readonly INadlanParcels? _nadlan;
    private readonly ImportSettings _settings;
    private readonly TextWriter _console;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string? _reportFolder;

    /// <param name="nadlan">Null = offline: the run is shown in full but nothing is saved.</param>
    /// <param name="delay">The pause between requests (tests record it instead of waiting).</param>
    public ImportRun(IKtimanetSite site, INadlanParcels? nadlan, ImportSettings settings, TextWriter console,
        Func<TimeSpan, CancellationToken, Task>? delay = null, string? reportFolder = null)
    {
        _site = site;
        _nadlan = nadlan;
        _settings = settings;
        _console = console;
        _delay = delay ?? Task.Delay;
        _reportFolder = reportFolder;
    }

    /// <summary>Reads the areas in the current view; returns them, or an error for the user.</summary>
    public async Task<(MapAreas? Areas, string? Error)> ReadViewAsync()
    {
        var (areas, error) = await _site.ReadAreasAsync(SampleStepMetres);
        if (areas is null) return (null, error);

        var e = areas.Extent;
        return e.Width > _settings.MaxViewMetres || e.Height > _settings.MaxViewMetres
            ? (null, $"Zoom in first: the view is {e.Width / 1000:0.0} x {e.Height / 1000:0.0} km; the limit is {_settings.MaxViewMetres / 1000:0.#} km.")
            : (areas, null);
    }

    /// <param name="checkExisting">Update mode: check the parcels GreekPlot already has in the view (one request each) instead
    /// of skipping them. Off = exactly the run as before.</param>
    public async Task RunAsync(MapAreas view, int? geographicAreaId, CancellationToken ct, bool checkExisting = false)
    {
        var session = new ImportSession(_site, _nadlan, geographicAreaId, _console, _reportFolder);
        var report = session.Report;
        var coverage = new Coverage();
        var excluded = new List<EgsaPoint>();
        var centre = view.Extent.Centre;
        var areas = view.Areas.OrderBy(a => Distance(a.Centre.Position, centre)).ToList();
        report.AreasTotal = areas.Count;

        await _site.SetSweepAsync(view.Extent);
        await _site.LogAsync($"{areas.Count} areas between the parcel lines in this view.");
        if (view.LinePixels == 0)
        {
            // E.g. "Cadastral Survey Post" chosen where the cadastre is in operation, or "Land Parcels" where it is not yet:
            // the layer is there but empty, and the whole view is one area. Still asked once, as before - it may be one parcel.
            await _site.LogAsync("No parcel lines are drawn in this view. If the map shows none here, switch its \"Θέλω να δω...\" " +
                                 "menu between Γεωτεμάχια Κτηματολογίου (Land Parcels) and Ανάρτηση Κτηματογράφησης (Cadastral Survey Post).", "warn");
        }
        try
        {
            var siteErrors = 0;
            if (_nadlan is not null)
            {
                var existing = await PreloadExistingAsync(view.Extent, coverage, session, _nadlan, checkExisting, ct);
                if (checkExisting && existing.Count > 0)
                {
                    siteErrors = await CheckExistingAsync(existing, view, session, coverage, ct);
                }
            }
            await _site.CountsAsync(report.Counts);

            foreach (var area in areas)
            {
                report.AreasDone++;
                for (var probes = 0; ; probes++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_settings.MaxParcels is int max && report.Count(ParcelResult.Created) + report.Count(ParcelResult.NotSaved) >= max)
                    {
                        throw new StopImportException($"Stopped after {max} new parcels (--max-parcels).");
                    }

                    var next = AreaProbePlanner.NextProbe(area, p => coverage.Contains(Egsa87.ToWgs84(p)), excluded);
                    if (next is not EgsaPoint point) break;
                    if (probes == AreaProbePlanner.MaxProbesPerArea)
                    {
                        await _site.LogAsync($"Part of an area near {point.X:0}, {point.Y:0} stayed unresolved after {probes} clicks.", "warn");
                        break;
                    }

                    await _site.SetProbeAsync(point);
                    await _site.StatusAsync($"Area {report.AreasDone} of {report.AreasTotal}: asking the site...");
                    string reply;
                    try
                    {
                        report.Requests++;
                        reply = await _site.QueryAsync(point, view.ClickRequest);
                        siteErrors = 0;
                    }
                    catch (PlaywrightException ex) when (!ct.IsCancellationRequested)
                    {
                        siteErrors = await SiteErrorAsync(ex, siteErrors, ct);
                        continue;
                    }

                    var shape = KtimanetReply.Parse(reply);
                    if (shape is null)
                    {
                        excluded.Add(point); // nothing registered here (sea, unmapped land): the area is done
                        await PauseAsync(_settings.MinMissDelay, _settings.MaxMissDelay, ct);
                        break;
                    }

                    var rings = Egsa87.ToWgs84(shape.Rings);
                    coverage.Add(rings);
                    if (!PolygonMath.Contains(rings, Egsa87.ToWgs84(point)))
                    {
                        excluded.Add(point); // the site answered with a shape that isn't here; don't ask here again
                    }

                    var outcome = await session.HandleAsync(shape, rings, ct, view.ClickRequest);
                    await (outcome is ShapeOutcome.Saved or ShapeOutcome.NadlanFailed
                        ? PauseAsync(_settings.MinParcelDelay, _settings.MaxParcelDelay, ct)
                        : PauseAsync(_settings.MinMissDelay, _settings.MaxMissDelay, ct));
                }
            }

            await session.FinishAsync("Done: " + report.Summary);
        }
        catch (OperationCanceledException)
        {
            await session.FinishAsync("Stopped: " + report.Summary);
        }
        catch (Exception ex) when (ex is StopImportException or HttpRequestException)
        {
            await session.FinishAsync(ex.Message + " So far: " + report.Summary);
        }
    }

    /// <summary>
    /// Parcels with a real KAEK that Nadlan already has need no request to the site; they are listed in the report.
    /// Provisional (TMP-) ones are not skipped - the real parcel is imported over them. Returns the real ones (to check).
    /// </summary>
    private async Task<IReadOnlyList<ExistingParcel>> PreloadExistingAsync(MapExtent extent, Coverage coverage, ImportSession session, INadlanParcels nadlan,
        bool checkExisting, CancellationToken ct)
    {
        var corners = new[]
        {
            Egsa87.ToWgs84(new EgsaPoint(extent.Left, extent.Top)), Egsa87.ToWgs84(new EgsaPoint(extent.Right, extent.Top)),
            Egsa87.ToWgs84(new EgsaPoint(extent.Left, extent.Bottom)), Egsa87.ToWgs84(new EgsaPoint(extent.Right, extent.Bottom)),
        };
        var existing = (await nadlan.ListParcelsInAsync(corners.Min(c => c.Lon), corners.Min(c => c.Lat),
            corners.Max(c => c.Lon), corners.Max(c => c.Lat), ct)).Where(p => !p.IsProvisional).ToList();

        foreach (var parcel in existing)
        {
            coverage.Add(parcel.Rings);
            session.MarkAlreadyInNadlan(parcel.RegistryId, listed: !checkExisting);
            await _site.AddShapeAsync(Egsa87.FromWgs84(parcel.Rings), "exists"); // so skipped parcels don't look missed
        }

        if (existing.Count > 0)
        {
            await _site.LogAsync(checkExisting
                ? $"{existing.Count} parcels in this view are already in GreekPlot; checking each of them first (one request each)."
                : $"{existing.Count} parcels in this view are already in GreekPlot; skipping them.");
        }

        return existing;
    }

    /// <summary>
    /// Update mode: one click per existing parcel, at a point well inside it (its pole of inaccessibility), with the view's
    /// request and the pace of a new parcel; nearest the centre of the view first. Returns the site errors in a row so far.
    /// </summary>
    private async Task<int> CheckExistingAsync(IReadOnlyList<ExistingParcel> existing, MapAreas view, ImportSession session, Coverage coverage,
        CancellationToken ct)
    {
        var centre = view.Extent.Centre;
        var todo = existing
            .Select(p => (Parcel: p, Inside: PolygonMath.InsidePoint(Egsa87.FromWgs84(p.Rings))))
            .OrderBy(x => x.Inside is { } i ? Distance(i.Point, centre) : double.MaxValue)
            .ToList();
        var siteErrors = 0;
        var done = 0;
        foreach (var (parcel, inside) in todo)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            if (inside is not { } at)
            {
                session.Report.Add(new ReportRow(parcel.RegistryId, ParcelResult.CheckFailed, parcel.ParcelId, null, Array.Empty<string>(),
                    "No point inside this parcel's polygon to click (degenerate shape) - not checked."));
                continue;
            }

            await _site.SetProbeAsync(at.Point);
            await _site.StatusAsync($"Checking parcel {done} of {todo.Count} already in GreekPlot...");
            string reply;
            try
            {
                session.Report.Requests++;
                reply = await _site.QueryAsync(at.Point, view.ClickRequest);
                siteErrors = 0;
            }
            catch (PlaywrightException ex) when (!ct.IsCancellationRequested)
            {
                siteErrors = await SiteErrorAsync(ex, siteErrors, ct);
                continue;
            }

            var seen = DateTime.UtcNow;
            KtimanetShape? shape;
            try
            {
                shape = KtimanetReply.Parse(reply);
            }
            catch (FormatException ex)
            {
                await _site.LogAsync($"{parcel.RegistryId}: could not read the site's reply - {ex.Message}", "err");
                await PauseAsync(_settings.MinParcelDelay, _settings.MaxParcelDelay, ct);
                continue;
            }

            if (shape is not null) coverage.Add(Egsa87.ToWgs84(shape.Rings)); // the sweep needn't ask there again
            try
            {
                await session.CheckAsync(parcel, shape, view.ClickRequest, seen, ct);
            }
            catch (ChecksUnsupportedException ex)
            {
                await _site.LogAsync(ex.Message, "warn");
                break; // an older GreekPlot: the sweep for new parcels still runs
            }

            await PauseAsync(_settings.MinParcelDelay, _settings.MaxParcelDelay, ct);
        }

        return siteErrors;
    }

    /// <summary>A failed request: logged, and after a few in a row the run stops. Returns the new count.</summary>
    private async Task<int> SiteErrorAsync(PlaywrightException ex, int siteErrors, CancellationToken ct)
    {
        await _site.LogAsync($"Site error: {ex.Message.Split('\n')[0].Trim()}", "err");
        if (++siteErrors >= MaxConsecutiveSiteErrors)
        {
            throw new StopImportException(
                "Ktimatologio stopped answering. Reload its page and start again - parcels already imported are skipped.");
        }

        await PauseAsync(_settings.MinParcelDelay, _settings.MaxParcelDelay, ct);
        return siteErrors;
    }

    private async Task PauseAsync(TimeSpan min, TimeSpan max, CancellationToken ct)
    {
        var delay = min + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * (max - min).TotalMilliseconds);
        if (delay >= TimeSpan.FromSeconds(3))
        {
            await _site.StatusAsync($"Waiting {delay.TotalSeconds:0} s before the next request...");
        }

        await _delay(delay, ct);
    }

    private static double Distance(EgsaPoint a, EgsaPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
