using Microsoft.Extensions.Options;
using Nadlan.Core.Files;

namespace Nadlan.Host.Configuration;

/// <summary>The "Nadlan" config section. Stored in app_config (DB-first), seeded from appsettings.json.</summary>
public sealed class NadlanOptions
{
    public const string SectionName = "Nadlan";

    public MapsOptions Maps { get; set; } = new();

    public AuthOptions Auth { get; set; } = new();

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
