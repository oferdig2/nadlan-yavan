using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;
using Nadlan.KaekImporter.Import;
using Nadlan.KaekImporter.Ktimanet;
using Nadlan.KaekImporter.Api;

namespace Nadlan.KaekImporter;

/// <summary>
/// Opens gis.ktimanet.gr in a visible browser with a Nadlan panel. The user zooms to an area and clicks
/// "Acquire polygons"; every land parcel in the view is then imported into Nadlan (KAEK + polygon + area).
/// Or the user ticks "Import each parcel I click" and every parcel they click on the map is imported.
/// </summary>
public static class Program
{
    private const string Usage = """
        NadlanKaekImporter [options]
          --api <url>            Nadlan web app (default http://localhost:5515)
          --offline              run without Nadlan: the whole process is shown, but nothing is saved
                                 (also used automatically when the Nadlan server cannot be reached)
          --token <token>        API token (sent as Bearer; not required yet)
          --delay <min-max>      seconds to wait after each new parcel (default 4-10)
          --miss-delay <min-max> seconds to wait after a click with nothing new (default 2-5)
          --max-view <metres>    largest view width/height allowed (default 2000)
          --max-parcels <n>      stop after n new parcels (for trying it out)
          --browser <name>       auto (installed Edge/Chrome, else Chromium), msedge, chrome or chromium
          --debug-port <port>    open the browser to automation on localhost:<port> (testing)
        Defaults can also be set in importer.json next to the program or in the user's Nadlan folder.
        """;

    public static async Task<int> Main(string[] args)
    {
        var logPath = AppHost.StartLog();
        Options options;
        try
        {
            options = Options.Parse(args, AppHost.LoadSettings());
        }
        catch (ArgumentException ex)
        {
            AppHost.ShowFatal(ex.Message + Environment.NewLine + Usage);
            return 2;
        }

        // Offline (asked for, or no server reachable): everything runs and is shown, nothing is saved.
        using var client = new NadlanApiClient(options.Api, options.Token);
        NadlanApiClient? nadlan = null;
        IReadOnlyList<GeographicAreaItem> areas = Array.Empty<GeographicAreaItem>();
        string? offlineReason = options.Offline ? "Offline mode (--offline)." : null;
        if (!options.Offline)
        {
            try
            {
                using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                areas = await client.ListActiveAreasAsync(quick.Token);
                nadlan = client;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                offlineReason = $"The Nadlan server at {options.Api} cannot be reached.";
                Console.WriteLine($"{offlineReason} ({ex.Message}) Running offline: nothing will be saved.");
            }
        }

        using var playwright = await Playwright.CreateAsync();
        IBrowser browser;
        try
        {
            browser = await LaunchBrowserAsync(playwright, options);
        }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException)
        {
            AppHost.ShowFatal($"Could not start a browser: {ex.Message}\n\nLog: {logPath}");
            return 1;
        }

        await using var _ = browser;
        var context = await browser.NewContextAsync(new() { ViewportSize = ViewportSize.NoViewport, Locale = "el-GR" });

        var areaJson = JsonSerializer.Serialize(areas.Select(a => new { id = a.Id, name = a.Name }));
        var offlineJson = JsonSerializer.Serialize(offlineReason);
        await context.AddInitScriptAsync($"window.__nadlanAreas = {areaJson};\nwindow.__nadlanOffline = {offlineJson};\n{LoadPanelScript()}");

        var page = await context.NewPageAsync();
        var site = new KtimanetPage(page);
        var run = new ImportRun(site, nadlan, options.Settings, Console.Out);
        CancellationTokenSource? current = null;
        Task? currentRun = null;
        ClickImport? clicks = null;

        await context.ExposeFunctionAsync("nadlanStart", async (string areaId) =>
        {
            if (current is not null) return "An import is already running.";
            if (clicks is not null) return "Switch off click import first.";
            MapAreas view;
            try
            {
                var (read, error) = await run.ReadViewAsync();
                if (read is null) return error ?? "Could not read the map.";
                view = read;
            }
            catch (PlaywrightException ex) { return "Could not read the map: " + ex.Message; }

            var cts = current = new CancellationTokenSource();
            Console.WriteLine($"Import started over {view.Extent.Width:0} x {view.Extent.Height:0} m, {view.Areas.Count} areas.");
            currentRun = Task.Run(async () =>
            {
                try { await run.RunAsync(view, AreaId(areaId), cts.Token); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Import failed: " + ex);
                    await site.FinishedAsync("Import failed: " + ex.Message);
                }
                finally { current = null; cts.Dispose(); }
            });
            return "";
        });
        await context.ExposeFunctionAsync("nadlanStop", () => { current?.Cancel(); });
        await context.ExposeFunctionAsync("nadlanClickMode", async (bool on, string areaId) =>
        {
            if (on)
            {
                if (current is not null) return "Stop the running import first.";
                clicks ??= new ClickImport(site, nadlan, AreaId(areaId), Console.Out);
                Console.WriteLine("Click import on.");
                return "";
            }

            var stopping = clicks;
            clicks = null;
            if (stopping is not null) await stopping.StopAsync();
            return "";
        });

