using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nadlan.KaekImporter.Geometry;

namespace Nadlan.KaekImporter.Api;

public sealed record GeographicAreaItem(int Id, string? Code, string Name, bool IsActive);

/// <summary>A Parcel already in Nadlan, with its outer ring and holes in lon/lat.</summary>
public sealed record ExistingParcel(string RegistryId, bool IsProvisional, IReadOnlyList<IReadOnlyList<LonLat>> Rings);

/// <summary>Rejected = Nadlan refused this parcel (4xx). ServerError = Nadlan failed (5xx); may be a one-off or the server being down.</summary>
public enum CreateOutcome { Created, AlreadyExists, Rejected, ServerError }

/// <param name="Message">Why it was not created, or - when it was - a warning from Nadlan (e.g. the overlap check was skipped).</param>
public sealed record CreateResult(CreateOutcome Outcome, long? ParcelId, IReadOnlyList<string> OverlapsWith, string? Message);

/// <summary>Talks to the Nadlan web app over its public HTTP API (the same calls the map page makes).</summary>
public sealed class NadlanApiClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public NadlanApiClient(Uri baseUrl, string? token)
    {
        _http = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrWhiteSpace(token))
        {
            // Nadlan API token (Admin > Users > API tokens): the importer acts as that user, with its permissions.
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<IReadOnlyList<GeographicAreaItem>> ListActiveAreasAsync(CancellationToken ct)
    {
        var reference = await _http.GetFromJsonAsync<ReferenceDto>("api/reference", Json, ct)
            ?? throw new InvalidOperationException("Empty reply from /api/reference.");
        return reference.GeographicAreas.Where(a => a.IsActive).OrderBy(a => a.Name, StringComparer.CurrentCulture).ToList();
    }

    public async Task<IReadOnlyList<ExistingParcel>> ListParcelsInAsync(double west, double south, double east, double north, CancellationToken ct)
    {
        var url = FormattableString.Invariant($"api/parcels?West={west}&South={south}&East={east}&North={north}");
        var page = await _http.GetFromJsonAsync<ParcelPageDto>(url, Json, ct)
            ?? throw new InvalidOperationException("Empty reply from /api/parcels.");
        return page.Items
            .Where(i => i.Summary.RegistryId is not null && i.Geometry?.Coordinates is not null)
            .Select(i => new ExistingParcel(
                i.Summary.RegistryId!,
                i.Summary.RegistryIdIsProvisional,
                i.Geometry!.Coordinates!.Select(r => (IReadOnlyList<LonLat>)r.Select(p => new LonLat(p[0], p[1])).ToList()).ToList()))
            .ToList();
    }

    /// <summary>
    /// Creates the Parcel. Overlaps are accepted on purpose: the legacy parcels with provisional (TMP-) ids stay for
    /// now and the real ones are laid over them, so the overlap is expected and only reported.
    /// </summary>
    public async Task<CreateResult> CreateParcelAsync(string kaek, int? geographicAreaId, IReadOnlyList<IReadOnlyList<LonLat>> rings,
        double areaSqm, string notes, CancellationToken ct)
    {
        var body = new
        {
            registryId = kaek,
            geographicAreaId,
            coordinates = rings.Select(r => r.Select(p => new[] { p.Lon, p.Lat })),
            officialAreaSqm = Math.Round((decimal)areaSqm, 2),
            notes,
            acceptOverlaps = true,
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
    private sealed record ParcelPageDto(List<ParcelItemDto> Items);
    private sealed record ParcelItemDto(ParcelSummaryDto Summary, PolygonDto? Geometry);
    private sealed record ParcelSummaryDto(string? RegistryId, bool RegistryIdIsProvisional);
    private sealed record PolygonDto([property: JsonPropertyName("coordinates")] double[][][]? Coordinates);
    private sealed record CreatedDto(long ParcelId, List<OverlapDto>? Overlaps, string? Warning);
    private sealed record OverlapDto(long ParcelId, string? RegistryId);
    private sealed record ErrorDto(string? Error, string? Message, long? ExistingParcelId);
}
