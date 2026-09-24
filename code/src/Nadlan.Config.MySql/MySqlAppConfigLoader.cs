using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Nadlan.Config.MySql;

/// <param name="MsKey">Process key; its row is ms:{MsKey}.</param>
/// <param name="MsRootSectionName">appsettings section that seeds the ms row.</param>
/// <param name="SeedCommonWhenMissing">
/// Only the web host seeds common:application (from its "Common" section). Other processes read it but never
/// write it - same rule as Futuristic, where otherwise the last process to start wins.
/// </param>
public sealed record AppConfigLoaderOptions(string MsKey, string MsRootSectionName, bool SeedCommonWhenMissing = false)
{
    public const string CommonSectionName = "Common";
}

/// <summary>
/// DB-first configuration, following Futuristic SaaS (BeReplay.SaaS.Persistence.Config.MySql):
/// 1) common:application, then 2) ms:{key} are loaded from app_config into IConfiguration.
/// - A missing/invalid row is seeded from appsettings.
/// - Settings added to appsettings later (new features) are added to the existing row; values already in the DB
///   are never changed, so the DB stays the source of truth and every setting is visible/editable in app_config.
/// - Seeding reads the JSON files only - never environment variables, so secrets passed as env vars stay out of the DB.
/// Callers re-add environment variables afterwards so ops overrides still win at runtime.
/// </summary>
public static class MySqlAppConfigLoader
{
    public static async Task AddDbBackedConfigAsync(
        IConfigurationBuilder builder, string connectionString, AppConfigLoaderOptions options, CancellationToken ct = default)
    {
        // Read the file defaults once, then dispose the temporary root (its file watchers would otherwise live on).
        string? commonDefaults, msDefaults;
        using (var fileConfig = FilesOnly(builder))
        {
            commonDefaults = options.SeedCommonWhenMissing ? AppConfigJson.FromSection(fileConfig, AppConfigLoaderOptions.CommonSectionName) : null;
            msDefaults = AppConfigJson.FromSection(fileConfig, options.MsRootSectionName);
        }

        var store = new AppConfigMySqlStore(connectionString);
        var common = await LoadAndSeedAsync(store, AppConfigKeys.Common, commonDefaults, ct);
        if (common is not null)
        {
            builder.AddInMemoryCollection(JsonKeyValueFlattener.Flatten(common));
        }

        var ms = await LoadAndSeedAsync(store, AppConfigKeys.ForMs(options.MsKey), msDefaults, ct);
        if (ms is not null)
        {
            builder.AddInMemoryCollection(JsonKeyValueFlattener.Flatten(ms));
        }
    }

    private static async Task<string?> LoadAndSeedAsync(AppConfigMySqlStore store, string configKey, string? defaults, CancellationToken ct)
    {
        var (found, json, _) = await store.TryGetAsync(configKey, ct);
        var hasDefaults = AppConfigJson.IsValidObject(defaults);

        // A row that exists but no longer parses (e.g. a typo from a manual SQL edit) is NOT replaced with defaults:
        // that would silently wipe every stored value. Stop and say where the problem is.
        if (found && !AppConfigJson.IsValidObject(json))
        {
            throw new InvalidOperationException(
                $"app_config row '{configKey}' is not valid JSON. Fix it (e.g. in MySQL Workbench) - it was left untouched.");
        }

        if (found)
        {
            if (!hasDefaults)
            {
                return json;
            }

            var (merged, changed) = AppConfigJson.AddMissing(json, defaults!);
            if (changed)
            {
                await store.UpsertAsync(configKey, merged, ct);
            }

            return merged;
        }

        if (!hasDefaults)
        {
            return null; // never overwrite the DB with nothing
        }

        await store.UpsertAsync(configKey, defaults!, ct);
        return defaults;
    }

    /// <summary>The builder's JSON file sources only (appsettings*.json), without env vars or command line.</summary>
    private static ConfigurationRoot FilesOnly(IConfigurationBuilder builder)
    {
        var files = new ConfigurationBuilder();
        foreach (var property in builder.Properties)
        {
            files.Properties[property.Key] = property.Value; // keeps the host's file provider / content root
        }

        foreach (var source in builder.Sources.OfType<JsonConfigurationSource>())
        {
            files.Add(source);
        }

        return (ConfigurationRoot)files.Build();
    }
}
