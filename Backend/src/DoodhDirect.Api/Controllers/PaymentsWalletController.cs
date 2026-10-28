using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Domain.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
            new CreatePaymentRequest(
                request.OrderId,
                request.Method,
                request.UseWalletBalance,
                request.ExpectedPayableAmount),
            idempotencyKey,
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResponse<PaymentResult>.Ok(result));
    }

    /// <summary>
    /// Starts a wallet-first split plan for an order. The client states only
    /// whether to apply the wallet; every amount is server-computed from the
    /// frozen order total and the actual wallet balance. With wallet usage
    /// off this returns a Razorpay-only plan identical to a create call.
    /// </summary>
    [HttpPost("create-plan")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.PaymentsCreateOwn)]
    [ProducesResponseType(typeof(ApiResponse<PaymentPlanResult>), StatusCodes.Status201Created)]
    public async Task<ActionResult<ApiResponse<PaymentPlanResult>>> CreatePlan(
        [FromBody] CreatePaymentPlanApiRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, MaxLength(100)] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await paymentService.CreateOrderPlanAsync(
            RequireUserId(),
            request.OrderId,
            request.UseWalletBalance,
            idempotencyKey,
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResponse<PaymentPlanResult>.Ok(result));
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
    IPaymentService paymentService,
    IWithdrawalService withdrawalService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "permission:" + AuthorizationCodes.WalletReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<WalletResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<WalletResult>>> Get(CancellationToken cancellationToken) =>
        Ok(ApiResponse<WalletResult>.Ok(await walletService.GetAsync(RequireUserId(), cancellationToken)));

    /// <summary>
    /// Starts a customer wallet top-up. The server records the authoritative amount,
    /// creates a pending Razorpay wallet-top-up payment and gateway order, and returns
    /// the checkout details. The wallet is NOT credited here; it is credited only after
    /// the payment is verified successfully on the server.
    /// </summary>
    [HttpPost("top-up")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.WalletTopUpOwn)]
    [ProducesResponseType(typeof(ApiResponse<PaymentResult>), StatusCodes.Status201Created)]
    public async Task<ActionResult<ApiResponse<PaymentResult>>> TopUp(
        [FromBody] WalletTopUpApiRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, MaxLength(100)] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await paymentService.CreateWalletTopUpAsync(
            RequireUserId(),
            request.Amount,
            idempotencyKey,
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResponse<PaymentResult>.Ok(result));
    }

    [HttpGet("transactions")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.WalletReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<WalletTransactionResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<WalletTransactionResult>>>> GetTransactions(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<WalletTransactionResult>>.Ok(
            await walletService.GetTransactionsAsync(RequireUserId(), cancellationToken)));

    /// <summary>
    /// Requests a wallet withdrawal to the customer's saved bank account.
    /// The backend revalidates bank details, authorization, and the live
    /// balance: the debit happens atomically with request creation and the
    /// request stays pending until staff confirm the bank transfer.
    /// </summary>
    [HttpPost("withdrawals")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.WalletWithdrawOwn)]
    [ProducesResponseType(typeof(ApiResponse<WithdrawalRequestResult>), StatusCodes.Status201Created)]
    public async Task<ActionResult<ApiResponse<WithdrawalRequestResult>>> SubmitWithdrawal(
        [FromBody] CreateWithdrawalApiRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, MaxLength(100)] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await withdrawalService.SubmitAsync(
            RequireUserId(),
            new CreateWithdrawalRequest(request.Amount, idempotencyKey),
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResponse<WithdrawalRequestResult>.Ok(
            result, $"Withdrawal request {result.RequestNumber} is pending review."));
    }

    [HttpGet("withdrawals")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.WalletWithdrawOwn)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<WithdrawalRequestResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<WithdrawalRequestResult>>>> ListWithdrawals(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<WithdrawalRequestResult>>.Ok(
            await withdrawalService.ListForCustomerAsync(RequireUserId(), cancellationToken)));

    [HttpGet("withdrawals/{withdrawalId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.WalletWithdrawOwn)]
    [ProducesResponseType(typeof(ApiResponse<WithdrawalRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<WithdrawalRequestResult>>> GetWithdrawal(
        Guid withdrawalId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<WithdrawalRequestResult>.Ok(
            await withdrawalService.GetForCustomerAsync(RequireUserId(), withdrawalId, cancellationToken)));

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

[ApiController]
[Route("api/v1/admin/withdrawals")]
[Tags("Withdrawal administration")]
[Produces("application/json")]
public sealed class WithdrawalAdministrationController(IWithdrawalService withdrawalService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "permission:" + AuthorizationCodes.WithdrawalsProcess)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<StaffWithdrawalRequestResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StaffWithdrawalRequestResult>>>> List(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<StaffWithdrawalRequestResult>>.Ok(
            await withdrawalService.ListForStaffAsync(cancellationToken)));

    [HttpGet("{withdrawalId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.WithdrawalsProcess)]
    [ProducesResponseType(typeof(ApiResponse<StaffWithdrawalRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<StaffWithdrawalRequestResult>>> Get(
        Guid withdrawalId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<StaffWithdrawalRequestResult>.Ok(
            await withdrawalService.GetForStaffAsync(withdrawalId, cancellationToken)));

    /// <summary>
    /// Returns the full destination bank details for a withdrawal request.
    /// Restricted to staff holding the withdrawal-processing permission;
    /// every view is audit-logged and the account number is never logged.
    /// </summary>
    [HttpGet("{withdrawalId:guid}/bank-detail")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.WithdrawalsProcess)]
    [ProducesResponseType(typeof(ApiResponse<WithdrawalBankDetailResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<WithdrawalBankDetailResult>>> GetBankDetail(
        Guid withdrawalId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<WithdrawalBankDetailResult>.Ok(
            await withdrawalService.GetBankDetailForStaffAsync(RequireUserId(), withdrawalId, cancellationToken)));

    /// <summary>
    /// Confirms the bank transfer for a pending withdrawal. Moves no money:
    /// the wallet was debited atomically at submission. Requires the bank
    /// transfer reference (UTR) recorded when staff made the transfer.
    /// </summary>
    [HttpPost("{withdrawalId:guid}/complete")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.WithdrawalsProcess)]
    [ProducesResponseType(typeof(ApiResponse<StaffWithdrawalRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<StaffWithdrawalRequestResult>>> Complete(
        Guid withdrawalId,
        [FromBody] CompleteWithdrawalApiRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<StaffWithdrawalRequestResult>.Ok(
            await withdrawalService.CompleteAsync(
                RequireUserId(),
                withdrawalId,
                new CompleteWithdrawalRequest(request.TransferReference, request.Remarks),
                cancellationToken)));

    /// <summary>
    /// Rejects a withdrawal and returns the amount to the wallet through a
    /// compensating reversal credit. Repeating a rejection never credits twice.
    /// </summary>
    [HttpPost("{withdrawalId:guid}/reject")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.WithdrawalsProcess)]
    [ProducesResponseType(typeof(ApiResponse<StaffWithdrawalRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<StaffWithdrawalRequestResult>>> Reject(
        Guid withdrawalId,
        [FromBody] DecideWithdrawalApiRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<StaffWithdrawalRequestResult>.Ok(
            await withdrawalService.RejectAsync(
                RequireUserId(), withdrawalId, new DecideWithdrawalRequest(request.Remarks), cancellationToken)));

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
    PaymentMethod Method,
    bool UseWalletBalance = false,
    decimal? ExpectedPayableAmount = null);

public sealed record CreatePaymentPlanApiRequest(
    Guid OrderId,
    bool UseWalletBalance = false);

public sealed record VerifyPaymentApiRequest(
    Guid PaymentId,
    [Required, MaxLength(100)] string GatewayOrderId,
    [Required, MaxLength(100)] string GatewayPaymentId,
    [Required, MaxLength(500)] string Signature);

public sealed record RefundPaymentApiRequest(
    [Range(typeof(decimal), "0.01", "9999999999.99")] decimal? Amount,
    [Required, MaxLength(500)] string Reason);

public sealed record WalletAdjustmentApiRequest(
    decimal Amount,
    [Required, MaxLength(500)] string Reason);

public sealed record WalletTopUpApiRequest(
    [Range(typeof(decimal), "0.01", "9999999999.99")] decimal Amount);

public sealed record CreateWithdrawalApiRequest(
    [Range(typeof(decimal), "0.01", "9999999999.99")] decimal Amount);

public sealed record DecideWithdrawalApiRequest(
    [MaxLength(1000)] string? Remarks);

public sealed record CompleteWithdrawalApiRequest(
    [Required, MinLength(6), MaxLength(100)] string TransferReference,
    [MaxLength(1000)] string? Remarks);
