using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

public abstract class NotificationControllerBase : ControllerBase
{
    protected NotificationActor RequireNotificationActor() =>
        new(RequireUserId());

    protected long RequireUserId()
    {
        var value = User.FindFirstValue("user_id");
        return long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var userId)
            ? userId
            : throw new UnauthorizedAppException();
    }
}

[ApiController]
[Route("api/v1/notifications")]
[Tags("Notifications")]
[Produces("application/json")]
[Authorize]
public sealed class NotificationsController(
    INotificationService notificationService) : NotificationControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<NotificationPageResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<NotificationPageResult>>> Get(
        [FromQuery] NotificationListRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<NotificationPageResult>.Ok(
            await notificationService.GetAsync(
                RequireNotificationActor(),
                request,
                cancellationToken)));

    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(ApiResponse<NotificationUnreadCountResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<NotificationUnreadCountResult>>> GetUnreadCount(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<NotificationUnreadCountResult>.Ok(
            await notificationService.GetUnreadCountAsync(
                RequireNotificationActor(),
                cancellationToken)));

    [HttpPost("{notificationId:guid}/read")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<object>>> MarkRead(
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        await notificationService.MarkReadAsync(
            RequireNotificationActor(),
            notificationId,
            cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "Notification marked as read."));
    }
}

[ApiController]
[Route("api/v1/devices")]
[Tags("Notification devices")]
[Produces("application/json")]
[Authorize]
public sealed class NotificationDevicesController(
    INotificationService notificationService) : NotificationControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<UserDeviceResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<UserDeviceResult>>> Register(
        [FromBody] RegisterDeviceRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<UserDeviceResult>.Ok(
            await notificationService.RegisterDeviceAsync(
                RequireNotificationActor(),
                request,
                cancellationToken),
            "Notification device registered."));
}

[ApiController]
[Route("api/v1/notification-preferences")]
[Tags("Notification preferences")]
[Produces("application/json")]
[Authorize]
public sealed class NotificationPreferencesController(
    INotificationService notificationService) : NotificationControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyCollection<NotificationPreferenceResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<NotificationPreferenceResult>>>> Get(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyCollection<NotificationPreferenceResult>>.Ok(
            await notificationService.GetPreferencesAsync(
                RequireNotificationActor(),
                cancellationToken)));

    [HttpPatch]
    [ProducesResponseType(typeof(ApiResponse<NotificationPreferenceResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<NotificationPreferenceResult>>> Update(
        [FromBody] UpdateNotificationPreferenceRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<NotificationPreferenceResult>.Ok(
            await notificationService.UpdatePreferenceAsync(
                RequireNotificationActor(),
                request,
                cancellationToken),
            "Notification preference updated."));
}

[ApiController]
[Route("api/v1/admin/notification-templates")]
[Tags("Notification template administration")]
[Produces("application/json")]
public sealed class NotificationTemplateAdministrationController(
    INotificationTemplateService templateService) : NotificationControllerBase
{
    [HttpGet]
    [Authorize(Policy = "permission:" + AuthorizationCodes.NotificationTemplatesRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyCollection<NotificationTemplateResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<NotificationTemplateResult>>>> Get(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyCollection<NotificationTemplateResult>>.Ok(
            await templateService.GetAsync(cancellationToken)));

    [HttpPatch("{templateId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.NotificationTemplatesManage)]
    [ProducesResponseType(typeof(ApiResponse<NotificationTemplateResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<NotificationTemplateResult>>> Update(
        Guid templateId,
        [FromBody] UpdateNotificationTemplateRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<NotificationTemplateResult>.Ok(
            await templateService.UpdateAsync(
                RequireUserId(),
                templateId,
                request,
                cancellationToken),
            "Notification template updated."));
}
