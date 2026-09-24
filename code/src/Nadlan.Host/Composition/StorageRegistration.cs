using Nadlan.Core.Files;
using Nadlan.Storage.S3;

namespace Nadlan.Host.Composition;

public static class StorageRegistration
{
    /// <summary>Reads Nadlan:Storage once at startup (DB-first config is already loaded). Restart after changing it.</summary>
    /// <summary>S3 SigV4 presigned URLs live at most 7 days; 0 or less would produce dead links.</summary>
    private const int MaxPresignMinutes = 7 * 24 * 60;

    public static IServiceCollection AddNadlanFileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
        options.Delivery.UrlMinutes = Math.Clamp(options.Delivery.UrlMinutes, 1, MaxPresignMinutes);
        var storage = new S3ObjectStorage(options);

        services.AddSingleton(options);
        services.AddSingleton<IObjectStorage>(storage);
        services.AddSingleton<IFileUrlProvider>(storage.IsConfigured
            ? FileUrlProviderFactory.Create(options, storage)
            : new NoStorageUrlProvider());
        services.AddSingleton(new FileStorageSettings
        {
            PartSizeBytes = Math.Max(5, options.PartSizeMb) * 1024L * 1024,
            MaxFileSizeBytes = Math.Max(1, options.MaxFileSizeGb) * 1024L * 1024 * 1024,
            PartUrlLifetime = TimeSpan.FromMinutes(Math.Clamp(options.UploadUrlMinutes, 5, MaxPresignMinutes)),
        });
        services.AddSingleton<FileService>();
        services.AddHostedService<Nadlan.Host.Files.AbandonedUploadSweeper>();
        return services;
    }

    private sealed class NoStorageUrlProvider : IFileUrlProvider
    {
        public string GetUrl(string storageKey) => "";
    }
}
