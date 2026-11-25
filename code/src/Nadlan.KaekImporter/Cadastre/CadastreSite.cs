using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Nadlan.KaekImporter.Cadastre;

/// <summary>The site said something is wrong (an HTTP error, a sign-in page, a redirect, HTML instead of a PDF): the run stops.</summary>
public sealed class CadastreTroubleException(string message) : Exception(message);

/// <summary>A request the allow-list refuses. Never sent.</summary>
public sealed class CadastreBlockedException(string message) : Exception(message);

/// <param name="Text">The reply as text (HTML), or null for a PDF.</param>
/// <param name="Bytes">The reply's bytes, for a PDF (kept in memory only).</param>
public sealed record SiteResponse(int Status, string Url, bool Redirected, string ContentType, string? Text, byte[]? Bytes);

/// <summary>The lawyer service, as the records pipeline sees it: one request at a time.</summary>
public interface ICadastreSite
{
    /// <param name="expectPdf">A document download: the reply must be a PDF.</param>
    Task<SiteResponse> RequestAsync(string method, string path, string? body, bool expectPdf, CancellationToken ct);
}

/// <summary>
/// The only requests the module may send (brief §1): search one KAEK, read a record, download a document the record offers,
/// open the search page. Everything else is refused before it is sent - above all anything under /Professionals/Contracts/
/// (filings that create a payable "Έντυπο Οφειλής" in the lawyer's name), the extracts (ShowDocs), the person and act
/// searches, any other POST, and Logout. The same list is enforced again in the page by <see cref="CadastreAllowList.RequestScript"/>.
/// </summary>
public static partial class CadastreAllowList
{
    public const string Origin = "https://ktimatologio.gov.gr";
    public const string LawyerServiceUrl = "https://ktimatologio.gr/e-services/29";
    public const string SearchPageUrl = Origin + "/Professionals/Inquiry/Main/SearchByEstate";
    public const string FetchEstatesPath = "/Professionals/Inquiry/Main/FetchEstates";
    public const string LogoutSelector = "a[href=\"/Professionals/Account/Logout\"]";

    /// <summary>The search form's fields exactly as the site's own form sends them (§2.2); only the KAEK changes.</summary>
    public static string FetchEstatesBody(string kaek) =>
        KaekPattern().IsMatch(kaek)
            ? $"KAEK={kaek}&PropVert=&PropHor=&Street=&StreetFrom=&StreetTo=&TK=&Locality=&OTA=&CadoffId=0&X-Requested-With=XMLHttpRequest"
            : throw new ArgumentException("A KAEK is 12 digits.", nameof(kaek));

    /// <summary>Throws <see cref="CadastreBlockedException"/> unless the request is on the allow-list.</summary>
    public static void Check(string method, string pathAndQuery, string? body)
    {
        if (!Uri.TryCreate(new Uri(Origin), pathAndQuery, out var url) || url.GetLeftPart(UriPartial.Authority) != Origin)
        {
            throw new CadastreBlockedException($"Blocked: {pathAndQuery} is not on ktimatologio.gov.gr.");
        }

        var path = url.AbsolutePath.ToLowerInvariant();
        var query = Uri.UnescapeDataString(url.Query.TrimStart('?'));
        if (path.Contains("/contracts/") || path.Contains("/account/") || path.Contains("showdocs"))
        {
            throw new CadastreBlockedException($"Blocked: {url.AbsolutePath} (filings, extracts and sign-out are never requested).");
        }

        var allowed = (method, path) switch
        {
            ("POST", "/professionals/inquiry/main/fetchestates") => query.Length == 0 && body is not null && SearchBody().IsMatch(body),
            ("GET", var p) when RecordPath().IsMatch(p) => query.Length == 0 || query == "p=1",
            ("GET", "/professionals/inquiry/main/showscans") => body is null && IsScansQuery(url.Query),
            ("GET", "/professionals/inquiry/main/searchbyestate") => body is null && query.Length == 0,
            _ => false,
        };
        if (!allowed || (method == "GET" && body is not null))
        {
            throw new CadastreBlockedException($"Blocked: {method} {url.AbsolutePath} is not one of the requests the module may send.");
        }
    }

