using Microsoft.Playwright;

namespace Nadlan.KaekImporter.Api;

/// <summary>
/// "Connect to Nadlan" without copy-paste: opens Nadlan's connect-importer page in a new tab of the importer's own browser.
/// The user signs in there (email + password; Google usually refuses automated browsers) and clicks Connect; Nadlan puts
/// a token for that user in the page, and we read it from the tab we drive and close it. Nothing is exposed to other
/// sites: the token is only taken from a page on the configured Nadlan address.
/// </summary>
public static class NadlanConnect
{
    private static readonly TimeSpan WaitForUser = TimeSpan.FromMinutes(15);

    /// <summary>The new token, or null when the user closed the tab (or took too long).</summary>
    public static async Task<string?> GetTokenAsync(IBrowserContext context, Uri nadlan, TextWriter console)
    {
        var tab = await context.NewPageAsync();
        var closed = new TaskCompletionSource();
        tab.Close += (_, _) => closed.TrySetResult();
        try
        {
            var device = Uri.EscapeDataString(Environment.MachineName);
            await tab.GotoAsync(new Uri(nadlan, $"connect-importer.html?device={device}").ToString(), new() { Timeout = 60_000 });
            console.WriteLine("Waiting for sign-in and Connect in the GreekPlot tab...");

            // Survives the detour through the login page and back.
            var token = tab.Locator("#importer-token[data-token]");
            var ready = token.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = (float)WaitForUser.TotalMilliseconds });
            await Task.WhenAny(ready, closed.Task);
            if (!ready.IsCompletedSuccessfully)
            {
                _ = ready.ContinueWith(t => t.Exception, TaskScheduler.Default); // observed: the tab was closed or timed out
                return null;
            }

            var here = new Uri(tab.Url);
            if (here.Scheme != nadlan.Scheme || !string.Equals(here.Host, nadlan.Host, StringComparison.OrdinalIgnoreCase) || here.Port != nadlan.Port)
            {
                console.WriteLine($"Ignored a token from {here.GetLeftPart(UriPartial.Authority)}: not the GreekPlot address {nadlan}.");
                return null;
            }

            return await token.GetAttributeAsync("data-token");
        }
        catch (PlaywrightException) when (closed.Task.IsCompleted)
        {
            return null; // closed by the user
        }
        finally
        {
            if (!closed.Task.IsCompleted)
            {
                try { await tab.CloseAsync(); } catch (PlaywrightException) { /* already gone */ }
            }
        }
    }
}
