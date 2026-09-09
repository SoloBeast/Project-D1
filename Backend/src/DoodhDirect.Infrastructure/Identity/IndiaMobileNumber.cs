using System.Linq;

namespace DoodhDirect.Infrastructure.Identity;

/// <summary>
/// Canonicalizes Indian mobile numbers to the shared E.164 identity format
/// <c>+91XXXXXXXXXX</c> used across registration, login, OTP and password-reset
/// flows. Mirrors <c>canonicalizeIndianMobile</c> in the Flutter app.
///
/// Legacy users were stored with a bare 10-digit national number; the
/// <see cref="ToNational"/> helper reconciles lookups so those accounts keep
/// working without a data migration.
/// </summary>
public static class IndiaMobileNumber
{
    /// <summary>
    /// E.164 canonical form for a valid Indian mobile number, e.g.
    /// <c>+919876543210</c>. Returns <c>null</c> when the value is empty or not
    /// a valid Indian mobile number (too short, non-<c>6..9</c> leading digit,
    /// or a non-Indian country code).
    /// </summary>
    public static string? Canonicalize(string? value)
    {
        var digits = DigitsOnly(value);
        if (digits is null) return null;

        return digits.Length switch
        {
            10 when IsValidLocal(digits) => "+91" + digits,
            11 when digits[0] == '0' && IsValidLocal(digits.Substring(1)) =>
                "+91" + digits.Substring(1),
            12 when digits.StartsWith("91", StringComparison.Ordinal)
                && IsValidLocal(digits.Substring(2)) =>
                "+91" + digits.Substring(2),
            14 when digits.StartsWith("0091", StringComparison.Ordinal)
                && IsValidLocal(digits.Substring(4)) =>
                "+91" + digits.Substring(4),
            _ => null,
        };
    }

    /// <summary>
    /// National 10-digit form (no country code), e.g. <c>9876543210</c>.
    /// Used to match legacy rows that were stored without the country code.
    /// Returns <c>null</c> when <paramref name="value"/> is not a valid mobile.
    /// </summary>
    public static string? ToNational(string? value)
    {
        var canonical = Canonicalize(value);
        return canonical is null ? null : canonical.Substring(3);
    }

    /// <summary>
    /// Digits-only form (e.g. <c>919876543210</c>) used to reconcile
    /// provider-attested identifiers, which omit the <c>+</c> and separators.
    /// </summary>
    public static string? ToDigits(string? value)
    {
        var digits = DigitsOnly(value);
        return digits;
    }

    private static string? DigitsOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = value.Where(c => c is >= '0' and <= '9').ToArray();
        return digits.Length == 0 ? null : new string(digits);
    }

    private static bool IsValidLocal(string digits) =>
        digits.Length == 10 && digits[0] is >= '6' and <= '9';
}