    /// <summary>
    /// The one function in the page that sends requests to the site (same session and cookies as the lawyer's own tab). It
    /// checks the allow-list itself before sending, and reads the reply as text, or a PDF as base64.
    /// </summary>
    public const string RequestScript = """
        async ([method, path, body, expectPdf]) => {
          const blocked = why => { throw new Error('NADLAN_BLOCKED: ' + why); };
          if (!/^(www\.)?ktimatologio\.gov\.gr$/i.test(location.hostname)) blocked('this tab is not on ktimatologio.gov.gr');
          const url = new URL(path, location.origin);
          if (url.origin !== location.origin) blocked(path + ' is not on this site');
          const p = url.pathname.toLowerCase();
          const q = decodeURIComponent(url.search.replace(/^\?/, ''));
          if (p.includes('/contracts/') || p.includes('/account/') || p.includes('showdocs')) blocked(url.pathname);
          const ok =
            (method === 'POST' && p === '/professionals/inquiry/main/fetchestates' && q === '' &&
              /^KAEK=\d{12}&PropVert=&PropHor=&Street=&StreetFrom=&StreetTo=&TK=&Locality=&OTA=&CadoffId=0&X-Requested-With=XMLHttpRequest$/.test(body || '')) ||
            (method === 'GET' && body == null && /^\/professionals\/inquiry\/main\/viewresults\/\d{12}-\d+-\d+$/.test(p) && (q === '' || q === 'p=1')) ||
            (method === 'GET' && body == null && p === '/professionals/inquiry/main/showscans' && (() => {
              const s = new URLSearchParams(url.search), keys = Array.from(s.keys());
              return keys.length === 3 && new Set(keys).size === 3 && /^\d{12}-\d+-\d+$/.test(s.get('KAEK') || '') &&
                /^[a-z]+\d+$/i.test(s.get('item') || '') && /^\S{1,64}$/.test(s.get('pid') || '');
            })()) ||
            (method === 'GET' && body == null && p === '/professionals/inquiry/main/searchbyestate' && q === '');
          if (!ok) blocked(method + ' ' + url.pathname);

          // Never follow a redirect: the browser would request its target before anything could check it against the
          // allow-list. 'manual' hands back an opaque reply instead (status 0, no body), which stops the run as trouble.
          const init = { method, credentials: 'same-origin', redirect: 'manual' };
          if (method === 'POST') {
            init.body = body;
            init.headers = { 'X-Requested-With': 'XMLHttpRequest', 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8' };
          }
          const r = await fetch(url.pathname + url.search, init);
          if (r.type === 'opaqueredirect') return { status: 0, url: '', redirected: true, contentType: '' };
          const out = { status: r.status, url: r.url, redirected: r.redirected, contentType: r.headers.get('content-type') || '' };
          if (expectPdf && r.ok) {
            const b = new Uint8Array(await r.arrayBuffer());
            if (b.length > 20 * 1024 * 1024) throw new Error('The document is larger than 20 MB; not read.');
            let bin = '';
            for (let i = 0; i < b.length; i += 0x8000) bin += String.fromCharCode.apply(null, b.subarray(i, i + 0x8000));
            out.base64 = btoa(bin);
          } else {
            out.text = await r.text();
          }
          return out;
        }
        """;

    [GeneratedRegex(@"^\d{12}$")]
    private static partial Regex KaekPattern();

    [GeneratedRegex(@"^KAEK=\d{12}&PropVert=&PropHor=&Street=&StreetFrom=&StreetTo=&TK=&Locality=&OTA=&CadoffId=0&X-Requested-With=XMLHttpRequest$")]
    private static partial Regex SearchBody();

    [GeneratedRegex(@"^/professionals/inquiry/main/viewresults/\d{12}-\d+-\d+$")]
    private static partial Regex RecordPath();

    /// <summary>A document link's query: exactly KAEK={KAEK}-{v}-{h}, item={letters}{number} and pid, in any order (§2.3).</summary>
    private static bool IsScansQuery(string rawQuery)
    {
        var parts = rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Select(kv => (Key: kv[0], Value: kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "")).ToList();
        if (parts.Count != 3 || parts.Select(p => p.Key).Distinct().Count() != 3) return false;
        string? Get(string key) => parts.FirstOrDefault(p => p.Key == key).Value;
        return Get("KAEK") is { } k && ScansKaek().IsMatch(k) && Get("item") is { } i && ScansItem().IsMatch(i)
            && Get("pid") is { Length: > 0 and <= 64 } pid && !pid.Any(char.IsWhiteSpace);
    }

    [GeneratedRegex(@"^\d{12}-\d+-\d+$")]
    private static partial Regex ScansKaek();

