using Microsoft.Extensions.Options;
using Nadlan.Core.Files;

namespace Nadlan.Host.Configuration;

/// <summary>The "Nadlan" config section. Stored in app_config (DB-first), seeded from appsettings.json.</summary>
public sealed class NadlanOptions
{
    public const string SectionName = "Nadlan";

    public MapsOptions Maps { get; set; } = new();

    public AuthOptions Auth { get; set; } = new();

    public PlacesOptions Places { get; set; } = new();

    public GeoOptions Geo { get; set; } = new();

    public VideoOptions Video { get; set; } = new();

    /// <summary>
    /// Large videos made light (migration 028; owner 2026-11-10/15): ffmpeg in a container on AWS Fargate, started per video in the
    /// bucket's region. Off until switched on with the cluster's subnets (from the CloudShell setup script).
    /// </summary>
    public sealed class VideoOptions
    {
        public bool Enabled { get; set; }

        public string Cluster { get; set; } = "nadlan-video";

        public string TaskDefinition { get; set; } = "nadlan-video-worker";

        public string Subnets { get; set; } = "";

        public string SecurityGroup { get; set; } = "";

        public int Cpu { get; set; } = 4096;

        public int MemoryMb { get; set; } = 8192;

        public int StorageGb { get; set; } = 50;

        public int MaxRunMinutes { get; set; } = 90;

        public double CostPerVcpuHour { get; set; } = 0.0356;

        public double CostPerGbHour { get; set; } = 0.00392;

        public int FrameCount { get; set; } = 10;

        public bool HeightsOnByDefault { get; set; } = true;

        public double FloorHeightM { get; set; } = 3.0;

        public double EyeHeightM { get; set; } = 1.6;

        public int OriginalRetentionDays { get; set; } = 7;

        /// <summary>yyyy-MM-dd: videos uploaded since then are converted by themselves. Empty = every video (older ones too).</summary>
        public string QueueUploadedAfter { get; set; } = "";

        public Nadlan.Core.Video.VideoSettings ToSettings() => new()
        {
            Enabled = Enabled,
            Cluster = (Cluster ?? "").Trim(),
            TaskDefinition = (TaskDefinition ?? "").Trim(),
            Subnets = (Subnets ?? "").Trim(),
            SecurityGroup = (SecurityGroup ?? "").Trim(),
            Cpu = Cpu is 1024 or 2048 or 4096 or 8192 or 16384 ? Cpu : 4096,
            MemoryMb = Math.Clamp(MemoryMb, 2048, 32768),
            StorageGb = Math.Clamp(StorageGb, 21, 200),
            MaxRunMinutes = Math.Clamp(MaxRunMinutes, 10, 600),
            CostPerVcpuHour = CostPerVcpuHour > 0 ? CostPerVcpuHour : 0.0356,
            CostPerGbHour = CostPerGbHour > 0 ? CostPerGbHour : 0.00392,
            FrameCount = Math.Clamp(FrameCount, 0, 30),
            HeightsOnByDefault = HeightsOnByDefault,
            FloorHeightM = FloorHeightM is > 1 and < 10 ? FloorHeightM : 3.0,
            EyeHeightM = EyeHeightM is >= 0 and < 5 ? EyeHeightM : 1.6,
            OriginalRetentionDays = Math.Clamp(OriginalRetentionDays, 1, 365),
            QueueUploadedAfterUtc = DateTime.TryParseExact((QueueUploadedAfter ?? "").Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : DateTime.MinValue,
        };
    }

    /// <summary>
    /// Where a visitor's IP is placed (analytics, migration 026): MaxMind GeoLite2 files on this machine, no call to anyone.
    /// Empty or a missing file = the IP is kept and the place left empty (filled in later by the daily job).
    /// </summary>
    public sealed class GeoOptions
    {
        /// <summary>GeoLite2-City.mmdb: country, region, city, approximate lat/lng.</summary>
        public string CityDbPath { get; set; } = "";

        /// <summary>GeoLite2-ASN.mmdb: the network number and its organisation (ISP).</summary>
        public string AsnDbPath { get; set; } = "";
    }

