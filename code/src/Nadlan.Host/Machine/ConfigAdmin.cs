using Nadlan.Config.MySql;
using Nadlan.Core.Validation;
using Nadlan.Host.Configuration;
using Nadlan.Persistence.MySql;
using Nadlan.Storage.S3;

namespace Nadlan.Host.Machine;

/// <summary>How the settings page shows one setting. Kind: text, url, integer, number, bool, choice, secret, multiline-secret.</summary>
public sealed record ConfigField(string Path, string Label, string Kind, string Help, IReadOnlyList<string>? Choices = null);

public sealed record ConfigSection(string Path, string Title, string Help);

/// <summary>
/// The app_config rows (DB-first settings) for the settings page. Secrets never leave the server (AppConfigSecrets).
/// A save is checked the way the app reads it at startup - every typed value must convert, the storage settings must make
/// a working URL provider - because a row that doesn't bind would stop the app from starting on its next restart.
/// </summary>
public sealed class ConfigAdmin
{
    private const string HostRow = "ms:host";

    private readonly AppConfigMySqlStore _store;
    private readonly ILogger<ConfigAdmin> _log;

    public ConfigAdmin(MySqlDatabase db, ILogger<ConfigAdmin> log)
    {
        _store = new AppConfigMySqlStore(db.ConnectionString);
        _log = log;
    }

    public static readonly IReadOnlyList<ConfigSection> Sections = new[]
    {
        new ConfigSection("Nadlan:Maps", "Map", "Google Maps and where the map opens."),
        new ConfigSection("Nadlan:Auth", "Sign-in", "Sessions, lockout and password links."),
        new ConfigSection("Nadlan:Auth:Google", "Google sign-in", "OAuth client of type \"Web application\"; redirect URI https://<domain>/signin-google. Both empty = no Google button."),
        new ConfigSection("Nadlan:Auth:Email", "Email (SMTP)", "For \"forgot password\" emails, e.g. Amazon SES SMTP. Empty host = Admins hand out password links instead."),
        new ConfigSection("Nadlan:Storage", "File storage (S3)", "Where uploaded documents, photos and videos are kept. On EC2 the instance role gives access: leave AwsProfile empty."),
        new ConfigSection("Nadlan:Storage:Delivery", "File delivery", "How browsers download files: straight from S3 (presigned) or through CloudFront."),
    };

