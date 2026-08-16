using DoodhDirect.Domain.Common;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Subscriptions;

namespace DoodhDirect.Domain.Payments;

public enum PaymentMethod
{
    Razorpay,
    Wallet
}

public enum PaymentStatus
{
    Initiated,
    Pending,
    Success,
    Failed,
    Expired,
    RefundPending,
    PartiallyRefunded,
    Refunded
}

public enum RefundStatus
{
    Pending,
    Processing,
    Succeeded,
    Failed
}

public enum PaymentWebhookStatus
{
    Received,
    Processing,
    Processed,
    Rejected,
    Failed
}

public sealed class Payment : AuditableEntity
{
    private Payment() { }

    public Payment(
        long orderId,
        long customerId,
        PaymentMethod method,
        decimal amount,
        string currency,
        string idempotencyKey,
        DateTime expiresAtUtc)
        : this(orderId, null, customerId, method, amount, currency, idempotencyKey, expiresAtUtc)
    {
    }

    private Payment(
        long? orderId,
        long? subscriptionId,
        long customerId,
        PaymentMethod method,
        decimal amount,
        string currency,
        string idempotencyKey,
        DateTime expiresAtUtc)
    {
        if ((orderId is > 0) == (subscriptionId is > 0))
        {
            throw new ArgumentException("A payment must reference exactly one order or subscription.");
        }
        if (customerId <= 0) throw new ArgumentOutOfRangeException(nameof(customerId));
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        EnsureUtc(expiresAtUtc, nameof(expiresAtUtc));

        OrderId = orderId;
        SubscriptionId = subscriptionId;
        CustomerId = customerId;
        Method = method;
        Amount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        Currency = Required(currency, nameof(currency)).ToUpperInvariant();
        IdempotencyKey = Required(idempotencyKey, nameof(idempotencyKey));
        Status = PaymentStatus.Initiated;
        ExpiresAtUtc = expiresAtUtc;
    }

    public static Payment CreateForSubscription(
        long subscriptionId,
        long customerId,
        PaymentMethod method,
        decimal amount,
        string currency,
        string idempotencyKey,
        DateTime expiresAtUtc) =>
        new(null, subscriptionId, customerId, method, amount, currency, idempotencyKey, expiresAtUtc);

