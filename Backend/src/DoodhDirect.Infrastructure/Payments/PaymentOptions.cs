using System.ComponentModel.DataAnnotations;

namespace DoodhDirect.Infrastructure.Payments;

public sealed class PaymentOptions : IValidatableObject
{
    public const string SectionName = "Payments";

    [Required]
    public string Provider { get; init; } = "Razorpay";

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

    public bool IsValid =>
        IsRazorpay && IsRazorpayConfigured;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IsRazorpay)
        {
            yield return new ValidationResult(
                "Payments:Provider must be 'Razorpay'. The Mock provider is only valid for automated test construction and is never accepted in runtime configuration.",
                [nameof(Provider)]);
        }

        // Razorpay credentials are intentionally NOT validated here. They may be supplied
        // at runtime through the Integration settings store (Integration.Razorpay.*) rather
        // than appsettings, so the application must boot even when static options are blank.

        if (Currency.Length != 3)
        {
            yield return new ValidationResult(
                "Payments:Currency must be a three-letter ISO currency code.",
                [nameof(Currency)]);
        }
    }
}