    public static readonly IReadOnlyList<ConfigField> Fields = new[]
    {
        new ConfigField("Nadlan:Maps:GoogleApiKey", "Google Maps key", "text", "Browser key for the Maps JavaScript API (and Map Tiles for 3D). Pages show it by nature: restrict it to your domain in Google Cloud."),
        new ConfigField("Nadlan:Maps:DefaultCenterLat", "Default centre - latitude", "number", "Where the map opens when no Start area applies."),
        new ConfigField("Nadlan:Maps:DefaultCenterLon", "Default centre - longitude", "number", ""),
        new ConfigField("Nadlan:Maps:DefaultZoom", "Default zoom", "integer", "1 = the world, 21 = a house."),
        new ConfigField("Nadlan:Maps:ParcelDetailMinZoom", "Parcels one by one from zoom", "integer", "Below this zoom, users who see all parcels get the united surface."),
        new ConfigField("Nadlan:Maps:MaxParcelPolygons", "Most parcels drawn one by one", "integer", "More parcels than this in view: the united surface is drawn instead."),
        new ConfigField("Nadlan:Maps:StartArea", "Start area", "text", "Area code or name the map opens on, e.g. SKR."),
        new ConfigField("Nadlan:Maps:Maps3dChannel", "3D maps channel", "choice", "Maps JavaScript API version for the 3D presentation: beta until Google ships 3D in weekly.", new[] { "beta", "weekly", "alpha" }),

        new ConfigField("Nadlan:Auth:SessionHours", "Session length (hours)", "integer", "A sign-in lasts this long after the last use."),
        new ConfigField("Nadlan:Auth:MaxFailedLogins", "Wrong passwords before lockout", "integer", ""),
        new ConfigField("Nadlan:Auth:LockoutMinutes", "Lockout (minutes)", "integer", ""),
        new ConfigField("Nadlan:Auth:ResetLinkHours", "Reset link valid (hours)", "integer", ""),
        new ConfigField("Nadlan:Auth:InviteLinkHours", "Invitation link valid (hours)", "integer", ""),
        new ConfigField("Nadlan:Auth:PublicBaseUrl", "Public address", "url", "Base of links in emails, e.g. https://www.greekplot.com. Never taken from the request."),
        new ConfigField("Nadlan:Auth:TrustForwardedHeaders", "Behind a proxy (nginx)", "bool", "On for the server (nginx terminates HTTPS); off on a PC. Wrong here = insecure cookies or broken Google sign-in."),
        new ConfigField("Nadlan:Auth:Google:ClientId", "Client ID", "text", ""),
        new ConfigField("Nadlan:Auth:Google:ClientSecret", "Client secret", "secret", ""),
        new ConfigField("Nadlan:Auth:Email:SmtpHost", "SMTP host", "text", "e.g. email-smtp.eu-north-1.amazonaws.com"),
        new ConfigField("Nadlan:Auth:Email:SmtpPort", "SMTP port", "integer", "587 with STARTTLS."),
        new ConfigField("Nadlan:Auth:Email:SmtpUser", "SMTP user", "text", ""),
        new ConfigField("Nadlan:Auth:Email:SmtpPassword", "SMTP password", "secret", ""),
        new ConfigField("Nadlan:Auth:Email:From", "From address", "text", "A sender address verified with the mail service."),
        new ConfigField("Nadlan:Auth:Email:EnableSsl", "Use TLS", "bool", ""),

        new ConfigField("Nadlan:Storage:Bucket", "Bucket", "text", "Empty = file features off (the rest of the app still runs)."),
        new ConfigField("Nadlan:Storage:Region", "Region", "text", "e.g. eu-north-1"),
        new ConfigField("Nadlan:Storage:AwsProfile", "AWS profile", "text", "Developer PCs only. Empty on the server (instance role)."),
        new ConfigField("Nadlan:Storage:RootFolder", "Root folder", "text", "Folder in the bucket, e.g. nadlan/prod. Changing it does NOT move files: copy them first (aws s3 sync)."),
        new ConfigField("Nadlan:Storage:PartSizeMb", "Upload part size (MB)", "integer", "At least 5."),
        new ConfigField("Nadlan:Storage:MaxFileSizeGb", "Largest file (GB)", "integer", ""),
        new ConfigField("Nadlan:Storage:UploadUrlMinutes", "Upload link valid (minutes)", "integer", ""),
        new ConfigField("Nadlan:Storage:Delivery:Mode", "Mode", "choice", "S3Presigned: no CDN. CloudFrontSigned: private bucket behind CloudFront. CloudFrontPublic: public CDN.",
            new[] { DeliveryModes.S3Presigned, DeliveryModes.CloudFrontSigned, DeliveryModes.CloudFrontPublic }),
        new ConfigField("Nadlan:Storage:Delivery:CloudFrontDomain", "CloudFront domain", "text", "e.g. d1234abcd.cloudfront.net"),
        new ConfigField("Nadlan:Storage:Delivery:CloudFrontOriginPath", "CloudFront origin path", "text", "The distribution's origin path, if any (part of Root folder)."),
        new ConfigField("Nadlan:Storage:Delivery:KeyPairId", "CloudFront key pair id", "text", ""),
        new ConfigField("Nadlan:Storage:Delivery:PrivateKeyPem", "CloudFront private key (PEM)", "multiline-secret", ""),
        new ConfigField("Nadlan:Storage:Delivery:UrlMinutes", "Download link valid (minutes)", "integer", ""),
    };

    public static string TitleOf(string key) => key switch
    {
        AppConfigKeys.Common => "Shared settings (all GreekPlot programs)",
        HostRow => "Web app (GreekPlot server)",
        _ => key,
    };

    public async Task<object> ListAsync(CancellationToken ct)
    {
        var rows = await _store.ListAsync(ct);
        return rows.Select(r => new { key = r.ConfigKey, title = TitleOf(r.ConfigKey), updatedUtcMs = r.UpdatedUtcMs }).ToList();
    }

