using Nadlan.Core.Files;
using Nadlan.Storage.S3;

namespace Nadlan.Host.Composition;

public static class StorageRegistration
{
    /// <summary>Reads Nadlan:Storage once at startup (DB-first config is already loaded). Restart after changing it.</summary>
    public static IServiceCollection AddNadlanFileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
        var storage = new S3ObjectStorage(options);

        services.AddSingleton(options);
        services.AddSingleton<IObjectStorage>(storage);
        services.AddSingleton<IFileUrlProvider>(storage.IsConfigured
            ? FileUrlProviderFactory.Create(options, storage)
            : new NoStorageUrlProvider());
        services.AddSingleton(new FileStorageSettings
        {
            KeyPrefix = string.IsNullOrWhiteSpace(options.KeyPrefix) ? "dev" : options.KeyPrefix,
            PartSizeBytes = Math.Max(5, options.PartSizeMb) * 1024L * 1024,
            MaxFileSizeBytes = Math.Max(1, options.MaxFileSizeGb) * 1024L * 1024 * 1024,
            PartUrlLifetime = TimeSpan.FromMinutes(Math.Max(5, options.UploadUrlMinutes)),
        });
        services.AddSingleton<FileService>();
        return services;
    }

    private sealed class NoStorageUrlProvider : IFileUrlProvider
    {
        public string GetUrl(string storageKey) => "";
    }
}
