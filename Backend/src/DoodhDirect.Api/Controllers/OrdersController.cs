using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

[ApiController]
[Route("api/v1/orders")]
[Tags("Orders")]
[Produces("application/json")]
public sealed class OrdersController(IOrderService orderService) : ControllerBase
{
    [HttpPost("checkout-preview")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.OrdersCreateOwn)]
    [ProducesResponseType(typeof(ApiResponse<CheckoutResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CheckoutResult>>> Preview(
        [FromBody] CheckoutApiRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<CheckoutResult>.Ok(await orderService.PreviewAsync(
            RequireUserId(), request.ToApplicationRequest(), cancellationToken)));

    [HttpPost]
    [Authorize(Policy = "permission:" + AuthorizationCodes.OrdersCreateOwn)]
    [ProducesResponseType(typeof(ApiResponse<OrderResult>), StatusCodes.Status201Created)]
    public async Task<ActionResult<ApiResponse<OrderResult>>> Create(
        [FromBody] CheckoutApiRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, MaxLength(100)] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await orderService.CreateAsync(
            RequireUserId(), request.ToApplicationRequest(), idempotencyKey, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResponse<OrderResult>.Ok(result));
    }

    [HttpGet]
    [Authorize(Policy = "permission:" + AuthorizationCodes.OrdersReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<OrderResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<OrderResult>>>> GetMine(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<OrderResult>>.Ok(
            await orderService.GetForCustomerAsync(RequireUserId(), cancellationToken)));

    [HttpGet("{orderId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.OrdersReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<OrderResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<OrderResult>>> Get(Guid orderId, CancellationToken cancellationToken) =>
        Ok(ApiResponse<OrderResult>.Ok(await orderService.GetAsync(
            RequireUserId(), orderId, bypassOwnership: false, cancellationToken)));

    [HttpPost("{orderId:guid}/cancel")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.OrdersCancelOwn)]
    [ProducesResponseType(typeof(ApiResponse<OrderResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<OrderResult>>> Cancel(Guid orderId, CancellationToken cancellationToken) =>
        Ok(ApiResponse<OrderResult>.Ok(await orderService.CancelAsync(
            RequireUserId(), orderId, cancellationToken)));

    private long RequireUserId()
    {
        var value = User.FindFirstValue("user_id");
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : throw new UnauthorizedAppException();
    }
}

[ApiController]
[Route("api/v1/admin/orders")]
[Tags("Order administration")]
[Produces("application/json")]
[Authorize(Policy = "permission:" + AuthorizationCodes.OrdersRead)]
public sealed class OrderAdministrationController(IOrderService orderService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<OrderResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<OrderResult>>>> GetOrders(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<OrderResult>>.Ok(
            await orderService.GetForAdministrationAsync(cancellationToken)));

    [HttpGet("{orderId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<OrderResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<OrderResult>>> GetOrder(Guid orderId, CancellationToken cancellationToken) =>
        Ok(ApiResponse<OrderResult>.Ok(await orderService.GetAsync(
            customerId: 0, orderId, bypassOwnership: true, cancellationToken)));
}

public sealed record CheckoutApiRequest(
    Guid AddressId,
    [Required, MinLength(1)] IReadOnlyCollection<OrderItemApiRequest> Items)
{
    public CheckoutRequest ToApplicationRequest() =>
        new(AddressId, Items.Select(item => new OrderItemRequest(item.ProductId, item.Quantity)).ToArray());
}

public sealed record OrderItemApiRequest(Guid ProductId, decimal Quantity);
