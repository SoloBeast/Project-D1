using Microsoft.AspNetCore.DataProtection;

namespace DoodhDirect.Infrastructure.OtpProvider;

/// <summary>
/// Protects the MSG91 Auth Key before it is persisted in SystemConfiguration
/// (encrypted at rest). Purpose string follows the DeliveryOtpHandoffProtector
/// pattern and can be rotated independently.
/// </summary>
public sealed class OtpProviderSecretProtector(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(
        "DoodhDirect.Identity.OtpProviderSecret.v1");

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
