using System.Security.Cryptography;
using System.Text;
using DoodhDirect.Application.Identity;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Identity;

public sealed class Pbkdf2PasswordHasher(IOptions<IdentityOptions> identityOptions) : IPasswordHasher
{
    private readonly IdentityOptions _options = identityOptions.Value;
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int FormatVersion = 1;

    public string Hash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(value, salt, _options.PasswordIterations, HashAlgorithmName.SHA512, KeySize);
        return $"pbkdf2-sha512-v{FormatVersion}${_options.PasswordIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public bool Verify(string hash, string value)
    {
        if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrEmpty(value)) return false;
        var parts = hash.Split('$');
        if (parts.Length != 4 || parts[0] != $"pbkdf2-sha512-v{FormatVersion}" || !int.TryParse(parts[1], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(value, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class SecureTokenGenerator
{
    public string Create(int byteCount = 32) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount));

    public string Hash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
