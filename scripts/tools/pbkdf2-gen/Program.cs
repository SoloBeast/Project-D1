using System.Security.Cryptography;

// DoodhDirect PBKDF2-SHA512 password hash helper.
//
// Generates a password hash in the exact format used by DoodhDirect's
// Pbkdf2PasswordHasher (Backend/src/DoodhDirect.Infrastructure/Identity/SecurityPrimitives.cs):
//   pbkdf2-sha512-v1$<iterations>$<saltB64>$<keyB64>
// Salt size 16, key size 32, SHA-512, iterations default 120000 (must match
// appsettings.json -> Authentication:Identity:PasswordIterations).
//
// Usage:
//   pbkdf2-gen [password] [iterations]                     -> generate new hash (random salt)
//   pbkdf2-gen verify <password> <storedHash>              -> prints True/False

var mode = args.Length > 0 ? args[0] : "generate";
if (mode.Equals("verify", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: pbkdf2-gen verify <password> <storedHash>");
        return 2;
    }

    var verifyPassword = args[1];
    var storedHash = args[2];
    var storedParts = storedHash.Split('$');
    if (storedParts.Length != 4 ||
        !storedParts[0].Equals("pbkdf2-sha512-v1", StringComparison.Ordinal) ||
        !int.TryParse(storedParts[1], out var storedIterations))
    {
        Console.WriteLine("False");
        return 0;
    }

    try
    {
        var storedSalt = Convert.FromBase64String(storedParts[2]);
        var storedKey = Convert.FromBase64String(storedParts[3]);
        var derivedKey = Rfc2898DeriveBytes.Pbkdf2(verifyPassword, storedSalt, storedIterations, HashAlgorithmName.SHA512, storedKey.Length);
        Console.WriteLine(CryptographicOperations.FixedTimeEquals(derivedKey, storedKey) ? "True" : "False");
    }
    catch (FormatException)
    {
        Console.WriteLine("False");
    }

    return 0;
}

var genPassword = args.Length > 0 ? args[0] : "DoodhDirect@123";
var genIterations = args.Length > 1 && int.TryParse(args[1], out var parsedIterations) ? parsedIterations : 120000;
const int SaltSize = 16;
const int KeySize = 32;
const int FormatVersion = 1;

var genSalt = RandomNumberGenerator.GetBytes(SaltSize);
var genKey = Rfc2898DeriveBytes.Pbkdf2(genPassword, genSalt, genIterations, HashAlgorithmName.SHA512, KeySize);
var genHash = $"pbkdf2-sha512-v{FormatVersion}${genIterations}${Convert.ToBase64String(genSalt)}${Convert.ToBase64String(genKey)}";
Console.WriteLine(genHash);
return 0;
