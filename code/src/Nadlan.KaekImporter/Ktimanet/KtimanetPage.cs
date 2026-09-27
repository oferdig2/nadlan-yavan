using System.Text.Json;
using Microsoft.Playwright;
using Nadlan.KaekImporter.Geometry;
using Nadlan.KaekImporter.Import;

namespace Nadlan.KaekImporter.Ktimanet;

/// <summary>The gis.ktimanet.gr map tab: asks it about points, and drives the injected Nadlan panel.</summary>
public sealed class KtimanetPage
{
    public const string MapUrl = "https://gis.ktimanet.gr/gis/map";

    // Exactly what the site sends on a map click (same session, same anti-forgery token), just at our coordinates.
    private const string QueryScript = """
        async ([x, y]) => {
          const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
          if (!token) throw new Error('The Ktimatologio page is not ready (no request token). Reload it.');
          const response = await fetch('/gis/map/PostHandler', {
            method: 'POST',
            headers: {
              'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
              'X-Requested-With': 'XMLHttpRequest',
              'RequestVerificationToken': token
            },
            body: 'Message=' + encodeURIComponent('GETPSTKG|' + x.toFixed(3) + ',' + y.toFixed(3) + '|')
          });
          if (!response.ok) throw new Error('Ktimatologio replied HTTP ' + response.status);
          const text = await response.text();
          try { return JSON.parse(text); } catch { return text; }
        }
        """;

    private readonly IPage _page;

    public KtimanetPage(IPage page) => _page = page;

    /// <summary>
    /// The enclosed areas between the parcel lines in the current view (found by the panel script from the lines
    /// layer's pixels), or an error for the user. Waits a few seconds for line tiles that are still loading.
    /// </summary>
    public async Task<(MapAreas? Areas, string? Error)> ReadAreasAsync(double sampleStepMetres)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var reply = await _page.EvaluateAsync<JsonElement>(
                "s => typeof MyMap === 'undefined' || !window.NadlanPanel ? { error: 'The Ktimatologio map is not loaded yet.' } : NadlanPanel.findAreas(s)",
                sampleStepMetres);
            var dto = reply.Deserialize<AreasDto>(Json)!;
            if (dto.Error is not null) return (null, dto.Error);
            if (dto.Retry)
            {
                await Task.Delay(500);
                continue;
            }

            var e = dto.Extent!;
            return (new MapAreas(new MapExtent(e.Left, e.Right, e.Top, e.Bottom), dto.MetresPerPixel,
                dto.Areas!.Select(a => new MapArea(a.Size, Point(a.Point), a.Samples.Select(Point).ToList())).ToList()), null);
        }

        return (null, "The parcel lines did not finish loading. Wait for the map, then try again.");
    }

    private static AreaPoint Point(double[] p) => new(new EgsaPoint(p[0], p[1]), p[2]);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AreasDto(string? Error, bool Retry, ExtentDto? Extent, double MetresPerPixel, List<AreaDto>? Areas);
    private sealed record ExtentDto(double Left, double Right, double Top, double Bottom);
    private sealed record AreaDto(int Size, double[] Point, List<double[]> Samples);

    /// <summary>The raw reply for one point, e.g. "GETPSTKG|x,y|2@[...]@@120981108035@...".</summary>
    public Task<string> QueryAsync(EgsaPoint p) => _page.EvaluateAsync<string>(QueryScript, new[] { p.X, p.Y });

    // Panel calls are best effort: the user may reload or close the tab, and that must not stop the import.
    public Task StatusAsync(string text) => Ui("t => window.NadlanPanel && NadlanPanel.status(t)", text);
    public Task LogAsync(string text, string kind = "") => Ui("([t, k]) => window.NadlanPanel && NadlanPanel.log(t, k)", new[] { text, kind });
    public Task CountsAsync(object counts) => Ui("c => window.NadlanPanel && NadlanPanel.counts(c)", counts);
    public Task SetSweepAsync(MapExtent e) =>
        Ui("e => window.NadlanPanel && NadlanPanel.setSweep(e)", new { left = e.Left, right = e.Right, top = e.Top, bottom = e.Bottom });
    public Task SetProbeAsync(EgsaPoint p) => Ui("([x, y]) => window.NadlanPanel && NadlanPanel.setProbe(x, y)", new[] { p.X, p.Y });
    public Task FinishedAsync(string text) => Ui("t => window.NadlanPanel && NadlanPanel.finished(t)", text);

    /// <param name="kind">created | exists | rejected | road</param>
    public Task AddShapeAsync(IReadOnlyList<IReadOnlyList<EgsaPoint>> rings, string kind) =>
        Ui("([r, k]) => window.NadlanPanel && NadlanPanel.addShape(r, k)",
            new object[] { rings.Select(r => r.Select(p => new[] { p.X, p.Y })), kind });

    private async Task Ui(string script, object? arg)
    {
        try
        {
            await _page.EvaluateAsync(script, arg);
        }
        catch (PlaywrightException)
        {
            // Tab reloading or closed.
        }
    }
}
