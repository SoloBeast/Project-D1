using DoodhDirect.Domain.Payments;

namespace DoodhDirect.Application.Payments;

public sealed record CreatePaymentRequest(
    Guid OrderId,
    PaymentMethod Method);

public sealed record VerifyPaymentRequest(
    Guid PaymentId,
    string GatewayOrderId,
    string GatewayPaymentId,
    string Signature);

public sealed record RefundPaymentRequest(
    decimal? Amount,
    string Reason,
    string IdempotencyKey,
    string? IpAddress,
    string? UserAgent);

public sealed record PaymentResult(
    Guid PublicId,
    Guid? OrderId,
    string? OrderNumber,
    PaymentMethod Method,
    PaymentStatus Status,
    decimal Amount,
    decimal RefundedAmount,
    string Currency,
    string? GatewayOrderId,
    string? GatewayPaymentId,
    string? GatewayKeyId,
    string? FailureCode,
    string? FailureMessage,
    DateTime ExpiresAtUtc,
    DateTime? VerifiedAtUtc,
    DateTime CreatedAtUtc,
    Guid? SubscriptionId = null);

public sealed record RefundResult(
    Guid PublicId,
    Guid PaymentId,
    RefundStatus Status,
    decimal Amount,
    string Currency,
    string Reason,
    string? GatewayRefundId,
    string? FailureCode,
    string? FailureMessage,
    DateTime? CompletedAtUtc,
    DateTime CreatedAtUtc);

public sealed record GatewayOrderRequest(
    Guid PaymentId,
    string Receipt,
    long AmountMinor,
    string Currency,
    DateTime ExpiresAtUtc);

public sealed record GatewayOrderResult(
    string GatewayOrderId,
    string Status,
    long AmountMinor,
    string Currency);

public sealed record GatewayPaymentStatusResult(
    string GatewayPaymentId,
    string GatewayOrderId,
    string Status,
    long AmountMinor,
    string Currency,
    bool IsSuccessful,
    bool IsTerminalFailure);

public sealed record GatewayRefundResult(
    string GatewayRefundId,
    string Status,
    bool IsSuccessful,
    bool IsPending,
    string? FailureCode,
    string? FailureMessage);

public sealed record GatewayWebhookEvent(
    string EventId,
    string EventType,
    string? GatewayOrderId,
    string? GatewayPaymentId,
    string? GatewayRefundId,
    string Status,
    long? AmountMinor,
    string? Currency);

public interface IPaymentGateway
{
    string ProviderName { get; }
    string? PublicKeyId { get; }
    bool IsLive { get; }

    Task<GatewayOrderResult> CreateOrderAsync(
        GatewayOrderRequest request,
        CancellationToken cancellationToken);

    bool VerifyPaymentSignature(
        string gatewayOrderId,
        string gatewayPaymentId,
        string signature);

    Task<GatewayPaymentStatusResult> GetPaymentStatusAsync(
        string gatewayPaymentId,
        CancellationToken cancellationToken);

    bool VerifyWebhookSignature(
        ReadOnlySpan<byte> payload,
        string signature);

    GatewayWebhookEvent ParseWebhook(ReadOnlySpan<byte> payload);

    Task<GatewayRefundResult> RefundAsync(
        string gatewayPaymentId,
        long amountMinor,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public interface IPaymentService
{
    Task<PaymentResult> CreateAsync(
        long customerId,
        CreatePaymentRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<PaymentResult> CreateForSubscriptionAsync(
        long customerId,
        long subscriptionId,
        PaymentMethod method,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<PaymentResult> RetrySubscriptionAsync(
        long customerId,
        Guid subscriptionId,
        PaymentMethod method,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<PaymentResult> VerifyAsync(
        long customerId,
        VerifyPaymentRequest request,
        CancellationToken cancellationToken);

    Task<PaymentResult> GetAsync(
        long userId,
        Guid paymentId,
        bool bypassOwnership,
        CancellationToken cancellationToken);

    Task<RefundResult> RefundAsync(
        long requestedByUserId,
        Guid paymentId,
        RefundPaymentRequest request,
        CancellationToken cancellationToken);

    Task ProcessWebhookAsync(
        byte[] payload,
        string signature,
        CancellationToken cancellationToken);
}

public static class PaymentMappings
{
    public static PaymentResult ToResult(this Payment payment, string? gatewayKeyId = null) => new(
        payment.PublicId,
        payment.Order?.PublicId,
        payment.Order?.OrderNumber,
        payment.Method,
        payment.Status,
        payment.Amount,
        payment.RefundedAmount,
        payment.Currency,
        payment.GatewayOrderId,
        payment.GatewayPaymentId,
        gatewayKeyId,
        payment.FailureCode,
        payment.FailureMessage,
        payment.ExpiresAtUtc,
        payment.VerifiedAtUtc,
        payment.CreatedAtUtc,
        payment.Subscription?.PublicId);

    public static RefundResult ToResult(this Refund refund) => new(
        refund.PublicId,
        refund.Payment.PublicId,
        refund.Status,
        refund.Amount,
        refund.Currency,
        refund.Reason,
        refund.GatewayRefundId,
        refund.FailureCode,
        refund.FailureMessage,
        refund.CompletedAtUtc,
        refund.CreatedAtUtc);
}
