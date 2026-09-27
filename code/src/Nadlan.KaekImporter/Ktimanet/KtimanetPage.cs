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

    public async Task<MapExtent> GetExtentAsync()
    {
        var e = await _page.EvaluateAsync<JsonElement>("() => typeof MyMap === 'undefined' ? null : MyMap.MapExtents()");
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The Ktimatologio map is not loaded yet.");
        }

        return new MapExtent(e.GetProperty("left").GetDouble(), e.GetProperty("right").GetDouble(),
            e.GetProperty("top").GetDouble(), e.GetProperty("bottom").GetDouble());
    }

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
