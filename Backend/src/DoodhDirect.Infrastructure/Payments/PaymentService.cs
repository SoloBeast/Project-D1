using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Integrations;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
    IOneTimeDeliveryCreator? oneTimeDeliveryCreator = null,
    IIntegrationSettingsProvider? integrationSettings = null,
    IOptions<PaymentReconciliationOptions>? reconciliationOptions = null) : IPaymentService
{
    private readonly PaymentOptions options = paymentOptions.Value;
    private readonly PaymentReconciliationOptions reconciliation =
        reconciliationOptions?.Value ?? new PaymentReconciliationOptions();

    public async Task<int> ProcessBatchAsync(
        int batchSize,
        int candidateAgeMinutes,
        CancellationToken cancellationToken)
        => await ProcessReconciliationBatchAsync(batchSize, candidateAgeMinutes, cancellationToken);

    public async Task<int> ProcessReconciliationBatchAsync(
        int batchSize,
        int candidateAgeMinutes,
        CancellationToken cancellationToken)
    {
        if (batchSize is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }
        if (candidateAgeMinutes is < 1 or > 1440)
        {
            throw new ArgumentOutOfRangeException(nameof(candidateAgeMinutes));
        }

        var cutoff = timeProvider.Now.AddMinutes(-candidateAgeMinutes);
        var paymentIds = await dbContext.Payments
            .Where(x => x.Method == PaymentMethod.Razorpay &&
                (x.Status == PaymentStatus.Pending ||
                 x.Status == PaymentStatus.Expired) &&
                x.CreatedAt <= cutoff &&
                x.GatewayOrderId != null)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(x => x.PublicId)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var paymentId in paymentIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ReconcileForRecoveryAsync(paymentId, cancellationToken);
                processed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A single gateway/payment failure must not stop the bounded cycle.
            }
        }

        return processed;
    }

    private async Task ReconcileForRecoveryAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await PaymentQuery().SingleOrDefaultAsync(
            x => x.PublicId == paymentId,
            cancellationToken);
        if (payment is null || payment.Method != PaymentMethod.Razorpay ||
            payment.Status is not (PaymentStatus.Pending or PaymentStatus.Expired))
        {
            return;
        }

        var evidence = PaymentAttemptEvidence.From(payment);
        var resolution = await ResolveGatewayPaymentAsync(
            payment,
            payment.GatewayPaymentId,
            cancellationToken);

        await ExecuteSerializableAsync(async () =>
        {
            await ReloadPaymentAndTargetAsync(payment, cancellationToken);
            if (payment.Status is not (PaymentStatus.Pending or PaymentStatus.Expired) ||
                PaymentAttemptEvidence.From(payment) != evidence)
            {
                return;
            }

            if (resolution.Outcome is PaymentReconciliationOutcome.Pending or
                PaymentReconciliationOutcome.Ambiguous)
            {
                return;
            }

            EnsureGatewayIdentity(payment, resolution.Status.GatewayPaymentId, resolution.Status);
            EnsureGatewayFinancials(payment, resolution.Status.AmountMinor, resolution.Status.Currency);
            var now = timeProvider.Now;
            if (resolution.Outcome == PaymentReconciliationOutcome.Captured)
            {
                var recovered = payment.Status == PaymentStatus.Expired;
                if (recovered)
                {
                    payment.RecoverCaptured(resolution.Status.GatewayPaymentId, resolution.Status.Status, now);
                }
                else
                {
                    payment.Succeed(resolution.Status.GatewayPaymentId, resolution.Status.Status, now);
                }

                var planConfirmed = await ConfirmPlanOrTargetAsync(payment, now, cancellationToken, recovered);
                if (!planConfirmed || !payment.PaymentGroupId.HasValue)
                {
                    AddPaymentOutcomeEvents(payment);
                }
            }
            else
            {
                if (payment.Status == PaymentStatus.Pending && now < payment.ExpiresAt)
                {
                    payment.Fail(
                        "GATEWAY_PAYMENT_FAILED",
                        "The gateway reported a terminal payment failure.",
                        resolution.Status.Status,
                        now);
                }
                else
                {
                    payment.Expire(now);
                }

                await FailPlanOrTargetAsync(payment, cancellationToken);
                AddPaymentOutcomeEvents(payment);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<PaymentResult> CreateAsync(
        long customerId,
        CreatePaymentRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(idempotencyKey);
        if (request.UseWalletBalance && request.Method == PaymentMethod.Razorpay)
        {
            var routed = await CreatePlanCoreAsync(
                customerId,
                request.OrderId,
                subscriptionPublicId: null,
                useWalletBalance: true,
                idempotencyKey.Trim(),
                cancellationToken);
            var routedResult = await ToPlanResultAsync(routed, cancellationToken);
            if (request.ExpectedPayableAmount.HasValue &&
                request.ExpectedPayableAmount.Value != routedResult.PayableAmount)
            {
                throw new ConflictException("The checkout total changed. Please refresh and try again.");
            }

            return routedResult.Payment;
        }
        var existing = await PaymentQuery().SingleOrDefaultAsync(
            x => x.CustomerId == customerId && x.IdempotencyKey == idempotencyKey.Trim(),
            cancellationToken);
        if (existing is not null)
        {
            if (existing.Order?.PublicId != request.OrderId || existing.Method != request.Method)
            {
                throw new ConflictException("The idempotency key is already associated with a different payment request.");
            }

            return existing.ToResult(await PublicKeyForAsync(existing.Method, cancellationToken), ProviderFor(existing.Method));
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

        var orderPayments = await dbContext.Payments
            .Where(x => x.OrderId == order.Id)
            .ToListAsync(cancellationToken);
        await EnsurePreviousAttemptsSafeAsync(orderPayments, cancellationToken);
        EnsureNoActivePayment(WithoutAbandonedPlanComponents(orderPayments), "order");
        var attemptEvidence = CaptureAttemptEvidence(orderPayments);

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
                await dbContext.Entry(order).ReloadAsync(cancellationToken);
                EnsureOrderCanStartPayment(order);
                var currentPayments = await RevalidateAttemptEvidenceAsync(
                    dbContext.Payments.Where(x => x.OrderId == order.Id),
                    attemptEvidence,
                    cancellationToken);
                EnsureNoActivePayment(WithoutAbandonedPlanComponents(currentPayments), "order");
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
                await ConfirmTargetAsync(payment, timeProvider.Now, cancellationToken);
                AddPaymentOutcomeEvents(payment);
                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }
        else
        {
            await ExecuteSerializableAsync(async () =>
            {
                await dbContext.Entry(order).ReloadAsync(cancellationToken);
                EnsureOrderCanStartPayment(order);
                var currentPayments = await RevalidateAttemptEvidenceAsync(
                    dbContext.Payments.Where(x => x.OrderId == order.Id),
                    attemptEvidence,
                    cancellationToken);
                EnsureNoActivePayment(WithoutAbandonedPlanComponents(currentPayments), "order");
                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
            var selectedGateway = await GatewayForAsync(payment.Method, cancellationToken);
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
        return payment.ToResult(await PublicKeyForAsync(payment.Method, cancellationToken), ProviderFor(payment.Method));
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

            return existing.ToResult(await PublicKeyForAsync(existing.Method, cancellationToken), ProviderFor(existing.Method));
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

        var subscriptionPayments = await dbContext.Payments
            .Where(x => x.SubscriptionId == subscription.Id)
            .ToListAsync(cancellationToken);
        await EnsurePreviousAttemptsSafeAsync(subscriptionPayments, cancellationToken);
        EnsureNoActivePayment(WithoutAbandonedPlanComponents(subscriptionPayments), "subscription");
        var attemptEvidence = CaptureAttemptEvidence(subscriptionPayments);

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
                await dbContext.Entry(subscription).ReloadAsync(cancellationToken);
                EnsureSubscriptionCanStartPayment(subscription, allowFailed: false);
                var currentPayments = await RevalidateAttemptEvidenceAsync(
                    dbContext.Payments.Where(x => x.SubscriptionId == subscription.Id),
                    attemptEvidence,
                    cancellationToken);
                EnsureNoActivePayment(WithoutAbandonedPlanComponents(currentPayments), "subscription");
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
                await ConfirmTargetAsync(payment, timeProvider.Now, cancellationToken);
                AddPaymentOutcomeEvents(payment, subscription);
                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }
        else
        {
            await ExecuteSerializableAsync(async () =>
            {
                await dbContext.Entry(subscription).ReloadAsync(cancellationToken);
                EnsureSubscriptionCanStartPayment(subscription, allowFailed: false);
                var currentPayments = await RevalidateAttemptEvidenceAsync(
                    dbContext.Payments.Where(x => x.SubscriptionId == subscription.Id),
                    attemptEvidence,
                    cancellationToken);
                EnsureNoActivePayment(WithoutAbandonedPlanComponents(currentPayments), "subscription");
                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
            var selectedGateway = await GatewayForAsync(payment.Method, cancellationToken);
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
        return payment.ToResult(await PublicKeyForAsync(payment.Method, cancellationToken), ProviderFor(payment.Method));
    }

    public async Task<PaymentResult> CreateWalletTopUpAsync(
        long customerId,
        decimal amount,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(idempotencyKey);
        ValidatePositiveAmount(amount, nameof(amount));
        var normalizedIdempotencyKey = idempotencyKey.Trim();
        var existing = await PaymentQuery().SingleOrDefaultAsync(
            x => x.CustomerId == customerId && x.IdempotencyKey == normalizedIdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.Order is not null || existing.Subscription is not null || existing.Method != PaymentMethod.Razorpay)
            {
                throw new ConflictException(
                    "The idempotency key is already associated with a different payment request.");
            }

            return existing.ToResult(await PublicKeyForAsync(existing.Method, cancellationToken), ProviderFor(existing.Method));
        }

        // Stage 1 employee self-ordering: active employees may top up their own
        // wallet exactly like customers. Inactive accounts stay rejected here.
        var customerExists = await dbContext.Users.AnyAsync(
            x => x.Id == customerId && x.IsActive && (x.UserType == DoodhDirect.Domain.Identity.UserType.Customer || x.UserType == DoodhDirect.Domain.Identity.UserType.Employee),
            cancellationToken);
        if (!customerExists)
        {
            throw new NotFoundException("The customer was not found.");
        }

        var payment = Payment.CreateForWalletTopUp(
            customerId,
            PaymentMethod.Razorpay,
            amount,
            options.Currency,
            normalizedIdempotencyKey,
            timeProvider.Now.AddMinutes(options.PaymentExpiryMinutes));
        dbContext.Payments.Add(payment);

        await ExecuteSerializableAsync(async () =>
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        var selectedGateway = await GatewayForAsync(payment.Method, cancellationToken);
        GatewayOrderResult gatewayOrder;
        try
        {
            gatewayOrder = await selectedGateway.CreateOrderAsync(
                new GatewayOrderRequest(
                    payment.PublicId,
                    $"WALLET-{payment.PublicId:N}",
                    ToMinorUnits(payment.Amount),
                    payment.Currency,
                    ToUtc(payment.ExpiresAt)),
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            payment.Fail("GATEWAY_ORDER_FAILED", exception.Message, null, timeProvider.Now);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new BusinessRuleException("The payment gateway could not create the payment order.");
        }

        EnsureGatewayFinancials(payment, gatewayOrder.AmountMinor, gatewayOrder.Currency);
        payment.AttachGatewayOrder(gatewayOrder.GatewayOrderId, gatewayOrder.Status);
        await dbContext.SaveChangesAsync(cancellationToken);

        await LoadPaymentNavigationsAsync(payment, cancellationToken);
        return payment.ToResult(await PublicKeyForAsync(payment.Method, cancellationToken), ProviderFor(payment.Method));
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

            return existing.ToResult(await PublicKeyForAsync(existing.Method, cancellationToken), ProviderFor(existing.Method));
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

        var subscriptionPayments = await dbContext.Payments
            .Where(x => x.SubscriptionId == subscription.Id)
            .ToListAsync(cancellationToken);
        await EnsurePreviousAttemptsSafeAsync(subscriptionPayments, cancellationToken);
        EnsureNoCompletedPayment(WithoutAbandonedPlanComponents(subscriptionPayments), "subscription");
        var attemptEvidence = CaptureAttemptEvidence(subscriptionPayments);

        var payment = Payment.CreateForSubscription(
            subscription.Id,
            customerId,
            method,
            subscription.PayableAmount,
            options.Currency,
            normalizedIdempotencyKey,
            timeProvider.Now.AddMinutes(options.PaymentExpiryMinutes));
        dbContext.Payments.Add(payment);

        async Task PrepareReplacementAsync()
        {
            await dbContext.Entry(subscription).ReloadAsync(cancellationToken);
            EnsureSubscriptionCanStartPayment(subscription, allowFailed: true);
            var currentPayments = await RevalidateAttemptEvidenceAsync(
                dbContext.Payments.Where(x => x.SubscriptionId == subscription.Id),
                attemptEvidence,
                cancellationToken);
            EnsureNoCompletedPayment(WithoutAbandonedPlanComponents(currentPayments), "subscription");
            foreach (var activePayment in currentPayments.Where(x =>
                x.Status == PaymentStatus.Initiated || x.Status == PaymentStatus.Pending))
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
                await PrepareReplacementAsync();
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
                await ConfirmTargetAsync(payment, timeProvider.Now, cancellationToken);
                AddPaymentOutcomeEvents(payment, subscription);
                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }
        else
        {
            await ExecuteSerializableAsync(async () =>
            {
                await PrepareReplacementAsync();
                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
            var selectedGateway = await GatewayForAsync(payment.Method, cancellationToken);
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
        return payment.ToResult(await PublicKeyForAsync(payment.Method, cancellationToken), ProviderFor(payment.Method));
    }

    /// <summary>
    /// Wallet-first split plan for a one-time order. The client states only
    /// <paramref name="useWalletBalance"/>; the payable total comes from the
    /// frozen order amount and every component amount is server-computed.
    /// With wallet usage off (or an empty wallet) this behaves exactly like a
    /// Razorpay-only <see cref="CreateAsync"/> call.
    /// </summary>
    public async Task<PaymentPlanResult> CreateOrderPlanAsync(
        long customerId,
        Guid orderId,
        bool useWalletBalance,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(idempotencyKey);
        var plan = await CreatePlanCoreAsync(
            customerId,
            orderId,
            subscriptionPublicId: null,
            useWalletBalance,
            idempotencyKey.Trim(),
            cancellationToken);
        return await ToPlanResultAsync(plan, cancellationToken);
    }

    /// <summary>
    /// Wallet-first split plan for a prepaid subscription's initial purchase
    /// (or a retry when <paramref name="expirePriorAttempts"/> expires
    /// still-active prior attempts and releases their held wallet legs first,
    /// mirroring the legacy retry replacement protocol).
    /// </summary>
    public async Task<PaymentPlanResult> CreateSubscriptionPlanAsync(
        long customerId,
        long subscriptionId,
        bool useWalletBalance,
        string idempotencyKey,
        CancellationToken cancellationToken,
        bool expirePriorAttempts = false)
    {
        ValidateIdempotencyKey(idempotencyKey);
        if (expirePriorAttempts)
        {
            await ExpirePriorSubscriptionAttemptsAsync(customerId, subscriptionId, cancellationToken);
        }

        var plan = await CreatePlanCoreAsync(
            customerId,
            orderPublicId: null,
            subscriptionPublicId: null,
            useWalletBalance,
            idempotencyKey.Trim(),
            cancellationToken,
            subscriptionIdOverride: subscriptionId);
        return await ToPlanResultAsync(plan, cancellationToken);
    }

    private async Task ExpirePriorSubscriptionAttemptsAsync(
        long customerId,
        long subscriptionId,
        CancellationToken cancellationToken)
    {
        await ExecuteSerializableAsync(async () =>
        {
            var target = await dbContext.Subscriptions.SingleOrDefaultAsync(
                x => x.Id == subscriptionId && x.CustomerId == customerId,
                cancellationToken)
                ?? throw new NotFoundException("The subscription was not found.");
            EnsureSubscriptionCanStartPayment(target, allowFailed: true);
            var priors = await dbContext.Payments
                .Include(x => x.Refunds)
                .Where(x => x.SubscriptionId == target.Id)
                .ToListAsync(cancellationToken);
            EnsureNoCompletedPayment(WithoutAbandonedPlanComponents(priors), "subscription");
            var now = timeProvider.Now;
            foreach (var active in priors.Where(x =>
                x.Status == PaymentStatus.Initiated || x.Status == PaymentStatus.Pending))
            {
                active.Expire(now);
                AddPaymentOutcomeEvents(active, target);
            }
            foreach (var groupId in priors
                .Where(x => x.PaymentGroupId.HasValue)
                .Select(x => x.PaymentGroupId!.Value)
                .Distinct()
                .ToList())
            {
                await ReleasePlanWalletAsync(groupId, customerId, cancellationToken);
            }
            if (target.Status == SubscriptionStatus.PaymentFailed)
            {
                target.RetryPayment();
            }
            await dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
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
        var razorpayGateway = await GatewayForAsync(PaymentMethod.Razorpay, cancellationToken);
        if (payment.Status == PaymentStatus.Success)
        {
            if (!string.Equals(payment.GatewayPaymentId, request.GatewayPaymentId, StringComparison.Ordinal))
            {
                throw new ConflictException("A different gateway payment is already verified.");
            }

            return payment.ToResult(await razorpayGateway.GetPublicKeyIdAsync(cancellationToken), razorpayGateway.ProviderName);
        }
        if (payment.Status is not (PaymentStatus.Pending or PaymentStatus.Expired))
        {
            throw new BusinessRuleException($"A payment in status '{payment.Status}' cannot be verified.");
        }
        if (!string.Equals(payment.GatewayOrderId, request.GatewayOrderId, StringComparison.Ordinal) ||
            !await razorpayGateway.VerifyPaymentSignatureAsync(
                request.GatewayOrderId,
                request.GatewayPaymentId,
                request.Signature,
                cancellationToken))
        {
            throw new ValidationAppException("The payment signature is invalid.", nameof(request.Signature));
        }

        var resolution = await ResolveGatewayPaymentAsync(
            payment,
            request.GatewayPaymentId,
            cancellationToken);
        if (resolution.Outcome == PaymentReconciliationOutcome.Ambiguous)
        {
            throw new ConflictException("The gateway response is ambiguous; replacement charging is blocked.");
        }
        if (resolution.Outcome == PaymentReconciliationOutcome.Pending)
        {
            throw new ConflictException("The payment is still pending gateway confirmation.");
        }

        await ExecuteSerializableAsync(async () =>
        {
            await ReloadPaymentAndTargetAsync(payment, cancellationToken);
            EnsureGatewayIdentity(payment, request.GatewayPaymentId, resolution.Status);
            EnsureGatewayFinancials(payment, resolution.Status.AmountMinor, resolution.Status.Currency);

            if (payment.Status == PaymentStatus.Success)
            {
                if (!string.Equals(
                        payment.GatewayPaymentId,
                        request.GatewayPaymentId,
                        StringComparison.Ordinal))
                {
                    throw new ConflictException("A different gateway payment is already verified.");
                }

                return;
            }
            if (payment.Status is not (PaymentStatus.Pending or PaymentStatus.Expired))
            {
                throw new BusinessRuleException(
                    $"A payment in status '{payment.Status}' cannot be verified.");
            }

            var now = timeProvider.Now;
            var planPurchaseConfirmed = false;
            if (resolution.Outcome == PaymentReconciliationOutcome.Captured)
            {
                var recover = payment.Status == PaymentStatus.Expired;
                if (recover)
                {
                    payment.RecoverCaptured(resolution.Status.GatewayPaymentId, resolution.Status.Status, now);
                }
                else
                {
                    payment.Succeed(resolution.Status.GatewayPaymentId, resolution.Status.Status, now);
                }

                planPurchaseConfirmed = await ConfirmPlanOrTargetAsync(payment, now, cancellationToken, recover);
            }
            else if (payment.Status == PaymentStatus.Pending)
            {
                payment.Fail("GATEWAY_PAYMENT_FAILED", "The gateway reported a terminal payment failure.", resolution.Status.Status, now);
                await FailPlanOrTargetAsync(payment, cancellationToken);
            }
            else
            {
                throw new BusinessRuleException("The payment was not captured and is already expired.");
            }

            if (!planPurchaseConfirmed || !payment.PaymentGroupId.HasValue)
            {
                AddPaymentOutcomeEvents(payment);
            }
            await dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return payment.ToResult(await razorpayGateway.GetPublicKeyIdAsync(cancellationToken), razorpayGateway.ProviderName);
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
        var developmentGateway = await GatewayForAsync(PaymentMethod.Development, cancellationToken);
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
            await ConfirmTargetAsync(payment, timeProvider.Now, cancellationToken);
            AddPaymentOutcomeEvents(payment);
            await dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return payment.ToResult(null, developmentGateway.ProviderName);
    }

    public async Task<PaymentResult> CancelAsync(
        long customerId,
        Guid paymentId,
        CancellationToken cancellationToken)
    {
        var payment = await PaymentQuery().SingleOrDefaultAsync(
            x => x.PublicId == paymentId && x.CustomerId == customerId,
            cancellationToken)
            ?? throw new NotFoundException("The payment was not found.");

        if (payment.Method != PaymentMethod.Razorpay)
        {
            throw new BusinessRuleException("Only Razorpay payments can be cancelled.");
        }
        if (payment.Status == PaymentStatus.Cancelled)
        {
            return payment.ToResult(await PublicKeyForAsync(payment.Method, cancellationToken), ProviderFor(payment.Method));
        }
        if (payment.Status == PaymentStatus.Success)
        {
            return payment.ToResult(await PublicKeyForAsync(payment.Method, cancellationToken), ProviderFor(payment.Method));
        }
        if (payment.Status is not (PaymentStatus.Pending or PaymentStatus.Expired))
        {
            throw new BusinessRuleException(
                $"A payment in status '{payment.Status}' cannot be cancelled.");
        }

        var evidence = PaymentAttemptEvidence.From(payment);
        var resolution = await ResolveGatewayPaymentAsync(
            payment,
            payment.GatewayPaymentId,
            cancellationToken);
        if (resolution.Outcome == PaymentReconciliationOutcome.Pending)
        {
            throw new ConflictException("The payment is still pending gateway confirmation and cannot be cancelled.");
        }
        if (resolution.Outcome == PaymentReconciliationOutcome.Ambiguous)
        {
            throw new ConflictException("The gateway response is ambiguous; cancellation is blocked.");
        }

        await ExecuteSerializableAsync(async () =>
        {
            await ReloadPaymentAndTargetAsync(payment, cancellationToken);
            if (payment.Status == PaymentStatus.Success)
            {
                if (resolution.Outcome != PaymentReconciliationOutcome.Captured ||
                    !string.Equals(
                        payment.GatewayPaymentId,
                        resolution.Status.GatewayPaymentId,
                        StringComparison.Ordinal))
                {
                    throw new ConflictException(
                        "The payment changed while Razorpay evidence was being collected.");
                }

                return;
            }
            if (payment.Status == PaymentStatus.Cancelled &&
                resolution.Outcome == PaymentReconciliationOutcome.DefinitivelyNotCaptured)
            {
                return;
            }
            if (PaymentAttemptEvidence.From(payment) != evidence)
            {
                throw new ConflictException(
                    "The payment changed while Razorpay evidence was being collected.");
            }

            EnsureGatewayIdentity(
                payment,
                resolution.Status.GatewayPaymentId,
                resolution.Status);
            EnsureGatewayFinancials(
                payment,
                resolution.Status.AmountMinor,
                resolution.Status.Currency);

            var now = timeProvider.Now;
            if (resolution.Outcome == PaymentReconciliationOutcome.Captured)
            {
                var recovered = payment.Status == PaymentStatus.Expired;
                if (recovered)
                {
                    payment.RecoverCaptured(
                        resolution.Status.GatewayPaymentId,
                        resolution.Status.Status,
                        now);
                }
                else
                {
                    payment.Succeed(
                        resolution.Status.GatewayPaymentId,
                        resolution.Status.Status,
                        now);
                }

                var planConfirmed = await ConfirmPlanOrTargetAsync(payment, now, cancellationToken, recovered);
                if (!planConfirmed || !payment.PaymentGroupId.HasValue)
                {
                    AddPaymentOutcomeEvents(payment);
                }
            }
            else if (payment.Status == PaymentStatus.Expired)
            {
                throw new BusinessRuleException(
                    "The payment was not captured and is already expired.");
            }
            else
            {
                payment.Cancel(now);
                await ReleasePlanWalletIfGroupedAsync(payment, cancellationToken);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        await LoadPaymentNavigationsAsync(payment, cancellationToken);
        return payment.ToResult(await PublicKeyForAsync(payment.Method, cancellationToken), ProviderFor(payment.Method));
    }

    public async Task<IReadOnlyList<PaymentCapability>> GetCapabilitiesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var razorpayConfigured = await RazorpayConfiguredAsync(cancellationToken);
        IReadOnlyList<PaymentCapability> capabilities =
        [
            new(PaymentMethod.Wallet, "Wallet", "Wallet", true),
            new(
                PaymentMethod.Razorpay,
                "Razorpay",
                "Razorpay",
                razorpayConfigured,
                razorpayConfigured
                    ? null
                    : "Razorpay is unavailable because it is not the effective provider or valid credentials are not configured.")
        ];
        return capabilities;
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
        return payment.ToResult(await PublicKeyForAsync(payment.Method, cancellationToken), ProviderFor(payment.Method));
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
                gatewayRefund = await (await GatewayForAsync(payment.Method, cancellationToken)).RefundAsync(
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

    public async Task<PaymentReconciliationResult> ReconcileAsync(
        long requestedByUserId,
        Guid paymentId,
        bool bypassOwnership,
        CancellationToken cancellationToken)
    {
        var payment = await PaymentQuery().SingleOrDefaultAsync(
            x => x.PublicId == paymentId && (bypassOwnership || x.CustomerId == requestedByUserId),
            cancellationToken)
            ?? throw new NotFoundException("The payment was not found.");
        if (payment.Method != PaymentMethod.Razorpay)
        {
            throw new BusinessRuleException("Only Razorpay payments can be reconciled.");
        }

        var resolution = await ResolveGatewayPaymentAsync(payment, payment.GatewayPaymentId, cancellationToken);
        var recovered = false;
        if (resolution.Outcome == PaymentReconciliationOutcome.Captured)
        {
            await ExecuteSerializableAsync(async () =>
            {
                await ReloadPaymentAndTargetAsync(payment, cancellationToken);
                EnsureGatewayIdentity(
                    payment,
                    resolution.Status.GatewayPaymentId,
                    resolution.Status);
                EnsureGatewayFinancials(
                    payment,
                    resolution.Status.AmountMinor,
                    resolution.Status.Currency);

                if (payment.Status is not (PaymentStatus.Pending or PaymentStatus.Expired))
                {
                    return;
                }

                var now = timeProvider.Now;
                recovered = payment.Status == PaymentStatus.Expired;
                if (recovered)
                {
                    payment.RecoverCaptured(resolution.Status.GatewayPaymentId, resolution.Status.Status, now);
                }
                else
                {
                    payment.Succeed(resolution.Status.GatewayPaymentId, resolution.Status.Status, now);
                }

                var planConfirmed = await ConfirmPlanOrTargetAsync(payment, now, cancellationToken, recovered);
                if (!planConfirmed || !payment.PaymentGroupId.HasValue)
                {
                    AddPaymentOutcomeEvents(payment);
                }
                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }

        await LoadPaymentNavigationsAsync(payment, cancellationToken);
        return new PaymentReconciliationResult(
            payment.ToResult(await PublicKeyForAsync(payment.Method, cancellationToken), ProviderFor(payment.Method)),
            resolution.Outcome,
            resolution.Status.Status,
            recovered);
    }

    public async Task ProcessWebhookAsync(
        byte[] payload,
        string signature,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var razorpayGateway = await GatewayForAsync(PaymentMethod.Razorpay, cancellationToken);
        if (payload.Length == 0 || !await razorpayGateway.VerifyWebhookSignatureAsync(payload, signature, cancellationToken))
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

        var webhook = await dbContext.PaymentWebhooks.SingleOrDefaultAsync(
            x => x.Provider == razorpayGateway.ProviderName && x.EventId == gatewayEvent.EventId,
            cancellationToken);
        if (webhook is not null &&
            webhook.Status is PaymentWebhookStatus.Processed or PaymentWebhookStatus.Rejected)
        {
            return;
        }

        webhook ??= new PaymentWebhook(
            razorpayGateway.ProviderName,
            gatewayEvent.EventId,
            gatewayEvent.EventType,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            timeProvider.Now);
        if (webhook.Id == 0)
        {
            dbContext.PaymentWebhooks.Add(webhook);
        }
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            webhook = await dbContext.PaymentWebhooks.SingleOrDefaultAsync(
                x => x.Provider == razorpayGateway.ProviderName && x.EventId == gatewayEvent.EventId,
                cancellationToken);
            if (webhook is null)
            {
                throw;
            }
        }

        dbContext.ChangeTracker.Clear();
        var processingAt = timeProvider.Now;
        var claimed = false;
        await ExecuteSerializableAsync(async () =>
        {
            webhook = await dbContext.PaymentWebhooks.SingleAsync(
                x => x.Provider == razorpayGateway.ProviderName && x.EventId == gatewayEvent.EventId,
                cancellationToken);
            if (webhook.Status is PaymentWebhookStatus.Processed or PaymentWebhookStatus.Rejected)
            {
                return;
            }
            if (webhook.Status == PaymentWebhookStatus.Processing &&
                webhook.ProcessedAt.HasValue &&
                processingAt - webhook.ProcessedAt.Value < TimeSpan.FromMinutes(
                    reconciliation.WebhookProcessingStaleMinutes))
            {
                return;
            }

            webhook.StartProcessing(
                processingAt,
                TimeSpan.FromMinutes(
                    reconciliation.WebhookProcessingStaleMinutes));
            await dbContext.SaveChangesAsync(cancellationToken);
            claimed = true;
        }, cancellationToken);
        if (!claimed)
        {
            return;
        }

        try
        {
            dbContext.ChangeTracker.Clear();
            await ExecuteSerializableAsync(async () =>
            {
                webhook = await dbContext.PaymentWebhooks.SingleAsync(
                    x => x.Provider == razorpayGateway.ProviderName && x.EventId == gatewayEvent.EventId,
                    cancellationToken);
                if (webhook.Status != PaymentWebhookStatus.Processing ||
                    webhook.ProcessedAt != processingAt)
                {
                    return;
                }

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
        catch (Exception)
        {
            dbContext.ChangeTracker.Clear();
            await ExecuteSerializableAsync(async () =>
            {
                webhook = await dbContext.PaymentWebhooks.SingleAsync(
                    x => x.Provider == razorpayGateway.ProviderName && x.EventId == gatewayEvent.EventId,
                    cancellationToken);
                if (webhook.Status != PaymentWebhookStatus.Processing ||
                    webhook.ProcessedAt != processingAt)
                {
                    return;
                }

                webhook.FailProcessing(
                    "WEBHOOK_PROCESSING_FAILED",
                    "Webhook processing failed.",
                    timeProvider.Now);
                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
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
        if (payment.Status is not (PaymentStatus.Pending or PaymentStatus.Expired))
        {
            throw new ConflictException($"A payment in status '{payment.Status}' cannot process this webhook.");
        }
        if (payment.Method != PaymentMethod.Razorpay)
        {
            throw new ConflictException("Razorpay webhooks cannot update a non-Razorpay payment.");
        }

        var resolution = await ResolveGatewayPaymentAsync(
            payment,
            gatewayEvent.GatewayPaymentId,
            cancellationToken);
        if (resolution.Outcome == PaymentReconciliationOutcome.Captured)
        {
            var now = timeProvider.Now;
            var recover = payment.Status == PaymentStatus.Expired;
            if (recover)
            {
                payment.RecoverCaptured(resolution.Status.GatewayPaymentId, resolution.Status.Status, now);
            }
            else
            {
                payment.Succeed(resolution.Status.GatewayPaymentId, resolution.Status.Status, now);
            }

            var planConfirmed = await ConfirmPlanOrTargetAsync(payment, now, cancellationToken, recover);
            if (!planConfirmed || !payment.PaymentGroupId.HasValue)
            {
                AddPaymentOutcomeEvents(payment);
            }
        }
        else if (resolution.Outcome == PaymentReconciliationOutcome.DefinitivelyNotCaptured &&
                 payment.Status == PaymentStatus.Pending)
        {
            payment.Fail("GATEWAY_PAYMENT_FAILED", "The gateway reported a terminal payment failure.", resolution.Status.Status, timeProvider.Now);
            await FailPlanOrTargetAsync(payment, cancellationToken);
            AddPaymentOutcomeEvents(payment);
        }
        else if (resolution.Outcome is PaymentReconciliationOutcome.Pending or PaymentReconciliationOutcome.Ambiguous)
        {
            throw new ConflictException("The gateway response is not safe to resolve.");
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

    private async Task ReloadPaymentAndTargetAsync(
        Payment payment,
        CancellationToken cancellationToken)
    {
        await dbContext.Entry(payment).ReloadAsync(cancellationToken);
        await LoadPaymentNavigationsAsync(payment, cancellationToken);
        if (payment.Order is not null)
        {
            await dbContext.Entry(payment.Order).ReloadAsync(cancellationToken);
        }
        if (payment.Subscription is not null)
        {
            await dbContext.Entry(payment.Subscription).ReloadAsync(cancellationToken);
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

    private async Task ConfirmTargetAsync(
        Payment payment,
        DateTime indiaLocalNow,
        CancellationToken cancellationToken,
        bool recoverCaptured = false)
    {
        if (payment.Order is not null)
        {
            if (recoverCaptured)
            {
                payment.Order.RecoverCapturedPayment();
            }
            else
            {
                payment.Order.ConfirmPayment();
            }
            if (oneTimeDeliveryCreator is not null)
            {
                await oneTimeDeliveryCreator.AddIfMissing(payment.Order, timeProvider.Today, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                await oneTimeDeliveryCreator.IssuePendingOtpsAsync(cancellationToken);
            }
            return;
        }
        if (payment.Subscription is not null)
        {
            if (recoverCaptured)
            {
                payment.Subscription.RecoverCapturedPayment(indiaLocalNow);
            }
            else
            {
                payment.Subscription.Activate(indiaLocalNow);
            }
            return;
        }

        if (payment.Order is null && payment.Subscription is null)
        {
            await walletService.CreditWalletTopUpAsync(
                payment.CustomerId,
                payment.Id,
                payment.Amount,
                $"payment:{payment.PublicId:N}",
                cancellationToken);
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

        if (payment.Order is null && payment.Subscription is null)
        {
            return;
        }

        throw new InvalidOperationException("The payment has no payable target.");
    }

    /// <summary>
    /// Wallet-first split-plan core shared by order and subscription targets.
    /// One of <paramref name="orderPublicId"/> or
    /// <paramref name="subscriptionIdOverride"/> must identify the target.
    /// All amounts are server-computed from the frozen payable total and the
    /// actual wallet balance. The database transaction never spans gateway I/O.
    /// </summary>
    private async Task<SplitPlan> CreatePlanCoreAsync(
        long customerId,
        Guid? orderPublicId,
        Guid? subscriptionPublicId,
        bool useWalletBalance,
        string planKey,
        CancellationToken cancellationToken,
        long? subscriptionIdOverride = null)
    {
        Order? order = null;
        Domain.Subscriptions.Subscription? subscription = null;
        if (orderPublicId.HasValue)
        {
            order = await dbContext.Orders.SingleOrDefaultAsync(
                x => x.PublicId == orderPublicId.Value && x.CustomerId == customerId,
                cancellationToken)
                ?? throw new NotFoundException("The order was not found.");
        }
        else if (subscriptionIdOverride.HasValue)
        {
            subscription = await dbContext.Subscriptions.SingleOrDefaultAsync(
                x => x.Id == subscriptionIdOverride.Value && x.CustomerId == customerId,
                cancellationToken)
                ?? throw new NotFoundException("The subscription was not found.");
        }
        else
        {
            throw new ValidationAppException("A payment target is required.");
        }

        var walletKey = $"{planKey}:wallet";
        var razorpayKey = $"{planKey}:razorpay";
        var wantedKeys = new[] { planKey, walletKey, razorpayKey };
        var existing = await PaymentQuery()
            .Where(x => x.CustomerId == customerId && wantedKeys.Contains(x.IdempotencyKey))
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            return ResolvePlanReplay(order?.PublicId, subscription?.PublicId, existing);
        }

        decimal total;
        if (order is not null)
        {
            if (order.Status != OrderStatus.PendingPayment)
            {
                throw new BusinessRuleException($"An order in status '{order.Status}' cannot start a payment.");
            }
            if (order.PayableAmount <= 0)
            {
                throw new BusinessRuleException("The order does not have a positive payable amount.");
            }
            total = order.PayableAmount;
        }
        else
        {
            if (subscription!.Status != SubscriptionStatus.PaymentPending)
            {
                throw new BusinessRuleException(
                    $"A subscription in status '{subscription.Status}' cannot start a payment.");
            }
            if (subscription.PayableAmount <= 0)
            {
                throw new BusinessRuleException("The subscription does not have a positive payable amount.");
            }
            total = subscription.PayableAmount;
        }

        var balance = await dbContext.Wallets
            .AsNoTracking()
            .Where(x => x.CustomerId == customerId)
            .Select(x => (decimal?)x.Balance)
            .SingleOrDefaultAsync(cancellationToken) ?? 0m;
        var walletApplied = useWalletBalance ? decimal.Min(balance, total) : 0m;
        var razorpayAmount = total - walletApplied;
        if (razorpayAmount > 0 && razorpayAmount < 1.00m)
        {
            throw new WalletRemainderBelowGatewayMinimumException(
                walletApplied,
                razorpayAmount,
                total,
                1.00m,
                options.Currency);
        }

        IQueryable<Payment> targetPayments = order is not null
            ? dbContext.Payments.Where(x => x.OrderId == order.Id)
            : dbContext.Payments.Where(x => x.SubscriptionId == subscription!.Id);
        var priorPayments = await targetPayments.ToListAsync(cancellationToken);
        await EnsurePreviousAttemptsSafeAsync(priorPayments, cancellationToken);
        EnsureNoActivePayment(WithoutAbandonedPlanComponents(priorPayments), order is not null ? "order" : "subscription");
        var attemptEvidence = CaptureAttemptEvidence(priorPayments);

        var groupId = Guid.NewGuid();
        var expiresAt = timeProvider.Now.AddMinutes(options.PaymentExpiryMinutes);
        Payment? walletRow = null;
        Payment? razorpayRow = null;
        await ExecuteSerializableAsync(async () =>
        {
            if (order is not null)
            {
                await dbContext.Entry(order).ReloadAsync(cancellationToken);
                EnsureOrderCanStartPayment(order);
            }
            else
            {
                await dbContext.Entry(subscription!).ReloadAsync(cancellationToken);
                EnsureSubscriptionCanStartPayment(subscription!, allowFailed: false);
            }
            var currentPayments = await RevalidateAttemptEvidenceAsync(
                order is not null
                    ? dbContext.Payments.Where(x => x.OrderId == order.Id)
                    : dbContext.Payments.Where(x => x.SubscriptionId == subscription!.Id),
                attemptEvidence,
                cancellationToken);
            EnsureNoActivePayment(WithoutAbandonedPlanComponents(currentPayments), order is not null ? "order" : "subscription");
            await dbContext.SaveChangesAsync(cancellationToken);

            var now = timeProvider.Now;
            if (walletApplied > 0)
            {
                walletRow = order is not null
                    ? new Payment(order.Id, customerId, PaymentMethod.Wallet, walletApplied, options.Currency, walletKey, expiresAt)
                    : Payment.CreateForSubscription(subscription!.Id, customerId, PaymentMethod.Wallet, walletApplied, options.Currency, walletKey, expiresAt);
                walletRow.AssignGroup(groupId);
                dbContext.Payments.Add(walletRow);
                await dbContext.SaveChangesAsync(cancellationToken);
                walletRow.MarkWalletPending();
                if (order is not null)
                {
                    await walletService.DebitOrderAsync(customerId, order.Id, walletRow.Id, walletRow.Amount, $"payment:{walletRow.PublicId:N}", cancellationToken);
                }
                else
                {
                    await walletService.DebitSubscriptionAsync(customerId, subscription!.Id, walletRow.Id, walletRow.Amount, $"payment:{walletRow.PublicId:N}", cancellationToken);
                }
                walletRow.Succeed(null, "wallet_debited", timeProvider.Now);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            if (razorpayAmount >= 1.00m)
            {
                razorpayRow = order is not null
                    ? new Payment(order.Id, customerId, PaymentMethod.Razorpay, razorpayAmount, options.Currency, razorpayKey, expiresAt)
                    : Payment.CreateForSubscription(subscription!.Id, customerId, PaymentMethod.Razorpay, razorpayAmount, options.Currency, razorpayKey, expiresAt);
                razorpayRow.AssignGroup(groupId);
                dbContext.Payments.Add(razorpayRow);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            if (razorpayRow is null)
            {
                // Wallet-only completion mutates the purchase target (order
                // confirmation / subscription activation) and stages outcome
                // events. Persist them here inside the serializable attempt
                // so the committed state — never just in-memory state — is
                // what later reads and the API response observe.
                await TryCompletePlanAsync(groupId, timeProvider.Now, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }, cancellationToken);

        if (razorpayRow is not null)
        {
            var selectedGateway = await GatewayForAsync(PaymentMethod.Razorpay, cancellationToken);
            GatewayOrderResult gatewayOrder;
            try
            {
                gatewayOrder = await selectedGateway.CreateOrderAsync(
                    new GatewayOrderRequest(
                        razorpayRow.PublicId,
                        order is not null ? order.OrderNumber : $"SUB-{subscription!.PublicId:N}",
                        ToMinorUnits(razorpayRow.Amount),
                        razorpayRow.Currency,
                        ToUtc(razorpayRow.ExpiresAt)),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                await ExecuteSerializableAsync(async () =>
                {
                    await ReloadPaymentAndTargetAsync(razorpayRow!, cancellationToken);
                    razorpayRow!.Fail("GATEWAY_ORDER_FAILED", exception.Message, null, timeProvider.Now);
                    await FailPlanOrTargetAsync(razorpayRow!, cancellationToken);
                    AddPaymentOutcomeEvents(razorpayRow!);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }, cancellationToken);
                throw new BusinessRuleException("The payment gateway could not create the payment order.");
            }

            EnsureGatewayFinancials(razorpayRow, gatewayOrder.AmountMinor, gatewayOrder.Currency);
            razorpayRow.AttachGatewayOrder(gatewayOrder.GatewayOrderId, gatewayOrder.Status);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return await LoadPlanAsync(groupId, walletRow, razorpayRow, cancellationToken);
    }

    private static SplitPlan ResolvePlanReplay(
        Guid? orderPublicId,
        Guid? subscriptionPublicId,
        IReadOnlyList<Payment> existing)
    {
        foreach (var row in existing)
        {
            if (orderPublicId.HasValue && row.Order?.PublicId != orderPublicId.Value)
            {
                throw new ConflictException("The idempotency key is already associated with a different payment request.");
            }
            if (subscriptionPublicId.HasValue && row.Subscription?.PublicId != subscriptionPublicId.Value)
            {
                throw new ConflictException("The idempotency key is already associated with a different payment request.");
            }
        }

        var groups = existing
            .Where(x => x.PaymentGroupId.HasValue)
            .Select(x => x.PaymentGroupId!.Value)
            .Distinct()
            .ToList();
        if (groups.Count > 1 || (groups.Count == 1 && existing.Any(x => !x.PaymentGroupId.HasValue)))
        {
            throw new ConflictException("The idempotency key is already associated with a different payment request.");
        }

        var walletRows = existing.Where(x => x.Method == PaymentMethod.Wallet).ToList();
        var gatewayRows = existing.Where(x => x.Method is PaymentMethod.Razorpay or PaymentMethod.Development).ToList();
        if (walletRows.Count > 1 || gatewayRows.Count > 1 || walletRows.Count + gatewayRows.Count != existing.Count)
        {
            throw new ConflictException("The idempotency key is already associated with a different payment request.");
        }

        return new SplitPlan(groups.SingleOrDefault(), walletRows.SingleOrDefault(), gatewayRows.SingleOrDefault());
    }

    private async Task<SplitPlan> LoadPlanAsync(
        Guid groupId,
        Payment? walletRow,
        Payment? razorpayRow,
        CancellationToken cancellationToken)
    {
        if (walletRow is not null) await LoadPaymentNavigationsAsync(walletRow, cancellationToken);
        if (razorpayRow is not null) await LoadPaymentNavigationsAsync(razorpayRow, cancellationToken);
        return new SplitPlan(groupId, walletRow, razorpayRow);
    }

    private async Task<PaymentPlanResult> ToPlanResultAsync(SplitPlan plan, CancellationToken cancellationToken)
    {
        PaymentResult? walletResult = null;
        PaymentResult? razorpayResult = null;
        if (plan.WalletRow is not null)
        {
            await LoadPaymentNavigationsAsync(plan.WalletRow, cancellationToken);
            walletResult = plan.WalletRow.ToResult(null, ProviderFor(PaymentMethod.Wallet));
        }
        if (plan.RazorpayRow is not null)
        {
            await LoadPaymentNavigationsAsync(plan.RazorpayRow, cancellationToken);
            var gatewayKey = await PublicKeyForAsync(plan.RazorpayRow.Method, cancellationToken);
            razorpayResult = plan.RazorpayRow.ToResult(gatewayKey, ProviderFor(plan.RazorpayRow.Method));
        }

        var components = new List<PaymentResult>();
        if (walletResult is not null) components.Add(walletResult);
        if (razorpayResult is not null) components.Add(razorpayResult);
        var total = components.Sum(x => x.Amount);
        var walletApplied = walletResult?.Amount ?? 0m;
        var primary = razorpayResult ?? walletResult
            ?? throw new InvalidOperationException("The payment plan has no components.");
        return new PaymentPlanResult(
            plan.GroupId == Guid.Empty ? null : plan.GroupId,
            total,
            walletApplied,
            total - walletApplied,
            walletResult is not null,
            primary,
            components);
    }

    /// <summary>
    /// Plan-aware confirmation: a grouped purchase is paid only when every
    /// component succeeded and the amounts reconcile with the frozen total.
    /// Returns true when the target was (or already is) confirmed.
    /// </summary>
    private async Task<bool> TryCompletePlanAsync(
        Guid groupId,
        DateTime indiaLocalNow,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Payments
            .Include(x => x.Order)
            .Include(x => x.Subscription)
            .Where(x => x.PaymentGroupId == groupId)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return false;
        var first = rows[0];
        if (rows.Any(x => x.OrderId != first.OrderId || x.SubscriptionId != first.SubscriptionId || x.CustomerId != first.CustomerId))
        {
            throw new ConflictException("The split payment plan references inconsistent targets.");
        }
        if (rows.Any(x => x.Status != PaymentStatus.Success)) return false;
        var total = first.Order?.PayableAmount
            ?? first.Subscription?.PayableAmount
            ?? throw new InvalidOperationException("The payment plan has no payable target.");
        if (rows.Sum(x => x.Amount) != total)
        {
            throw new ConflictException("The split payment components do not reconcile with the payable amount.");
        }

        if (first.Order is not null)
        {
            if (first.Order.Status != OrderStatus.PendingPayment) return true;
            await ConfirmTargetAsync(first, indiaLocalNow, cancellationToken);
        }
        else if (first.Subscription is not null)
        {
            if (first.Subscription.Status != SubscriptionStatus.PaymentPending) return true;
            await ConfirmTargetAsync(first, indiaLocalNow, cancellationToken);
        }
        else
        {
            return false;
        }

        // Exactly-once outcome events: the first row carries the full
        // outcome (including subscription activation); every other
        // succeeded component gets its own payment event. Callers skip
        // their own AddPaymentOutcomeEvents when the plan confirmed, so no
        // payment key is ever inserted twice.
        AddPaymentOutcomeEvents(first);
        foreach (var sibling in rows.Where(x =>
            x.Id != first.Id && x.Status == PaymentStatus.Success))
        {
            AddPlanComponentSuccessEvent(sibling);
        }
        return true;
    }

    /// <summary>
    /// Per-component success event for a split-plan sibling. Carries only
    /// the payment event (unique per payment row); subscription activation
    /// is emitted once via the first row's full outcome event.
    /// </summary>
    private void AddPlanComponentSuccessEvent(Payment payment)
    {
        var deepLink = payment.Order is not null
            ? $"/orders/{payment.Order.PublicId}"
            : payment.Subscription is not null
                ? $"/subscriptions/{payment.Subscription.PublicId}"
                : $"/payments/{payment.PublicId}";
        notificationEventWriter.Add(new NotificationEventRequest(
            payment.CustomerId,
            NotificationEventTypes.PaymentSucceeded,
            $"payment:{payment.PublicId:N}:succeeded",
            new Dictionary<string, string>
            {
                ["amount"] = payment.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                ["currency"] = payment.Currency,
                ["method"] = payment.Method.ToString(),
                ["paymentId"] = payment.PublicId.ToString(),
                ["message"] = $"Your payment of {payment.Currency} {payment.Amount:0.00} was successful."
            },
            deepLink,
            payment.VerifiedAt));
    }

    /// <summary>
    /// Confirmation that respects split plans: grouped rows confirm only via
    /// <see cref="TryCompletePlanAsync"/>; standalone rows keep legacy behavior.
    /// The caller must already be inside the plan's serializable transaction
    /// with reloaded state.
    /// </summary>
    /// <summary>
    /// Returns true when the purchase is fully confirmed (now or previously).
    /// When a grouped plan confirms, every component's outcome event is
    /// emitted inside <see cref="TryCompletePlanAsync"/> and the caller must
    /// skip its own <see cref="AddPaymentOutcomeEvents"/> to avoid inserting
    /// the same payment event key twice.
    /// </summary>
    private async Task<bool> ConfirmPlanOrTargetAsync(
        Payment payment,
        DateTime indiaLocalNow,
        CancellationToken cancellationToken,
        bool recoverCaptured = false)
    {
        if (!payment.PaymentGroupId.HasValue)
        {
            await ConfirmTargetAsync(payment, indiaLocalNow, cancellationToken, recoverCaptured);
            return true;
        }

        if (recoverCaptured)
        {
            var rows = await dbContext.Payments
                .Where(x => x.PaymentGroupId == payment.PaymentGroupId.Value)
                .ToListAsync(cancellationToken);
            if (rows.All(x => x.Status == PaymentStatus.Success))
            {
                return await TryCompletePlanAsync(payment.PaymentGroupId.Value, indiaLocalNow, cancellationToken);
            }

            return false;
        }

        return await TryCompletePlanAsync(payment.PaymentGroupId.Value, indiaLocalNow, cancellationToken);
    }

    /// <summary>
    /// Failure handling that respects split plans: when a grouped gateway leg
    /// reaches a definitive non-captured terminal state, the held wallet
    /// contribution is restored exactly once before the target fails. The
    /// caller must have already moved the gateway row terminal in-transaction.
    /// </summary>
    private async Task FailPlanOrTargetAsync(Payment payment, CancellationToken cancellationToken)
    {
        if (!payment.PaymentGroupId.HasValue)
        {
            FailTarget(payment);
            return;
        }

        await ReleasePlanWalletAsync(payment.PaymentGroupId.Value, payment.CustomerId, cancellationToken);
        await ReloadPaymentAndTargetAsync(payment, cancellationToken);
        FailTarget(payment);
    }

    /// <summary>
    /// Releases the held wallet contribution of a grouped plan without failing
    /// the target (used when the customer cancels the gateway leg: the plan is
    /// abandoned but the order stays payable for a fresh attempt).
    /// </summary>
    private async Task ReleasePlanWalletIfGroupedAsync(Payment payment, CancellationToken cancellationToken)
    {
        if (!payment.PaymentGroupId.HasValue) return;
        await ReleasePlanWalletAsync(payment.PaymentGroupId.Value, payment.CustomerId, cancellationToken);
    }

    /// <summary>
    /// Restores a split plan's wallet contribution through the existing refund
    /// ledger (full component refund to wallet). Idempotent: the release
    /// refund and ledger rows carry the stable group-derived key, so replays
    /// reuse committed records and never duplicate money movement.
    /// </summary>
    private async Task<bool> ReleasePlanWalletAsync(
        Guid groupId,
        long requestedByUserId,
        CancellationToken cancellationToken)
    {
        var walletRow = await dbContext.Payments
            .Include(x => x.Order)
            .Include(x => x.Subscription)
            .Include(x => x.Refunds)
            .SingleOrDefaultAsync(
                x => x.PaymentGroupId == groupId && x.Method == PaymentMethod.Wallet,
                cancellationToken);
        if (walletRow is null || walletRow.Status != PaymentStatus.Success) return false;

        var releaseKey = $"split-release:{groupId:N}";
        var existingRefund = walletRow.Refunds.SingleOrDefault(x => x.IdempotencyKey == releaseKey);
        if (existingRefund is not null)
        {
            return existingRefund.Status == RefundStatus.Succeeded;
        }

        var now = timeProvider.Now;
        var refund = walletRow.StartRefund(
            walletRow.Amount,
            "Split-plan gateway leg failed; wallet contribution restored.",
            releaseKey,
            requestedByUserId);
        if (walletRow.OrderId is long orderId)
        {
            await walletService.CreditRefundAsync(
                walletRow.CustomerId,
                orderId,
                walletRow.Id,
                walletRow.Amount,
                $"{releaseKey}:wallet",
                cancellationToken);
        }
        else if (walletRow.SubscriptionId is long subscriptionId)
        {
            await walletService.CreditSubscriptionRefundAsync(
                walletRow.CustomerId,
                subscriptionId,
                walletRow.Id,
                walletRow.Amount,
                $"{releaseKey}:wallet",
                cancellationToken);
        }
        else
        {
            throw new InvalidOperationException("The split-plan wallet payment has no payable target.");
        }

        refund.Succeed(now);
        walletRow.CompleteRefund(walletRow.Amount);
        WriteRefundAudit(
            walletRow,
            refund,
            requestedByUserId,
            new RefundPaymentRequest(
                walletRow.Amount,
                "Split-plan gateway leg failed; wallet contribution restored.",
                releaseKey,
                null,
                null));
        return true;
    }

    private sealed record SplitPlan(Guid? GroupId, Payment? WalletRow, Payment? RazorpayRow);

    private void WriteRefundAudit(
        Payment payment,
        Refund refund,
        long requestedByUserId,
        RefundPaymentRequest request)
    {
        dbContext.AddAuditLog(new AuditLog(
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

    private async Task EnsurePreviousAttemptsSafeAsync(
        IReadOnlyList<Payment> payments,
        CancellationToken cancellationToken)
    {
        foreach (var payment in payments)
        {
            if (payment.Method != PaymentMethod.Razorpay ||
                payment.Status is
                    PaymentStatus.Success or
                    PaymentStatus.RefundPending or
                    PaymentStatus.PartiallyRefunded or
                    PaymentStatus.Refunded)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(payment.GatewayOrderId))
            {
                // Never submitted to the gateway (the gateway order was never
                // attached): no capture is possible through this row, so there
                // is nothing to reconcile before a fresh attempt.
                continue;
            }

            var resolution = await ResolveGatewayPaymentAsync(
                payment,
                payment.GatewayPaymentId,
                cancellationToken);
            if (resolution.Outcome != PaymentReconciliationOutcome.DefinitivelyNotCaptured)
            {
                throw new SplitPlanOutcomeAmbiguousException();
            }
        }
    }

    private static IReadOnlyList<PaymentAttemptEvidence> CaptureAttemptEvidence(
        IReadOnlyList<Payment> payments) => payments
        .Select(PaymentAttemptEvidence.From)
        .OrderBy(x => x.PublicId)
        .ToList();

    private async Task<IReadOnlyList<Payment>> RevalidateAttemptEvidenceAsync(
        IQueryable<Payment> query,
        IReadOnlyList<PaymentAttemptEvidence> evidence,
        CancellationToken cancellationToken)
    {
        var currentPayments = await query.ToListAsync(cancellationToken);
        foreach (var currentPayment in currentPayments)
        {
            await dbContext.Entry(currentPayment).ReloadAsync(cancellationToken);
        }

        var currentEvidence = CaptureAttemptEvidence(currentPayments);
        if (!currentEvidence.SequenceEqual(evidence))
        {
            throw new ConflictException(
                "Payment attempts changed while Razorpay evidence was being collected. Retry after reconciliation.");
        }

        return currentPayments;
    }

    /// <summary>
    /// Fresh attempts must not be blocked by split plans that are already
    /// over: every component reached a terminal state that holds no funds
    /// (Failed/Cancelled/Expired gateway legs plus fully released — Refunded
    /// — wallet legs). Any group still holding funds (Success,
    /// RefundPending, PartiallyRefunded) or awaiting a gateway outcome
    /// (Initiated/Pending) keeps blocking, as do standalone refunded
    /// payments, whose existing refund semantics are preserved.
    /// </summary>
    private static IReadOnlyList<Payment> WithoutAbandonedPlanComponents(
        IReadOnlyList<Payment> payments)
    {
        if (payments.All(x => !x.PaymentGroupId.HasValue))
        {
            return payments;
        }

        var abandoned = payments
            .Where(x => x.PaymentGroupId.HasValue)
            .GroupBy(x => x.PaymentGroupId!.Value)
            .Where(group => group.All(x => x.Status is
                PaymentStatus.Failed or
                PaymentStatus.Cancelled or
                PaymentStatus.Expired or
                PaymentStatus.Refunded))
            .Select(group => group.Key)
            .ToHashSet();
        if (abandoned.Count == 0)
        {
            return payments;
        }

        return payments
            .Where(x => !x.PaymentGroupId.HasValue || !abandoned.Contains(x.PaymentGroupId.Value))
            .ToList();
    }

    private static void EnsureNoActivePayment(
        IReadOnlyList<Payment> payments,
        string targetName)
    {
        if (payments.Any(x => x.Status is PaymentStatus.Initiated or PaymentStatus.Pending))
        {
            throw new ConflictException($"The {targetName} already has an active payment.");
        }

        EnsureNoCompletedPayment(payments, targetName);
    }

    private static void EnsureNoCompletedPayment(
        IReadOnlyList<Payment> payments,
        string targetName)
    {
        EnsureNoLiveSplitPlanAttempt(payments, targetName);
        if (payments.Any(x => x.Status is
            PaymentStatus.Success or
            PaymentStatus.RefundPending or
            PaymentStatus.PartiallyRefunded or
            PaymentStatus.Refunded))
        {
            throw new ConflictException($"The {targetName} already has a completed payment.");
        }
    }

    /// <summary>
    /// A split-plan group with a succeeded component and a still-live gateway
    /// leg is an in-progress attempt, not a completed purchase: report it
    /// accurately instead of claiming a completed payment. Fully released
    /// groups (Failed/Cancelled/Expired/Refunded legs) are filtered by
    /// <see cref="WithoutAbandonedPlanComponents"/> before this runs, so
    /// definitively failed plans remain retryable here.
    /// </summary>
    private static void EnsureNoLiveSplitPlanAttempt(
        IReadOnlyList<Payment> payments,
        string targetName)
    {
        var live = payments
            .Where(x => x.PaymentGroupId.HasValue)
            .GroupBy(x => x.PaymentGroupId!.Value)
            .Any(group => group.Any(x => x.Status == PaymentStatus.Success) &&
                group.Any(x => x.Status is PaymentStatus.Initiated or PaymentStatus.Pending));
        if (live)
        {
            throw new SplitPlanAttemptInProgressException(targetName);
        }
    }

    private static void EnsureOrderCanStartPayment(Order order)
    {
        if (order.Status != OrderStatus.PendingPayment)
        {
            throw new ConflictException(
                $"The order changed to status '{order.Status}' while payment safety checks were running.");
        }
    }

    private static void EnsureSubscriptionCanStartPayment(
        Subscription subscription,
        bool allowFailed)
    {
        var allowed = subscription.Status == SubscriptionStatus.PaymentPending ||
            allowFailed && subscription.Status == SubscriptionStatus.PaymentFailed;
        if (!allowed)
        {
            throw new ConflictException(
                $"The subscription changed to status '{subscription.Status}' while payment safety checks were running.");
        }
    }

    private sealed record PaymentAttemptEvidence(
        Guid PublicId,
        Guid? PaymentGroupId,
        PaymentMethod Method,
        PaymentStatus Status,
        decimal Amount,
        string Currency,
        string? GatewayOrderId,
        string? GatewayPaymentId)
    {
        public static PaymentAttemptEvidence From(Payment payment) => new(
            payment.PublicId,
            payment.PaymentGroupId,
            payment.Method,
            payment.Status,
            payment.Amount,
            payment.Currency,
            payment.GatewayOrderId,
            payment.GatewayPaymentId);
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

    private async Task<IPaymentGateway> GatewayForAsync(
        PaymentMethod method,
        CancellationToken cancellationToken)
    {
        if (method == PaymentMethod.Razorpay)
        {
            if (await RazorpayConfiguredAsync(cancellationToken))
            {
                return gateway;
            }

            throw new BusinessRuleException(
                "Razorpay is unavailable because valid credentials are not configured.");
        }

        return method switch
        {
            PaymentMethod.Development when mockGateway is not null => mockGateway,
            PaymentMethod.Development when string.Equals(
                gateway.ProviderName, "Mock", StringComparison.OrdinalIgnoreCase) => gateway,
            PaymentMethod.Development => throw new BusinessRuleException(
                "Development payment is unavailable because the Mock provider is not configured."),
            PaymentMethod.Wallet => throw new InvalidOperationException(
                "Wallet payments do not use a gateway."),
            _ => throw new BusinessRuleException("The selected payment method is unavailable.")
        };
    }

    private async Task<bool> RazorpayConfiguredAsync(CancellationToken cancellationToken) =>
        options.IsRazorpay &&
        (options.IsRazorpayConfigured ||
            (integrationSettings is not null &&
                (await integrationSettings.GetRazorpayAsync(cancellationToken)).IsConfigured));

    private async Task<string?> PublicKeyForAsync(PaymentMethod method, CancellationToken cancellationToken) =>
        method == PaymentMethod.Razorpay
            ? await (await GatewayForAsync(PaymentMethod.Razorpay, cancellationToken)).GetPublicKeyIdAsync(cancellationToken)
            : null;

    private string ProviderFor(PaymentMethod method) => method switch
    {
        PaymentMethod.Wallet => "Wallet",
        PaymentMethod.Razorpay => "Razorpay",
        PaymentMethod.Development => "Mock",
        _ => throw new BusinessRuleException("The selected payment method is unavailable.")
    };

    private async Task<GatewayResolution> ResolveGatewayPaymentAsync(
        Payment payment,
        string? gatewayPaymentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payment.GatewayOrderId))
        {
            throw new ConflictException("The gateway order reference is unavailable.");
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(gatewayPaymentId))
            {
                var status = await (await GatewayForAsync(payment.Method, cancellationToken)).GetPaymentStatusAsync(
                    gatewayPaymentId,
                    cancellationToken);
                EnsureGatewayIdentity(payment, gatewayPaymentId, status);
                EnsureGatewayFinancials(payment, status.AmountMinor, status.Currency);
                return new GatewayResolution(ClassifyGatewayStatus(status), status);
            }

            var discovered = await (await GatewayForAsync(payment.Method, cancellationToken)).GetPaymentsForOrderAsync(
                payment.GatewayOrderId,
                cancellationToken);
            if (!string.Equals(
                    discovered.GatewayOrderId,
                    payment.GatewayOrderId,
                    StringComparison.Ordinal) ||
                discovered.Payments.Any(x =>
                    string.IsNullOrWhiteSpace(x.GatewayPaymentId) ||
                    !string.Equals(
                        x.GatewayOrderId,
                        payment.GatewayOrderId,
                        StringComparison.Ordinal)) ||
                discovered.Payments
                    .GroupBy(x => x.GatewayPaymentId, StringComparer.Ordinal)
                    .Any(x => x.Count() > 1))
            {
                return AmbiguousResolution(payment);
            }

            if (discovered.Payments.Count == 0)
            {
                return AmbiguousResolution(payment, "no_payments");
            }

            if (discovered.Payments.Any(x =>
                    x.AmountMinor != ToMinorUnits(payment.Amount) ||
                    !string.Equals(
                        x.Currency,
                        payment.Currency,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return AmbiguousResolution(payment);
            }

            var classified = discovered.Payments
                .Select(x => new GatewayResolution(ClassifyGatewayStatus(x), x))
                .ToArray();
            if (classified.Any(x =>
                    x.Outcome == PaymentReconciliationOutcome.Ambiguous))
            {
                return AmbiguousResolution(payment);
            }

            var captured = classified
                .Where(x => x.Outcome == PaymentReconciliationOutcome.Captured)
                .ToArray();
            if (captured.Length > 1)
            {
                return AmbiguousResolution(payment);
            }
            if (captured.Length == 1)
            {
                return captured[0];
            }

            var pending = classified.FirstOrDefault(x =>
                x.Outcome == PaymentReconciliationOutcome.Pending);
            return pending ?? new GatewayResolution(
                PaymentReconciliationOutcome.DefinitivelyNotCaptured,
                classified[0].Status);
        }
        catch (ConflictException)
        {
            return AmbiguousResolution(payment);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            TaskCanceledException or
            JsonException or
            InvalidOperationException or
            FormatException)
        {
            return AmbiguousResolution(payment, exception.Message);
        }
    }

    private static PaymentReconciliationOutcome ClassifyGatewayStatus(
        GatewayPaymentStatusResult status)
    {
        if (string.Equals(status.Status, "captured", StringComparison.OrdinalIgnoreCase))
        {
            return status.IsSuccessful && !status.IsTerminalFailure
                ? PaymentReconciliationOutcome.Captured
                : PaymentReconciliationOutcome.Ambiguous;
        }
        if (string.Equals(status.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return !status.IsSuccessful && status.IsTerminalFailure
                ? PaymentReconciliationOutcome.DefinitivelyNotCaptured
                : PaymentReconciliationOutcome.Ambiguous;
        }
        if (string.Equals(status.Status, "created", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.Status, "authorized", StringComparison.OrdinalIgnoreCase))
        {
            return !status.IsSuccessful && !status.IsTerminalFailure
                ? PaymentReconciliationOutcome.Pending
                : PaymentReconciliationOutcome.Ambiguous;
        }

        // Refunded proves prior capture; unknown statuses are unsafe until explicitly supported.
        return PaymentReconciliationOutcome.Ambiguous;
    }

    private static GatewayResolution AmbiguousResolution(
        Payment payment,
        string? status = null) =>
        new(PaymentReconciliationOutcome.Ambiguous, EmptyGatewayStatus(payment, status));

    private static GatewayPaymentStatusResult EmptyGatewayStatus(
        Payment payment,
        string? status = null) =>
        new("", payment.GatewayOrderId ?? "", status ?? "ambiguous", 0, payment.Currency, false, false);

    private sealed record GatewayResolution(
        PaymentReconciliationOutcome Outcome,
        GatewayPaymentStatusResult Status);

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
