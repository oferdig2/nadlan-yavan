using Nadlan.Core.Files;

namespace Nadlan.Storage.S3;

public static class FileUrlProviderFactory
{
    public static IFileUrlProvider Create(StorageOptions options, S3ObjectStorage storage) => options.Delivery.Mode switch
    {
        DeliveryModes.CloudFrontSigned => new CloudFrontSignedUrlProvider(options),
        DeliveryModes.CloudFrontPublic => new CloudFrontPublicUrlProvider(options),
        DeliveryModes.S3Presigned => new S3PresignedUrlProvider(options, storage),
        _ => throw new InvalidOperationException(
            $"Unknown Nadlan:Storage:Delivery:Mode '{options.Delivery.Mode}'. Use S3Presigned, CloudFrontSigned or CloudFrontPublic."),
    };

    /// <summary>
    /// URL for a stored (relative) key on CloudFront: full S3 key minus the distribution's origin path, URL-encoded per segment.
    /// </summary>
    internal static string CloudFrontUrl(StorageOptions options, string relativeKey)
    {
        var d = options.Delivery;
        var domain = d.CloudFrontDomain.Trim().TrimEnd('/');
        if (domain.Length == 0)
        {
            throw new InvalidOperationException("Nadlan:Storage:Delivery:CloudFrontDomain is required for CloudFront delivery.");
        }

        var baseUrl = domain.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? domain : "https://" + domain;
        var key = options.FullKey(relativeKey);
        var originPath = d.CloudFrontOriginPath.Trim().Trim('/');
        if (originPath.Length > 0)
        {
            if (!key.StartsWith(originPath + "/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"CloudFrontOriginPath '/{originPath}' is not a prefix of the storage key '{key}'. It must match (part of) RootFolder.");
            }

            key = key[(originPath.Length + 1)..];
        }

        return $"{baseUrl}/{string.Join('/', key.Split('/').Select(Uri.EscapeDataString))}";
    }
}

/// <summary>Dev / no-CDN: short-lived S3 GET URLs straight from the private bucket.</summary>
public sealed class S3PresignedUrlProvider : IFileUrlProvider
{
    private readonly StorageOptions _options;
    private readonly S3ObjectStorage _storage;

    public S3PresignedUrlProvider(StorageOptions options, S3ObjectStorage storage)
    {
        _options = options;
        _storage = storage;
    }

    public string GetUrl(string storageKey) => _storage.GetDownloadUrl(storageKey, TimeSpan.FromMinutes(_options.Delivery.UrlMinutes));
}

/// <summary>
/// Production: the bucket stays private, CloudFront reads it through Origin Access Control, and every URL is signed
/// with a CloudFront key pair (canned policy, expires). Ready for per-file permissions later without changing callers.
/// </summary>
public sealed class CloudFrontSignedUrlProvider : IFileUrlProvider, IDisposable
{
    private readonly StorageOptions _options;
    private readonly CloudFrontUrlSigner _signer;

    public CloudFrontSignedUrlProvider(StorageOptions options)
    {
        _options = options;
        var d = options.Delivery;
        if (string.IsNullOrWhiteSpace(d.KeyPairId) || string.IsNullOrWhiteSpace(d.PrivateKeyPem))
        {
            throw new InvalidOperationException("CloudFrontSigned delivery needs Nadlan:Storage:Delivery:KeyPairId and PrivateKeyPem.");
        }

        _signer = new CloudFrontUrlSigner(d.PrivateKeyPem.Replace("\\n", "\n"), d.KeyPairId); // allow one-line PEM in env vars
        FileUrlProviderFactory.CloudFrontUrl(options, "startup-check"); // bad domain/origin path fails at startup, not per request
    }

    public string GetUrl(string storageKey)
        => _signer.Sign(FileUrlProviderFactory.CloudFrontUrl(_options, storageKey), DateTimeOffset.UtcNow.AddMinutes(_options.Delivery.UrlMinutes));

    public void Dispose() => _signer.Dispose();
}

/// <summary>Public CDN: plain CloudFront URLs. Only for content that may be public (no per-file permissions).</summary>
public sealed class CloudFrontPublicUrlProvider : IFileUrlProvider
{
    private readonly StorageOptions _options;

    public CloudFrontPublicUrlProvider(StorageOptions options)
    {
        _options = options;
        FileUrlProviderFactory.CloudFrontUrl(options, "startup-check"); // bad domain/origin path fails at startup, not per request
    }

    public string GetUrl(string storageKey) => FileUrlProviderFactory.CloudFrontUrl(_options, storageKey);
}
