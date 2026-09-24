using Microsoft.Extensions.Options;
using Nadlan.Core.Files;

namespace Nadlan.Host.Configuration;

/// <summary>The "Nadlan" config section. Stored in app_config (DB-first), seeded from appsettings.json.</summary>
public sealed class NadlanOptions
{
    public const string SectionName = "Nadlan";

    public MapsOptions Maps { get; set; } = new();

    public sealed class MapsOptions
    {
        /// <summary>Browser key for the Maps JavaScript API. Public by nature; restrict it by HTTP referrer in Google Cloud.</summary>
        public string GoogleApiKey { get; set; } = "";

        public double DefaultCenterLat { get; set; } = 38.62;
        public double DefaultCenterLon { get; set; } = 23.28;
        public int DefaultZoom { get; set; } = 12;
    }
}

public static class ClientConfigEndpoints
{
    /// <summary>Only non-secret, browser-safe settings go through here.</summary>
    public static void MapClientConfigEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/config/client", (IOptions<NadlanOptions> options, IObjectStorage storage, FileStorageSettings files) =>
        {
            var maps = options.Value.Maps;
            return Results.Ok(new
            {
                googleMapsApiKey = maps.GoogleApiKey,
                defaultCenter = new { lat = maps.DefaultCenterLat, lng = maps.DefaultCenterLon },
                defaultZoom = maps.DefaultZoom,
                storageConfigured = storage.IsConfigured,
                maxFileSizeBytes = files.MaxFileSizeBytes,
            });
        });
    }
}
