using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nadlan.KaekImporter.Geometry;

namespace Nadlan.KaekImporter.Api;

public sealed record GeographicAreaItem(int Id, string? Code, string Name, bool IsActive);

/// <summary>An area's KAEKs still to research: <see cref="Kaeks"/> (sorted), out of <see cref="Total"/> real KAEKs there.</summary>
public sealed record AreaKaeks(int AreaId, int Total, int AlreadyFetched, IReadOnlyList<string> Kaeks);

/// <summary>A Parcel already in Nadlan, with its outer ring and holes in lon/lat.</summary>
public sealed record ExistingParcel(string RegistryId, bool IsProvisional, IReadOnlyList<IReadOnlyList<LonLat>> Rings, long ParcelId = 0);

/// <summary>
/// What GreekPlot said about one check of a Parcel it already has (<c>PUT api/parcels/{id}/cadastre-check</c>, migration 034).
/// Unsupported = the server is older (no such call yet); Unauthorized = the token was refused; Failed = any other refusal.
/// </summary>
public enum CheckOutcome { Unchanged, StageUpdated, ProposalFiled, KaekMismatch, NotFound, Failed, ServerError, Unsupported, Unauthorized }

public sealed record CheckResult(CheckOutcome Outcome, double? DifferenceSqm, string? Message);

/// <summary>
/// What an import needs from GreekPlot: the Parcels it already has in the view, create one, and check one (update mode).
/// The real one is <see cref="NadlanApiClient"/>; tests use a fake.
/// </summary>
public interface INadlanParcels
{
    Task<IReadOnlyList<ExistingParcel>> ListParcelsInAsync(double west, double south, double east, double north, CancellationToken ct);

    Task<CreateResult> CreateParcelAsync(string kaek, int? geographicAreaId, IReadOnlyList<IReadOnlyList<LonLat>> rings, double areaSqm, string notes,
        CancellationToken ct, string? cadastralStage = null, DateTime? seenUtc = null);

    /// <param name="found">The site answered a shape at the point (then kaekSeen, rings and areaSqm), or nothing.</param>
    Task<CheckResult> CheckParcelAsync(long parcelId, bool found, string? kaekSeen, string cadastralStage, DateTime seenUtc,
        IReadOnlyList<IReadOnlyList<LonLat>>? rings, double? areaSqm, CancellationToken ct);
}

/// <summary>Rejected = Nadlan refused this parcel (4xx). ServerError = Nadlan failed (5xx); may be a one-off or the server being down.
/// Unauthorized = Nadlan refused the importer's token (revoked, expired, user deactivated): connect again.</summary>
public enum CreateOutcome { Created, AlreadyExists, Rejected, ServerError, Unauthorized }

/// <param name="Message">Why it was not created, or - when it was - a warning from Nadlan (e.g. the overlap check was skipped).</param>
public sealed record CreateResult(CreateOutcome Outcome, long? ParcelId, IReadOnlyList<string> OverlapsWith, string? Message);

