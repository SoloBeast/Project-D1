using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Setup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

[ApiController]
[Route("api/v1/admin/setup/number-series")]
[Tags("Number series setup")]
[Produces("application/json")]
[Authorize(Policy = "permission:" + AuthorizationCodes.SetupNumberSeriesRead)]
public sealed class NumberSeriesController(INumberSeriesService numberSeriesService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<NumberSeriesResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<NumberSeriesResult>>>> List(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<NumberSeriesResult>>.Ok(
            await numberSeriesService.ListAsync(cancellationToken)));

    [HttpGet("{code}")]
    [ProducesResponseType(typeof(ApiResponse<NumberSeriesResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<NumberSeriesResult>>> Get(
        string code,
        [FromQuery] string? scope,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<NumberSeriesResult>.Ok(
            await numberSeriesService.GetAsync(code, cancellationToken, scope)));

    [HttpPost("preview")]
    [ProducesResponseType(typeof(ApiResponse<NumberSeriesPreviewResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<NumberSeriesPreviewResult>>> Preview(
        [FromBody] NumberSeriesPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var nextNumber = request.NextNumber
            ?? (await numberSeriesService.PreviewNextNumberAsync(request.Code, cancellationToken, request.Scope)).NextNumber;

        return Ok(ApiResponse<NumberSeriesPreviewResult>.Ok(
            numberSeriesService.PreviewTemplate(request.Code, request.Template, nextNumber, request.Scope)));
    }

    [HttpPost]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupNumberSeriesManage)]
    [ProducesResponseType(typeof(ApiResponse<NumberSeriesResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<NumberSeriesResult>>> Create(
        [FromBody] CreateNumberSeriesRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<NumberSeriesResult>.Ok(
            await numberSeriesService.CreateAsync(
                request,
                RequireUserId(),
                cancellationToken)));

    [HttpPut("{code}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupNumberSeriesManage)]
    [ProducesResponseType(typeof(ApiResponse<NumberSeriesResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<NumberSeriesResult>>> Update(
        string code,
        [FromQuery] string? scope,
        [FromBody] UpdateNumberSeriesRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<NumberSeriesResult>.Ok(
            await numberSeriesService.UpdateAsync(
                code,
                request,
                RequireUserId(),
                cancellationToken,
                scope)));

    [HttpPost("{code}/activate")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupNumberSeriesManage)]
    [ProducesResponseType(typeof(ApiResponse<NumberSeriesResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<NumberSeriesResult>>> Activate(
        string code,
        [FromQuery] string? scope,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<NumberSeriesResult>.Ok(
            await numberSeriesService.SetActiveAsync(
                code,
                true,
                RequireUserId(),
                cancellationToken,
                scope)));

    [HttpPost("{code}/deactivate")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupNumberSeriesManage)]
    [ProducesResponseType(typeof(ApiResponse<NumberSeriesResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<NumberSeriesResult>>> Deactivate(
        string code,
        [FromQuery] string? scope,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<NumberSeriesResult>.Ok(
            await numberSeriesService.SetActiveAsync(
                code,
                false,
                RequireUserId(),
                cancellationToken,
                scope)));

    private long RequireUserId()
    {
        var value = User.FindFirstValue("user_id");
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : throw new UnauthorizedAppException();
    }
}