    /// <summary>Public places - beaches - and the distance to the sea (owner, 2026-10-27).</summary>
    public sealed class PlacesOptions
    {
        /// <summary>
        /// Managing Contact (id) of a beach whose creator has no linked Contact (owner: Alon Schwarz's). Empty = the user picks
        /// one. Text, so it can stay empty in the settings form; anything but a positive number counts as empty.
        /// </summary>
        public string PublicContactId { get; set; } = "";

        /// <summary>Distances to the sea up to this are shown; longer ones are not interesting.</summary>
        public double SeaDistanceMaxKm { get; set; } = 5;

        /// <summary>Beaches further than this don't count. Kept distances go up to it, so the display limit can rise to it without recalculating.</summary>
        public double SeaSearchKm { get; set; } = 10;

        public Nadlan.Core.Places.PlacesSettings ToSettings() => new()
        {
            PublicContactId = long.TryParse(PublicContactId?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null,
            SearchMetres = (int)Math.Round(SeaSearchKm * 1000),
            ShownMetres = (int)Math.Round(Math.Min(SeaDistanceMaxKm, SeaSearchKm) * 1000),
        };
    }

    /// <summary>Sign-in. Everything here lives in app_config (ms:host) like the rest; secrets are set with config.ps1.</summary>
    public sealed class AuthOptions
    {
        /// <summary>Google sign-in (OAuth client of type "Web application"). Both empty = the Google button is hidden.</summary>
        public GoogleSsoOptions Google { get; set; } = new();

        public int SessionHours { get; set; } = 12;
        public int MaxFailedLogins { get; set; } = 5;
        public int LockoutMinutes { get; set; } = 15;
        public int ResetLinkHours { get; set; } = 2;
        public int InviteLinkHours { get; set; } = 72;

        /// <summary>Base of links in emails, e.g. https://nadlan.example.com. Empty = taken from the request.</summary>
        public string PublicBaseUrl { get; set; } = "";

        /// <summary>Behind a load balancer / CloudFront that terminates HTTPS: honour X-Forwarded-Proto/For.</summary>
        public bool TrustForwardedHeaders { get; set; }

        /// <summary>SMTP for "forgot password" emails (e.g. Amazon SES SMTP). Empty host = Admins hand out links instead.</summary>
        public EmailOptions Email { get; set; } = new();
    }

    public sealed class GoogleSsoOptions
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
    }

    public sealed class EmailOptions
    {
        public string SmtpHost { get; set; } = "";
        public int SmtpPort { get; set; } = 587;
        public string SmtpUser { get; set; } = "";
        public string SmtpPassword { get; set; } = "";
        public string From { get; set; } = "";
        public bool EnableSsl { get; set; } = true;
    }

    public sealed class MapsOptions
    {
        /// <summary>Browser key for the Maps JavaScript API. Public by nature; restrict it by HTTP referrer in Google Cloud.</summary>
        public string GoogleApiKey { get; set; } = "";

        public double DefaultCenterLat { get; set; } = 38.62;
        public double DefaultCenterLon { get; set; } = 23.28;
        public int DefaultZoom { get; set; } = 12;

        /// <summary>From this zoom on, Parcels are drawn one by one; below it, the united surface (users who see all Parcels).</summary>
        public int ParcelDetailMinZoom { get; set; } = 15;

        /// <summary>More Parcels than this in view: the map draws the united surface instead (nothing is capped or dropped).</summary>
        public int MaxParcelPolygons { get; set; } = 3000;

        /// <summary>Area (code or name) the map opens on, if it has Parcels; else it opens around all Parcels.</summary>
        public string StartArea { get; set; } = "SKR";

        /// <summary>
        /// Maps JavaScript API version (v=) for the 3D customer presentation (Photorealistic 3D, present.html): "beta"
        /// while Google ships it there, "weekly" once it is in the stable channel.
        /// </summary>
        public string Maps3dChannel { get; set; } = "beta";
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
                parcelDetailMinZoom = maps.ParcelDetailMinZoom,
                maps3dChannel = string.IsNullOrWhiteSpace(maps.Maps3dChannel) ? "beta" : maps.Maps3dChannel.Trim(),
                storageConfigured = storage.IsConfigured,
                maxFileSizeBytes = files.MaxFileSizeBytes,
            });
        });
    }
}
