using System.ComponentModel.DataAnnotations;

namespace DoodhDirect.Infrastructure.Payments;

public sealed class PaymentOptions : IValidatableObject
{
    public const string SectionName = "Payments";

    [Required]
    public string Provider { get; init; } = "Mock";

    [Required]
    public string Currency { get; init; } = "INR";

    [Range(1, 1440)]
    public int PaymentExpiryMinutes { get; init; } = 15;

    public string? RazorpayKeyId { get; init; }
    public string? RazorpayKeySecret { get; init; }
    public string? RazorpayWebhookSecret { get; init; }

    [Required]
    public string MockSigningSecret { get; init; } = "development-mock-payment-secret";

    public bool IsRazorpay =>
        string.Equals(Provider, "Razorpay", StringComparison.OrdinalIgnoreCase);

    public bool IsMock =>
        string.Equals(Provider, "Mock", StringComparison.OrdinalIgnoreCase);

    public bool IsRazorpayConfigured =>
        !string.IsNullOrWhiteSpace(RazorpayKeyId) &&
        !string.IsNullOrWhiteSpace(RazorpayKeySecret);

    public bool IsValidForEnvironment(bool isDevelopment) =>
        isDevelopment
            ? IsMock || IsRazorpay && IsRazorpayConfigured
            : IsRazorpay && IsRazorpayConfigured;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IsRazorpay && !string.Equals(Provider, "Mock", StringComparison.OrdinalIgnoreCase))
        {
            yield return new ValidationResult(
                "Payments:Provider must be either 'Razorpay' or 'Mock'.",
                [nameof(Provider)]);
        }

        if (Currency.Length != 3)
        {
            yield return new ValidationResult(
                "Payments:Currency must be a three-letter ISO currency code.",
                [nameof(Currency)]);
        }

        // Missing Razorpay credentials produce an unavailable capability. Requests still
        // fail closed, without substituting the Development Mock provider.
    }
}
