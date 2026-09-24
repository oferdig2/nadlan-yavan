using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;
using Nadlan.Core.Files;

namespace Nadlan.Storage.S3;

/// <summary>
/// S3 implementation of <see cref="IObjectStorage"/>. Bytes go browser → S3 directly; this only coordinates.
/// Callers pass keys relative to RootFolder; the folder is added here, in one place.
/// </summary>
public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly StorageOptions _options;
    private readonly Lazy<IAmazonS3> _client;

    public S3ObjectStorage(StorageOptions options)
    {
        _options = options;
        // PublicationOnly: a failed creation (e.g. missing profile) is retried on the next call, not cached forever.
        _client = new Lazy<IAmazonS3>(() => CreateClient(options), LazyThreadSafetyMode.PublicationOnly);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Bucket);

    internal IAmazonS3 Client => _client.Value;

    public Task<string> StartMultipartUploadAsync(string key, string contentType, string contentDisposition, CancellationToken ct = default)
        => Guard(async () =>
        {
            var request = new InitiateMultipartUploadRequest { BucketName = _options.Bucket, Key = _options.FullKey(key), ContentType = contentType };
            request.Headers.ContentDisposition = contentDisposition;
            return (await Client.InitiateMultipartUploadAsync(request, ct)).UploadId;
        });

    /// <summary>Short-lived GET URL (dev delivery mode). Same error handling as every other S3 call.</summary>
    public string GetDownloadUrl(string key, TimeSpan lifetime)
        => Guard(() => Client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = _options.FullKey(key),
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = Protocol.HTTPS,
        }));

    public string GetPartUploadUrl(string key, string uploadId, int partNumber, TimeSpan lifetime)
        => Guard(() => Client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = _options.FullKey(key),
            Verb = HttpVerb.PUT,
            UploadId = uploadId,
            PartNumber = partNumber,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = Protocol.HTTPS,
        }));

    public Task<IReadOnlyList<UploadedPart>> ListUploadedPartsAsync(string key, string uploadId, CancellationToken ct = default)
        => Guard<IReadOnlyList<UploadedPart>>(async () =>
        {
            var parts = new List<UploadedPart>();
            string? marker = null;
            while (true)
            {
                var response = await Client.ListPartsAsync(new ListPartsRequest
                {
                    BucketName = _options.Bucket, Key = _options.FullKey(key), UploadId = uploadId, PartNumberMarker = marker,
                }, ct);
                parts.AddRange((response.Parts ?? new List<PartDetail>()).Select(p => new UploadedPart(p.PartNumber ?? 0, p.ETag)));
                if (response.IsTruncated != true)
                {
                    return parts;
                }

                marker = response.NextPartNumberMarker?.ToString();
            }
        });

    public Task CompleteMultipartUploadAsync(string key, string uploadId, IReadOnlyList<UploadedPart> parts, CancellationToken ct = default)
        => Guard(async () => await Client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = _options.Bucket,
            Key = _options.FullKey(key),
            UploadId = uploadId,
            PartETags = parts.Select(p => new PartETag(p.PartNumber, p.ETag)).ToList(),
        }, ct));

    public Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct = default)
        => Guard(async () =>
        {
            try
            {
                await Client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = _options.Bucket, Key = _options.FullKey(key), UploadId = uploadId }, ct);
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchUpload")
            {
                // Already aborted/completed, or expired by the bucket lifecycle rule - nothing left to clean up.
            }

            return true;
        });

    public Task<long?> GetObjectSizeAsync(string key, CancellationToken ct = default)
        => Guard(async () =>
        {
            try
            {
                var meta = await Client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _options.Bucket, Key = _options.FullKey(key) }, ct);
                return meta.ContentLength;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (long?)null;
            }
        });

    public Task DeleteObjectAsync(string key, CancellationToken ct = default)
        => Guard(async () => await Client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _options.Bucket, Key = _options.FullKey(key) }, ct));

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    // AWS failures (no credentials, access denied, wrong bucket/region) become one readable storage error.
    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (AmazonServiceException ex)
        {
            throw new StorageUnavailableException($"S3 refused the request ({ex.ErrorCode ?? ex.StatusCode.ToString()}): {ex.Message}", ex);
        }
        catch (AmazonClientException ex)
        {
            throw new StorageUnavailableException($"S3 client error (credentials/network): {ex.Message}", ex);
        }
    }

    private static Task Guard(Func<Task> action) => Guard(async () => { await action(); return true; });

    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (AmazonClientException ex)
        {
            throw new StorageUnavailableException($"S3 client error (credentials): {ex.Message}", ex);
        }
    }

    private static IAmazonS3 CreateClient(StorageOptions options)
    {
        var region = RegionEndpoint.GetBySystemName(options.Region);
        if (string.IsNullOrWhiteSpace(options.AwsProfile))
        {
            return new AmazonS3Client(region); // default chain: AWS_PROFILE, env keys, EC2/ECS role
        }

        if (!new CredentialProfileStoreChain().TryGetAWSCredentials(options.AwsProfile, out AWSCredentials credentials))
        {
            throw new StorageUnavailableException(
                $"AWS profile '{options.AwsProfile}' was not found. Create it with: aws configure --profile {options.AwsProfile}");
        }

        return new AmazonS3Client(credentials, region);
    }
}