/// <summary>Talks to the Nadlan web app over its public HTTP API (the same calls the map page makes).</summary>
public sealed class NadlanApiClient : INadlanParcels, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public NadlanApiClient(Uri baseUrl, string? token)
    {
        _http = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(60) };
        SetToken(token);
    }

    public Uri BaseUrl => _http.BaseAddress!;

    /// <summary>Raised when Nadlan answers 401/403: the token is missing, revoked or its user deactivated.</summary>
    public event Action? TokenRejected;

    /// <summary>Nadlan API token ("Connect to Nadlan", or Admin > Users > API tokens): the importer acts as that user.</summary>
    public void SetToken(string? token) => _http.DefaultRequestHeaders.Authorization =
        string.IsNullOrWhiteSpace(token) ? null : new AuthenticationHeaderValue("Bearer", token.Trim());

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            return await _http.GetFromJsonAsync<T>(url, Json, ct) ?? throw new InvalidOperationException($"Empty reply from /{url}.");
        }
        catch (HttpRequestException ex) when (IsRefused(ex.StatusCode))
        {
            TokenRejected?.Invoke();
            throw;
        }
    }

    private static bool IsRefused(HttpStatusCode? status) => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;


    /// <summary>Who the token signs in as, and whether that user is an Admin (the records module is Admin-only).</summary>
    public async Task<(string DisplayName, bool IsAdmin)> MeAsync(CancellationToken ct)
    {
        var me = await GetJsonAsync<MeDto>("api/auth/me", ct);
        return (me.DisplayName, me.IsAdmin);
    }

    /// <summary>
    /// The real KAEKs of a geographic area's Parcels that have no Cadastre record yet (any status but Error counts as fetched).
    /// Admin only: anyone else gets 404.
    /// </summary>
    public async Task<AreaKaeks> GetAreaKaeksAsync(int areaId, CancellationToken ct) =>
        await GetJsonAsync<AreaKaeks>(FormattableString.Invariant($"api/cadastre/kaeks?areaId={areaId}"), ct);

    /// <summary>Saves one KAEK's record (a new snapshot; the older ones are kept). Returns null when saved, else why not.</summary>
    public async Task<string?> SaveCadastreRecordAsync(Cadastre.CadastreRecordDto record, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("api/cadastre/records", record, Json, ct);
        }
        catch (HttpRequestException ex)
        {
            return "GreekPlot could not be reached: " + ex.Message;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return "GreekPlot did not answer in time.";
        }

        using (response)
        {
            if (response.IsSuccessStatusCode) return null;
            var text = await response.Content.ReadAsStringAsync(CancellationToken.None);
            if (IsRefused(response.StatusCode)) TokenRejected?.Invoke();
            if (response.StatusCode == HttpStatusCode.NotFound && ParseError(text) is null)
            {
                return "HTTP 404: this GreekPlot server has no Cadastre records yet (it needs the update with database change 031).";
            }

            var firstLine = text.Split('\n', 2)[0].Trim();
            return $"HTTP {(int)response.StatusCode}: " + (ParseError(text)?.Message ?? (firstLine.Length > 0 ? firstLine[..Math.Min(firstLine.Length, 300)] : "(empty reply)"));
        }
    }

    public async Task<IReadOnlyList<GeographicAreaItem>> ListActiveAreasAsync(CancellationToken ct)
    {
        var reference = await GetJsonAsync<ReferenceDto>("api/reference", ct);
        return reference.GeographicAreas.Where(a => a.IsActive).OrderBy(a => a.Name, StringComparer.CurrentCulture).ToList();
    }

    public async Task<IReadOnlyList<ExistingParcel>> ListParcelsInAsync(double west, double south, double east, double north, CancellationToken ct)
    {
        var url = FormattableString.Invariant($"api/parcels?West={west}&South={south}&East={east}&North={north}");
        var page = await GetJsonAsync<ParcelPageDto>(url, ct);
        return page.Items
            .Where(i => i.Summary.RegistryId is not null && i.Geometry?.Coordinates is not null)
            .Select(i => new ExistingParcel(
                i.Summary.RegistryId!,
                i.Summary.RegistryIdIsProvisional,
                i.Geometry!.Coordinates!.Select(r => (IReadOnlyList<LonLat>)r.Select(p => new LonLat(p[0], p[1])).ToList()).ToList(),
                i.Summary.ParcelId))
            .ToList();
    }

    /// <summary>
    /// One check of a Parcel GreekPlot already has (update mode, Admin only): what the site answered at a point well inside
    /// it. GreekPlot measures the difference itself and decides (stage updated, a change filed for an Admin, ...).
    /// </summary>
    public async Task<CheckResult> CheckParcelAsync(long parcelId, bool found, string? kaekSeen, string cadastralStage, DateTime seenUtc,
        IReadOnlyList<IReadOnlyList<LonLat>>? rings, double? areaSqm, CancellationToken ct)
    {
        var body = new
        {
            outcome = found ? "Found" : "NotFound",
            kaekSeen,
            cadastralStage,
            seenUtc,
            request = Ktimanet.KtimanetClick.RequestOf(cadastralStage),
            rings = rings?.Select(r => r.Select(p => new[] { p.Lon, p.Lat })),
            areaSqm = areaSqm is double a ? Math.Round((decimal)a, 2) : (decimal?)null,
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.PutAsJsonAsync(FormattableString.Invariant($"api/parcels/{parcelId}/cadastre-check"), body, Json, ct);
        }
        catch (HttpRequestException ex)
        {
            return new CheckResult(CheckOutcome.ServerError, null, "GreekPlot could not be reached: " + ex.Message);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
            {
                var reply = JsonSerializer.Deserialize<CheckDto>(text, Json);
                var outcome = Enum.TryParse<CheckOutcome>(reply?.Outcome, out var o) && o <= CheckOutcome.NotFound ? o : CheckOutcome.Failed;
                return new CheckResult(outcome, reply?.DifferenceSqm, reply?.Note ?? (outcome == CheckOutcome.Failed ? "Unknown reply: " + reply?.Outcome : null));
            }

            var error = ParseError(text);
            if (IsRefused(response.StatusCode))
            {
                TokenRejected?.Invoke();
                return new CheckResult(CheckOutcome.Unauthorized, null, error?.Message ?? $"HTTP {(int)response.StatusCode}");
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed && error is null)
            {
                return new CheckResult(CheckOutcome.Unsupported, null,
                    "this GreekPlot server can't check existing parcels yet (it needs the update with database change 034).");
            }

            var firstLine = text.Split('\n', 2)[0].Trim();
            var message = $"HTTP {(int)response.StatusCode}: " + (error?.Message ?? (firstLine.Length > 0 ? firstLine[..Math.Min(firstLine.Length, 300)] : "(empty reply)"));
            return new CheckResult((int)response.StatusCode >= 500 ? CheckOutcome.ServerError : CheckOutcome.Failed, null, message);
        }
    }

    /// <summary>
    /// Creates the Parcel. Overlaps are accepted on purpose: the legacy parcels with provisional (TMP-) ids stay for
    /// now and the real ones are laid over them, so the overlap is expected and only reported.
    /// </summary>
    public async Task<CreateResult> CreateParcelAsync(string kaek, int? geographicAreaId, IReadOnlyList<IReadOnlyList<LonLat>> rings,
        double areaSqm, string notes, CancellationToken ct, string? cadastralStage = null, DateTime? seenUtc = null)
    {
        var body = new
        {
            registryId = kaek,
            geographicAreaId,
            coordinates = rings.Select(r => r.Select(p => new[] { p.Lon, p.Lat })),
            officialAreaSqm = Math.Round((decimal)areaSqm, 2),
            notes,
            acceptOverlaps = true,
            // The Cadastre stage the click request answers for (GreekPlot with migration 034 keeps it; older ones ignore it).
            cadastralStage,
            cadastralStageSeenUtc = seenUtc,
            cadastralStageRequest = cadastralStage is null ? null : Ktimanet.KtimanetClick.RequestOf(cadastralStage),
        };

        using var response = await _http.PostAsJsonAsync("api/parcels", body, Json, ct);
        if (response.IsSuccessStatusCode)
        {
            var created = await response.Content.ReadFromJsonAsync<CreatedDto>(Json, ct);
            return new CreateResult(CreateOutcome.Created, created?.ParcelId,
                created?.Overlaps?.Select(o => o.RegistryId ?? $"#{o.ParcelId}").ToList() ?? new List<string>(), created?.Warning);
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        var error = ParseError(text);
        if (IsRefused(response.StatusCode))
        {
            TokenRejected?.Invoke();
            return new CreateResult(CreateOutcome.Unauthorized, null, Array.Empty<string>(), error?.Message ?? $"HTTP {(int)response.StatusCode}");
        }

        if (response.StatusCode == HttpStatusCode.Conflict && error?.Error == "PARCEL_KAEK_EXISTS")
        {
            return new CreateResult(CreateOutcome.AlreadyExists, error.ExistingParcelId, Array.Empty<string>(), error.Message);
        }

        // A non-JSON reply is usually an exception page; its first line says what went wrong.
        var firstLine = text.Split('\n', 2)[0].Trim();
        var message = error?.Message ?? (firstLine.Length > 0 ? firstLine[..Math.Min(firstLine.Length, 300)] : "(empty reply)");
        return new CreateResult((int)response.StatusCode >= 500 ? CreateOutcome.ServerError : CreateOutcome.Rejected,
            null, Array.Empty<string>(), $"HTTP {(int)response.StatusCode}: {message}");
    }

    private static ErrorDto? ParseError(string text)
    {
        try { return text.Length == 0 ? null : JsonSerializer.Deserialize<ErrorDto>(text, Json); }
        catch (JsonException) { return null; }
    }

    public void Dispose() => _http.Dispose();

    private sealed record ReferenceDto(List<GeographicAreaItem> GeographicAreas);
    private sealed record MeDto(string DisplayName, bool IsAdmin);
    private sealed record ParcelPageDto(List<ParcelItemDto> Items);
    private sealed record ParcelItemDto(ParcelSummaryDto Summary, PolygonDto? Geometry);
    private sealed record ParcelSummaryDto(string? RegistryId, bool RegistryIdIsProvisional, long ParcelId = 0);
    private sealed record CheckDto(string? Outcome, double? DifferenceSqm, string? Note);
    private sealed record PolygonDto([property: JsonPropertyName("coordinates")] double[][][]? Coordinates);
    private sealed record CreatedDto(long ParcelId, List<OverlapDto>? Overlaps, string? Warning);
    private sealed record OverlapDto(long ParcelId, string? RegistryId);
    private sealed record ErrorDto(string? Error, string? Message, long? ExistingParcelId);
}