    public async Task<object> GetAsync(string key, CancellationToken ct)
    {
        var (found, json, updated) = await _store.TryGetAsync(key, ct);
        if (!found)
        {
            throw new EntityNotFoundException("Setting row", key);
        }

        return new
        {
            key,
            title = TitleOf(key),
            updatedUtcMs = updated,
            valid = AppConfigJson.IsValidObject(json),
            // A row broken by a hand edit is shown as text (secrets can't be masked in it) so it can be repaired here.
            json = AppConfigJson.IsValidObject(json) ? AppConfigSecrets.Mask(json) : json,
            masked = AppConfigSecrets.MaskedValue,
            sections = Sections,
            fields = Fields,
        };
    }

    /// <summary>Saves an edited row. Returns the new version and the changed setting paths (empty = nothing to save).</summary>
    public async Task<(long UpdatedUtcMs, IReadOnlyList<string> Changed)> SaveAsync(string key, string? editedJson, long updatedUtcMs, string user, CancellationToken ct)
    {
        if (!AppConfigJson.IsValidObject(editedJson))
        {
            throw new DomainValidationException("CONFIG_NOT_JSON", "The settings must be one JSON object { ... }.");
        }

        var (found, storedJson, storedUpdated) = await _store.TryGetAsync(key, ct);
        if (!found)
        {
            throw new EntityNotFoundException("Setting row", key);
        }

        if (storedUpdated != updatedUtcMs)
        {
            throw new EditConflictException($"Setting row {key}");
        }

        var json = AppConfigSecrets.Unmask(editedJson!, storedJson);
        var changed = AppConfigSecrets.ChangedPaths(AppConfigJson.IsValidObject(storedJson) ? storedJson : null, json);
        if (changed.Count == 0)
        {
            return (storedUpdated, changed);
        }

        await ValidateAsync(key, json, ct);
        var saved = await _store.TryReplaceAsync(key, json, storedUpdated, ct) ?? throw new EditConflictException($"Setting row {key}");
        _log.LogWarning("Settings page: {User} changed app_config {Key}: {Paths}", user, key, string.Join(", ", changed));
        return (saved, changed);
    }

    private async Task ValidateAsync(string key, string json, CancellationToken ct)
    {
        // The host reads common:application, then ms:host; check the pair it will read after this save.
        string? common = null, host = null;
        if (key == AppConfigKeys.Common)
        {
            common = json;
            host = (await _store.TryGetAsync(HostRow, ct)).JsonText;
        }
        else if (key == HostRow)
        {
            common = (await _store.TryGetAsync(AppConfigKeys.Common, ct)).JsonText;
            host = json;
        }
        else
        {
            return; // a row this app doesn't read: being a JSON object is all we can check
        }

        var config = MySqlAppConfigLoader.BuildFromRows(common, host);
        NadlanOptions options;
        StorageOptions storage;
        try
        {
            options = config.GetSection(NadlanOptions.SectionName).Get<NadlanOptions>() ?? new();
            storage = config.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new();
        }
        catch (InvalidOperationException ex)
        {
            // e.g. "Failed to convert configuration value at 'Nadlan:Auth:SessionHours' to type 'System.Int32'."
            throw new DomainValidationException("CONFIG_INVALID", (ex.InnerException is null ? ex.Message : $"{ex.Message} {ex.InnerException.Message}") +
                " The app would not start with this, so nothing was saved.");
        }

        var url = options.Auth.PublicBaseUrl.Trim();
        if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)))
        {
            throw new DomainValidationException("CONFIG_INVALID", "Public address must be a full address such as https://www.greekplot.com.");
        }

        if (string.IsNullOrWhiteSpace(options.Auth.Google.ClientId) != string.IsNullOrWhiteSpace(options.Auth.Google.ClientSecret))
        {
            throw new DomainValidationException("CONFIG_INVALID", "Google sign-in needs both the client ID and the client secret (or neither).");
        }

        if (!string.IsNullOrWhiteSpace(storage.Bucket))
        {
            // Exactly what startup does (StorageRegistration): an unknown mode, a bad PEM or CloudFront domain throws there.
            try
            {
                using var s3 = new S3ObjectStorage(storage);
                (FileUrlProviderFactory.Create(storage, s3) as IDisposable)?.Dispose();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or System.Security.Cryptography.CryptographicException)
            {
                throw new DomainValidationException("CONFIG_INVALID", $"File storage: {ex.Message} The app would not start with this, so nothing was saved.");
            }
        }
    }
}
