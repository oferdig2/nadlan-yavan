using Amazon.S3;
using Amazon.S3.Model;
using Nadlan.Core.Files;

namespace Nadlan.Storage.S3;

public static class FileUrlProviderFactory
{
    public static IFileUrlProvider Create(StorageOptions options, S3ObjectStorage storage) => options.Delivery.Mode switch
    {
        DeliveryModes.CloudFrontSigned => new CloudFrontSignedUrlProvider(options.Delivery),
        DeliveryModes.CloudFrontPublic => new CloudFrontPublicUrlProvider(options.Delivery),
        DeliveryModes.S3Presigned => new S3PresignedUrlProvider(options, storage),
        _ => throw new InvalidOperationException(
            $"Unknown Nadlan:Storage:Delivery:Mode '{options.Delivery.Mode}'. Use S3Presigned, CloudFrontSigned or CloudFrontPublic."),
    };

    internal static string ObjectPath(string storageKey)
        => string.Join('/', storageKey.Split('/').Select(Uri.EscapeDataString));

    internal static string RequireDomain(StorageOptions.DeliveryOptions d)
    {
        var domain = d.CloudFrontDomain.Trim().TrimEnd('/');
        if (domain.Length == 0)
        {
            throw new InvalidOperationException("Nadlan:Storage:Delivery:CloudFrontDomain is required for CloudFront delivery.");
        }

        return domain.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? domain : "https://" + domain;
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

    public string GetUrl(string storageKey)
        => _storage.Client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = storageKey,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(_options.Delivery.UrlMinutes),
            Protocol = Protocol.HTTPS,
        });
}

/// <summary>
/// Production: the bucket stays private, CloudFront reads it through Origin Access Control, and every URL is signed
/// with a CloudFront key pair (canned policy, expires). Ready for per-file permissions later without changing callers.
/// </summary>
public sealed class CloudFrontSignedUrlProvider : IFileUrlProvider, IDisposable
{
    private readonly StorageOptions.DeliveryOptions _options;
    private readonly string _baseUrl;
    private readonly CloudFrontUrlSigner _signer;

    public CloudFrontSignedUrlProvider(StorageOptions.DeliveryOptions options)
    {
        _options = options;
        _baseUrl = FileUrlProviderFactory.RequireDomain(options);
        if (string.IsNullOrWhiteSpace(options.KeyPairId) || string.IsNullOrWhiteSpace(options.PrivateKeyPem))
        {
            throw new InvalidOperationException("CloudFrontSigned delivery needs Nadlan:Storage:Delivery:KeyPairId and PrivateKeyPem.");
        }

        _signer = new CloudFrontUrlSigner(options.PrivateKeyPem.Replace("\\n", "\n"), options.KeyPairId); // allow one-line PEM in env vars
    }

    public string GetUrl(string storageKey)
        => _signer.Sign($"{_baseUrl}/{FileUrlProviderFactory.ObjectPath(storageKey)}", DateTimeOffset.UtcNow.AddMinutes(_options.UrlMinutes));

    public void Dispose() => _signer.Dispose();
}

/// <summary>Public CDN: plain CloudFront URLs. Only for content that may be public (no per-file permissions).</summary>
public sealed class CloudFrontPublicUrlProvider : IFileUrlProvider
{
    private readonly string _baseUrl;

    public CloudFrontPublicUrlProvider(StorageOptions.DeliveryOptions options)
    {
        _baseUrl = FileUrlProviderFactory.RequireDomain(options);
    }

    public string GetUrl(string storageKey) => $"{_baseUrl}/{FileUrlProviderFactory.ObjectPath(storageKey)}";
}
