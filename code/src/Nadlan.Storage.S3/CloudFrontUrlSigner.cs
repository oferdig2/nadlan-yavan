using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nadlan.Storage.S3;

/// <summary>
/// CloudFront signed URLs with a canned policy (AWS docs: "Create a signed URL using a canned policy").
/// AWS SDK v4 dropped its signer, and the algorithm is small: RSA-SHA1 over a fixed JSON policy, CloudFront-safe base64.
/// </summary>
public sealed class CloudFrontUrlSigner : IDisposable
{
    private readonly RSA _rsa;
    private readonly string _keyPairId;

    public CloudFrontUrlSigner(string privateKeyPem, string keyPairId)
    {
        _rsa = RSA.Create();
        _rsa.ImportFromPem(privateKeyPem);
        _keyPairId = keyPairId;
    }

    public string Sign(string url, DateTimeOffset expiresUtc)
    {
        var expires = expiresUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        // Exact format matters: CloudFront re-creates this string and checks the signature against it (no spaces).
        var policy = $"{{\"Statement\":[{{\"Resource\":\"{url}\",\"Condition\":{{\"DateLessThan\":{{\"AWS:EpochTime\":{expires}}}}}}}]}}";
        var signature = _rsa.SignData(Encoding.UTF8.GetBytes(policy), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);

        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}Expires={expires}&Signature={UrlSafe(signature)}&Key-Pair-Id={_keyPairId}";
    }

    /// <summary>Base64 with the three characters CloudFront can't take in a query string replaced: + → -, = → _, / → ~.</summary>
    internal static string UrlSafe(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('=', '_').Replace('/', '~');

    public void Dispose() => _rsa.Dispose();
}
