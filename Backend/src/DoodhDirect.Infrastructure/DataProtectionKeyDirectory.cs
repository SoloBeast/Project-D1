namespace DoodhDirect.Infrastructure;

/// <summary>
/// Resolves the ASP.NET Core Data Protection key directory.
/// Explicit configuration always wins. Development keeps the existing
/// LocalAppData fallback so local restarts survive with zero setup.
/// Non-development runs with no explicit value fall back to the
/// machine-wide persistent directory outside the repository, so FCM
/// push tokens and bank details remain decryptable across API restarts
/// and across instances sharing the same volume.
/// </summary>
public static class DataProtectionKeyDirectory
{
    /// <summary>
    /// Machine-wide persistent key location for non-development hosts.
    /// Outside the repository by design; deployment-managed with OS ACLs.
    /// </summary>
    public const string ProductionDefault = @"C:\ProgramData\DoodhDirect\DataProtection-Keys";

    public static string? Resolve(string? configuredValue, bool isDevelopment)
    {
        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            return configuredValue;
        }

        if (isDevelopment)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DoodhDirect",
                "DataProtection-Keys");
        }

        return ProductionDefault;
    }
}
