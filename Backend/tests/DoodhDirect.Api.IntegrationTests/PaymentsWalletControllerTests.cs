using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using DoodhDirect.Api.Controllers;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class PaymentsWalletControllerTests
{
    [Theory]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.Create), AuthorizationCodes.PaymentsCreateOwn)]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.Verify), AuthorizationCodes.PaymentsCreateOwn)]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.Get), AuthorizationCodes.PaymentsReadOwn)]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.Reconcile), AuthorizationCodes.PaymentsRefund)]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.Refund), AuthorizationCodes.PaymentsRefund)]
    [InlineData(typeof(WalletController), nameof(WalletController.Get), AuthorizationCodes.WalletReadOwn)]
    [InlineData(typeof(WalletController), nameof(WalletController.GetTransactions), AuthorizationCodes.WalletReadOwn)]
    [InlineData(typeof(WalletAdministrationController), nameof(WalletAdministrationController.Adjust), AuthorizationCodes.WalletAdjust)]
    public void FinancialRoute_RequiresExpectedPermission(Type controllerType, string methodName, string permission)
    {
        var method = controllerType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);

        var authorize = Assert.Single(Assert.IsType<AuthorizeAttribute[]>(
            method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)));
        Assert.Equal($"permission:{permission}", authorize.Policy);
        Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
    }

    [Fact]
    public void WebhookRoute_IsAnonymousBecauseProviderSignatureIsItsAuthenticationBoundary()
    {
        var attributes = typeof(RazorpayWebhooksController)
            .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true);

        Assert.Single(attributes);
    }

    [Fact]
    public async Task Webhook_ForwardsExactRawBodyAndSignature()
    {
        var paymentService = new CapturingPaymentService();
        var controller = new RazorpayWebhooksController(paymentService);
        var payload = Encoding.UTF8.GetBytes("{\"event\":\"payment.captured\",\"value\":123}");
        controller.ControllerContext = ContextWithBody(payload);

        var response = await controller.Receive("signed-value", CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal(payload, paymentService.WebhookPayload);
        Assert.Equal("signed-value", paymentService.WebhookSignature);
        Assert.Equal(1, paymentService.WebhookCalls);
    }

    [Fact]
    public async Task Webhook_RejectsEmptyPayloadBeforeServiceInvocation()
    {
        var paymentService = new CapturingPaymentService();
        var controller = new RazorpayWebhooksController(paymentService)
        {
            ControllerContext = ContextWithBody([])
        };

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            controller.Receive("signed-value", CancellationToken.None));

        Assert.Equal("The webhook payload is required.", exception.Message);
        Assert.Equal(0, paymentService.WebhookCalls);
    }

    [Fact]
    public async Task Webhook_RejectsDeclaredOversizedPayloadBeforeReadingBody()
    {
        var paymentService = new CapturingPaymentService();
        var context = ContextWithBody(Encoding.UTF8.GetBytes("{}"));
        context.HttpContext.Request.ContentLength = 1_048_577;
        var controller = new RazorpayWebhooksController(paymentService)
        {
            ControllerContext = context
        };

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            controller.Receive("signed-value", CancellationToken.None));

        Assert.Equal("The webhook payload is too large.", exception.Message);
        Assert.Equal(0, paymentService.WebhookCalls);
    }

    [Fact]
    public async Task Reconcile_ForwardsAuthenticatedOperatorWithOwnershipBypass()
    {
        var paymentService = new CapturingPaymentService();
        var controller = CreatePaymentsController(paymentService, userId: 91);
        var paymentId = Guid.NewGuid();

        var response = await controller.Reconcile(paymentId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var envelope = Assert.IsType<ApiResponse<PaymentReconciliationResult>>(ok.Value);
        Assert.Equal(paymentService.ReconciliationResult, envelope.Data);
        Assert.Equal(91, paymentService.ReconciliationRequestedByUserId);
        Assert.Equal(paymentId, paymentService.ReconciliationPaymentId);
        Assert.True(paymentService.ReconciliationBypassOwnership);
        Assert.Equal(1, paymentService.ReconciliationCalls);
    }

    [Fact]
    public void PaymentOptions_RazorpayWithoutCredentials_PassesStaticValidationButIsNotRuntimeReady()
    {
        // §11 relaxation: Razorpay credentials may be supplied at runtime through the
        // Integration settings store (Integration.Razorpay.*), so blank static values
        // must NOT fail boot-time validation. Per-call availability is resolved at
        // runtime instead (IsRazorpayConfigured / IsValid remain false here).
        var options = new PaymentOptions
        {
            Provider = "Razorpay",
            Currency = "INR",
            RazorpayKeyId = "",
            RazorpayKeySecret = "",
            RazorpayWebhookSecret = "",
            MockSigningSecret = "unused-but-required"
        };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            results,
            validateAllProperties: true);

        Assert.True(isValid);
        Assert.Empty(results);
        Assert.False(options.IsRazorpayConfigured);
        Assert.False(options.IsValid);
        Assert.DoesNotContain(results, result =>
            result.MemberNames.Contains(nameof(PaymentOptions.RazorpayWebhookSecret)));
    }

    [Fact]
    public void PaymentOptions_RazorpayWithCredentials_IsValid()
    {
        var options = new PaymentOptions
        {
            Provider = "Razorpay",
            Currency = "INR",
            PaymentExpiryMinutes = 15,
            RazorpayKeyId = "rzp_test_key",
            RazorpayKeySecret = "test-secret",
            MockSigningSecret = "development-test-signing-secret"
        };

        Assert.True(options.IsValid);
        Assert.True(options.IsRazorpayConfigured);
    }

    [Fact]
    public void PaymentOptions_MockProvider_IsInvalidEverywhere()
    {
        var options = new PaymentOptions
        {
            Provider = "Mock",
            Currency = "INR",
            PaymentExpiryMinutes = 15,
            MockSigningSecret = "development-test-signing-secret"
        };

        Assert.False(options.IsValid);
        Assert.True(options.IsMock);
        Assert.False(options.IsRazorpayConfigured);
    }

    [Fact]
    public void PaymentOptions_RazorpayWithoutCredentials_FailsClosed()
    {
        var options = new PaymentOptions
        {
            Provider = "Razorpay",
            Currency = "INR",
            PaymentExpiryMinutes = 15,
            MockSigningSecret = "production-secret-placeholder"
        };

        Assert.False(options.IsValid);
        Assert.False(options.IsRazorpayConfigured);
    }

    [Fact]
    public void PaymentOptions_MockProvider_IsRejected()
    {
        var options = new PaymentOptions
        {
            Provider = "Mock",
            Currency = "INR",
            PaymentExpiryMinutes = 15,
            MockSigningSecret = "production-secret-placeholder"
        };

        Assert.False(options.IsValid);
    }

    private static PaymentsController CreatePaymentsController(
        IPaymentService paymentService,
        long userId)
    {
        var controller = new PaymentsController(paymentService);
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("user_id", userId.ToString())],
            authenticationType: "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static ControllerContext ContextWithBody(byte[] payload)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(payload);
        context.Request.ContentLength = payload.Length;
        return new ControllerContext { HttpContext = context };
    }

    private sealed class CapturingPaymentService : IPaymentService
    {
        private static readonly DateTime PaymentOccurredAt =
            new(2026, 8, 23, 10, 30, 0, DateTimeKind.Unspecified);

        public byte[]? WebhookPayload { get; private set; }
        public string? WebhookSignature { get; private set; }
        public int WebhookCalls { get; private set; }
        public long? ReconciliationRequestedByUserId { get; private set; }
        public Guid? ReconciliationPaymentId { get; private set; }
        public bool ReconciliationBypassOwnership { get; private set; }
        public int ReconciliationCalls { get; private set; }
        public PaymentReconciliationResult ReconciliationResult { get; } = new(
            new PaymentResult(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "ORD-TEST-1",
                PaymentMethod.Razorpay,
                "Razorpay",
                PaymentStatus.Pending,
                125.50m,
                0m,
                "INR",
                "order_test_1",
                null,
                "rzp_test_key",
                null,
                null,
                PaymentOccurredAt.AddMinutes(15),
                null,
                PaymentOccurredAt),
            PaymentReconciliationOutcome.Pending,
            "authorized",
            TargetRecovered: false);

        public Task ProcessWebhookAsync(byte[] payload, string signature, CancellationToken cancellationToken)
        {
            WebhookPayload = payload;
            WebhookSignature = signature;
            WebhookCalls++;
            return Task.CompletedTask;
        }

        public Task<PaymentResult> CreateAsync(long customerId, CreatePaymentRequest request, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentResult> CreateForSubscriptionAsync(
            long customerId,
            long subscriptionId,
            PaymentMethod method,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentResult> CreateWalletTopUpAsync(
            long customerId,
            decimal amount,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentResult> CompleteDevelopmentAsync(
            long customerId,
            Guid paymentId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentResult> CancelAsync(
            long customerId,
            Guid paymentId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PaymentCapability>> GetCapabilitiesAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentResult> RetrySubscriptionAsync(
            long customerId,
            Guid subscriptionId,
            PaymentMethod method,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentResult> VerifyAsync(long customerId, VerifyPaymentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentResult> GetAsync(long userId, Guid paymentId, bool bypassOwnership, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentReconciliationResult> ReconcileAsync(
            long requestedByUserId,
            Guid paymentId,
            bool bypassOwnership,
            CancellationToken cancellationToken)
        {
            ReconciliationRequestedByUserId = requestedByUserId;
            ReconciliationPaymentId = paymentId;
            ReconciliationBypassOwnership = bypassOwnership;
            ReconciliationCalls++;
            return Task.FromResult(ReconciliationResult);
        }

        public Task<RefundResult> RefundAsync(long requestedByUserId, Guid paymentId, RefundPaymentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingWalletService : IWalletService
    {
        private static readonly DateTime OccurredAt = new(2026, 8, 16, 7, 30, 0, DateTimeKind.Unspecified);

        public long? CreditWalletTopUpCustomerId { get; private set; }
        public long? CreditWalletTopUpPaymentId { get; private set; }
        public decimal? CreditWalletTopUpAmount { get; private set; }
        public string? CreditWalletTopUpIdempotencyKey { get; private set; }
        public int CreditWalletTopUpCalls { get; private set; }

        public Task<WalletTransactionResult> CreditWalletTopUpAsync(
            long customerId,
            long paymentId,
            decimal amount,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            CreditWalletTopUpCustomerId = customerId;
            CreditWalletTopUpPaymentId = paymentId;
            CreditWalletTopUpAmount = amount;
            CreditWalletTopUpIdempotencyKey = idempotencyKey;
            CreditWalletTopUpCalls++;
            return Task.FromResult(new WalletTransactionResult(
                Guid.NewGuid(),
                WalletTransactionType.TopUp,
                0,
                amount,
                amount,
                "INR",
                "Wallet top-up",
                OccurredAt,
                null,
                null));
        }

        public Task<WalletResult> GetAsync(long customerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WalletTransactionResult>> GetTransactionsAsync(long customerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WalletTransactionResult> AdjustAsync(long administratorUserId, Guid customerId, WalletAdjustmentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WalletTransactionResult> DebitOrderAsync(long customerId, long orderId, long paymentId, decimal amount, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WalletTransactionResult> CreditRefundAsync(long customerId, long orderId, long paymentId, decimal amount, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
