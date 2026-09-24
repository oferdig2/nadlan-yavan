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

    /// <summary>First key segment ("dev", "prod") so environments can share a bucket without mixing files.</summary>
    public string KeyPrefix { get; set; } = "dev";

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