        // Click import: the site's reply to the user's own map click already holds the KAEK and polygon.
        // Our sweep's requests look the same, so they are ignored while a sweep runs.
        page.Response += (_, response) =>
        {
            var session = clicks;
            if (session is null || current is not null
                || !response.Url.EndsWith("/gis/map/PostHandler", StringComparison.OrdinalIgnoreCase)
                || response.Request.PostData?.Contains("GETPSTKG", StringComparison.Ordinal) != true)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try { session.OnClickReply(await response.TextAsync()); }
                catch (PlaywrightException) { } // page navigated away meanwhile
            });
        };

        // A reload rebuilds the panel with click import off, so end the session to match.
        page.FrameNavigated += (_, frame) =>
        {
            if (frame != page.MainFrame || clicks is not { } session) return;
            clicks = null;
            _ = session.StopAsync();
        };

        var closed = new TaskCompletionSource();
        page.Close += (_, _) => closed.TrySetResult();
        browser.Disconnected += (_, _) => closed.TrySetResult();

        await page.GotoAsync(KtimanetPage.MapUrl, new() { Timeout = 90_000 });
        Console.WriteLine("Browser is open. Zoom in until the yellow parcel lines show, then click 'Acquire polygons'" +
                          " (or tick 'Import each parcel I click'). Close the browser to exit.");
        await closed.Task;

        // Let a stopped run or click session write its report before exiting.
        current?.Cancel();
        if (clicks is not null) await clicks.StopAsync();
        if (currentRun is not null) await Task.WhenAny(currentRun, Task.Delay(TimeSpan.FromSeconds(10)));
        return 0;
    }

    /// <summary>
    /// The browser already on the machine if there is one (Edge is on every Windows PC; Chrome on most Macs), so a
    /// customer does not download ~150 MB first. Otherwise Playwright's own Chromium, downloaded once.
    /// </summary>
    private static async Task<IBrowser> LaunchBrowserAsync(IPlaywright playwright, Options options)
    {
        var args = options.DebugPort is int port
            ? new[] { "--start-maximized", $"--remote-debugging-port={port}" }
            : new[] { "--start-maximized" };
        var channels = options.Browser switch
        {
            "auto" => OperatingSystem.IsWindows() ? new[] { "msedge", "chrome" } : new[] { "chrome", "msedge" },
            "chromium" => Array.Empty<string>(),
            var name => new[] { name },
        };

        foreach (var channel in channels)
        {
            try
            {
                var browser = await playwright.Chromium.LaunchAsync(new() { Headless = false, Channel = channel, Args = args });
                Console.WriteLine($"Using the installed {channel} browser.");
                return browser;
            }
            catch (PlaywrightException)
            {
                // not installed; try the next one
            }
        }

        Console.WriteLine("No Edge or Chrome found; getting Chromium (first time only, ~150 MB)...");
        var installExit = Microsoft.Playwright.Program.Main(new[] { "install", "--no-shell", "chromium" });
        if (installExit != 0)
        {
            throw new InvalidOperationException("Chromium could not be downloaded (Playwright exit code " + installExit + ").");
        }

        return await playwright.Chromium.LaunchAsync(new() { Headless = false, Args = args });
    }

    private static int? AreaId(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    private static string LoadPanelScript()
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("panel.js")
            ?? throw new InvalidOperationException("panel.js is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record Options(Uri Api, string? Token, ImportSettings Settings, int? DebugPort, string Browser, bool Offline)
    {
        /// <summary>Built-in defaults, then importer.json, then the command line.</summary>
        public static Options Parse(string[] args, ImporterSettingsFile file)
        {
            var api = Url(file.ApiUrl ?? "http://localhost:5515");
            var token = file.Token;
            var delay = file.Delay is null ? (4.0, 10.0) : Range(file.Delay);
            var missDelay = file.MissDelay is null ? (2.0, 5.0) : Range(file.MissDelay);
            var maxView = file.MaxViewMetres ?? 2000;
            var browser = BrowserName(file.Browser ?? "auto");
            int? maxParcels = null;
            int? debugPort = null;
            var offline = file.Offline ?? false;

            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
                if (args[i].StartsWith("-psn_", StringComparison.Ordinal)) continue; // added by older macOS Finder launches
                switch (args[i])
                {
                    case "--api": api = Url(Next()); break;
                    case "--token": token = Next(); break;
                    case "--delay": delay = Range(Next()); break;
                    case "--miss-delay": missDelay = Range(Next()); break;
                    case "--max-view": maxView = Number(Next()); break;
                    case "--max-parcels": maxParcels = (int)Number(Next()); break;
                    case "--browser": browser = BrowserName(Next()); break;
                    case "--offline": offline = true; break;
                    case "--debug-port": debugPort = (int)Number(Next()); break;
                    default: throw new ArgumentException($"Unknown option {args[i]}.");
                }
            }

            return new Options(api, token, new ImportSettings(
                TimeSpan.FromSeconds(delay.Item1), TimeSpan.FromSeconds(delay.Item2),
                TimeSpan.FromSeconds(missDelay.Item1), TimeSpan.FromSeconds(missDelay.Item2),
                maxView, maxParcels), debugPort, browser, offline);
        }

        private static Uri Url(string value) =>
            Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")
                ? uri : throw new ArgumentException($"Bad Nadlan address {value}; expected e.g. https://nadlan.example.com");

        private static string BrowserName(string value) => value.ToLowerInvariant() switch
        {
            "auto" or "msedge" or "chrome" or "chromium" => value.ToLowerInvariant(),
            _ => throw new ArgumentException($"Unknown browser {value}; use auto, msedge, chrome or chromium."),
        };

        private static (double, double) Range(string value)
        {
            var parts = value.Split('-');
            var min = Number(parts[0]);
            var max = parts.Length > 1 ? Number(parts[1]) : min;
            return max < min ? throw new ArgumentException($"Bad range {value}.") : (min, max);
        }

        private static double Number(string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= 0
                ? n : throw new ArgumentException($"Bad number {value}.");
    }
}
