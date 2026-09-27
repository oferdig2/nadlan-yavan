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
/// One "Acquire polygons" run over the view the user chose. The enclosed areas between the yellow parcel lines are
/// found in the browser; each is clicked once in its middle (see <see cref="AreaProbePlanner"/>), nearest the centre
/// of the view first. Requests go at a human pace: a random pause after each new parcel, a shorter one otherwise.
/// </summary>
public sealed class ImportRun
{
    private const int MaxConsecutiveSiteErrors = 3;
    private const double SampleStepMetres = 5.0;

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

    public async Task RunAsync(MapAreas view, int? geographicAreaId, CancellationToken ct)
    {
        var session = new ImportSession(_site, _nadlan, geographicAreaId, _console);
        var report = session.Report;
        var coverage = new Coverage();
        var excluded = new List<EgsaPoint>();
        var centre = view.Extent.Centre;
        var areas = view.Areas.OrderBy(a => Distance(a.Centre.Position, centre)).ToList();
        report.AreasTotal = areas.Count;

        await _site.SetSweepAsync(view.Extent);
        await _site.LogAsync($"{areas.Count} areas between the parcel lines in this view.");
        try
        {
            await PreloadExistingAsync(view.Extent, coverage, session, ct);
            await _site.CountsAsync(report.Counts);

            var siteErrors = 0;
            foreach (var area in areas)
            {
                report.AreasDone++;
                for (var probes = 0; ; probes++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_settings.MaxParcels is int max && report.Count(ParcelResult.Created) >= max)
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
                        reply = await _site.QueryAsync(point);
                        siteErrors = 0;
                    }
                    catch (PlaywrightException ex) when (!ct.IsCancellationRequested)
                    {
                        await _site.LogAsync($"Site error: {ex.Message.Split('\n')[0].Trim()}", "err");
                        if (++siteErrors >= MaxConsecutiveSiteErrors)
                        {
                            throw new StopImportException(
                                "Ktimatologio stopped answering. Reload its page and start again - parcels already imported are skipped.");
                        }

                        await PauseAsync(_settings.MinParcelDelay, _settings.MaxParcelDelay, ct);
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

                    var outcome = await session.HandleAsync(shape, rings, ct);
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
    /// Provisional (TMP-) ones are not skipped - the real parcel is imported over them.
    /// </summary>
    private async Task PreloadExistingAsync(MapExtent extent, Coverage coverage, ImportSession session, CancellationToken ct)
    {
        var corners = new[]
        {
            Egsa87.ToWgs84(new EgsaPoint(extent.Left, extent.Top)), Egsa87.ToWgs84(new EgsaPoint(extent.Right, extent.Top)),
            Egsa87.ToWgs84(new EgsaPoint(extent.Left, extent.Bottom)), Egsa87.ToWgs84(new EgsaPoint(extent.Right, extent.Bottom)),
        };
        var existing = (await _nadlan.ListParcelsInAsync(corners.Min(c => c.Lon), corners.Min(c => c.Lat),
            corners.Max(c => c.Lon), corners.Max(c => c.Lat), ct)).Where(p => !p.IsProvisional).ToList();

        foreach (var parcel in existing)
        {
            coverage.Add(parcel.Rings);
            session.MarkAlreadyInNadlan(parcel.RegistryId);
        }

        if (existing.Count > 0)
        {
            await _site.LogAsync($"{existing.Count} parcels in this view are already in Nadlan; skipping them.");
        }
    }

    private async Task PauseAsync(TimeSpan min, TimeSpan max, CancellationToken ct)
    {
        var delay = min + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * (max - min).TotalMilliseconds);
        if (delay >= TimeSpan.FromSeconds(3))
        {
            await _site.StatusAsync($"Waiting {delay.TotalSeconds:0} s before the next request...");
        }

        await Task.Delay(delay, ct);
    }

    private static double Distance(EgsaPoint a, EgsaPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
