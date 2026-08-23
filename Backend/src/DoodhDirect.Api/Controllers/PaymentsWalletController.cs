using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Infrastructure.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.Controllers;

[ApiController]
[Route("api/v1/payments")]
[Tags("Payments")]
[Produces("application/json")]
public sealed class PaymentsController(IPaymentService paymentService) : ControllerBase
{
    [HttpGet("capabilities")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.PaymentsCreateOwn)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PaymentCapability>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PaymentCapability>>>> GetCapabilities(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<PaymentCapability>>.Ok(
            await paymentService.GetCapabilitiesAsync(cancellationToken)));

    [HttpPost("create")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.PaymentsCreateOwn)]
    [ProducesResponseType(typeof(ApiResponse<PaymentResult>), StatusCodes.Status201Created)]
    public async Task<ActionResult<ApiResponse<PaymentResult>>> Create(
        [FromBody] CreatePaymentApiRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, MaxLength(100)] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await paymentService.CreateAsync(
            RequireUserId(),
            new CreatePaymentRequest(request.OrderId, request.Method),
            idempotencyKey,
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResponse<PaymentResult>.Ok(result));
    }

    [HttpPost("verify")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.PaymentsCreateOwn)]
    [ProducesResponseType(typeof(ApiResponse<PaymentResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PaymentResult>>> Verify(
        [FromBody] VerifyPaymentApiRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<PaymentResult>.Ok(await paymentService.VerifyAsync(
            RequireUserId(),
            new VerifyPaymentRequest(
                request.PaymentId,
                request.GatewayOrderId,
                request.GatewayPaymentId,
                request.Signature),
            cancellationToken)));

    [HttpPost("{paymentId:guid}/complete-development")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.PaymentsCreateOwn)]
    [ProducesResponseType(typeof(ApiResponse<PaymentResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PaymentResult>>> CompleteDevelopment(
        Guid paymentId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<PaymentResult>.Ok(await paymentService.CompleteDevelopmentAsync(
            RequireUserId(), paymentId, cancellationToken)));

    [HttpPost("{paymentId:guid}/cancel")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.PaymentsCreateOwn)]
    [ProducesResponseType(typeof(ApiResponse<PaymentResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PaymentResult>>> Cancel(
        Guid paymentId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<PaymentResult>.Ok(await paymentService.CancelAsync(
            RequireUserId(), paymentId, cancellationToken)));

    [HttpGet("{paymentId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.PaymentsReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<PaymentResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PaymentResult>>> Get(
        Guid paymentId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<PaymentResult>.Ok(await paymentService.GetAsync(
            RequireUserId(), paymentId, bypassOwnership: false, cancellationToken)));

    [HttpPost("{paymentId:guid}/reconcile")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.PaymentsRefund)]
    [ProducesResponseType(typeof(ApiResponse<PaymentReconciliationResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PaymentReconciliationResult>>> Reconcile(
        Guid paymentId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<PaymentReconciliationResult>.Ok(await paymentService.ReconcileAsync(
            RequireUserId(),
            paymentId,
            bypassOwnership: true,
            cancellationToken)));

    [HttpPost("{paymentId:guid}/refund")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.PaymentsRefund)]
    [ProducesResponseType(typeof(ApiResponse<RefundResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundResult>>> Refund(
        Guid paymentId,
        [FromBody] RefundPaymentApiRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, MaxLength(100)] string idempotencyKey,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundResult>.Ok(await paymentService.RefundAsync(
            RequireUserId(),
            paymentId,
            new RefundPaymentRequest(
                request.Amount,
                request.Reason,
                idempotencyKey,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString()),
            cancellationToken)));

    private long RequireUserId()
    {
        var value = User.FindFirstValue("user_id");
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : throw new UnauthorizedAppException();
    }
}

[ApiController]
[Route("api/v1/webhooks/razorpay")]
[Tags("Payment webhooks")]
[Produces("application/json")]
[AllowAnonymous]
public sealed class RazorpayWebhooksController(IPaymentService paymentService) : ControllerBase
{
    private const int MaximumPayloadBytes = 1_048_576;

    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<object>>> Receive(
        [FromHeader(Name = "X-Razorpay-Signature"), Required, MaxLength(500)] string signature,
        CancellationToken cancellationToken)
    {
        if (Request.ContentLength is > MaximumPayloadBytes)
        {
            throw new ValidationAppException("The webhook payload is too large.");
        }

        await using var stream = new MemoryStream();
        await Request.Body.CopyToAsync(stream, cancellationToken);
        if (stream.Length == 0)
        {
            throw new ValidationAppException("The webhook payload is required.");
        }
        if (stream.Length > MaximumPayloadBytes)
        {
            throw new ValidationAppException("The webhook payload is too large.");
        }

        await paymentService.ProcessWebhookAsync(stream.ToArray(), signature, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "Webhook processed."));
    }
}

[ApiController]
[Route("api/v1/wallet")]
[Tags("Wallet")]
[Produces("application/json")]
public sealed class WalletController(
    IWalletService walletService,
    IWebHostEnvironment environment,
    IOptions<PaymentOptions> paymentOptions) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "permission:" + AuthorizationCodes.WalletReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<WalletResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<WalletResult>>> Get(CancellationToken cancellationToken) =>
        Ok(ApiResponse<WalletResult>.Ok(await walletService.GetAsync(RequireUserId(), cancellationToken)));

    [HttpGet("transactions")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.WalletReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<WalletTransactionResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<WalletTransactionResult>>>> GetTransactions(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<WalletTransactionResult>>.Ok(
            await walletService.GetTransactionsAsync(RequireUserId(), cancellationToken)));

    [HttpPost("topup")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.WalletTopUpOwn)]
    [ProducesResponseType(typeof(ApiResponse<WalletTransactionResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<WalletTransactionResult>>> TopUp(
        [FromBody] WalletTopUpApiRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, MaxLength(100)] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment() || paymentOptions.Value.IsRazorpay)
        {
            throw new NotFoundException("The development wallet top-up endpoint is not available.");
        }

        var result = await walletService.TopUpAsync(
            RequireUserId(), new WalletTopUpRequest(request.Amount, idempotencyKey), cancellationToken);
        return Ok(ApiResponse<WalletTransactionResult>.Ok(result));
    }

    private long RequireUserId()
    {
        var value = User.FindFirstValue("user_id");
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : throw new UnauthorizedAppException();
    }
}

[ApiController]
[Route("api/v1/admin/customers/{customerId:guid}/wallet")]
[Tags("Wallet administration")]
[Produces("application/json")]
public sealed class WalletAdministrationController(IWalletService walletService) : ControllerBase
{
    [HttpPost("adjust")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.WalletAdjust)]
    [ProducesResponseType(typeof(ApiResponse<WalletTransactionResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<WalletTransactionResult>>> Adjust(
        Guid customerId,
        [FromBody] WalletAdjustmentApiRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, MaxLength(100)] string idempotencyKey,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<WalletTransactionResult>.Ok(await walletService.AdjustAsync(
            RequireUserId(),
            customerId,
            new WalletAdjustmentRequest(
                request.Amount,
                request.Reason,
                idempotencyKey,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString()),
            cancellationToken)));

    private long RequireUserId()
    {
        var value = User.FindFirstValue("user_id");
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : throw new UnauthorizedAppException();
    }
}

public sealed record CreatePaymentApiRequest(
    Guid OrderId,
    PaymentMethod Method);

public sealed record VerifyPaymentApiRequest(
    Guid PaymentId,
    [Required, MaxLength(100)] string GatewayOrderId,
    [Required, MaxLength(100)] string GatewayPaymentId,
    [Required, MaxLength(500)] string Signature);

public sealed record RefundPaymentApiRequest(
    [Range(typeof(decimal), "0.01", "9999999999.99")] decimal? Amount,
    [Required, MaxLength(500)] string Reason);

public sealed record WalletTopUpApiRequest(
    [Range(typeof(decimal), "0.01", "9999999999.99")] decimal Amount);

public sealed record WalletAdjustmentApiRequest(
    decimal Amount,
    [Required, MaxLength(500)] string Reason);
