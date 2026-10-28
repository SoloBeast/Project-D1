using System.Globalization;

namespace DoodhDirect.Application.Common;

public sealed record ApiError(string Code, string? Field, string Message);

public sealed record ApiResponse<T>(
    bool Success,
    T? Data,
    string? Message,
    IReadOnlyCollection<ApiError> Errors)
{
    public static ApiResponse<T> Ok(T data, string? message = null) =>
        new(true, data, message, []);

    public static ApiResponse<T> Failure(string message, params ApiError[] errors) =>
        new(false, default, message, errors);
}

public abstract class AppException(
    string message,
    string code,
    int statusCode,
    string? field = null) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public string? Field { get; } = field;
}

public sealed class ValidationAppException(string message, string? field = null)
    : AppException(message, "VALIDATION_ERROR", 400, field);

public sealed class BusinessRuleException(string message)
    : AppException(message, "BUSINESS_RULE", 422);

public sealed class InsufficientWalletBalanceException(
    decimal availableBalance,
    decimal requiredAmount,
    decimal shortfall,
    string currency)
    : AppException(
        CreateMessage(shortfall, currency),
        "INSUFFICIENT_WALLET_BALANCE",
        422)
{
    public decimal AvailableBalance { get; } = availableBalance;
    public decimal RequiredAmount { get; } = requiredAmount;
    public decimal Shortfall { get; } = shortfall;
    public string Currency { get; } = currency;

    private static string CreateMessage(decimal shortfall, string currency)
    {
        var normalizedCurrency = currency.Trim().ToUpperInvariant();
        var amount = shortfall.ToString("0.##", CultureInfo.InvariantCulture);
        var formattedAmount = normalizedCurrency == "INR"
            ? $"₹{amount}"
            : $"{normalizedCurrency} {amount}";
        return $"Insufficient wallet balance. Please add {formattedAmount} to your wallet or choose another payment method.";
    }
}

/// <summary>
/// Wallet-first opt-in produced a positive Razorpay remainder below the
/// gateway's ₹1 minimum. Rejected BEFORE any wallet debit so the customer can
/// retry with wallet usage off (full Razorpay) or add wallet funds.
/// </summary>
public sealed class WalletRemainderBelowGatewayMinimumException(
    decimal walletApplied,
    decimal razorpayRemainder,
    decimal payableAmount,
    decimal minimumRazorpayAmount,
    string currency)
    : AppException(
        CreateMessage(razorpayRemainder, minimumRazorpayAmount, currency),
        "WALLET_SPLIT_REMAINDER_BELOW_MINIMUM",
        422)
{
    public decimal WalletApplied { get; } = walletApplied;
    public decimal RazorpayRemainder { get; } = razorpayRemainder;
    public decimal PayableAmount { get; } = payableAmount;
    public decimal MinimumRazorpayAmount { get; } = minimumRazorpayAmount;
    public string Currency { get; } = currency;

    private static string CreateMessage(
        decimal remainder,
        decimal minimum,
        string currency)
    {
        var normalizedCurrency = currency.Trim().ToUpperInvariant();
        string Format(decimal value)
        {
            var amount = value.ToString("0.00", CultureInfo.InvariantCulture);
            return normalizedCurrency == "INR" ? $"₹{amount}" : $"{normalizedCurrency} {amount}";
        }

        return $"Your wallet balance would leave {Format(remainder)} to pay by card, " +
            $"which is below the {Format(minimum)} minimum. Turn off wallet usage to pay the " +
            "full amount by card, or add enough wallet balance to cover the whole purchase.";
    }
}

/// <summary>
/// A subscription initial payment plan was rejected after the subscription
/// row was already persisted (e.g. the wallet balance changed concurrently
/// after pre-creation validation). Carries the persisted subscription ID so
/// the client can recover: the subscription stays PaymentPending with no
/// payment and can be retried without creating a duplicate.
/// </summary>
public sealed class SubscriptionInitialPaymentRejectedException(
    Guid subscriptionId,
    string code,
    string message)
    : AppException(message, code, 422)
{
    public Guid SubscriptionId { get; } = subscriptionId;
}

/// <summary>
/// A split payment plan still holds funds while its gateway leg is live: a
/// wallet component succeeded and the card leg is still initiated or pending.
/// A fresh attempt must not start (no second gateway order, no second wallet
/// debit) until the existing attempt is completed, reconciled, or safely
/// released after a definitive terminal outcome.
/// </summary>
public sealed class SplitPlanAttemptInProgressException(string targetName)
    : AppException(
        $"The {targetName} has a payment attempt in progress: wallet funds are held " +
        "while the card payment is still pending. Complete the existing card payment " +
        "or reconcile it before starting a new attempt.",
        "SPLIT_PLAN_ATTEMPT_IN_PROGRESS",
        409);

/// <summary>
/// A previous gateway leg is captured or its outcome cannot be proven
/// uncaptured: no fresh attempt may start and no funds may move until
/// reconciliation resolves the ambiguity.
/// </summary>
public sealed class SplitPlanOutcomeAmbiguousException()
    : AppException(
        "A previous card payment is captured or its outcome is still being reconciled. " +
        "Refresh the payment status before retrying: no new attempt was started and no funds moved.",
        "SPLIT_PLAN_OUTCOME_AMBIGUOUS",
        409);

public sealed class NotFoundException(string message)
    : AppException(message, "NOT_FOUND", 404);

public sealed class ConflictException(string message)
    : AppException(message, "CONFLICT", 409);

public sealed class CameraStreamUnavailableException(
    string message = "The live camera stream is currently unavailable.")
    : AppException(message, "CAMERA_STREAM_UNAVAILABLE", 503);

public sealed class OtpProviderUnavailableException(
    string message = "The OTP service is temporarily unavailable. Please try again in a few moments.")
    : AppException(message, "OTP_PROVIDER_UNAVAILABLE", 503);

public sealed class OtpProviderRejectedException(
    string message = "The OTP service rejected the request.")
    : AppException(message, "OTP_PROVIDER_REJECTED", 422);