    public long? OrderId { get; private set; }
    public long? SubscriptionId { get; private set; }
    public long CustomerId { get; private set; }
    public PaymentMethod Method { get; private set; }
    public PaymentStatus Status { get; private set; }
    public decimal Amount { get; private set; }
    public decimal RefundedAmount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string? GatewayOrderId { get; private set; }
    public string? GatewayPaymentId { get; private set; }
    public string? GatewayStatus { get; private set; }
    public string? FailureCode { get; private set; }
    public string? FailureMessage { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? VerifiedAtUtc { get; private set; }
    public DateTime? FailedAtUtc { get; private set; }

    public Order? Order { get; private set; }
    public Subscription? Subscription { get; private set; }
    public User Customer { get; private set; } = null!;
    public ICollection<Refund> Refunds { get; private set; } = [];

    public void AttachGatewayOrder(string gatewayOrderId, string gatewayStatus)
    {
        if (Method != PaymentMethod.Razorpay)
        {
            throw new InvalidOperationException("Gateway references can only be attached to a Razorpay payment.");
        }

        if (Status != PaymentStatus.Initiated)
        {
            throw new InvalidOperationException($"A payment in status '{Status}' cannot be submitted to a gateway.");
        }

        GatewayOrderId = Required(gatewayOrderId, nameof(gatewayOrderId));
        GatewayStatus = Required(gatewayStatus, nameof(gatewayStatus));
        Status = PaymentStatus.Pending;
    }

    public void MarkWalletPending()
    {
        if (Method != PaymentMethod.Wallet || Status != PaymentStatus.Initiated)
        {
            throw new InvalidOperationException("Only an initiated wallet payment can become pending.");
        }

        Status = PaymentStatus.Pending;
    }

    public void Succeed(
        string? gatewayPaymentId,
        string gatewayStatus,
        DateTime verifiedAtUtc)
    {
        EnsureUtc(verifiedAtUtc, nameof(verifiedAtUtc));

        if (Status == PaymentStatus.Success)
        {
            if (!string.Equals(GatewayPaymentId, Optional(gatewayPaymentId), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A different gateway payment reference is already verified.");
            }

            return;
        }

        if (Status is not (PaymentStatus.Initiated or PaymentStatus.Pending))
        {
            throw new InvalidOperationException($"A payment in status '{Status}' cannot succeed.");
        }

        if (Method == PaymentMethod.Razorpay && string.IsNullOrWhiteSpace(gatewayPaymentId))
        {
            throw new ArgumentException("A gateway payment reference is required.", nameof(gatewayPaymentId));
        }

        GatewayPaymentId = Optional(gatewayPaymentId);
        GatewayStatus = Required(gatewayStatus, nameof(gatewayStatus));
        Status = PaymentStatus.Success;
        VerifiedAtUtc = verifiedAtUtc;
        FailureCode = null;
        FailureMessage = null;
    }

    public void Fail(
        string failureCode,
        string? failureMessage,
        string? gatewayStatus,
        DateTime failedAtUtc)
    {
        EnsureUtc(failedAtUtc, nameof(failedAtUtc));

        if (Status == PaymentStatus.Failed)
        {
            return;
        }

        if (Status is not (PaymentStatus.Initiated or PaymentStatus.Pending))
        {
            throw new InvalidOperationException($"A payment in status '{Status}' cannot fail.");
        }

        Status = PaymentStatus.Failed;
        FailureCode = Required(failureCode, nameof(failureCode));
        FailureMessage = Optional(failureMessage);
        GatewayStatus = Optional(gatewayStatus);
        FailedAtUtc = failedAtUtc;
    }

    public void Expire(DateTime expiredAtUtc)
    {
        EnsureUtc(expiredAtUtc, nameof(expiredAtUtc));

        if (Status == PaymentStatus.Expired)
        {
            return;
        }

        if (Status is not (PaymentStatus.Initiated or PaymentStatus.Pending))
        {
            throw new InvalidOperationException($"A payment in status '{Status}' cannot expire.");
        }

        Status = PaymentStatus.Expired;
        FailureCode = "PAYMENT_EXPIRED";
        FailedAtUtc = expiredAtUtc;
    }

    public Refund StartRefund(
        decimal amount,
        string reason,
        string idempotencyKey,
        long requestedByUserId)
    {
        if (Status is not (PaymentStatus.Success or PaymentStatus.PartiallyRefunded))
        {
            throw new InvalidOperationException($"A payment in status '{Status}' cannot be refunded.");
        }

        var roundedAmount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (roundedAmount <= 0 || RefundedAmount + roundedAmount > Amount)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        Status = PaymentStatus.RefundPending;
        var refund = new Refund(Id, roundedAmount, Currency, reason, idempotencyKey, requestedByUserId);
        Refunds.Add(refund);
        return refund;
    }

    public void CompleteRefund(decimal amount)
    {
        if (Status != PaymentStatus.RefundPending)
        {
            throw new InvalidOperationException($"A payment in status '{Status}' cannot complete a refund.");
        }

        var roundedAmount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (roundedAmount <= 0 || RefundedAmount + roundedAmount > Amount)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        RefundedAmount += roundedAmount;
        Status = RefundedAmount == Amount
            ? PaymentStatus.Refunded
            : PaymentStatus.PartiallyRefunded;
    }

    public void FailRefund()
    {
        if (Status != PaymentStatus.RefundPending)
        {
            throw new InvalidOperationException($"A payment in status '{Status}' does not have a pending refund.");
        }

        Status = RefundedAmount == 0
            ? PaymentStatus.Success
            : PaymentStatus.PartiallyRefunded;
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();

    private static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void EnsureUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }
}

public sealed class Refund : AuditableEntity
{
    private Refund() { }

    internal Refund(
        long paymentId,
        decimal amount,
        string currency,
        string reason,
        string idempotencyKey,
        long requestedByUserId)
    {
        if (paymentId < 0) throw new ArgumentOutOfRangeException(nameof(paymentId));
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (requestedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(requestedByUserId));

        PaymentId = paymentId;
        Amount = amount;
        Currency = Required(currency, nameof(currency)).ToUpperInvariant();
        Reason = Required(reason, nameof(reason));
        IdempotencyKey = Required(idempotencyKey, nameof(idempotencyKey));
        RequestedByUserId = requestedByUserId;
        Status = RefundStatus.Pending;
    }

    public long PaymentId { get; private set; }
    public long RequestedByUserId { get; private set; }
    public RefundStatus Status { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string? GatewayRefundId { get; private set; }
    public string? FailureCode { get; private set; }
    public string? FailureMessage { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    public Payment Payment { get; private set; } = null!;
    public User RequestedByUser { get; private set; } = null!;

    public void MarkProcessing(string gatewayRefundId)
    {
        if (Status != RefundStatus.Pending)
        {
            throw new InvalidOperationException($"A refund in status '{Status}' cannot start processing.");
        }

        GatewayRefundId = Required(gatewayRefundId, nameof(gatewayRefundId));
        Status = RefundStatus.Processing;
    }

    public void Succeed(DateTime completedAtUtc)
    {
        EnsureUtc(completedAtUtc, nameof(completedAtUtc));

        if (Status == RefundStatus.Succeeded)
        {
            return;
        }

        if (Status is not (RefundStatus.Pending or RefundStatus.Processing))
        {
            throw new InvalidOperationException($"A refund in status '{Status}' cannot succeed.");
        }

        Status = RefundStatus.Succeeded;
        CompletedAtUtc = completedAtUtc;
        FailureCode = null;
        FailureMessage = null;
    }

    public void Fail(string failureCode, string? failureMessage, DateTime completedAtUtc)
    {
        EnsureUtc(completedAtUtc, nameof(completedAtUtc));

        if (Status == RefundStatus.Failed)
        {
            return;
        }

        if (Status is not (RefundStatus.Pending or RefundStatus.Processing))
        {
            throw new InvalidOperationException($"A refund in status '{Status}' cannot fail.");
        }

        Status = RefundStatus.Failed;
        FailureCode = Required(failureCode, nameof(failureCode));
        FailureMessage = string.IsNullOrWhiteSpace(failureMessage) ? null : failureMessage.Trim();
        CompletedAtUtc = completedAtUtc;
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();

    private static void EnsureUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }
}

public sealed class PaymentWebhook : AuditableEntity
{
    private PaymentWebhook() { }

    public PaymentWebhook(
        string provider,
        string eventId,
        string eventType,
        string payloadHash,
        DateTime receivedAtUtc)
    {
        if (receivedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(receivedAtUtc));
        }

        Provider = Required(provider, nameof(provider));
        EventId = Required(eventId, nameof(eventId));
        EventType = Required(eventType, nameof(eventType));
        PayloadHash = Required(payloadHash, nameof(payloadHash));
        Status = PaymentWebhookStatus.Received;
        ReceivedAtUtc = receivedAtUtc;
    }

    public string Provider { get; private set; } = string.Empty;
    public string EventId { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public string PayloadHash { get; private set; } = string.Empty;
    public PaymentWebhookStatus Status { get; private set; }
    public DateTime ReceivedAtUtc { get; private set; }
    public DateTime? ProcessedAtUtc { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }

    public void StartProcessing()
    {
        if (Status != PaymentWebhookStatus.Received)
        {
            throw new InvalidOperationException($"A webhook in status '{Status}' cannot start processing.");
        }

        Status = PaymentWebhookStatus.Processing;
    }

    public void Complete(DateTime processedAtUtc)
    {
        EnsureCompletable(processedAtUtc);
        Status = PaymentWebhookStatus.Processed;
        ProcessedAtUtc = processedAtUtc;
    }

    public void Reject(string errorCode, string? errorMessage, DateTime processedAtUtc)
    {
        EnsureCompletable(processedAtUtc);
        Status = PaymentWebhookStatus.Rejected;
        ErrorCode = Required(errorCode, nameof(errorCode));
        ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage.Trim();
        ProcessedAtUtc = processedAtUtc;
    }

    public void Fail(string errorCode, string? errorMessage, DateTime processedAtUtc)
    {
        EnsureCompletable(processedAtUtc);
        Status = PaymentWebhookStatus.Failed;
        ErrorCode = Required(errorCode, nameof(errorCode));
        ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage.Trim();
        ProcessedAtUtc = processedAtUtc;
    }

    private void EnsureCompletable(DateTime processedAtUtc)
    {
        if (processedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(processedAtUtc));
        }

        if (Status != PaymentWebhookStatus.Processing)
        {
            throw new InvalidOperationException($"A webhook in status '{Status}' cannot complete processing.");
        }
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();
}
