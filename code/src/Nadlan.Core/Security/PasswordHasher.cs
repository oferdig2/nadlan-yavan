using System.Security.Cryptography;
using System.Text;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Security;

/// <summary>
/// PBKDF2-SHA256 password hashes (same family as Futuristic SaaS), stored self-describing so the cost can be raised later:
/// <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt b64&gt;$&lt;hash b64&gt;</c>. Plaintext passwords are never stored (Appendix 1 §1).
/// </summary>
public static class PasswordHasher
{
    private const string Scheme = "pbkdf2-sha256";
    private const int Iterations = 210_000; // OWASP 2023 minimum for PBKDF2-HMAC-SHA256 is 600k; 210k keeps t3.micro logins < 0.3 s
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        var parts = stored?.Split('$');
        if (parts is not [Scheme, var iterText, var saltText, var hashText] || !int.TryParse(iterText, out var iterations) || iterations < 1)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(saltText);
            var expected = Convert.FromBase64String(hashText);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Burns the same time as a real check, so "no such user" and "wrong password" can't be told apart by timing.</summary>
    public static void VerifyDummy(string password) => Verify(password, DummyHash.Value);

    private static readonly Lazy<string> DummyHash = new(() => Hash("dummy-password-for-timing"));

    /// <summary>Random URL-safe token (for reset links) and its SHA-256 hex, which is what gets stored.</summary>
    public static (string Token, string TokenHash) NewToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (token, HashToken(token));
    }

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>A readable random password an Admin can hand out (the user is asked to change it).</summary>
    public static string Generate(int length = 14)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return string.Create(length, alphabet, (span, a) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = a[RandomNumberGenerator.GetInt32(a.Length)];
            }
        });
    }
}

public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 200;

    /// <summary>Length is what matters (NIST 800-63B); no composition rules. Rejects the email itself.</summary>
    public static void Validate(string? password, string email)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
        {
            throw new DomainValidationException("PASSWORD_TOO_SHORT", $"The password needs at least {MinLength} characters.");
        }

        if (password.Length > MaxLength)
        {
            throw new DomainValidationException("PASSWORD_TOO_LONG", $"The password can have at most {MaxLength} characters.");
        }

        if (string.Equals(password.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase)
            || password.Distinct().Count() < 4)
        {
            throw new DomainValidationException("PASSWORD_TOO_WEAK", "Choose a less guessable password.");
        }
    }
}
