using Microsoft.AspNetCore.DataProtection;

namespace DoodhDirect.Infrastructure.Integrations;

/// <summary>
/// Protects sensitive integration values (SMTP password, Razorpay secrets, Google
/// Maps server key) before they are persisted in SystemConfiguration (encrypted at
/// rest). Purpose string follows the OtpProviderSecretProtector pattern and can be
/// rotated independently.
/// </summary>
public sealed class IntegrationSecretProtector(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(
        "DoodhDirect.IntegrationSecret.v1");

    public string Protect(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return _protector.Protect(value.Trim());
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        return _protector.Unprotect(protectedValue);
    }
}
