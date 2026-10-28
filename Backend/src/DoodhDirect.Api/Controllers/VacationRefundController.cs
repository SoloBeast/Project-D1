using DoodhDirect.Api.Authorization;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.RefundReplacement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

/// <summary>
/// Support-facing end-of-subscription vacation-refund endpoints. Requests are
/// created automatically at the natural end of a prepaid subscription and
/// carry a 0% deduction on frozen pricing; support reviews them here and
/// settles through the authorized completion workflow. Creation and approval
/// never credit the wallet — only completion does.
/// </summary>
[ApiController]
[Route("api/v1/vacation-refunds")]
[Tags("Vacation-refund requests")]
[Produces("application/json")]
public sealed class VacationRefundController(
    IVacationRefundService vacationRefundService) : RefundReplacementControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicyNames.AnyRefundReplacementRequestView)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<VacationRefundRequestResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<VacationRefundRequestResult>>>> List(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<VacationRefundRequestResult>>.Ok(
            await vacationRefundService.ListForStaffAsync(
                RequireRefundReplacementActor(), cancellationToken)));

    [HttpGet("{requestId:guid}")]
    [Authorize(Policy = AuthorizationPolicyNames.AnyRefundReplacementRequestView)]
    [ProducesResponseType(typeof(ApiResponse<VacationRefundRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<VacationRefundRequestResult>>> Get(
        Guid requestId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<VacationRefundRequestResult>.Ok(
            await vacationRefundService.GetForStaffAsync(
                RequireRefundReplacementActor(), requestId, cancellationToken)));

    [HttpPost("{requestId:guid}/approve")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementManageBranch)]
    [ProducesResponseType(typeof(ApiResponse<VacationRefundRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<VacationRefundRequestResult>>> Approve(
        Guid requestId,
        [FromBody] DecideVacationRefundRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<VacationRefundRequestResult>.Ok(
            await vacationRefundService.ApproveAsync(
                RequireRefundReplacementActor(), requestId, request, cancellationToken)));

    [HttpPost("{requestId:guid}/reject")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementManageBranch)]
    [ProducesResponseType(typeof(ApiResponse<VacationRefundRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<VacationRefundRequestResult>>> Reject(
        Guid requestId,
        [FromBody] DecideVacationRefundRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<VacationRefundRequestResult>.Ok(
            await vacationRefundService.RejectAsync(
                RequireRefundReplacementActor(), requestId, request, cancellationToken)));

    /// <summary>
    /// Authorized settlement point: completes the Approved request and
    /// credits the frozen 0%-deduction total to the wallet.
    /// </summary>
    [HttpPost("{requestId:guid}/complete")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementManageBranch)]
    [ProducesResponseType(typeof(ApiResponse<VacationRefundRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<VacationRefundRequestResult>>> Complete(
        Guid requestId,
        [FromBody] DecideVacationRefundRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<VacationRefundRequestResult>.Ok(
            await vacationRefundService.CompleteAsync(
                RequireRefundReplacementActor(), requestId, request, cancellationToken)));
}

/// <summary>
/// Customer-facing vacation-refund history. States are distinct: a request
/// being listed as created or approved never means the refund was issued —
/// only a completed request carries a wallet credit.
/// </summary>
[ApiController]
[Route("api/v1/subscriptions/vacation-refunds")]
[Tags("Customer vacation-refund requests")]
[Produces("application/json")]
public sealed class CustomerVacationRefundController(
    IVacationRefundService vacationRefundService) : RefundReplacementControllerBase
{
    [HttpGet]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<VacationRefundRequestResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<VacationRefundRequestResult>>>> ListMine(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<VacationRefundRequestResult>>.Ok(
            await vacationRefundService.ListForCustomerAsync(
                RequireRefundReplacementActor(), cancellationToken)));

    [HttpGet("{subscriptionId:guid}/vacation-refund-status")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<VacationRefundSubscriptionStatus>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<VacationRefundSubscriptionStatus>>> GetStatus(
        Guid subscriptionId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<VacationRefundSubscriptionStatus>.Ok(
            await vacationRefundService.GetStatusForCustomerAsync(
                RequireRefundReplacementActor(), subscriptionId, cancellationToken)));

    [HttpGet("{requestId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<VacationRefundRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<VacationRefundRequestResult>>> GetMine(
        Guid requestId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<VacationRefundRequestResult>.Ok(
            await vacationRefundService.GetForCustomerAsync(
                RequireRefundReplacementActor(), requestId, cancellationToken)));
}
