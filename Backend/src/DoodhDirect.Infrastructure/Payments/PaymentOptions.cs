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

        if (!IsRazorpay)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(RazorpayKeyId))
        {
            yield return RequiredForRazorpay(nameof(RazorpayKeyId));
        }

        if (string.IsNullOrWhiteSpace(RazorpayKeySecret))
        {
            yield return RequiredForRazorpay(nameof(RazorpayKeySecret));
        }

        if (string.IsNullOrWhiteSpace(RazorpayWebhookSecret))
        {
            yield return RequiredForRazorpay(nameof(RazorpayWebhookSecret));
        }
    }

    private static ValidationResult RequiredForRazorpay(string memberName) =>
        new($"Payments:{memberName} is required when Payments:Provider is Razorpay.", [memberName]);
}
