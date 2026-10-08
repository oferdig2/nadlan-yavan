using System.Text.Json.Nodes;
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
        new ConfigSection("Nadlan:Auth:Email", "Email (SMTP)", "Only two emails use it: \"Forgot password?\" on the sign-in page, and \"also email it\" when an Admin makes a password link (Users). Empty host = no emails: \"Forgot password?\" tells the user to ask an Admin, who copies the link from Users instead. To turn it on, e.g. Amazon SES SMTP."),
        new ConfigSection("Nadlan:Storage", "File storage (S3)", "Where uploaded documents, photos and videos are kept. On EC2 the instance role gives access: leave AwsProfile empty."),
        new ConfigSection("Nadlan:Storage:Delivery", "File delivery", "Keep Mode = S3Presigned and leave the CloudFront fields empty: browsers then get short-lived signed links straight to the S3 bucket. CloudFront is Amazon's CDN, optional, only worth it if photos and videos load slowly far from the server."),
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

        EnsureSecretsStayWithTheirServer(AppConfigJson.IsValidObject(storedJson) ? storedJson : null, editedJson!);
        var json = AppConfigSecrets.Unmask(editedJson!, storedJson);
        // First: a spelling or null that would break the app is refused even when the values look unchanged (the change
        // detection ignores case, as the app does).
        EnsureSafeChange(key, AppConfigJson.IsValidObject(storedJson) ? storedJson : null, json);
        var changed = AppConfigSecrets.ChangedPaths(AppConfigJson.IsValidObject(storedJson) ? storedJson : null, json);
        if (changed.Count == 0)
        {
            return (storedUpdated, changed);
        }

        await ValidateAsync(key, json, ct);
        // The version before this save is kept (app_config_history): "sudo nadlan-db config undo <key>" puts it back.
        var saved = await _store.TryReplaceAsync(key, json, storedUpdated, $"settings page: {user}", ct) ?? throw new EditConflictException($"Setting row {key}");
        _log.LogWarning("Settings page: {User} changed app_config {Key}: {Paths}", user, key, string.Join(", ", changed));
        return (saved, changed);
    }

    /// <summary>Allowed range of each number setting (<see cref="AppConfigLimits"/>, shared with nadlan-db config set).</summary>
    public static IReadOnlyDictionary<string, (double Min, double Max)> Limits => AppConfigLimits.All;

    /// <summary>
    /// What a save may not do, whichever view it comes from:
    /// - spell one setting twice in different case ("Nadlan" and "nadlan"): the app reads the last one, and at its next
    ///   start fills it with development defaults that override the real values and secrets;
    /// - set a value to null, or remove a setting (also by changing its case): at the next start the app would put back its
    ///   built-in (development) value, e.g. the development storage folder, so every stored file would look missing;
    /// - in the rows this app reads, add or change anything outside "Nadlan:" (Urls, Kestrel, Logging, ...): the app
    ///   obeys those at startup, before any of our checks.
    /// </summary>
    public static void EnsureSafeChange(string key, string? storedJson, string json)
    {
        var before = Read(storedJson).Leaves;
        var (after, caseVariants, nulls) = Read(json);
        if (caseVariants.Count > 0)
        {
            throw new DomainValidationException("CONFIG_CASE_VARIANT",
                $"{string.Join("; ", caseVariants)}: the same setting spelled twice in different case. The app would use the last one and " +
                "fill it with development defaults at its next start. Keep one spelling. Nothing was saved.");
        }

        if (nulls.Count > 0)
        {
            throw new DomainValidationException("CONFIG_NULL_VALUE",
                $"{string.Join(", ", nulls)}: null means \"no value\" to the app, which then uses its development default. Give it a value, " +
                "or an empty one (\"\"). Nothing was saved.");
        }

        var removed = before.Keys.Where(k => !after.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (removed.Count > 0)
        {
            throw new DomainValidationException("CONFIG_SETTING_REMOVED",
                $"{string.Join(", ", removed)}: settings can't be removed or renamed here - at its next start the app would put back its built-in " +
                "development value. Give it a value (or an empty one) instead. Nothing was saved.");
        }

        if (key != AppConfigKeys.Common && key != HostRow)
        {
            return;
        }

        var outside = after.Where(kv => !kv.Key.StartsWith(NadlanOptions.SectionName + ":", StringComparison.Ordinal) &&
                                        (!before.TryGetValue(kv.Key, out var b) || b != kv.Value))
            .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (outside.Count > 0)
        {
            throw new DomainValidationException("CONFIG_OUTSIDE_APP_SETTINGS",
                $"{string.Join(", ", outside)}: only settings under \"{NadlanOptions.SectionName}\" can be added or changed here - the app obeys the " +
                "others when it starts, before any check (a wrong one keeps it from starting). Nothing was saved.");
        }
    }

    /// <summary>
    /// A new SMTP server must get its own password: keeping the stored one (still masked in the form) would send it to the
    /// new server at the first email.
    /// </summary>
    public static void EnsureSecretsStayWithTheirServer(string? storedJson, string editedJson)
    {
        if (storedJson is null)
        {
            return;
        }

        var stored = AppConfigJson.Paths(storedJson);
        var edited = AppConfigJson.Paths(editedJson);
        string Value(IReadOnlyDictionary<string, string?> d, string p) => (d.TryGetValue(p, out var v) ? v : null)?.Trim() ?? "";
        const string host = "Nadlan:Auth:Email:SmtpHost", password = "Nadlan:Auth:Email:SmtpPassword";
        if (!string.Equals(Value(stored, host), Value(edited, host), StringComparison.OrdinalIgnoreCase) &&
            Value(stored, password).Length > 0 && Value(edited, password) == AppConfigSecrets.MaskedValue)
        {
            throw new DomainValidationException("CONFIG_SECRET_FOR_NEW_SERVER",
                "You changed the SMTP server: type the SMTP password for the new server too (or clear it) - the stored one belongs to the old " +
                "server and would be sent to the new one. Nothing was saved.");
        }
    }

    /// <summary>
    /// At startup: the settings the app is about to run with must bind and be in range. A bad value already stored (a tool,
    /// a hand edit, an older release) stops the app here, with the way back in the log - instead of errors on every request.
    /// </summary>
    public static void EnsureStartupSettings(IConfiguration config)
    {
        const string fix = " Undo the last settings change: sudo nadlan-db config undo ms:host (or 7-server-admin.ps1 -> \"Undo the last " +
                           "settings change\"; \"config history ms:host\" lists earlier versions), then restart the app.";
        try
        {
            config.GetSection(NadlanOptions.SectionName).Get<NadlanOptions>();
            config.GetSection(StorageOptions.SectionName).Get<StorageOptions>();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Settings problem in app_config: {ex.Message} {ex.InnerException?.Message}{fix}", ex);
        }

        try
        {
            EnsureLimits(config, "");
        }
        catch (DomainValidationException ex)
        {
            throw new InvalidOperationException($"Settings problem in app_config: {ex.Message}{fix}", ex);
        }
    }

    /// <summary>Every number setting within its <see cref="Limits"/> (as the app will read it, after this save).</summary>
    public static void EnsureLimits(IConfiguration config, string after = " Nothing was saved.")
    {
        foreach (var path in Limits.Keys.Concat(AppConfigLimits.Booleans))
        {
            if (config[path] is null)
            {
                continue; // not set: the app's default applies
            }

            var label = Fields.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))?.Label;
            if (AppConfigLimits.Check(path, config[path], label) is { } error)
            {
                throw new DomainValidationException("CONFIG_OUT_OF_RANGE", error + after);
            }
        }
    }

    /// <summary>A row's settings with their exact spelling, plus the keys that differ only in case and the null values.</summary>
    private static (Dictionary<string, string?> Leaves, List<string> CaseVariants, List<string> Nulls) Read(string? json)
    {
        var leaves = new Dictionary<string, string?>(StringComparer.Ordinal);
        var variants = new List<string>();
        var nulls = new List<string>();
        if (json is null)
        {
            return (leaves, variants, nulls);
        }

        static string Join(string path, string name) => path.Length == 0 ? name : path + ":" + name;
        void Visit(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject obj:
                    variants.AddRange(obj.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
                        .Select(g => string.Join(" / ", g.Select(p => Join(path, p.Key)))));
                    foreach (var (name, child) in obj)
                    {
                        Visit(child, Join(path, name));
                    }

                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        Visit(array[i], Join(path, i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    }

                    break;
                case null:
                    nulls.Add(path);
                    leaves[path] = null;
                    break;
                default:
                    leaves[path] = node.GetValueKind() == System.Text.Json.JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
                    break;
            }
        }

        try
        {
            Visit(AppConfigJson.ParseObject(json), "");
        }
        catch (ArgumentException ex) // the same key twice, spelled exactly the same
        {
            throw new DomainValidationException("CONFIG_CASE_VARIANT", $"A setting appears twice: {ex.Message} Nothing was saved.");
        }

        return (leaves, variants, nulls);
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

        EnsureLimits(config);

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