    [GeneratedRegex(@"^[a-z]+\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex ScansItem();
}

/// <summary>A person's pace (owner, 2026-11-25): one request to the site every 10 s ± 5 s at random, one at a time.</summary>
public interface IRequestPacer
{
    Task WaitTurnAsync(CancellationToken ct);
    void Done();
}

public sealed class RandomPacer : IRequestPacer
{
    public static readonly TimeSpan Min = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Max = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Func<DateTime> _utcNow;
    private readonly Func<double> _random;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private DateTime? _lastDoneUtc;

    public RandomPacer() : this(() => DateTime.UtcNow, Random.Shared.NextDouble, Task.Delay)
    {
    }

    /// <summary>For tests: the clock, the random number (0..1) and the waiting.</summary>
    public RandomPacer(Func<DateTime> utcNow, Func<double> random, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _utcNow = utcNow;
        _random = random;
        _delay = delay;
    }

    /// <summary>Waits until the previous request ended at least a random 5-15 s ago; only one request at a time.</summary>
    public async Task WaitTurnAsync(CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            if (_lastDoneUtc is DateTime last)
            {
                var gap = Min + TimeSpan.FromMilliseconds(Math.Clamp(_random(), 0, 1) * (Max - Min).TotalMilliseconds);
                var wait = last + gap - _utcNow();
                if (wait > TimeSpan.Zero) await _delay(wait, ct);
            }
        }
        catch
        {
            _one.Release();
            throw;
        }
    }

    public void Done()
    {
        _lastDoneUtc = _utcNow();
        _one.Release();
    }
}

/// <summary>Sends one request to the site and returns the raw reply (no checks).</summary>
public interface ICadastreTransport
{
    Task<SiteResponse> SendAsync(string method, string path, string? body, bool expectPdf);
}

/// <summary>
/// Every request of the records pipeline: the allow-list first (a refused request is never sent and never waits), then the
/// pace, then the request, then the reply's trouble checks.
/// </summary>
public sealed class GuardedCadastreSite(ICadastreTransport transport, IRequestPacer pacer) : ICadastreSite
{
    public async Task<SiteResponse> RequestAsync(string method, string path, string? body, bool expectPdf, CancellationToken ct)
    {
        CadastreAllowList.Check(method, path, body);
        await pacer.WaitTurnAsync(ct);
        SiteResponse response;
        try
        {
            response = await transport.SendAsync(method, path, body, expectPdf);
        }
        finally
        {
            pacer.Done();
        }

        CadastreReplyCheck.ThrowOnTrouble(method, path, expectPdf, response);
        return response;
    }
}

/// <summary>
/// The lawyer's own tab in the importer's visible browser: each request is sent from inside that page (same session, the
/// user signed in by hand) by <see cref="CadastreAllowList.RequestScript"/>, which checks the allow-list again.
/// </summary>
public sealed class PageTransport(IPage page) : ICadastreTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SiteResponse> SendAsync(string method, string path, string? body, bool expectPdf)
    {
        JsonElement raw;
        try
        {
            raw = await page.EvaluateAsync<JsonElement>(CadastreAllowList.RequestScript, new object?[] { method, path, body, expectPdf });
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("NADLAN_BLOCKED", StringComparison.Ordinal))
        {
            throw new CadastreBlockedException(ex.Message.Split('\n')[0]);
        }
        catch (PlaywrightException ex)
        {
            // The tab navigated or closed mid-request, or the network failed.
            throw new CadastreTroubleException("The request did not complete: " + ex.Message.Split('\n')[0].Trim());
        }

        var dto = raw.Deserialize<ReplyDto>(Json)!;
        return new SiteResponse(dto.Status, dto.Url ?? "", dto.Redirected, dto.ContentType ?? "", dto.Text,
            dto.Base64 is null ? null : Convert.FromBase64String(dto.Base64));
    }

    private sealed record ReplyDto(int Status, string? Url, bool Redirected, string? ContentType, string? Text, string? Base64);
}


/// <summary>The signs of trouble that stop a run (brief §1).</summary>
public static class CadastreReplyCheck
{
    public static void ThrowOnTrouble(string method, string path, bool expectPdf, SiteResponse r)
    {
        // Any redirect is trouble: the request script never follows one (its target could be anything, e.g. the sign-in
        // page or a filing), so the run stops before the target is ever requested.
        if (r.Redirected)
        {
            throw new CadastreTroubleException(
                $"The site tried to send {method} {ShortPath(path)} elsewhere (signed out?). Nothing was followed; sign in again and restart.");
        }

        if (r.Status != 200)
        {
            throw new CadastreTroubleException($"The site answered HTTP {r.Status} to {method} {ShortPath(path)}.");
        }

        if (r.Text is not null && CadastrePages.IsSignInPage(r.Text))
        {
            throw new CadastreTroubleException("The site showed a sign-in page: the lawyer's session has ended. Sign in again.");
        }

        if (expectPdf)
        {
            var isPdf = r.Bytes is { Length: > 4 } b && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F';
            if (!isPdf)
            {
                throw new CadastreTroubleException($"Expected a PDF from {ShortPath(path)} but got {(r.ContentType.Length > 0 ? r.ContentType : "something else")}.");
            }
        }
    }

    private static string ShortPath(string path) => path.Split('?')[0];
}
