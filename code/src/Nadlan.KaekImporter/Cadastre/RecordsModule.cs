using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;
using Nadlan.KaekImporter.Api;

namespace Nadlan.KaekImporter.Cadastre;

/// <summary>What the records panel needs to know about the importer's GreekPlot connection.</summary>
public interface IRecordsConnection
{
    IReadOnlyList<GeographicAreaItem> Areas { get; }
    string? UserName { get; }
    bool IsAdmin { get; }
    string? Offline { get; }
    bool NeedsConnect { get; }
}

/// <summary>
/// "Inspect KAEK" (owner, 2026-11-25): the lawyer service in a new tab of the importer's own visible browser, the records
/// panel in it, and the records run. The lawyer signs in by hand; nothing here signs in, and nothing runs headless.
/// </summary>
public sealed class RecordsModule(IBrowserContext context, NadlanApiClient client, IRecordsConnection connection,
    Func<string, Func<Task<string>>, Task<string>> safely, TextWriter console)
{
    private readonly RandomPacer _pacer = new(); // one pace for the whole session, across runs
    private IPage? _tab;
    private bool _signedInSeen;
    private CancellationTokenSource? _run;
    private RecordsPanelPage? _panel;

    public Task? RunTask { get; private set; }
    public bool Running => _run is not null;

    public async Task RegisterAsync(string panelScript)
    {
        await context.AddInitScriptAsync(panelScript);
        await context.ExposeFunctionAsync("nadlanInspectKaek", () => safely("Opening the lawyer service", InspectAsync));
        await context.ExposeFunctionAsync("nadlanRecordsState", State);

        // The state is also pushed into each lawyer-service page once it has loaded.
        void Watch(IPage p) => p.DOMContentLoaded += async (_, _) =>
        {
            if (!p.Url.Contains("ktimatologio.gov.gr/Professionals/", StringComparison.OrdinalIgnoreCase)) return;
            try { await p.EvaluateAsync("s => window.NadlanRecords && NadlanRecords.applyState(s)", State()); }
            catch (PlaywrightException) { /* navigated on */ }
        };
        context.Page += (_, p) => Watch(p);
        foreach (var p in context.Pages) Watch(p);
        await context.ExposeFunctionAsync("nadlanRecordsSignedIn", (bool signedIn) =>
        {
            if (signedIn && !_signedInSeen) console.WriteLine("Lawyer service: signed in.");
            _signedInSeen |= signedIn;
        });
        await context.ExposeBindingAsync("nadlanRecordsStart", (BindingSource source, string json) =>
            safely("Starting the records run", () => StartAsync(source.Page, json)));
        await context.ExposeFunctionAsync("nadlanRecordsStop", () => { _run?.Cancel(); });
    }

    public void Stop() => _run?.Cancel();

    private object State() => new
    {
        areas = connection.Areas.Select(a => new { id = a.Id, name = a.Name }),
        userName = connection.UserName,
        isAdmin = connection.IsAdmin,
        offline = connection.Offline,
        needsConnect = connection.NeedsConnect,
        running = Running,
        status = _panel?.LastStatus,
        progress = _panel?.LastProgress,
    };

    /// <summary>
    /// Opens the lawyer service (e-service 29) in a new tab; once the lawyer has signed in during this session, straight to the
    /// property search. An open records tab is brought to the front instead.
    /// </summary>
    private async Task<string> InspectAsync()
    {
        if (Problem() is string problem) return problem;
        if (_tab is { IsClosed: false })
        {
            await _tab.BringToFrontAsync();
            return "";
        }

        _tab = await context.NewPageAsync();
        var url = _signedInSeen ? CadastreAllowList.SearchPageUrl : CadastreAllowList.LawyerServiceUrl;
        console.WriteLine($"Inspect KAEK: opening {url}");
        try
        {
            await _tab.GotoAsync(url, new() { Timeout = 60_000 });
        }
        catch (PlaywrightException ex)
        {
            // The tab stays open: the user can retry or type the address.
            return "The lawyer service did not open: " + ex.Message.Split('\n')[0].Trim();
        }

        return "";
    }

    private string? Problem()
    {
        if (connection.Offline is not null) return "Offline: owners and sales can only be kept in GreekPlot. Restart the importer when GreekPlot is reachable.";
        if (connection.NeedsConnect) return "Connect to GreekPlot first (in the map tab's panel).";
        if (!connection.IsAdmin) return $"Only an Admin can research owners and sales; the importer is connected as {connection.UserName}.";
        return null;
    }

    private sealed record StartDto(string? AreaId, string? Kaeks);

    private async Task<string> StartAsync(IPage page, string json)
    {
        if (Problem() is string problem) return problem;
        if (Running) return "A records run is already going.";
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var here) || !here.Host.EndsWith("ktimatologio.gov.gr", StringComparison.OrdinalIgnoreCase))
        {
            return "Start the run from the lawyer service's tab.";
        }

        if (!await page.EvaluateAsync<bool>("s => !!document.querySelector(s)", CadastreAllowList.LogoutSelector))
        {
            return "Sign in first.";
        }

        var dto = JsonSerializer.Deserialize<StartDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new StartDto(null, null);
        var panel = new RecordsPanelPage(page);
        IReadOnlyList<string> kaeks;
        if (dto.Kaeks is not null)
        {
            var parsed = KaekList.Parse(dto.Kaeks);
            if (parsed.Kaeks.Count == 0) return "No 12-digit KAEK in the pasted text.";
            if (parsed.Kaeks.Count > RecordsRun.MaxKaeks) return $"At most {RecordsRun.MaxKaeks} KAEKs per run: {parsed.Kaeks.Count} pasted.";
            kaeks = parsed.Kaeks;
            await panel.LogAsync($"{kaeks.Count} KAEK(s) accepted" + (parsed.Ignored > 0 ? $", {parsed.Ignored} ignored (not 12 digits)" : "") +
                                 (parsed.Duplicates > 0 ? $", {parsed.Duplicates} repeated" : "") + "; each is fetched again.");
        }
        else
        {
            if (!int.TryParse(dto.AreaId, NumberStyles.None, CultureInfo.InvariantCulture, out var areaId) || connection.Areas.All(a => a.Id != areaId))
            {
                return "Choose the GreekPlot area first.";
            }

            AreaKaeks area;
            try
            {
                using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                area = await client.GetAreaKaeksAsync(areaId, quick.Token);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return "GreekPlot refused the area's KAEK list (HTTP 404): the server needs the Cadastre records update (database change 033), and the user must be an Admin.";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return "Could not get the area's KAEKs from GreekPlot: " + ex.Message;
            }

            var name = connection.Areas.First(a => a.Id == areaId).Name;
            if (area.Kaeks.Count == 0)
            {
                return area.Total == 0
                    ? $"{name} has no parcels with a real KAEK in GreekPlot."
                    : $"All {area.Total} KAEKs of {name} are already fetched.";
            }

            kaeks = area.Kaeks.Take(RecordsRun.MaxKaeks).ToList();
            await panel.LogAsync($"{name}: {area.Total} KAEK(s), {area.AlreadyFetched} already fetched; this run does {kaeks.Count}" +
                                 (area.Kaeks.Count > kaeks.Count ? $" of the {area.Kaeks.Count} left (at most {RecordsRun.MaxKaeks} per run)." : "."));
        }

        var cts = _run = new CancellationTokenSource();
        _panel = panel;
        var site = new GuardedCadastreSite(new PageTransport(page), _pacer);
        var run = new RecordsRun(site, new GreekPlotRecordsSink(client), panel, console);
        console.WriteLine($"Records run started: {kaeks.Count} KAEK(s).");
        RunTask = Task.Run(async () =>
        {
            try
            {
                await run.RunAsync(kaeks, cts.Token);
            }
            catch (Exception ex)
            {
                console.WriteLine("Records run failed: " + ex);
                await panel.FinishedAsync("The records run failed: " + ex.Message);
            }
            finally
            {
                _run = null;
                cts.Dispose();
            }
        });
        await panel.RunningAsync();
        return "";
    }
}
