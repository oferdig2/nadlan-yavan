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
/// </summary>
public static class Program
{
    private const string Usage = """
        NadlanKaekImporter [options]
          --api <url>            Nadlan web app (default http://localhost:5515)
          --token <token>        API token (sent as Bearer; not required yet)
          --delay <min-max>      seconds to wait after each new parcel (default 10-30)
          --miss-delay <min-max> seconds to wait after a point with nothing new (default 2-5)
          --step <metres>        sweep spacing (default 5)
          --max-view <metres>    largest view width/height allowed (default 2000)
          --max-parcels <n>      stop after n new parcels (for trying it out)
          --debug-port <port>    open the browser to automation on localhost:<port> (testing)
        """;

    public static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        using var nadlan = new NadlanApiClient(options.Api, options.Token);
        IReadOnlyList<GeographicAreaItem> areas;
        try
        {
            areas = await nadlan.ListActiveAreasAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.Error.WriteLine($"Cannot reach Nadlan at {options.Api}: {ex.Message}");
            return 1;
        }

        // First run on a machine downloads Chromium (~150 MB); afterwards this is a quick no-op.
        Console.WriteLine("Checking the browser...");
        var installExit = Microsoft.Playwright.Program.Main(new[] { "install", "--no-shell", "chromium" });
        if (installExit != 0)
        {
            Console.Error.WriteLine("Could not install the browser (Playwright exit code " + installExit + ").");
            return 1;
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = false, Args = options.DebugPort is int port
            ? new[] { "--start-maximized", $"--remote-debugging-port={port}" }
            : new[] { "--start-maximized" } });
        var context = await browser.NewContextAsync(new() { ViewportSize = ViewportSize.NoViewport, Locale = "el-GR" });

        var areaJson = JsonSerializer.Serialize(areas.Select(a => new { id = a.Id, name = a.Name }));
        await context.AddInitScriptAsync($"window.__nadlanAreas = {areaJson};\n{LoadPanelScript()}");

        var page = await context.NewPageAsync();
        var site = new KtimanetPage(page);
        var run = new ImportRun(site, nadlan, options.Settings, Console.Out);
        CancellationTokenSource? current = null;

        await context.ExposeFunctionAsync("nadlanStart", async (string areaId) =>
        {
            if (current is not null) return "An import is already running.";
            MapExtent extent;
            try { extent = await site.GetExtentAsync(); }
            catch (Exception ex) when (ex is InvalidOperationException or PlaywrightException) { return ex.Message; }

            if (run.Validate(extent) is string error) return error;
            int? geographicAreaId = int.TryParse(areaId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

            var cts = current = new CancellationTokenSource();
            Console.WriteLine($"Import started over {extent.Width:0} x {extent.Height:0} m.");
            _ = Task.Run(async () =>
            {
                try { await run.RunAsync(extent, geographicAreaId, cts.Token); }
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

        var closed = new TaskCompletionSource();
        page.Close += (_, _) => closed.TrySetResult();
        browser.Disconnected += (_, _) => closed.TrySetResult();

        await page.GotoAsync(KtimanetPage.MapUrl, new() { Timeout = 90_000 });
        Console.WriteLine("Browser is open. Zoom to the area to import and click 'Acquire polygons'. Close the browser to exit.");
        await closed.Task;
        current?.Cancel();
        return 0;
    }

    private static string LoadPanelScript()
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("panel.js")
            ?? throw new InvalidOperationException("panel.js is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record Options(Uri Api, string? Token, ImportSettings Settings, int? DebugPort)
    {
        public static Options Parse(string[] args)
        {
            var api = new Uri("http://localhost:5515/");
            string? token = null;
            (double Min, double Max) delay = (10, 30), missDelay = (2, 5);
            double step = 5, maxView = 2000;
            int? maxParcels = null;
            int? debugPort = null;

            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
                switch (args[i])
                {
                    case "--api": api = new Uri(Next().TrimEnd('/') + "/"); break;
                    case "--token": token = Next(); break;
                    case "--delay": delay = Range(Next()); break;
                    case "--miss-delay": missDelay = Range(Next()); break;
                    case "--step": step = Number(Next()); break;
                    case "--max-view": maxView = Number(Next()); break;
                    case "--max-parcels": maxParcels = (int)Number(Next()); break;
                    case "--debug-port": debugPort = (int)Number(Next()); break;
                    default: throw new ArgumentException($"Unknown option {args[i]}.");
                }
            }

            return new Options(api, token, new ImportSettings(step,
                TimeSpan.FromSeconds(delay.Min), TimeSpan.FromSeconds(delay.Max),
                TimeSpan.FromSeconds(missDelay.Min), TimeSpan.FromSeconds(missDelay.Max),
                maxView, maxParcels), debugPort);
        }

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
