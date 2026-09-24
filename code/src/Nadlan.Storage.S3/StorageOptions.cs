namespace Nadlan.Storage.S3;

/// <summary>
/// "Nadlan:Storage" config section (app_config, ms:host). No AWS secrets here: credentials come from the standard
/// AWS chain - an AWS profile on a dev machine, the instance/task IAM role on EC2/Fargate.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Nadlan:Storage";

    /// <summary>Empty = file features disabled (the rest of the app still runs).</summary>
    public string Bucket { get; set; } = "";

    public string Region { get; set; } = "eu-central-1";

    /// <summary>Optional AWS profile name (~/.aws/credentials). Empty = default chain (AWS_PROFILE env var, role, ...).</summary>
    public string AwsProfile { get; set; } = "";

    /// <summary>
    /// Folder inside the bucket that holds everything, e.g. "nadlan/dev" in a shared bucket, or "" for a dedicated one.
    /// The DB stores keys RELATIVE to this folder, so moving to another bucket/folder = copy the objects
    /// (aws s3 sync s3://old/root s3://new/root) + change Bucket/RootFolder. No DB changes.
    /// </summary>
    public string RootFolder { get; set; } = "nadlan/dev";

    /// <summary>Full S3 key for a stored (relative) key.</summary>
    public string FullKey(string relativeKey)
    {
        var root = RootFolder.Trim().Trim('/');
        return root.Length == 0 ? relativeKey : $"{root}/{relativeKey}";
    }

    public int PartSizeMb { get; set; } = 8;
    public int MaxFileSizeGb { get; set; } = 20;
    public int UploadUrlMinutes { get; set; } = 60;

    public DeliveryOptions Delivery { get; set; } = new();

    public sealed class DeliveryOptions
    {
        /// <summary>S3Presigned (dev, no CDN) | CloudFrontSigned (private bucket behind CloudFront) | CloudFrontPublic.</summary>
        public string Mode { get; set; } = DeliveryModes.S3Presigned;

        /// <summary>CloudFront domain, e.g. d1234abcd.cloudfront.net or files.example.com.</summary>
        public string CloudFrontDomain { get; set; } = "";

        /// <summary>
        /// The distribution's "Origin path", if it has one (e.g. "/nadlan/prod"). That part of the S3 key is then
        /// not repeated in URLs. Empty = the distribution points at the bucket root and URLs carry the full key.
        /// </summary>
        public string CloudFrontOriginPath { get; set; } = "";

        /// <summary>CloudFront public key id (from the key group) used to sign URLs.</summary>
        public string KeyPairId { get; set; } = "";

        /// <summary>PEM private key matching KeyPairId. A secret - prefer the env var Nadlan__Storage__Delivery__PrivateKeyPem.</summary>
        public string PrivateKeyPem { get; set; } = "";

        public int UrlMinutes { get; set; } = 60;
    }
}

public static class DeliveryModes
{
    public const string S3Presigned = "S3Presigned";
    public const string CloudFrontSigned = "CloudFrontSigned";
    public const string CloudFrontPublic = "CloudFrontPublic";
}
