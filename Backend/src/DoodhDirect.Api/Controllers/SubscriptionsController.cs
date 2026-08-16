using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

[ApiController]
[Route("api/v1/subscriptions")]
[Tags("Subscriptions")]
[Produces("application/json")]
public sealed class SubscriptionsController(ISubscriptionService subscriptionService) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SubscriptionsCreateOwn)]
    [ProducesResponseType(typeof(ApiResponse<CreatedSubscriptionResult>), StatusCodes.Status201Created)]
    public async Task<ActionResult<ApiResponse<CreatedSubscriptionResult>>> Create(
        [FromBody] CreateSubscriptionRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, MaxLength(100)] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await subscriptionService.CreateAsync(
            RequireUserId(), request, idempotencyKey, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResponse<CreatedSubscriptionResult>.Ok(result));
    }

    [HttpGet]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SubscriptionsReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<SubscriptionResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<SubscriptionResult>>>> GetMine(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<SubscriptionResult>>.Ok(
            await subscriptionService.GetForCustomerAsync(RequireUserId(), cancellationToken)));

    [HttpGet("{subscriptionId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SubscriptionsReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<SubscriptionResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<SubscriptionResult>>> Get(
        Guid subscriptionId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<SubscriptionResult>.Ok(
            await subscriptionService.GetAsync(RequireUserId(), subscriptionId, cancellationToken)));

    [HttpPatch("{subscriptionId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SubscriptionsManageOwn)]
    [ProducesResponseType(typeof(ApiResponse<SubscriptionResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<SubscriptionResult>>> Update(
        Guid subscriptionId,
        [FromBody] UpdateSubscriptionRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<SubscriptionResult>.Ok(await subscriptionService.UpdateAsync(
            RequireUserId(), subscriptionId, request, cancellationToken)));

    [HttpPost("{subscriptionId:guid}/pause")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SubscriptionsManageOwn)]
    [ProducesResponseType(typeof(ApiResponse<SubscriptionResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<SubscriptionResult>>> Pause(
        Guid subscriptionId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<SubscriptionResult>.Ok(await subscriptionService.PauseAsync(
            RequireUserId(), subscriptionId, cancellationToken)));

    [HttpPost("{subscriptionId:guid}/resume")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SubscriptionsManageOwn)]
    [ProducesResponseType(typeof(ApiResponse<SubscriptionResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<SubscriptionResult>>> Resume(
        Guid subscriptionId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<SubscriptionResult>.Ok(await subscriptionService.ResumeAsync(
            RequireUserId(), subscriptionId, cancellationToken)));

    [HttpPost("{subscriptionId:guid}/cancel")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SubscriptionsManageOwn)]
    [ProducesResponseType(typeof(ApiResponse<SubscriptionResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<SubscriptionResult>>> Cancel(
        Guid subscriptionId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<SubscriptionResult>.Ok(await subscriptionService.CancelAsync(
            RequireUserId(), subscriptionId, cancellationToken)));

    [HttpPost("{subscriptionId:guid}/skip")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SubscriptionsManageOwn)]
    [ProducesResponseType(typeof(ApiResponse<SubscriptionDeliveryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<SubscriptionDeliveryResult>>> Skip(
        Guid subscriptionId,
        [FromBody] SkipSubscriptionDeliveryRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<SubscriptionDeliveryResult>.Ok(await subscriptionService.SkipAsync(
            RequireUserId(), subscriptionId, request, cancellationToken)));

    [HttpGet("{subscriptionId:guid}/calendar")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SubscriptionsReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<SubscriptionDeliveryResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<SubscriptionDeliveryResult>>>> GetCalendar(
        Guid subscriptionId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<SubscriptionDeliveryResult>>.Ok(
            await subscriptionService.GetCalendarAsync(RequireUserId(), subscriptionId, cancellationToken)));

    private long RequireUserId()
    {
        var value = User.FindFirstValue("user_id");
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : throw new UnauthorizedAppException();
    }
}
