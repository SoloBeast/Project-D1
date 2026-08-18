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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class PaymentsWalletControllerTests
{
    [Theory]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.Create), AuthorizationCodes.PaymentsCreateOwn)]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.Verify), AuthorizationCodes.PaymentsCreateOwn)]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.Get), AuthorizationCodes.PaymentsReadOwn)]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.Refund), AuthorizationCodes.PaymentsRefund)]
    [InlineData(typeof(WalletController), nameof(WalletController.Get), AuthorizationCodes.WalletReadOwn)]
    [InlineData(typeof(WalletController), nameof(WalletController.GetTransactions), AuthorizationCodes.WalletReadOwn)]
    [InlineData(typeof(WalletController), nameof(WalletController.TopUp), AuthorizationCodes.WalletTopUpOwn)]
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
    public async Task TopUp_InDevelopmentWithMockProvider_ForwardsAuthenticatedCustomerAndIdempotencyKey()
    {
        var walletService = new CapturingWalletService();
        var controller = CreateWalletController(walletService, "Development", "Mock", userId: 73);

        var response = await controller.TopUp(
            new WalletTopUpApiRequest(125.50m),
            "topup-73-1",
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal(73, walletService.TopUpCustomerId);
        Assert.Equal(new WalletTopUpRequest(125.50m, "topup-73-1"), walletService.TopUpRequest);
        Assert.Equal(1, walletService.TopUpCalls);
    }

    [Theory]
    [InlineData("Production", "Mock")]
    [InlineData("Development", "Razorpay")]
    public async Task TopUp_OutsideDevelopmentMockBoundary_IsNotAvailable(string environment, string provider)
    {
        var walletService = new CapturingWalletService();
        var controller = CreateWalletController(walletService, environment, provider, userId: 73);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            controller.TopUp(
                new WalletTopUpApiRequest(125.50m),
                "topup-73-1",
                CancellationToken.None));

        Assert.Equal("The development wallet top-up endpoint is not available.", exception.Message);
        Assert.Equal(0, walletService.TopUpCalls);
    }

    [Fact]
    public void PaymentOptions_RazorpayWithoutCredentials_FailsValidation()
    {
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

        Assert.False(isValid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(PaymentOptions.RazorpayKeyId)));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(PaymentOptions.RazorpayKeySecret)));
        Assert.DoesNotContain(results, result =>
            result.MemberNames.Contains(nameof(PaymentOptions.RazorpayWebhookSecret)));
    }

    [Fact]
    public void PaymentOptions_DevelopmentMockConfiguration_IsValid()
    {
        var options = new PaymentOptions
        {
            Provider = "Mock",
            Currency = "INR",
            PaymentExpiryMinutes = 15,
            MockSigningSecret = "development-test-signing-secret"
        };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            results,
            validateAllProperties: true);

        Assert.True(isValid);
        Assert.Empty(results);
    }

    private static WalletController CreateWalletController(
        IWalletService walletService,
        string environmentName,
        string provider,
        long userId)
    {
        var controller = new WalletController(
            walletService,
            new TestWebHostEnvironment { EnvironmentName = environmentName },
            Options.Create(new PaymentOptions
            {
                Provider = provider,
                Currency = "INR",
                MockSigningSecret = "test-signing-secret",
                RazorpayKeyId = provider == "Razorpay" ? "rzp_test" : null,
                RazorpayKeySecret = provider == "Razorpay" ? "key-secret" : null,
                RazorpayWebhookSecret = provider == "Razorpay" ? "webhook-secret" : null
            }));
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
        public byte[]? WebhookPayload { get; private set; }
        public string? WebhookSignature { get; private set; }
        public int WebhookCalls { get; private set; }

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

        public Task<RefundResult> RefundAsync(long requestedByUserId, Guid paymentId, RefundPaymentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingWalletService : IWalletService
    {
        private static readonly DateTime OccurredAtUtc = new(2026, 8, 16, 2, 0, 0, DateTimeKind.Utc);

        public long? TopUpCustomerId { get; private set; }
        public WalletTopUpRequest? TopUpRequest { get; private set; }
        public int TopUpCalls { get; private set; }

        public Task<WalletTransactionResult> TopUpAsync(long customerId, WalletTopUpRequest request, CancellationToken cancellationToken)
        {
            TopUpCustomerId = customerId;
            TopUpRequest = request;
            TopUpCalls++;
            return Task.FromResult(new WalletTransactionResult(
                Guid.NewGuid(),
                WalletTransactionType.TopUp,
                0,
                request.Amount,
                request.Amount,
                "INR",
                "Wallet top-up",
                OccurredAtUtc,
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

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DoodhDirect.Api.IntegrationTests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
