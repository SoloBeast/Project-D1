using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Payments;

public sealed class PaymentService(
    DoodhDirectDbContext dbContext,
    IPaymentGateway gateway,
    IWalletService walletService,
    IIndiaTimeProvider timeProvider,
    IOptions<PaymentOptions> paymentOptions,
    INotificationEventWriter notificationEventWriter,
    MockPaymentGateway? mockGateway = null,
    IHostEnvironment? hostEnvironment = null) : IPaymentService
{
    private readonly PaymentOptions options = paymentOptions.Value;
    private readonly bool isDevelopment = hostEnvironment?.IsDevelopment() ?? true;

    public async Task<PaymentResult> CreateAsync(
        long customerId,
        CreatePaymentRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(idempotencyKey);
        var existing = await PaymentQuery().SingleOrDefaultAsync(
            x => x.CustomerId == customerId && x.IdempotencyKey == idempotencyKey.Trim(),
            cancellationToken);
        if (existing is not null)
        {
            if (existing.Order?.PublicId != request.OrderId || existing.Method != request.Method)
            {
                throw new ConflictException("The idempotency key is already associated with a different payment request.");
            }

            return existing.ToResult(PublicKeyFor(existing.Method), ProviderFor(existing.Method));
        }

        var order = await dbContext.Orders.SingleOrDefaultAsync(
            x => x.PublicId == request.OrderId && x.CustomerId == customerId,
            cancellationToken)
            ?? throw new NotFoundException("The order was not found.");
        if (order.Status != OrderStatus.PendingPayment)
        {
            throw new BusinessRuleException($"An order in status '{order.Status}' cannot start a payment.");
        }
        if (order.PayableAmount <= 0)
        {
            throw new BusinessRuleException("The order does not have a positive payable amount.");
        }

        var activePaymentExists = await dbContext.Payments.AnyAsync(
            x => x.OrderId == order.Id &&
                x.Status != PaymentStatus.Failed &&
                x.Status != PaymentStatus.Expired,
            cancellationToken);
        if (activePaymentExists)
        {
            throw new ConflictException("The order already has an active payment.");
        }

        var payment = new Payment(
            order.Id,
            customerId,
            request.Method,
            order.PayableAmount,
            options.Currency,
            idempotencyKey.Trim(),
            timeProvider.Now.AddMinutes(options.PaymentExpiryMinutes));
        dbContext.Payments.Add(payment);

        if (request.Method == PaymentMethod.Wallet)
        {
            await ExecuteSerializableAsync(async () =>
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                payment.MarkWalletPending();
                await walletService.DebitOrderAsync(
                    customerId,
                    order.Id,
                    payment.Id,
                    payment.Amount,
                    $"payment:{payment.PublicId:N}",
                    cancellationToken);
                payment.Succeed(null, "wallet_debited", timeProvider.Now);
                ConfirmTarget(payment, timeProvider.Now);
                AddPaymentOutcomeEvents(payment);
                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }
        else
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            var selectedGateway = GatewayFor(payment.Method);
            GatewayOrderResult gatewayOrder;
            try
            {
                gatewayOrder = await selectedGateway.CreateOrderAsync(
                    new GatewayOrderRequest(
                        payment.PublicId,
                        order.OrderNumber,
                        ToMinorUnits(payment.Amount),
                        payment.Currency,
                        ToUtc(payment.ExpiresAt)),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                payment.Fail("GATEWAY_ORDER_FAILED", exception.Message, null, timeProvider.Now);
                AddPaymentOutcomeEvents(payment);
                await dbContext.SaveChangesAsync(cancellationToken);
                throw new BusinessRuleException("The payment gateway could not create the payment order.");
            }

            EnsureGatewayFinancials(payment, gatewayOrder.AmountMinor, gatewayOrder.Currency);
            payment.AttachGatewayOrder(gatewayOrder.GatewayOrderId, gatewayOrder.Status);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await LoadPaymentNavigationsAsync(payment, cancellationToken);
        return payment.ToResult(PublicKeyFor(payment.Method), ProviderFor(payment.Method));
    }

    public async Task<PaymentResult> CreateForSubscriptionAsync(
        long customerId,
        long subscriptionId,
        PaymentMethod method,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(idempotencyKey);
        var normalizedIdempotencyKey = idempotencyKey.Trim();
        var existing = await PaymentQuery().SingleOrDefaultAsync(
            x => x.CustomerId == customerId && x.IdempotencyKey == normalizedIdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.SubscriptionId != subscriptionId || existing.Method != method)
            {
                throw new ConflictException(
                    "The idempotency key is already associated with a different payment request.");
            }

            return existing.ToResult(PublicKeyFor(existing.Method), ProviderFor(existing.Method));
        }

        var subscription = await dbContext.Subscriptions.SingleOrDefaultAsync(
            x => x.Id == subscriptionId && x.CustomerId == customerId,
            cancellationToken)
            ?? throw new NotFoundException("The subscription was not found.");
        if (subscription.Status != SubscriptionStatus.PaymentPending)
        {
            throw new BusinessRuleException(
                $"A subscription in status '{subscription.Status}' cannot start a payment.");
        }
        if (subscription.PayableAmount <= 0)
        {
            throw new BusinessRuleException("The subscription does not have a positive payable amount.");
        }

        var activePaymentExists = await dbContext.Payments.AnyAsync(
            x => x.SubscriptionId == subscription.Id &&
                x.Status != PaymentStatus.Failed &&
                x.Status != PaymentStatus.Expired,
            cancellationToken);
        if (activePaymentExists)
        {
            throw new ConflictException("The subscription already has an active payment.");
        }

        var payment = Payment.CreateForSubscription(
            subscription.Id,
            customerId,
            method,
            subscription.PayableAmount,
            options.Currency,
            normalizedIdempotencyKey,
            timeProvider.Now.AddMinutes(options.PaymentExpiryMinutes));
        dbContext.Payments.Add(payment);

        if (method == PaymentMethod.Wallet)
        {
            await ExecuteSerializableAsync(async () =>
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                payment.MarkWalletPending();
                await walletService.DebitSubscriptionAsync(
                    customerId,
                    subscription.Id,
                    payment.Id,
                    payment.Amount,
                    $"payment:{payment.PublicId:N}",
                    cancellationToken);
                payment.Succeed(null, "wallet_debited", timeProvider.Now);
                ConfirmTarget(payment, timeProvider.Now);
                AddPaymentOutcomeEvents(payment, subscription);
                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }
        else
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            var selectedGateway = GatewayFor(payment.Method);
            GatewayOrderResult gatewayOrder;
            try
            {
                gatewayOrder = await selectedGateway.CreateOrderAsync(
                    new GatewayOrderRequest(
                        payment.PublicId,
                        $"SUB-{subscription.PublicId:N}",
                        ToMinorUnits(payment.Amount),
                        payment.Currency,
                        ToUtc(payment.ExpiresAt)),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                payment.Fail("GATEWAY_ORDER_FAILED", exception.Message, null, timeProvider.Now);
                subscription.FailPayment();
                AddPaymentOutcomeEvents(payment, subscription);
                await dbContext.SaveChangesAsync(cancellationToken);
                throw new BusinessRuleException("The payment gateway could not create the payment order.");
            }

            EnsureGatewayFinancials(payment, gatewayOrder.AmountMinor, gatewayOrder.Currency);
            payment.AttachGatewayOrder(gatewayOrder.GatewayOrderId, gatewayOrder.Status);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await LoadPaymentNavigationsAsync(payment, cancellationToken);
        return payment.ToResult(PublicKeyFor(payment.Method), ProviderFor(payment.Method));
    }

    public async Task<PaymentResult> RetrySubscriptionAsync(
        long customerId,
        Guid subscriptionId,
        PaymentMethod method,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(idempotencyKey);
        var normalizedIdempotencyKey = idempotencyKey.Trim();
        var existing = await PaymentQuery().SingleOrDefaultAsync(
            x => x.CustomerId == customerId && x.IdempotencyKey == normalizedIdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.Subscription?.PublicId != subscriptionId || existing.Method != method)
            {
                throw new ConflictException(
                    "The idempotency key is already associated with a different payment request.");
            }

            return existing.ToResult(PublicKeyFor(existing.Method), ProviderFor(existing.Method));
        }

        var subscription = await dbContext.Subscriptions.SingleOrDefaultAsync(
            x => x.PublicId == subscriptionId && x.CustomerId == customerId,
            cancellationToken)
            ?? throw new NotFoundException("The subscription was not found.");
        if (subscription.Status is not (
            SubscriptionStatus.PaymentPending or SubscriptionStatus.PaymentFailed))
        {
            throw new BusinessRuleException(
                $"A subscription in status '{subscription.Status}' cannot retry payment.");
        }
        if (subscription.PayableAmount <= 0)
        {
            throw new BusinessRuleException("The subscription does not have a positive payable amount.");
        }

        var activePayments = await dbContext.Payments
            .Where(x => x.SubscriptionId == subscription.Id &&
                x.Status != PaymentStatus.Failed &&
                x.Status != PaymentStatus.Expired)
            .ToListAsync(cancellationToken);
        if (activePayments.Any(x => x.Status is not (PaymentStatus.Initiated or PaymentStatus.Pending)))
        {
            throw new ConflictException("The subscription already has a completed payment.");
        }

        var payment = Payment.CreateForSubscription(
            subscription.Id,
            customerId,
            method,
            subscription.PayableAmount,
            options.Currency,
            normalizedIdempotencyKey,
            timeProvider.Now.AddMinutes(options.PaymentExpiryMinutes));
        dbContext.Payments.Add(payment);

        void PrepareReplacement()
        {
            foreach (var activePayment in activePayments)
            {
                activePayment.Expire(timeProvider.Now);
                AddPaymentOutcomeEvents(activePayment, subscription);
            }
            if (subscription.Status == SubscriptionStatus.PaymentFailed)
            {
                subscription.RetryPayment();
            }
        }

        if (method == PaymentMethod.Wallet)
        {
            await ExecuteSerializableAsync(async () =>
            {
                PrepareReplacement();
                await dbContext.SaveChangesAsync(cancellationToken);
                payment.MarkWalletPending();
                await walletService.DebitSubscriptionAsync(
                    customerId,
                    subscription.Id,
                    payment.Id,
                    payment.Amount,
                    $"payment:{payment.PublicId:N}",
                    cancellationToken);
                payment.Succeed(null, "wallet_debited", timeProvider.Now);
                ConfirmTarget(payment, timeProvider.Now);
                AddPaymentOutcomeEvents(payment, subscription);
                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }
        else
        {
            await ExecuteSerializableAsync(async () =>
            {
                PrepareReplacement();
                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
            var selectedGateway = GatewayFor(payment.Method);
            GatewayOrderResult gatewayOrder;
            try
            {
                gatewayOrder = await selectedGateway.CreateOrderAsync(
                    new GatewayOrderRequest(
                        payment.PublicId,
                        $"SUB-{subscription.PublicId:N}",
                        ToMinorUnits(payment.Amount),
                        payment.Currency,
                        ToUtc(payment.ExpiresAt)),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                payment.Fail("GATEWAY_ORDER_FAILED", exception.Message, null, timeProvider.Now);
                subscription.FailPayment();
                AddPaymentOutcomeEvents(payment, subscription);
                await dbContext.SaveChangesAsync(cancellationToken);
                throw new BusinessRuleException("The payment gateway could not create the payment order.");
            }

            EnsureGatewayFinancials(payment, gatewayOrder.AmountMinor, gatewayOrder.Currency);
            payment.AttachGatewayOrder(gatewayOrder.GatewayOrderId, gatewayOrder.Status);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await LoadPaymentNavigationsAsync(payment, cancellationToken);
        return payment.ToResult(PublicKeyFor(payment.Method), ProviderFor(payment.Method));
    }

    public async Task<PaymentResult> VerifyAsync(
        long customerId,
        VerifyPaymentRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequired(request.GatewayOrderId, nameof(request.GatewayOrderId), 100);
        ValidateRequired(request.GatewayPaymentId, nameof(request.GatewayPaymentId), 100);
        ValidateRequired(request.Signature, nameof(request.Signature), 500);

        var payment = await PaymentQuery().SingleOrDefaultAsync(
            x => x.PublicId == request.PaymentId && x.CustomerId == customerId,
            cancellationToken)
            ?? throw new NotFoundException("The payment was not found.");
        if (payment.Method != PaymentMethod.Razorpay)
        {
            throw new BusinessRuleException("Only Razorpay payments use Razorpay verification.");
        }
        var razorpayGateway = GatewayFor(PaymentMethod.Razorpay);
        if (payment.Status == PaymentStatus.Success)
        {
            if (!string.Equals(payment.GatewayPaymentId, request.GatewayPaymentId, StringComparison.Ordinal))
            {
                throw new ConflictException("A different gateway payment is already verified.");
            }

            return payment.ToResult(razorpayGateway.PublicKeyId, razorpayGateway.ProviderName);
        }
        if (payment.Status != PaymentStatus.Pending)
        {
            throw new BusinessRuleException($"A payment in status '{payment.Status}' cannot be verified.");
        }
        var now = timeProvider.Now;
        if (now > payment.ExpiresAt)
        {
            payment.Expire(now);
            FailTarget(payment);
            AddPaymentOutcomeEvents(payment);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new BusinessRuleException("The payment has expired.");
        }
        if (!string.Equals(payment.GatewayOrderId, request.GatewayOrderId, StringComparison.Ordinal) ||
            !razorpayGateway.VerifyPaymentSignature(
                request.GatewayOrderId,
                request.GatewayPaymentId,
                request.Signature))
        {
            throw new ValidationAppException("The payment signature is invalid.", nameof(request.Signature));
        }

        var gatewayStatus = await razorpayGateway.GetPaymentStatusAsync(
            request.GatewayPaymentId,
            cancellationToken);
        EnsureGatewayIdentity(payment, request.GatewayPaymentId, gatewayStatus);
        EnsureGatewayFinancials(payment, gatewayStatus.AmountMinor, gatewayStatus.Currency);

        await ExecuteSerializableAsync(async () =>
        {
            if (gatewayStatus.IsSuccessful)
            {
                payment.Succeed(gatewayStatus.GatewayPaymentId, gatewayStatus.Status, timeProvider.Now);
                ConfirmTarget(payment, timeProvider.Now);
            }
            else if (gatewayStatus.IsTerminalFailure)
            {
                payment.Fail("GATEWAY_PAYMENT_FAILED", "The gateway reported a terminal payment failure.", gatewayStatus.Status, timeProvider.Now);
                FailTarget(payment);
            }
            else
            {
                throw new ConflictException("The payment is still pending gateway confirmation.");
            }

            AddPaymentOutcomeEvents(payment);
            await dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return payment.ToResult(razorpayGateway.PublicKeyId, razorpayGateway.ProviderName);
    }

    public async Task<PaymentResult> CompleteDevelopmentAsync(
        long customerId,
        Guid paymentId,
        CancellationToken cancellationToken)
    {
        var payment = await PaymentQuery().SingleOrDefaultAsync(
            x => x.PublicId == paymentId && x.CustomerId == customerId,
            cancellationToken)
            ?? throw new NotFoundException("The payment was not found.");
        if (payment.Method != PaymentMethod.Development)
        {
            throw new BusinessRuleException(
                "Only Development payments can use Development completion.");
        }
        var developmentGateway = GatewayFor(PaymentMethod.Development);
        if (payment.Status == PaymentStatus.Success)
        {
            return payment.ToResult(null, developmentGateway.ProviderName);
        }
        if (payment.Status != PaymentStatus.Pending)
        {
            throw new BusinessRuleException(
                $"A payment in status '{payment.Status}' cannot be completed.");
        }
        var now = timeProvider.Now;
        if (now > payment.ExpiresAt)
        {
            payment.Expire(now);
            FailTarget(payment);
            AddPaymentOutcomeEvents(payment);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new BusinessRuleException("The payment has expired.");
        }
        if (string.IsNullOrWhiteSpace(payment.GatewayOrderId))
        {
            throw new ConflictException("The Development gateway order is unavailable.");
        }

        var gatewayPaymentId = $"pay_mock_{payment.PublicId:N}";
        var gatewayStatus = await developmentGateway.GetPaymentStatusAsync(
            gatewayPaymentId,
            cancellationToken);
        EnsureGatewayIdentity(payment, gatewayPaymentId, gatewayStatus);
        EnsureGatewayFinancials(payment, gatewayStatus.AmountMinor, gatewayStatus.Currency);
        if (!gatewayStatus.IsSuccessful)
        {
            throw new ConflictException("The Development payment could not be confirmed.");
        }

        await ExecuteSerializableAsync(async () =>
        {
            payment.Succeed(gatewayStatus.GatewayPaymentId, gatewayStatus.Status, timeProvider.Now);
            ConfirmTarget(payment, timeProvider.Now);
            AddPaymentOutcomeEvents(payment);
            await dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return payment.ToResult(null, developmentGateway.ProviderName);
    }

    public Task<IReadOnlyList<PaymentCapability>> GetCapabilitiesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<PaymentCapability> capabilities =
        [
            new(PaymentMethod.Wallet, "Wallet", "Wallet", true),
            new(
                PaymentMethod.Razorpay,
                "Razorpay",
                "Razorpay",
                options.IsRazorpay && options.IsRazorpayConfigured,
                options.IsRazorpay && options.IsRazorpayConfigured
                    ? null
                    : "Razorpay is unavailable because it is not the effective provider or valid credentials are not configured."),
            new(
                PaymentMethod.Development,
                "Mock",
                "Development payment",
                isDevelopment,
                isDevelopment
                    ? null
                    : "Development payment is available only in the Development environment.")
        ];
        return Task.FromResult(capabilities);
    }

    public async Task<PaymentResult> GetAsync(
        long userId,
        Guid paymentId,
        bool bypassOwnership,
        CancellationToken cancellationToken)
    {
        var payment = await PaymentQuery().SingleOrDefaultAsync(
            x => x.PublicId == paymentId && (bypassOwnership || x.CustomerId == userId),
            cancellationToken)
            ?? throw new NotFoundException("The payment was not found.");
        return payment.ToResult(PublicKeyFor(payment.Method), ProviderFor(payment.Method));
    }

    public async Task<RefundResult> RefundAsync(
        long requestedByUserId,
        Guid paymentId,
        RefundPaymentRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequired(request.Reason, nameof(request.Reason), 500);
        ValidateIdempotencyKey(request.IdempotencyKey);

        var payment = await PaymentQuery()
            .Include(x => x.Refunds)
            .SingleOrDefaultAsync(x => x.PublicId == paymentId, cancellationToken)
            ?? throw new NotFoundException("The payment was not found.");
        var existing = payment.Refunds.SingleOrDefault(
            x => x.IdempotencyKey == request.IdempotencyKey.Trim());
        if (existing is not null)
        {
            await dbContext.Entry(existing).Reference(x => x.Payment).LoadAsync(cancellationToken);
            return existing.ToResult();
        }

        var amount = request.Amount ?? payment.Amount - payment.RefundedAmount;
        ValidatePositiveAmount(amount, nameof(request.Amount));
        var refund = payment.StartRefund(
            amount,
            request.Reason.Trim(),
            request.IdempotencyKey.Trim(),
            requestedByUserId);

        if (payment.Method == PaymentMethod.Wallet)
        {
            await ExecuteSerializableAsync(async () =>
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                if (payment.OrderId is long orderId)
                {
                    await walletService.CreditRefundAsync(
                        payment.CustomerId,
                        orderId,
                        payment.Id,
                        amount,
                        $"refund:{refund.PublicId:N}",
                        cancellationToken);
                }
                else if (payment.SubscriptionId is long subscriptionId)
                {
                    await walletService.CreditSubscriptionRefundAsync(
                        payment.CustomerId,
                        subscriptionId,
                        payment.Id,
                        amount,
                        $"refund:{refund.PublicId:N}",
                        cancellationToken);
                }
                else
                {
                    throw new InvalidOperationException("The payment has no payable target.");
                }

                refund.Succeed(timeProvider.Now);
                payment.CompleteRefund(amount);
                WriteRefundAudit(payment, refund, requestedByUserId, request);
                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }
        else
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(payment.GatewayPaymentId))
            {
                throw new BusinessRuleException("The gateway payment reference is unavailable for this refund.");
            }

            GatewayRefundResult gatewayRefund;
            try
            {
                gatewayRefund = await GatewayFor(payment.Method).RefundAsync(
                    payment.GatewayPaymentId,
                    ToMinorUnits(amount),
                    request.IdempotencyKey.Trim(),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                refund.Fail("GATEWAY_REFUND_FAILED", exception.Message, timeProvider.Now);
                payment.FailRefund();
                await dbContext.SaveChangesAsync(cancellationToken);
                throw new BusinessRuleException("The payment gateway could not submit the refund.");
            }

            refund.MarkProcessing(gatewayRefund.GatewayRefundId);
            if (gatewayRefund.IsSuccessful)
            {
                refund.Succeed(timeProvider.Now);
                payment.CompleteRefund(amount);
            }
            else if (!gatewayRefund.IsPending)
            {
                refund.Fail(
                    gatewayRefund.FailureCode ?? "GATEWAY_REFUND_FAILED",
                    gatewayRefund.FailureMessage,
                    timeProvider.Now);
                payment.FailRefund();
            }

            WriteRefundAudit(payment, refund, requestedByUserId, request);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await dbContext.Entry(refund).Reference(x => x.Payment).LoadAsync(cancellationToken);
        return refund.ToResult();
    }

    public async Task ProcessWebhookAsync(
        byte[] payload,
        string signature,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var razorpayGateway = GatewayFor(PaymentMethod.Razorpay);
        if (payload.Length == 0 || !razorpayGateway.VerifyWebhookSignature(payload, signature))
        {
            throw new UnauthorizedAppException("The webhook signature is invalid.");
        }

        GatewayWebhookEvent gatewayEvent;
        try
        {
            gatewayEvent = razorpayGateway.ParseWebhook(payload);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new ValidationAppException($"The webhook payload is invalid: {exception.Message}");
        }

        var duplicate = await dbContext.PaymentWebhooks.AnyAsync(
            x => x.Provider == razorpayGateway.ProviderName && x.EventId == gatewayEvent.EventId,
            cancellationToken);
        if (duplicate)
        {
            return;
        }

        var webhook = new PaymentWebhook(
            razorpayGateway.ProviderName,
            gatewayEvent.EventId,
            gatewayEvent.EventType,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            timeProvider.Now);
        dbContext.PaymentWebhooks.Add(webhook);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            if (await dbContext.PaymentWebhooks.AnyAsync(
                    x => x.Provider == razorpayGateway.ProviderName && x.EventId == gatewayEvent.EventId,
                    cancellationToken))
            {
                return;
            }

            throw;
        }

        try
        {
            await ExecuteSerializableAsync(async () =>
            {
                webhook.StartProcessing();
                if (gatewayEvent.EventType.StartsWith("payment.", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessPaymentWebhookAsync(gatewayEvent, cancellationToken);
                    webhook.Complete(timeProvider.Now);
                }
                else if (gatewayEvent.EventType.StartsWith("refund.", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessRefundWebhookAsync(gatewayEvent, cancellationToken);
                    webhook.Complete(timeProvider.Now);
                }
                else
                {
                    webhook.Reject("UNSUPPORTED_EVENT", "The webhook event type is not handled.", timeProvider.Now);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            dbContext.ChangeTracker.Clear();
            webhook = await dbContext.PaymentWebhooks.SingleAsync(
                x => x.Provider == razorpayGateway.ProviderName && x.EventId == gatewayEvent.EventId,
                cancellationToken);
            webhook.StartProcessing();
            webhook.Fail("WEBHOOK_PROCESSING_FAILED", exception.Message, timeProvider.Now);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private async Task ProcessPaymentWebhookAsync(
        GatewayWebhookEvent gatewayEvent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gatewayEvent.GatewayPaymentId))
        {
            throw new ValidationAppException("The payment webhook has no payment reference.");
        }

        var payment = await dbContext.Payments
            .Include(x => x.Order)
            .Include(x => x.Subscription)
            .SingleOrDefaultAsync(
                x => x.GatewayOrderId == gatewayEvent.GatewayOrderId ||
                    x.GatewayPaymentId == gatewayEvent.GatewayPaymentId,
                cancellationToken)
            ?? throw new NotFoundException("The webhook payment was not found.");
        if (payment.Status == PaymentStatus.Success)
        {
            return;
        }
        if (payment.Status != PaymentStatus.Pending)
        {
            throw new ConflictException($"A payment in status '{payment.Status}' cannot process this webhook.");
        }

        if (payment.Method != PaymentMethod.Razorpay)
        {
            throw new ConflictException("Razorpay webhooks cannot update a non-Razorpay payment.");
        }
        var razorpayGateway = GatewayFor(PaymentMethod.Razorpay);
        var status = await razorpayGateway.GetPaymentStatusAsync(
            gatewayEvent.GatewayPaymentId,
            cancellationToken);
        EnsureGatewayIdentity(payment, gatewayEvent.GatewayPaymentId, status);
        EnsureGatewayFinancials(payment, status.AmountMinor, status.Currency);
        if (status.IsSuccessful)
        {
            payment.Succeed(status.GatewayPaymentId, status.Status, timeProvider.Now);
            ConfirmTarget(payment, timeProvider.Now);
            AddPaymentOutcomeEvents(payment);
        }
        else if (status.IsTerminalFailure)
        {
            payment.Fail("GATEWAY_PAYMENT_FAILED", "The gateway reported a terminal payment failure.", status.Status, timeProvider.Now);
            FailTarget(payment);
            AddPaymentOutcomeEvents(payment);
        }
    }

    private async Task ProcessRefundWebhookAsync(
        GatewayWebhookEvent gatewayEvent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gatewayEvent.GatewayRefundId))
        {
            throw new ValidationAppException("The refund webhook has no refund reference.");
        }

        var refund = await dbContext.Refunds
            .Include(x => x.Payment)
            .SingleOrDefaultAsync(x => x.GatewayRefundId == gatewayEvent.GatewayRefundId, cancellationToken)
            ?? throw new NotFoundException("The webhook refund was not found.");
        if (refund.Status is RefundStatus.Succeeded or RefundStatus.Failed)
        {
            return;
        }
        if (gatewayEvent.AmountMinor.HasValue && gatewayEvent.AmountMinor.Value != ToMinorUnits(refund.Amount))
        {
            throw new ConflictException("The webhook refund amount does not match the server refund.");
        }
        if (!string.IsNullOrWhiteSpace(gatewayEvent.Currency) &&
            !string.Equals(gatewayEvent.Currency, refund.Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConflictException("The webhook refund currency does not match the server refund.");
        }

        if (string.Equals(gatewayEvent.Status, "processed", StringComparison.OrdinalIgnoreCase))
        {
            refund.Succeed(timeProvider.Now);
            refund.Payment.CompleteRefund(refund.Amount);
        }
        else if (string.Equals(gatewayEvent.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            refund.Fail("GATEWAY_REFUND_FAILED", "The gateway reported a refund failure.", timeProvider.Now);
            refund.Payment.FailRefund();
        }
    }

    private IQueryable<Payment> PaymentQuery() => dbContext.Payments
        .Include(x => x.Order)
        .Include(x => x.Subscription);

    private async Task LoadPaymentNavigationsAsync(Payment payment, CancellationToken cancellationToken)
    {
        if (payment.OrderId.HasValue && !dbContext.Entry(payment).Reference(x => x.Order).IsLoaded)
        {
            await dbContext.Entry(payment).Reference(x => x.Order).LoadAsync(cancellationToken);
        }
        if (payment.SubscriptionId.HasValue &&
            !dbContext.Entry(payment).Reference(x => x.Subscription).IsLoaded)
        {
            await dbContext.Entry(payment).Reference(x => x.Subscription).LoadAsync(cancellationToken);
        }
    }

    private void AddPaymentOutcomeEvents(Payment payment, Subscription? subscriptionOverride = null)
    {
        var subscription = payment.Subscription ?? subscriptionOverride;
        var deepLink = payment.Order is not null
            ? $"/orders/{payment.Order.PublicId}"
            : subscription is not null
                ? $"/subscriptions/{subscription.PublicId}"
                : $"/payments/{payment.PublicId}";
        var variables = new Dictionary<string, string>
        {
            ["amount"] = payment.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["currency"] = payment.Currency,
            ["method"] = payment.Method.ToString(),
            ["paymentId"] = payment.PublicId.ToString()
        };

        if (payment.Status == PaymentStatus.Success)
        {
            variables["message"] = $"Your payment of {payment.Currency} {payment.Amount:0.00} was successful.";
            notificationEventWriter.Add(new NotificationEventRequest(
                payment.CustomerId,
                NotificationEventTypes.PaymentSucceeded,
                $"payment:{payment.PublicId:N}:succeeded",
                variables,
                deepLink,
                payment.VerifiedAt));

            if (subscription?.Status == SubscriptionStatus.Active)
            {
                notificationEventWriter.Add(new NotificationEventRequest(
                    payment.CustomerId,
                    NotificationEventTypes.SubscriptionActivated,
                    $"subscription:{subscription.PublicId:N}:activated",
                    new Dictionary<string, string>
                    {
                        ["message"] = $"Your {subscription.ProductNameSnapshot} subscription is now active.",
                        ["subscriptionId"] = subscription.PublicId.ToString()
                    },
                    $"/subscriptions/{subscription.PublicId}",
                    subscription.ActivatedAt));
            }

            return;
        }

        if (payment.Status is not (PaymentStatus.Failed or PaymentStatus.Expired))
        {
            return;
        }

        variables["failureCode"] = payment.FailureCode ?? "PAYMENT_FAILED";
        variables["message"] = payment.Status == PaymentStatus.Expired
            ? "Your payment expired before confirmation."
            : "Your payment could not be completed.";
        notificationEventWriter.Add(new NotificationEventRequest(
            payment.CustomerId,
            NotificationEventTypes.PaymentFailed,
            $"payment:{payment.PublicId:N}:failed",
            variables,
            deepLink,
            payment.FailedAt));
    }

    private static void ConfirmTarget(Payment payment, DateTime indiaLocalNow)
    {
        if (payment.Order is not null)
        {
            payment.Order.ConfirmPayment();
            return;
        }
        if (payment.Subscription is not null)
        {
            payment.Subscription.Activate(indiaLocalNow);
            return;
        }

        throw new InvalidOperationException("The payment has no payable target.");
    }

    private static void FailTarget(Payment payment)
    {
        if (payment.Order is not null)
        {
            payment.Order.FailPayment();
            return;
        }
        if (payment.Subscription is not null)
        {
            payment.Subscription.FailPayment();
            return;
        }

        throw new InvalidOperationException("The payment has no payable target.");
    }

    private void WriteRefundAudit(
        Payment payment,
        Refund refund,
        long requestedByUserId,
        RefundPaymentRequest request)
    {
        dbContext.AuditLogs.Add(new AuditLog(
            requestedByUserId,
            "PaymentRefundRequested",
            nameof(Payment),
            payment.PublicId.ToString(),
            JsonSerializer.Serialize(new { PaymentStatus = payment.Status, payment.RefundedAmount }),
            JsonSerializer.Serialize(new
            {
                PaymentStatus = payment.Status,
                payment.RefundedAmount,
                RefundId = refund.PublicId,
                refund.Amount,
                RefundStatus = refund.Status
            }),
            request.IpAddress,
            request.UserAgent,
            request.Reason.Trim(),
            timeProvider.Now));
    }

    private DateTime ToUtc(DateTime indiaLocal)
    {
        if (indiaLocal.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "Timestamp must be an India-local wall-clock value.",
                nameof(indiaLocal));
        }

        return timeProvider.ToUtc(indiaLocal);
    }

    private async Task ExecuteSerializableAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private IPaymentGateway GatewayFor(PaymentMethod method) => method switch
    {
        PaymentMethod.Razorpay when options.IsRazorpayConfigured => gateway,
        PaymentMethod.Razorpay => throw new BusinessRuleException(
            "Razorpay is unavailable because valid credentials are not configured."),
        PaymentMethod.Development when isDevelopment && mockGateway is not null => mockGateway,
        PaymentMethod.Development when isDevelopment &&
            string.Equals(gateway.ProviderName, "Mock", StringComparison.OrdinalIgnoreCase) => gateway,
        PaymentMethod.Development when !isDevelopment => throw new BusinessRuleException(
            "Development payment is available only in the Development environment."),
        PaymentMethod.Development => throw new BusinessRuleException(
            "Development payment is unavailable because the Mock provider is not configured."),
        PaymentMethod.Wallet => throw new InvalidOperationException(
            "Wallet payments do not use a gateway."),
        _ => throw new BusinessRuleException("The selected payment method is unavailable.")
    };

    private string? PublicKeyFor(PaymentMethod method) =>
        method == PaymentMethod.Razorpay ? GatewayFor(method).PublicKeyId : null;

    private string ProviderFor(PaymentMethod method) => method switch
    {
        PaymentMethod.Wallet => "Wallet",
        PaymentMethod.Razorpay => "Razorpay",
        PaymentMethod.Development => "Mock",
        _ => throw new BusinessRuleException("The selected payment method is unavailable.")
    };

    private static void EnsureGatewayIdentity(
        Payment payment,
        string expectedPaymentId,
        GatewayPaymentStatusResult status)
    {
        if (!string.Equals(status.GatewayPaymentId, expectedPaymentId, StringComparison.Ordinal) ||
            !string.Equals(status.GatewayOrderId, payment.GatewayOrderId, StringComparison.Ordinal))
        {
            throw new ConflictException("Gateway payment references do not match the server payment.");
        }
    }

    private static void EnsureGatewayFinancials(Payment payment, long amountMinor, string currency)
    {
        if (amountMinor != ToMinorUnits(payment.Amount) ||
            !string.Equals(currency, payment.Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConflictException("Gateway payment amount or currency does not match the server payment.");
        }
    }

    private static long ToMinorUnits(decimal amount)
    {
        var minor = decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);
        if (minor < 100 || minor > long.MaxValue)
        {
            throw new BusinessRuleException(
                "The payment amount must be at least 100 paise and representable by the gateway.");
        }

        return decimal.ToInt64(minor);
    }

    private static void ValidatePositiveAmount(decimal amount, string field)
    {
        if (amount <= 0 || decimal.Round(amount, 2, MidpointRounding.AwayFromZero) != amount)
        {
            throw new ValidationAppException("Amount must be positive and have at most two decimal places.", field);
        }
    }

    private static void ValidateIdempotencyKey(string idempotencyKey) =>
        ValidateRequired(idempotencyKey, nameof(idempotencyKey), 100);

    private static void ValidateRequired(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ValidationAppException("This field is required.", field);
        }
        if (value.Trim().Length > maxLength)
        {
            throw new ValidationAppException($"This field cannot exceed {maxLength} characters.", field);
        }
    }
}
