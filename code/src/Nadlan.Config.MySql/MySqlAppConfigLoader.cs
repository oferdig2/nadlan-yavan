using Microsoft.Extensions.Configuration;

namespace Nadlan.Config.MySql;

/// <param name="MsKey">Process key; its row is ms:{MsKey}.</param>
/// <param name="MsRootSectionName">appsettings section that seeds the ms row when it is missing.</param>
/// <param name="SeedCommonWhenMissing">
/// Only the web host seeds common:application (from its "Common" section). Other processes read it but never
/// overwrite it - same rule as Futuristic, where otherwise the last process to start wins.
/// </param>
public sealed record AppConfigLoaderOptions(string MsKey, string MsRootSectionName, bool SeedCommonWhenMissing = false)
{
    public const string CommonSectionName = "Common";
}

/// <summary>
/// DB-first configuration, following Futuristic SaaS (BeReplay.SaaS.Persistence.Config.MySql):
/// 1) common:application, then 2) ms:{key} are loaded from app_config into IConfiguration.
/// A missing/invalid row is seeded once from appsettings; after that, the DB is the source of truth.
/// Callers re-add environment variables afterwards so ops overrides still win.
/// </summary>
public static class MySqlAppConfigLoader
{
    public static async Task AddDbBackedConfigAsync(
        IConfigurationBuilder builder, string connectionString, AppConfigLoaderOptions options, CancellationToken ct = default)
    {
        var fileConfig = builder.Build();
        var store = new AppConfigMySqlStore(connectionString);

        var common = await LoadOrSeedAsync(store, AppConfigKeys.Common,
            options.SeedCommonWhenMissing ? () => AppConfigJson.FromSection(fileConfig, AppConfigLoaderOptions.CommonSectionName) : null, ct);
        if (common is not null)
        {
            builder.AddInMemoryCollection(JsonKeyValueFlattener.Flatten(common));
        }

        var ms = await LoadOrSeedAsync(store, AppConfigKeys.ForMs(options.MsKey),
            () => AppConfigJson.FromSection(fileConfig, options.MsRootSectionName), ct);
        if (ms is not null)
        {
            builder.AddInMemoryCollection(JsonKeyValueFlattener.Flatten(ms));
        }
    }

    private static async Task<string?> LoadOrSeedAsync(AppConfigMySqlStore store, string configKey, Func<string?>? seedFromFile, CancellationToken ct)
    {
        var (found, json, _) = await store.TryGetAsync(configKey, ct);
        if (found && AppConfigJson.IsValidObject(json))
        {
            return json;
        }

        var seed = seedFromFile?.Invoke();
        if (!AppConfigJson.IsValidObject(seed))
        {
            return null; // never overwrite the DB with nothing
        }

        await store.UpsertAsync(configKey, seed!, ct);
        return seed;
    }
}
