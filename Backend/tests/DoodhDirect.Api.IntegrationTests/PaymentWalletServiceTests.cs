using System.Net;
using DoodhDirect.Application.Common;
using DoodhDirect.Infrastructure.Notifications;
using Microsoft.AspNetCore.DataProtection;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Notifications;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Deliveries;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using DoodhDirect.Infrastructure.Wallets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class PaymentWalletServiceTests
{
    [Theory]
    [InlineData(500, 160, 340)]
    [InlineData(160, 160, 0)]
    public async Task WalletPayment_WithSufficientOrExactBalance_DebitsLedgerAndConfirmsOrder(
        decimal startingBalance,
        decimal payableAmount,
        decimal expectedBalance)
    {
        await using var harness = await PaymentHarness.CreateAsync(payableAmount);
        await harness.SeedWalletAsync(startingBalance, "topup-1");

        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Wallet),
            "payment-1",
            CancellationToken.None);
        var replay = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Wallet),
            "payment-1",
            CancellationToken.None);

        Assert.Equal(created.PublicId, replay.PublicId);
        Assert.Equal(PaymentStatus.Success, created.Status);
        Assert.Equal(OrderStatus.Confirmed, (await harness.Db.Orders.SingleAsync()).Status);

        var delivery = Assert.Single(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
        Assert.Equal(harness.Order.Id, delivery.OrderId);
        Assert.Equal(harness.Customer.Id, delivery.CustomerId);
        Assert.Equal(1, delivery.BranchId);
        Assert.Equal(DeliverySourceType.OneTimeOrder, delivery.SourceType);
        Assert.Equal(DeliveryStatus.ReadyForAssignment, delivery.Status);
        Assert.Equal(harness.TimeProvider.Today, delivery.ScheduledDate);

        var wallet = await harness.Db.Wallets.SingleAsync();
        var entries = await harness.Db.WalletTransactions.OrderBy(x => x.Id).ToListAsync();
        var expectedOccurredAt = new DateTime(2026, 8, 16, 7, 30, 0, DateTimeKind.Unspecified);
        Assert.Equal(expectedBalance, wallet.Balance);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal(expectedOccurredAt, entry.OccurredAt);
            Assert.Equal(DateTimeKind.Unspecified, entry.OccurredAt.Kind);
        });
        Assert.Equal(WalletTransactionType.OrderDebit, entries[1].Type);
        Assert.Equal(startingBalance, entries[1].BalanceBefore);
        Assert.Equal(-payableAmount, entries[1].Amount);
        Assert.Equal(expectedBalance, entries[1].BalanceAfter);
    }

    [Theory]
    [InlineData(340, 800, 460)]
    [InlineData(0, 100, 100)]
    public async Task WalletPayment_WithInsufficientBalance_ReturnsBusinessErrorWithoutFinancialSideEffects(
        decimal startingBalance,
        decimal payableAmount,
        decimal expectedShortfall)
    {
        await using var harness = await PaymentHarness.CreateAsync(payableAmount);
        if (startingBalance > 0)
        {
            await harness.SeedWalletAsync(startingBalance, "topup-1");
        }

        var exception = await Assert.ThrowsAsync<InsufficientWalletBalanceException>(() =>
            harness.PaymentService.CreateAsync(
                harness.Customer.Id,
                new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Wallet),
                "payment-insufficient-1",
                CancellationToken.None));

        Assert.Equal("INSUFFICIENT_WALLET_BALANCE", exception.Code);
        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(startingBalance, exception.AvailableBalance);
        Assert.Equal(payableAmount, exception.RequiredAmount);
        Assert.Equal(expectedShortfall, exception.Shortfall);
        Assert.Contains($"₹{expectedShortfall:0.##}", exception.Message);

        await harness.AssertInsufficientAttemptDidNotPersistAsync(startingBalance);
        Assert.Empty(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task WalletPayment_RepeatedInsufficientAttempts_CreateNoFinancialRecords()
    {
        await using var harness = await PaymentHarness.CreateAsync(payableAmount: 800m);
        await harness.SeedWalletAsync(340m, "topup-1");

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var exception = await Assert.ThrowsAsync<InsufficientWalletBalanceException>(() =>
                harness.PaymentService.CreateAsync(
                    harness.Customer.Id,
                    new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Wallet),
                    $"payment-insufficient-{attempt}",
                    CancellationToken.None));

            Assert.Equal(460m, exception.Shortfall);
            await harness.AssertInsufficientAttemptDidNotPersistAsync(expectedBalance: 340m);
            Assert.Empty(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
        }
    }

    [Fact]
    public async Task RazorpayPayment_BelowOneRupee_IsRejectedBeforeGatewayOrderCreation()
    {
        var options = Options.Create(new PaymentOptions
        {
            Provider = "Razorpay",
            Currency = "INR",
            RazorpayKeyId = "rzp_test_public",
            RazorpayKeySecret = "api-secret",
            MockSigningSecret = "unused"
        });
        await using var harness = await PaymentHarness.CreateAsync(
            payableAmount: 0.99m,
            paymentOptions: options,
            gateway: new TestRazorpayGateway(options));

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.PaymentService.CreateAsync(
                harness.Customer.Id,
                new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
                "payment-below-gateway-minimum",
                CancellationToken.None));

        Assert.Equal(
            "The payment amount must be at least 100 paise and representable by the gateway.",
            exception.Message);
        Assert.Equal(OrderStatus.PendingPayment, (await harness.Db.Orders.SingleAsync()).Status);
        Assert.Equal(PaymentStatus.Initiated, (await harness.Db.Payments.SingleAsync()).Status);
    }

    [Fact]
    public async Task RazorpayGateway_NonSuccessResponse_PreservesOnlySanitizedProviderDiagnostic()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":\"BAD_REQUEST_ERROR\",\"description\":\"Invalid   key_id\\nvalue\",\"metadata\":{\"secret\":\"do-not-return\"}}}")
        });
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.razorpay.com/v1/")
        };
        var gateway = new RazorpayPaymentGateway(
            client,
            Options.Create(new PaymentOptions
            {
                Provider = "Razorpay",
                Currency = "INR",
                RazorpayKeyId = "rzp_test_public",
                RazorpayKeySecret = "api-secret"
            }));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            gateway.CreateOrderAsync(
                new GatewayOrderRequest(
                    Guid.NewGuid(), "DD-TEST", 100, "INR", DateTime.UtcNow.AddMinutes(15)),
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("HTTP 400", exception.Message);
        Assert.Contains("Provider code: BAD_REQUEST_ERROR", exception.Message);
        Assert.Contains("Provider description: Invalid key_id value", exception.Message);
        Assert.DoesNotContain("do-not-return", exception.Message);
        Assert.DoesNotContain("api-secret", exception.Message);
    }

    [Fact]
    public async Task RazorpayCreateFailure_IsPersistedAndMappedToGenericBusinessRule()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":\"AUTHENTICATION_ERROR\",\"description\":\"Invalid API key\"}}")
        });
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.razorpay.com/v1/")
        };
        var options = Options.Create(new PaymentOptions
        {
            Provider = "Razorpay",
            Currency = "INR",
            RazorpayKeyId = "rzp_test_public",
            RazorpayKeySecret = "api-secret",
            MockSigningSecret = "unused"
        });
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: new RazorpayPaymentGateway(client, options));

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.PaymentService.CreateAsync(
                harness.Customer.Id,
                new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
                "razorpay-401-create",
                CancellationToken.None));

        Assert.Equal("The payment gateway could not create the payment order.", exception.Message);
        var payment = await harness.Db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("GATEWAY_ORDER_FAILED", payment.FailureCode);
        Assert.Contains("HTTP 401", payment.FailureMessage);
        Assert.Contains("AUTHENTICATION_ERROR", payment.FailureMessage);
        Assert.DoesNotContain("api-secret", payment.FailureMessage);
    }

    [Fact]
    public async Task RazorpayGateway_NonJsonFailure_UsesStatusOnlyDiagnostic()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream failure with sensitive detail")
        });
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.razorpay.com/v1/")
        };
        var gateway = new RazorpayPaymentGateway(
            client,
            Options.Create(new PaymentOptions
            {
                Provider = "Razorpay",
                Currency = "INR",
                RazorpayKeyId = "rzp_test_public",
                RazorpayKeySecret = "api-secret"
            }));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            gateway.CreateOrderAsync(
                new GatewayOrderRequest(
                    Guid.NewGuid(), "DD-TEST", 100, "INR", DateTime.UtcNow.AddMinutes(15)),
                CancellationToken.None));

        Assert.Equal("Razorpay request failed with HTTP 502.", exception.Message);
        Assert.DoesNotContain("sensitive detail", exception.Message);
    }

    [Fact]
    public async Task RazorpayWebhook_WithoutWebhookSecret_FailsClosed()
    {
        var options = Options.Create(new PaymentOptions
        {
            Provider = "Razorpay",
            Currency = "INR",
            RazorpayKeyId = "rzp_test_public",
            RazorpayKeySecret = "api-secret",
            RazorpayWebhookSecret = null,
            MockSigningSecret = "unused"
        });
        var gateway = new RazorpayPaymentGateway(new HttpClient(), options);

        Assert.False(await gateway.VerifyWebhookSignatureAsync("{}"u8, "any-signature", CancellationToken.None));
    }

    [Fact]
    public async Task RazorpayVerification_RequiresSignatureThenIndependentGatewayConfirmation()
    {
        var options = Options.Create(new PaymentOptions
        {
            Provider = "Razorpay",
            Currency = "INR",
            RazorpayKeyId = "rzp_test_public",
            RazorpayKeySecret = "api-secret",
            MockSigningSecret = "unused"
        });
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
            "payment-1",
            CancellationToken.None);

        var invalid = new VerifyPaymentRequest(
            created.PublicId, created.GatewayOrderId!, $"pay_test_{created.PublicId:N}", "invalid");
        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.PaymentService.VerifyAsync(harness.Customer.Id, invalid, CancellationToken.None));

        var result = await harness.PaymentService.VerifyAsync(
            harness.Customer.Id,
            invalid with { Signature = "test_verified" },
            CancellationToken.None);

        Assert.Equal(PaymentStatus.Success, result.Status);
        Assert.Equal($"pay_test_{created.PublicId:N}", result.GatewayPaymentId);
        Assert.Equal(OrderStatus.Confirmed, (await harness.Db.Orders.SingleAsync()).Status);

        var delivery = Assert.Single(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
        Assert.Equal(harness.Order.Id, delivery.OrderId);
        Assert.Equal(DeliverySourceType.OneTimeOrder, delivery.SourceType);
        Assert.Equal(DeliveryStatus.ReadyForAssignment, delivery.Status);

        var replay = await harness.PaymentService.VerifyAsync(
            harness.Customer.Id,
            invalid with { Signature = "test_verified" },
            CancellationToken.None);
        Assert.Equal(result.PublicId, replay.PublicId);
        Assert.Single(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RazorpayCancellation_DefinitiveFailure_CancelsWithoutCreatingDelivery()
    {
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
            "cancel-payment-failed",
            CancellationToken.None);
        gateway.OrderPaymentsResult = new GatewayOrderPaymentsResult(
            created.GatewayOrderId!,
            [GatewayStatus(created, "failed", "failed", false, true)]);

        var cancelled = await harness.PaymentService.CancelAsync(
            harness.Customer.Id, created.PublicId, CancellationToken.None);
        var replay = await harness.PaymentService.CancelAsync(
            harness.Customer.Id, created.PublicId, CancellationToken.None);

        Assert.Equal(PaymentStatus.Cancelled, cancelled.Status);
        Assert.Equal(PaymentStatus.Cancelled, replay.Status);
        Assert.Equal(OrderStatus.PendingPayment, (await harness.Db.Orders.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.NotificationEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RazorpayCancellation_ExpiredDefinitiveFailure_RemainsExpiredWithoutMutation()
    {
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
            "cancel-expired-payment-failed",
            CancellationToken.None);
        var payment = await harness.Db.Payments.SingleAsync();
        payment.Expire(new DateTime(2026, 8, 16, 7, 45, 0));
        harness.Order.FailPayment();
        await harness.Db.SaveChangesAsync();
        gateway.OrderPaymentsResult = new GatewayOrderPaymentsResult(
            created.GatewayOrderId!,
            [GatewayStatus(created, "failed", "failed", false, true)]);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.PaymentService.CancelAsync(
                harness.Customer.Id, created.PublicId, CancellationToken.None));

        Assert.Equal("The payment was not captured and is already expired.", exception.Message);
        Assert.Equal(PaymentStatus.Expired, (await harness.Db.Payments.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(OrderStatus.PaymentFailed, (await harness.Db.Orders.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.NotificationEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RazorpayCancellation_ExpiredValidatedCapture_RecoversOrderExactlyOnce()
    {
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
            "cancel-expired-payment-captured",
            CancellationToken.None);
        var payment = await harness.Db.Payments.SingleAsync();
        payment.Expire(new DateTime(2026, 8, 16, 7, 45, 0));
        harness.Order.FailPayment();
        await harness.Db.SaveChangesAsync();
        var captured = GatewayStatus(created, "captured", "captured", true, false);
        gateway.OrderPaymentsResult = new GatewayOrderPaymentsResult(
            created.GatewayOrderId!,
            [captured]);

        var recovered = await harness.PaymentService.CancelAsync(
            harness.Customer.Id, created.PublicId, CancellationToken.None);
        gateway.DirectStatusResult = captured;
        var replay = await harness.PaymentService.CancelAsync(
            harness.Customer.Id, created.PublicId, CancellationToken.None);

        Assert.Equal(PaymentStatus.Success, recovered.Status);
        Assert.Equal(PaymentStatus.Success, replay.Status);
        Assert.Equal(OrderStatus.Confirmed, (await harness.Db.Orders.AsNoTracking().SingleAsync()).Status);
        Assert.Single(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.DeliveryOtps.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.PaymentSucceeded)
            .ToListAsync());
    }

    [Fact]
    public async Task RazorpayCancellation_ValidatedCapture_CompletesOrderExactlyOnce()
    {
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
            "cancel-payment-captured",
            CancellationToken.None);
        gateway.OrderPaymentsResult = new GatewayOrderPaymentsResult(
            created.GatewayOrderId!,
            [GatewayStatus(created, "captured", "captured", true, false)]);

        var completed = await harness.PaymentService.CancelAsync(
            harness.Customer.Id, created.PublicId, CancellationToken.None);
        var replay = await harness.PaymentService.CancelAsync(
            harness.Customer.Id, created.PublicId, CancellationToken.None);

        Assert.Equal(PaymentStatus.Success, completed.Status);
        Assert.Equal(PaymentStatus.Success, replay.Status);
        Assert.Equal(OrderStatus.Confirmed, (await harness.Db.Orders.AsNoTracking().SingleAsync()).Status);
        Assert.Single(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.DeliveryOtps.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.PaymentSucceeded)
            .ToListAsync());
    }

    [Theory]
    [InlineData("created", false, false)]
    [InlineData("authorized", false, false)]
    [InlineData("captured", false, false)]
    [InlineData("failed", true, false)]
    [InlineData("refunded", false, false)]
    [InlineData("future_status", false, false)]
    public async Task RazorpayCancellation_UnresolvedEvidence_BlocksWithoutMutation(
        string status,
        bool isSuccessful,
        bool isTerminalFailure)
    {
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
            $"cancel-payment-{status}-{isSuccessful}-{isTerminalFailure}",
            CancellationToken.None);
        gateway.OrderPaymentsResult = new GatewayOrderPaymentsResult(
            created.GatewayOrderId!,
            [GatewayStatus(created, "unresolved", status, isSuccessful, isTerminalFailure)]);

        await Assert.ThrowsAsync<ConflictException>(() => harness.PaymentService.CancelAsync(
            harness.Customer.Id, created.PublicId, CancellationToken.None));

        Assert.Equal(PaymentStatus.Pending, (await harness.Db.Payments.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(OrderStatus.PendingPayment, (await harness.Db.Orders.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.NotificationEvents.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("malformed")]
    [InlineData("empty")]
    public async Task RazorpayCancellation_UnverifiableEvidence_BlocksWithoutMutation(string scenario)
    {
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
            $"cancel-payment-{scenario}",
            CancellationToken.None);
        gateway.OrderPaymentsResult = scenario == "empty"
            ? new GatewayOrderPaymentsResult(created.GatewayOrderId!, [])
            : null;
        gateway.OrderPaymentsException = scenario switch
        {
            "transport" => new HttpRequestException("Razorpay unavailable."),
            "malformed" => new System.Text.Json.JsonException("Invalid Razorpay JSON."),
            _ => null
        };

        await Assert.ThrowsAsync<ConflictException>(() => harness.PaymentService.CancelAsync(
            harness.Customer.Id, created.PublicId, CancellationToken.None));

        Assert.Equal(PaymentStatus.Pending, (await harness.Db.Payments.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(OrderStatus.PendingPayment, (await harness.Db.Orders.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task PaymentReadsAreOwnershipScoped()
    {
        await using var harness = await PaymentHarness.CreateAsync();
        var payment = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Development),
            "payment-1",
            CancellationToken.None);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.PaymentService.GetAsync(
                harness.OtherCustomer.Id, payment.PublicId, bypassOwnership: false, CancellationToken.None));

        var administratorRead = await harness.PaymentService.GetAsync(
            harness.OtherCustomer.Id, payment.PublicId, bypassOwnership: true, CancellationToken.None);
        Assert.Equal(payment.PublicId, administratorRead.PublicId);
    }

    [Fact]
    public async Task WalletAdjustment_IsIdempotentAndWritesAuditMetadata()
    {
        await using var harness = await PaymentHarness.CreateAsync();
        var request = new WalletAdjustmentRequest(
            45m, "Manual correction", "adjust-1", "203.0.113.10", "DoodhDirectTests/1.0");

        var first = await harness.WalletService.AdjustAsync(
            harness.Administrator.Id, harness.Customer.PublicId, request, CancellationToken.None);
        var replay = await harness.WalletService.AdjustAsync(
            harness.Administrator.Id, harness.Customer.PublicId, request, CancellationToken.None);

        Assert.Equal(first.PublicId, replay.PublicId);
        Assert.Equal(45m, (await harness.Db.Wallets.SingleAsync()).Balance);
        Assert.Single(await harness.Db.WalletTransactions.ToListAsync());

        var audit = await harness.Db.AuditLogs.SingleAsync();
        Assert.Equal("WalletAdjusted", audit.Action);
        Assert.Equal("203.0.113.10", audit.IPAddress);
        Assert.Equal("DoodhDirectTests/1.0", audit.UserAgent);
        Assert.Equal("Manual correction", audit.Reason);
    }

    [Fact]
    public async Task WalletRefund_CreditsWalletMarksRefundedAndWritesAudit()
    {
        await using var harness = await PaymentHarness.CreateAsync();
        await harness.SeedWalletAsync(100m, "topup-1");
        var payment = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Wallet),
            "payment-1", CancellationToken.None);

        var refund = await harness.PaymentService.RefundAsync(
            harness.Administrator.Id,
            payment.PublicId,
            new RefundPaymentRequest(40m, "Customer cancellation", "refund-1", "203.0.113.11", "Tests/1.0"),
            CancellationToken.None);

        Assert.Equal(RefundStatus.Succeeded, refund.Status);
        var storedPayment = await harness.Db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.PartiallyRefunded, storedPayment.Status);
        Assert.Equal(40m, storedPayment.RefundedAmount);
        Assert.Equal(40m, (await harness.Db.Wallets.SingleAsync()).Balance);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "PaymentRefundRequested");
    }

    [Fact]
    public async Task InvalidWebhookSignature_DoesNotPersistReceipt()
    {
        var options = Options.Create(new PaymentOptions
        {
            Provider = "Razorpay",
            Currency = "INR",
            RazorpayKeyId = "rzp_test_public",
            RazorpayKeySecret = "api-secret",
            MockSigningSecret = "unused"
        });
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("The invalid signature must be rejected before network access.")))
        {
            BaseAddress = new Uri("https://api.razorpay.com/v1/")
        };
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: new RazorpayPaymentGateway(client, options));
        var payload = "{\"event\":\"payment.captured\",\"id\":\"evt_1\"}"u8.ToArray();

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.PaymentService.ProcessWebhookAsync(payload, "not-a-signature", CancellationToken.None));

        Assert.Empty(await harness.Db.PaymentWebhooks.ToListAsync());
    }

    [Fact]
    public async Task RazorpayWebhook_WithUnavailableOtpTransport_ConfirmsOnceAndRetriesSameOtp()
    {
        var options = Options.Create(new PaymentOptions
        {
            Provider = "Razorpay",
            Currency = "INR",
            RazorpayKeyId = "rzp_test_public",
            RazorpayKeySecret = "api-secret",
            MockSigningSecret = "unused"
        });
        var gateway = new TestRazorpayGateway(options);
        var otpDelivery = new CapturingOtpDeliveryService { FailNextSend = true };
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway,
            otpDelivery: otpDelivery);
        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
            "razorpay-webhook-otp-transport-failure",
            CancellationToken.None);
        gateway.WebhookEvent = new GatewayWebhookEvent(
            "evt_payment_captured_1",
            "payment.captured",
            created.GatewayOrderId,
            $"pay_test_{created.PublicId:N}",
            GatewayRefundId: null,
            Status: "captured",
            AmountMinor: 10000,
            Currency: "INR");
        var payload = "{\"event\":\"payment.captured\",\"id\":\"evt_payment_captured_1\"}"u8.ToArray();

        await harness.PaymentService.ProcessWebhookAsync(
            payload, "test_webhook_verified", CancellationToken.None);
        await harness.PaymentService.ProcessWebhookAsync(
            payload, "test_webhook_verified", CancellationToken.None);

        var result = await harness.PaymentService.GetAsync(
            harness.Customer.Id,
            created.PublicId,
            bypassOwnership: false,
            CancellationToken.None);
        await harness.AssertConfirmedWithPendingOtpAsync(result);
        Assert.Single(await harness.Db.Payments.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.Orders.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.DeliveryOtps.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.DeliveryOtpIssued)
            .ToListAsync());
        var receipt = Assert.Single(await harness.Db.PaymentWebhooks.AsNoTracking().ToListAsync());
        Assert.Equal(PaymentWebhookStatus.Processed, receipt.Status);
    }

    [Fact]
    public async Task WalletPayment_WithUnavailableOtpTransport_ConfirmsAndLeavesOtpPending()
    {
        await using var harness = await PaymentHarness.CreateAsync(
            otpDelivery: new CapturingOtpDeliveryService { FailNextSend = true });
        await harness.SeedWalletAsync(100m, "topup-otp-transport");

        var result = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Wallet),
            "wallet-otp-transport-failure",
            CancellationToken.None);

        await harness.AssertConfirmedWithPendingOtpAsync(result);
    }

    [Fact]
    public async Task DevelopmentPayment_WithUnavailableOtpTransport_ConfirmsAndLeavesOtpPending()
    {
        await using var harness = await PaymentHarness.CreateAsync(
            otpDelivery: new CapturingOtpDeliveryService { FailNextSend = true });

        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Development),
            "development-otp-transport-failure",
            CancellationToken.None);
        var result = await harness.PaymentService.CompleteDevelopmentAsync(
            harness.Customer.Id,
            created.PublicId,
            CancellationToken.None);

        await harness.AssertConfirmedWithPendingOtpAsync(result);
    }

    [Fact]
    public async Task RazorpayVerification_WithUnavailableOtpTransport_ConfirmsAndLeavesOtpPending()
    {
        var options = Options.Create(new PaymentOptions
        {
            Provider = "Razorpay",
            Currency = "INR",
            RazorpayKeyId = "rzp_test_public",
            RazorpayKeySecret = "api-secret",
            MockSigningSecret = "unused"
        });
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: new TestRazorpayGateway(options),
            otpDelivery: new CapturingOtpDeliveryService { FailNextSend = true });

        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
            "razorpay-otp-transport-failure",
            CancellationToken.None);
        var result = await harness.PaymentService.VerifyAsync(
            harness.Customer.Id,
            new VerifyPaymentRequest(
                created.PublicId,
                created.GatewayOrderId!,
                $"pay_test_{created.PublicId:N}",
                "test_verified"),
            CancellationToken.None);

        await harness.AssertConfirmedWithPendingOtpAsync(result);
    }

    [Theory]
    [InlineData("captured", true, false, PaymentReconciliationOutcome.Captured, PaymentStatus.Success)]
    [InlineData("failed", false, true, PaymentReconciliationOutcome.DefinitivelyNotCaptured, PaymentStatus.Pending)]
    [InlineData("created", false, false, PaymentReconciliationOutcome.Pending, PaymentStatus.Pending)]
    [InlineData("authorized", false, false, PaymentReconciliationOutcome.Pending, PaymentStatus.Pending)]
    [InlineData("refunded", false, false, PaymentReconciliationOutcome.Ambiguous, PaymentStatus.Pending)]
    [InlineData("future_status", false, false, PaymentReconciliationOutcome.Ambiguous, PaymentStatus.Pending)]
    [InlineData("captured", false, false, PaymentReconciliationOutcome.Ambiguous, PaymentStatus.Pending)]
    [InlineData("failed", true, false, PaymentReconciliationOutcome.Ambiguous, PaymentStatus.Pending)]
    [InlineData("authorized", false, true, PaymentReconciliationOutcome.Ambiguous, PaymentStatus.Pending)]
    public async Task RazorpayReconciliation_ClassifiesDiscoveredStatusAndMutatesOnlyValidatedCapture(
        string gatewayStatus,
        bool isSuccessful,
        bool isTerminalFailure,
        PaymentReconciliationOutcome expectedOutcome,
        PaymentStatus expectedPaymentStatus)
    {
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
            $"reconcile-status-{gatewayStatus}-{isSuccessful}-{isTerminalFailure}",
            CancellationToken.None);
        gateway.OrderPaymentsResult = new GatewayOrderPaymentsResult(
            created.GatewayOrderId!,
            [GatewayStatus(created, "status", gatewayStatus, isSuccessful, isTerminalFailure)]);

        var result = await harness.PaymentService.ReconcileAsync(
            harness.Administrator.Id,
            created.PublicId,
            bypassOwnership: true,
            CancellationToken.None);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedPaymentStatus, result.Payment.Status);
        Assert.Equal(
            expectedPaymentStatus == PaymentStatus.Success ? OrderStatus.Confirmed : OrderStatus.PendingPayment,
            (await harness.Db.Orders.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(
            expectedPaymentStatus == PaymentStatus.Success ? 1 : 0,
            await harness.Db.Deliveries.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task RazorpayReconciliation_OneCaptureAmongFailures_RecoversExactlyOnce()
    {
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
            "reconcile-one-capture",
            CancellationToken.None);
        var captured = GatewayStatus(created, "captured", "captured", true, false);
        gateway.OrderPaymentsResult = new GatewayOrderPaymentsResult(
            created.GatewayOrderId!,
            [
                GatewayStatus(created, "failed-1", "failed", false, true),
                captured,
                GatewayStatus(created, "failed-2", "failed", false, true)
            ]);

        var first = await harness.PaymentService.ReconcileAsync(
            harness.Administrator.Id, created.PublicId, true, CancellationToken.None);
        gateway.DirectStatusResult = captured;
        var replay = await harness.PaymentService.ReconcileAsync(
            harness.Administrator.Id, created.PublicId, true, CancellationToken.None);

        Assert.Equal(PaymentReconciliationOutcome.Captured, first.Outcome);
        Assert.Equal(PaymentReconciliationOutcome.Captured, replay.Outcome);
        Assert.Equal(PaymentStatus.Success, replay.Payment.Status);
        Assert.Single(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.DeliveryOtps.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.PaymentSucceeded)
            .ToListAsync());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate-payment-id")]
    [InlineData("wrong-order-id")]
    [InlineData("amount-mismatch")]
    [InlineData("currency-mismatch")]
    [InlineData("multiple-captures")]
    [InlineData("transport-failure")]
    [InlineData("malformed-response")]
    public async Task RazorpayReconciliation_UnsafeDiscoveryEvidence_IsAmbiguous(string scenario)
    {
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
            $"reconcile-unsafe-{scenario}",
            CancellationToken.None);
        var captured = GatewayStatus(created, "captured-1", "captured", true, false);
        gateway.OrderPaymentsResult = scenario switch
        {
            "empty" => new(created.GatewayOrderId!, []),
            "duplicate-payment-id" => new(created.GatewayOrderId!, [captured, captured]),
            "wrong-order-id" => new(created.GatewayOrderId!, [captured with { GatewayOrderId = "order_other" }]),
            "amount-mismatch" => new(created.GatewayOrderId!, [captured with { AmountMinor = captured.AmountMinor + 1 }]),
            "currency-mismatch" => new(created.GatewayOrderId!, [captured with { Currency = "USD" }]),
            "multiple-captures" => new(created.GatewayOrderId!,
                [captured, GatewayStatus(created, "captured-2", "captured", true, false)]),
            _ => null
        };
        gateway.OrderPaymentsException = scenario switch
        {
            "transport-failure" => new HttpRequestException("Razorpay unavailable."),
            "malformed-response" => new System.Text.Json.JsonException("Invalid Razorpay JSON."),
            _ => null
        };

        var result = await harness.PaymentService.ReconcileAsync(
            harness.Administrator.Id, created.PublicId, true, CancellationToken.None);

        Assert.Equal(PaymentReconciliationOutcome.Ambiguous, result.Outcome);
        Assert.Equal(PaymentStatus.Pending, result.Payment.Status);
        Assert.Equal(OrderStatus.PendingPayment, (await harness.Db.Orders.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Wallet top-up (A–M) integration tests.
    // Real Razorpay wallet-top-up flow: CreateWalletTopUpAsync creates a Pending
    // Razorpay payment with a BOTH-NULL target; the wallet is credited ONLY when
    // a verified capture (Verify/Webhook/Reconcile/Cancel-captured) succeeds.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WalletTopUp_Create_PendingRazorpayPaymentWithoutWalletCredit()
    {
        // A. Customer recharge API creates a pending payment + Razorpay order and
        // MUST NOT credit the wallet at creation.
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);

        var created = await harness.PaymentService.CreateWalletTopUpAsync(
            harness.Customer.Id, 500m, "wallet-topup-create-a", CancellationToken.None);

        Assert.Equal(PaymentStatus.Pending, created.Status);
        Assert.Equal(PaymentMethod.Razorpay, created.Method);
        Assert.Equal("Razorpay", created.Provider);
        Assert.Equal(500m, created.Amount);
        Assert.Equal("INR", created.Currency);
        Assert.Null(created.OrderId);
        Assert.Null(created.OrderNumber);
        Assert.Null(created.SubscriptionId);
        Assert.Null(created.GatewayPaymentId);
        Assert.Equal($"order_test_{created.PublicId:N}", created.GatewayOrderId);
        Assert.Equal("rzp_test_public", created.GatewayKeyId);
        Assert.Equal(new DateTime(2026, 8, 16, 7, 45, 0, DateTimeKind.Unspecified), created.ExpiresAt);
        Assert.Null(created.VerifiedAt);

        harness.Db.ChangeTracker.Clear();
        var stored = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(harness.Customer.Id, stored.CustomerId);
        Assert.Equal(PaymentStatus.Pending, stored.Status);
        Assert.Equal(500m, stored.Amount);
        Assert.Equal("INR", stored.Currency);
        Assert.Equal("wallet-topup-create-a", stored.IdempotencyKey);
        Assert.Null(stored.OrderId);
        Assert.Null(stored.SubscriptionId);
        Assert.Equal($"order_test_{stored.PublicId:N}", stored.GatewayOrderId);
        Assert.Equal(new DateTime(2026, 8, 16, 7, 45, 0, DateTimeKind.Unspecified), stored.ExpiresAt);

        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.NotificationEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task WalletTopUp_Create_IsIdempotentAndStoresSinglePayment()
    {
        // B. Replayed create returns the existing (server-stored amount) result;
        // exactly one Payment row, no wallet credit.
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);

        var first = await harness.PaymentService.CreateWalletTopUpAsync(
            harness.Customer.Id, 250m, "wallet-topup-idempotent-b", CancellationToken.None);
        var replay = await harness.PaymentService.CreateWalletTopUpAsync(
            harness.Customer.Id, 999m, "wallet-topup-idempotent-b", CancellationToken.None);

        Assert.Equal(first.PublicId, replay.PublicId);
        Assert.Equal(250m, replay.Amount);
        Assert.Equal(PaymentStatus.Pending, replay.Status);
        Assert.Single(await harness.Db.Payments.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task WalletTopUp_InvalidAmounts_AreRejectedBeforeAnyCredit()
    {
        // C. Non-positive / over-precision amounts are rejected up front; sub-₹1
        // amounts (less than 100 paise) are rejected by the minor-unit guard.
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);

        foreach (var amount in new[] { 0m, -5m, 100.001m })
        {
            var validation = await Assert.ThrowsAsync<ValidationAppException>(() =>
                harness.PaymentService.CreateWalletTopUpAsync(
                    harness.Customer.Id, amount, $"wallet-topup-invalid-{amount}", CancellationToken.None));
            Assert.Equal("Amount must be positive and have at most two decimal places.", validation.Message);
        }

        Assert.Empty(await harness.Db.Payments.AsNoTracking().ToListAsync());

        var belowMinimum = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.PaymentService.CreateWalletTopUpAsync(
                harness.Customer.Id, 0.50m, "wallet-topup-below-minimum", CancellationToken.None));
        Assert.Equal("The payment amount must be at least 100 paise and representable by the gateway.", belowMinimum.Message);

        Assert.Single(await harness.Db.Payments.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.NotificationEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task WalletTopUp_ReusedKeyFromOrderPayment_IsRejectedAsConflict()
    {
        // C. An idempotency key already bound to a different payment kind must not
        // create a wallet top-up under the same key.
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var orderPayment = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
            "shared-topup-key",
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            harness.PaymentService.CreateWalletTopUpAsync(
                harness.Customer.Id, 300m, "shared-topup-key", CancellationToken.None));

        Assert.Equal("The idempotency key is already associated with a different payment request.", exception.Message);
        Assert.Equal(orderPayment.PublicId, (await harness.Db.Payments.AsNoTracking().SingleAsync()).PublicId);
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task WalletTopUp_Verify_CreditsWalletExactlyOnce()
    {
        // D. Server-side verify (valid Razorpay signature + independent capture
        // confirmation) credits the wallet once with the authoritative amount and
        // writes one PaymentSucceeded + one WalletUpdated event.
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateWalletTopUpAsync(
            harness.Customer.Id, 500m, "wallet-topup-verify-d", CancellationToken.None);
        var invalid = new VerifyPaymentRequest(
            created.PublicId, created.GatewayOrderId!, $"pay_test_{created.PublicId:N}", "invalid");
        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.PaymentService.VerifyAsync(harness.Customer.Id, invalid, CancellationToken.None));

        var verified = await harness.PaymentService.VerifyAsync(
            harness.Customer.Id,
            invalid with { Signature = "test_verified" },
            CancellationToken.None);

        Assert.Equal(PaymentStatus.Success, verified.Status);
        Assert.Equal($"pay_test_{created.PublicId:N}", verified.GatewayPaymentId);

        harness.Db.ChangeTracker.Clear();
        var stored = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentStatus.Success, stored.Status);

        var wallet = await harness.Db.Wallets.AsNoTracking().SingleAsync();
        Assert.Equal(harness.Customer.Id, wallet.CustomerId);
        Assert.Equal(500m, wallet.Balance);
        Assert.Equal("INR", wallet.Currency);

        var credit = Assert.Single(await harness.Db.WalletTransactions.AsNoTracking()
            .Where(x => x.Type == WalletTransactionType.TopUp)
            .ToListAsync());
        Assert.Equal(stored.Id, credit.PaymentId);
        Assert.Null(credit.OrderId);
        Assert.Null(credit.SubscriptionId);
        Assert.Null(credit.PerformedByUserId);
        Assert.Equal(0m, credit.BalanceBefore);
        Assert.Equal(500m, credit.Amount);
        Assert.Equal(500m, credit.BalanceAfter);
        Assert.Equal("INR", credit.Currency);
        Assert.Equal("Wallet top-up", credit.Description);
        Assert.Equal($"payment:{created.PublicId:N}", credit.IdempotencyKey);
        Assert.Equal(new DateTime(2026, 8, 16, 7, 30, 0, DateTimeKind.Unspecified), credit.OccurredAt);
        Assert.Equal(DateTimeKind.Unspecified, credit.OccurredAt.Kind);

        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.PaymentSucceeded)
            .ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.WalletUpdated)
            .ToListAsync());
    }

    [Fact]
    public async Task WalletTopUp_VerifyReplay_DoesNotDoubleCredit()
    {
        // E. One Razorpay payment = exactly one credit + one TopUp transaction;
        // replays return the existing result without double-crediting.
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateWalletTopUpAsync(
            harness.Customer.Id, 500m, "wallet-topup-verify-replay-e", CancellationToken.None);
        var valid = new VerifyPaymentRequest(
            created.PublicId, created.GatewayOrderId!, $"pay_test_{created.PublicId:N}", "test_verified");

        var first = await harness.PaymentService.VerifyAsync(
            harness.Customer.Id, valid, CancellationToken.None);
        var replay = await harness.PaymentService.VerifyAsync(
            harness.Customer.Id, valid, CancellationToken.None);

        Assert.Equal(PaymentStatus.Success, first.Status);
        Assert.Equal(PaymentStatus.Success, replay.Status);
        Assert.Equal(first.PublicId, replay.PublicId);

        harness.Db.ChangeTracker.Clear();
        Assert.Single(await harness.Db.Payments.AsNoTracking().ToListAsync());
        Assert.Equal(500m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Single(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.PaymentSucceeded)
            .ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.WalletUpdated)
            .ToListAsync());
    }

    [Fact]
    public async Task WalletTopUp_InvalidSignature_IsRejectedWithoutCredit()
    {
        // F. A client callback with a bad signature is not proof of payment; the
        // payment stays Pending and no wallet credit is produced.
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateWalletTopUpAsync(
            harness.Customer.Id, 500m, "wallet-topup-invalid-signature-f", CancellationToken.None);
        var invalid = new VerifyPaymentRequest(
            created.PublicId, created.GatewayOrderId!, $"pay_test_{created.PublicId:N}", "invalid");

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.PaymentService.VerifyAsync(harness.Customer.Id, invalid, CancellationToken.None));

        Assert.Equal("The payment signature is invalid.", exception.Message);
        Assert.Equal(PaymentStatus.Pending, (await harness.Db.Payments.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.NotificationEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task WalletTopUp_CrossCustomerVerifyOrCancel_IsRejectedWithoutCredit()
    {
        // G. Target ownership is enforced on every customer-visible operation.
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateWalletTopUpAsync(
            harness.Customer.Id, 500m, "wallet-topup-cross-customer-g", CancellationToken.None);
        var valid = new VerifyPaymentRequest(
            created.PublicId, created.GatewayOrderId!, $"pay_test_{created.PublicId:N}", "test_verified");

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.PaymentService.VerifyAsync(harness.OtherCustomer.Id, valid, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.PaymentService.CancelAsync(harness.OtherCustomer.Id, created.PublicId, CancellationToken.None));

        Assert.Equal(PaymentStatus.Pending, (await harness.Db.Payments.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.NotificationEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task WalletTopUp_Webhook_CreditsWalletExactlyOnceIdempotently()
    {
        // H. A signed payment.captured webhook reconciles success and credits the
        // wallet once; a duplicate webhook is idempotent.
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateWalletTopUpAsync(
            harness.Customer.Id, 500m, "wallet-topup-webhook-h", CancellationToken.None);
        gateway.WebhookEvent = new GatewayWebhookEvent(
            "evt_wallet_topup_1",
            "payment.captured",
            created.GatewayOrderId,
            $"pay_test_{created.PublicId:N}",
            GatewayRefundId: null,
            Status: "captured",
            AmountMinor: 50000,
            Currency: "INR");
        var payload = "{\"event\":\"payment.captured\",\"id\":\"evt_wallet_topup_1\"}"u8.ToArray();

        await harness.PaymentService.ProcessWebhookAsync(
            payload, "test_webhook_verified", CancellationToken.None);
        await harness.PaymentService.ProcessWebhookAsync(
            payload, "test_webhook_verified", CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(PaymentStatus.Success, (await harness.Db.Payments.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(500m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Single(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        var receipt = Assert.Single(await harness.Db.PaymentWebhooks.AsNoTracking().ToListAsync());
        Assert.Equal(PaymentWebhookStatus.Processed, receipt.Status);
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.PaymentSucceeded)
            .ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.WalletUpdated)
            .ToListAsync());
    }

    [Fact]
    public async Task WalletTopUp_CancelDefinitiveFailure_CancelsWithoutCredit()
    {
        // I. A definitive gateway failure cancels the payment; the wallet is NOT
        // credited and no payment events are emitted.
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateWalletTopUpAsync(
            harness.Customer.Id, 500m, "wallet-topup-cancel-failed-i", CancellationToken.None);
        gateway.OrderPaymentsResult = new GatewayOrderPaymentsResult(
            created.GatewayOrderId!,
            [GatewayStatus(created, "failed", "failed", false, true)]);

        var cancelled = await harness.PaymentService.CancelAsync(
            harness.Customer.Id, created.PublicId, CancellationToken.None);
        var replay = await harness.PaymentService.CancelAsync(
            harness.Customer.Id, created.PublicId, CancellationToken.None);

        Assert.Equal(PaymentStatus.Cancelled, cancelled.Status);
        Assert.Equal(PaymentStatus.Cancelled, replay.Status);
        Assert.Equal(PaymentStatus.Cancelled, (await harness.Db.Payments.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.NotificationEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task WalletTopUp_CancelValidatedCapture_CompletesAndCreditsWalletOnce()
    {
        // J. Cancelling a payment that was actually captured must recover success
        // and credit the wallet exactly once.
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateWalletTopUpAsync(
            harness.Customer.Id, 500m, "wallet-topup-cancel-captured-j", CancellationToken.None);
        gateway.OrderPaymentsResult = new GatewayOrderPaymentsResult(
            created.GatewayOrderId!,
            [GatewayStatus(created, "captured", "captured", true, false)]);

        var completed = await harness.PaymentService.CancelAsync(
            harness.Customer.Id, created.PublicId, CancellationToken.None);
        var replay = await harness.PaymentService.CancelAsync(
            harness.Customer.Id, created.PublicId, CancellationToken.None);

        Assert.Equal(PaymentStatus.Success, completed.Status);
        Assert.Equal(PaymentStatus.Success, replay.Status);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(PaymentStatus.Success, (await harness.Db.Payments.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(500m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Single(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.PaymentSucceeded)
            .ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.WalletUpdated)
            .ToListAsync());
    }

    [Fact]
    public async Task WalletTopUp_ReconcileCaptured_CreditsWalletExactlyOnce()
    {
        // K. Administrative reconciliation of a captured payment credits the
        // wallet exactly once and reports the Captured outcome.
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateWalletTopUpAsync(
            harness.Customer.Id, 500m, "wallet-topup-reconcile-k", CancellationToken.None);
        var captured = GatewayStatus(created, "captured", "captured", true, false);
        gateway.OrderPaymentsResult = new GatewayOrderPaymentsResult(
            created.GatewayOrderId!,
            [captured]);

        var first = await harness.PaymentService.ReconcileAsync(
            harness.Administrator.Id, created.PublicId, true, CancellationToken.None);
        gateway.DirectStatusResult = captured;
        var replay = await harness.PaymentService.ReconcileAsync(
            harness.Administrator.Id, created.PublicId, true, CancellationToken.None);

        Assert.Equal(PaymentReconciliationOutcome.Captured, first.Outcome);
        Assert.Equal(PaymentReconciliationOutcome.Captured, replay.Outcome);
        Assert.Equal(PaymentStatus.Success, replay.Payment.Status);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(500m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Single(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.PaymentSucceeded)
            .ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.WalletUpdated)
            .ToListAsync());
    }

    [Fact]
    public async Task WalletTopUp_ExpiredDefinitiveFailure_RemainsExpiredWithoutCredit()
    {
        // L. An expired payment with a definitive gateway failure is terminal and
        // never credits the wallet.
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateWalletTopUpAsync(
            harness.Customer.Id, 500m, "wallet-topup-expired-l", CancellationToken.None);
        var payment = await harness.Db.Payments.SingleAsync();
        payment.Expire(new DateTime(2026, 8, 16, 7, 45, 0));
        await harness.Db.SaveChangesAsync();
        gateway.OrderPaymentsResult = new GatewayOrderPaymentsResult(
            created.GatewayOrderId!,
            [GatewayStatus(created, "failed", "failed", false, true)]);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.PaymentService.CancelAsync(
                harness.Customer.Id, created.PublicId, CancellationToken.None));

        Assert.Equal("The payment was not captured and is already expired.", exception.Message);
        Assert.Equal(PaymentStatus.Expired, (await harness.Db.Payments.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.NotificationEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task WalletTopUp_TamperedGatewayAmount_IsAmbiguousAndNeverCredits()
    {
        // M. A gateway amount that does not match the authoritative server amount
        // is treated as ambiguous; the payment is never credited.
        var options = RazorpayOptions();
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateWalletTopUpAsync(
            harness.Customer.Id, 500m, "wallet-topup-tamper-m", CancellationToken.None);
        var captured = GatewayStatus(created, "captured", "captured", true, false);
        gateway.DirectStatusResult = captured with { AmountMinor = captured.AmountMinor + 1 };

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            harness.PaymentService.VerifyAsync(
                harness.Customer.Id,
                new VerifyPaymentRequest(
                    created.PublicId, created.GatewayOrderId!, $"pay_test_{created.PublicId:N}", "test_verified"),
                CancellationToken.None));

        Assert.Equal("The gateway response is ambiguous; replacement charging is blocked.", exception.Message);
        Assert.Equal(PaymentStatus.Pending, (await harness.Db.Payments.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.NotificationEvents.AsNoTracking().ToListAsync());
    }

    private static IOptions<PaymentOptions> RazorpayOptions() => Options.Create(new PaymentOptions
    {
        Provider = "Razorpay",
        Currency = "INR",
        RazorpayKeyId = "rzp_test_public",
        RazorpayKeySecret = "api-secret",
        MockSigningSecret = "unused"
    });

    private static GatewayPaymentStatusResult GatewayStatus(
        PaymentResult payment,
        string suffix,
        string status,
        bool isSuccessful,
        bool isTerminalFailure) =>
        new(
            $"pay_{suffix}_{payment.PublicId:N}",
            payment.GatewayOrderId!,
            status,
            checked((long)(payment.Amount * 100m)),
            payment.Currency,
            isSuccessful,
            isTerminalFailure);

    private sealed class PaymentHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private PaymentHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            User customer,
            User otherCustomer,
            User administrator,
            Order order,
            TestIndiaTimeProvider timeProvider,
            PaymentService paymentService,
            WalletService walletService,
            DeliveryService deliveryService,
            CapturingOtpDeliveryService otpDelivery,
            DeliveryOtpHandoffProtector otpHandoffProtector)
        {
            this.connection = connection;
            Db = db;
            Customer = customer;
            OtherCustomer = otherCustomer;
            Administrator = administrator;
            Order = order;
            TimeProvider = timeProvider;
            PaymentService = paymentService;
            WalletService = walletService;
            DeliveryService = deliveryService;
            OtpDelivery = otpDelivery;
            OtpHandoffProtector = otpHandoffProtector;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public User OtherCustomer { get; }
        public User Administrator { get; }
        public Order Order { get; }
        public TestIndiaTimeProvider TimeProvider { get; }
        public PaymentService PaymentService { get; }
        public WalletService WalletService { get; }
        public DeliveryService DeliveryService { get; }
        public CapturingOtpDeliveryService OtpDelivery { get; }
        public DeliveryOtpHandoffProtector OtpHandoffProtector { get; }

        public static async Task<PaymentHarness> CreateAsync(
            decimal payableAmount = 100m,
            IOptions<PaymentOptions>? paymentOptions = null,
            IPaymentGateway? gateway = null,
            CapturingOtpDeliveryService? otpDelivery = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            var otherCustomer = new User(UserType.Customer);
            otherCustomer.SetProfile("Other Customer");
            var administrator = new User(UserType.SystemAdministrator);
            administrator.SetProfile("Administrator");
            db.Users.AddRange(customer, otherCustomer, administrator);
            await db.SaveChangesAsync();
            db.NumberSeries.Add(new NumberSeries(
                "DELIVERY", "Delivery Number", "DEL/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never));

            var order = new Order(
                customer.Id, 1, 1, "checkout-1", "DD-20260816020000-PAYMENT",
                subtotal: payableAmount, discountAmount: 0m,
                branchCode: "MAIN", branchName: "Main Branch", addressLabel: "Home",
                addressLine1: "1 Main Road", addressLine2: null, locality: "Central",
                city: "Bengaluru", state: "Karnataka", pinCode: "560001", landmark: null,
                deliveryInstructions: null, contactName: "Customer", contactMobile: "9999999999",
                latitude: 12.9716m, longitude: 77.5946m);
            db.Orders.Add(order);
            await db.SaveChangesAsync();

            var clock = new TestClock(new DateTime(2026, 8, 16, 7, 30, 0, DateTimeKind.Unspecified));
            paymentOptions ??= Options.Create(new PaymentOptions
            {
                Provider = "Mock",
                Currency = "INR",
                PaymentExpiryMinutes = 15,
                MockSigningSecret = "test-signing-secret"
            });
            var notificationEventWriter = new TestNotificationEventWriter(db, clock);
            var indiaTime = new TestIndiaTimeProvider(clock);
            otpDelivery ??= new CapturingOtpDeliveryService();
            var otpHandoffProtector = new DeliveryOtpHandoffProtector(
                new EphemeralDataProtectionProvider());
            var deliveryService = new DeliveryService(
                db,
                indiaTime,
                new TestPasswordHasher(),
                otpDelivery,
                new NoOpDeliveryRealtimePublisher(),
                Options.Create(new DeliveryOptions()),
                notificationEventWriter,
                new NumberSeriesService(db, indiaTime),
                otpHandoffProtector);
            var walletService = new WalletService(
                db,
                indiaTime,
                paymentOptions,
                notificationEventWriter);
            var paymentService = new PaymentService(
                db,
                gateway ?? new MockPaymentGateway(paymentOptions),
                walletService,
                indiaTime,
                paymentOptions,
                notificationEventWriter,
                mockGateway: null,
                oneTimeDeliveryCreator: deliveryService);
            return new PaymentHarness(
                connection,
                db,
                customer,
                otherCustomer,
                administrator,
                order,
                indiaTime,
                paymentService,
                walletService,
                deliveryService,
                otpDelivery,
                otpHandoffProtector);
        }

        public async Task AssertConfirmedWithPendingOtpAsync(PaymentResult result)
        {
            Assert.Equal(PaymentStatus.Success, result.Status);
            Assert.Equal(
                OrderStatus.Confirmed,
                (await Db.Orders.AsNoTracking().SingleAsync()).Status);
            Assert.Single(await Db.Deliveries.AsNoTracking().ToListAsync());

            var otp = Assert.Single(await Db.DeliveryOtps.AsNoTracking().ToListAsync());
            Assert.Null(otp.SentAt);
            Assert.NotNull(otp.ProtectedCode);
            var protectedCode = otp.ProtectedCode;
            var expectedCode = OtpHandoffProtector.Unprotect(protectedCode);
            var events = await Db.NotificationEvents.AsNoTracking()
                .Where(x => x.EventType == NotificationEventTypes.DeliveryOtpIssued)
                .ToListAsync();
            Assert.Single(events);
            Assert.Equal(NotificationEventStatus.Pending, events[0].Status);

            await DeliveryService.IssuePendingOtpsAsync(CancellationToken.None);

            var retriedOtp = Assert.Single(await Db.DeliveryOtps.AsNoTracking().ToListAsync());
            Assert.NotNull(retriedOtp.SentAt);
            Assert.Equal(protectedCode, retriedOtp.ProtectedCode);
            var sent = Assert.Single(OtpDelivery.Messages);
            Assert.Equal(expectedCode, sent.Code);
            Assert.Single(await Db.NotificationEvents.AsNoTracking()
                .Where(x => x.EventType == NotificationEventTypes.DeliveryOtpIssued)
                .ToListAsync());
        }

        public async Task AssertInsufficientAttemptDidNotPersistAsync(decimal expectedBalance)
        {
            Db.ChangeTracker.Clear();

            Assert.Empty(await Db.Payments.AsNoTracking().ToListAsync());
            Assert.Equal(
                OrderStatus.PendingPayment,
                (await Db.Orders.AsNoTracking().SingleAsync()).Status);

            var wallet = await Db.Wallets.AsNoTracking().SingleOrDefaultAsync();
            if (expectedBalance == 0)
            {
                Assert.Null(wallet);
                Assert.Empty(await Db.WalletTransactions.AsNoTracking().ToListAsync());
                return;
            }

            Assert.NotNull(wallet);
            Assert.Equal(expectedBalance, wallet.Balance);
            var entries = await Db.WalletTransactions.AsNoTracking().ToListAsync();
            var topUp = Assert.Single(entries);
            Assert.Equal(WalletTransactionType.TopUp, topUp.Type);
            Assert.Equal(expectedBalance, topUp.Amount);
        }

        public async Task SeedWalletAsync(decimal amount, string idempotencyKey)
        {
            var wallet = await Db.Wallets.SingleOrDefaultAsync(x => x.CustomerId == Customer.Id);
            if (wallet is null)
            {
                wallet = new Wallet(Customer.Id, "INR");
                Db.Wallets.Add(wallet);
            }

            wallet.Credit(WalletTransactionType.TopUp, amount, idempotencyKey, "Wallet top-up", TimeProvider.Now);
            await Db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestRazorpayGateway(IOptions<PaymentOptions> options) : IPaymentGateway
    {
        private readonly Dictionary<Guid, GatewayOrderRequest> orders = [];

        public GatewayWebhookEvent? WebhookEvent { get; set; }
        public GatewayPaymentStatusResult? DirectStatusResult { get; set; }
        public Exception? DirectStatusException { get; set; }
        public GatewayOrderPaymentsResult? OrderPaymentsResult { get; set; }
        public Exception? OrderPaymentsException { get; set; }
        public string ProviderName => "Razorpay";
        public Task<string?> GetPublicKeyIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(options.Value.RazorpayKeyId);
        public bool IsLive => true;

        public Task<GatewayOrderResult> CreateOrderAsync(
            GatewayOrderRequest request,
            CancellationToken cancellationToken)
        {
            orders[request.PaymentId] = request;
            return Task.FromResult(new GatewayOrderResult(
                $"order_test_{request.PaymentId:N}",
                "created",
                request.AmountMinor,
                request.Currency));
        }

        public Task<bool> VerifyPaymentSignatureAsync(
            string gatewayOrderId,
            string gatewayPaymentId,
            string signature,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                signature == "test_verified" &&
                TryGetPaymentId(gatewayPaymentId, out var paymentId) &&
                gatewayOrderId == $"order_test_{paymentId:N}" &&
                orders.ContainsKey(paymentId));

        public Task<GatewayPaymentStatusResult> GetPaymentStatusAsync(
            string gatewayPaymentId,
            CancellationToken cancellationToken)
        {
            if (DirectStatusException is not null)
            {
                return Task.FromException<GatewayPaymentStatusResult>(DirectStatusException);
            }
            if (DirectStatusResult is not null)
            {
                return Task.FromResult(DirectStatusResult);
            }
            if (!TryGetPaymentId(gatewayPaymentId, out var paymentId) ||
                !orders.TryGetValue(paymentId, out var request))
            {
                throw new InvalidOperationException("The test payment was not created by this gateway.");
            }

            return Task.FromResult(CreateCapturedStatus(paymentId, request));
        }

        public Task<GatewayOrderPaymentsResult> GetPaymentsForOrderAsync(
            string gatewayOrderId,
            CancellationToken cancellationToken)
        {
            if (OrderPaymentsException is not null)
            {
                return Task.FromException<GatewayOrderPaymentsResult>(OrderPaymentsException);
            }
            if (OrderPaymentsResult is not null)
            {
                return Task.FromResult(OrderPaymentsResult);
            }

            var match = orders.SingleOrDefault(x => gatewayOrderId == $"order_test_{x.Key:N}");
            IReadOnlyList<GatewayPaymentStatusResult> payments = match.Value is null
                ? []
                : [CreateCapturedStatus(match.Key, match.Value)];
            return Task.FromResult(new GatewayOrderPaymentsResult(gatewayOrderId, payments));
        }

        public Task<bool> VerifyWebhookSignatureAsync(ReadOnlySpan<byte> payload, string signature, CancellationToken cancellationToken) =>
            Task.FromResult(payload.Length > 0 && signature == "test_webhook_verified");

        public GatewayWebhookEvent ParseWebhook(ReadOnlySpan<byte> payload) =>
            WebhookEvent ?? throw new InvalidOperationException("No test webhook event was configured.");

        public Task<GatewayRefundResult> RefundAsync(
            string gatewayPaymentId,
            long amountMinor,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(new GatewayRefundResult(
                $"refund_test_{idempotencyKey}",
                "processed",
                IsSuccessful: true,
                IsPending: false,
                FailureCode: null,
                FailureMessage: null));

        private static GatewayPaymentStatusResult CreateCapturedStatus(
            Guid paymentId,
            GatewayOrderRequest request) =>
            new(
                $"pay_test_{paymentId:N}",
                $"order_test_{paymentId:N}",
                "captured",
                request.AmountMinor,
                request.Currency,
                IsSuccessful: true,
                IsTerminalFailure: false);

        private static bool TryGetPaymentId(string gatewayPaymentId, out Guid paymentId)
        {
            const string prefix = "pay_test_";
            paymentId = Guid.Empty;
            return gatewayPaymentId.StartsWith(prefix, StringComparison.Ordinal) &&
                Guid.TryParseExact(gatewayPaymentId[prefix.Length..], "N", out paymentId);
        }
    }

    private sealed class CapturingOtpDeliveryService : IOtpDeliveryService
    {
        public List<(string Destination, string Code)> Messages { get; } = [];
        public bool FailNextSend { get; set; }

        public Task SendAsync(
            string destination,
            string code,
            CancellationToken cancellationToken)
        {
            if (FailNextSend)
            {
                FailNextSend = false;
                throw new InvalidOperationException("Simulated OTP transport failure.");
            }

            Messages.Add((destination, code));
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpDeliveryRealtimePublisher : IDeliveryRealtimePublisher
    {
        public Task DeliveryChangedAsync(DeliveryResult delivery, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task LocationChangedAsync(
            Guid deliveryId,
            DeliveryLocationResult location,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
    }
}
