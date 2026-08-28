using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

/// <summary>
/// Employee management and invitation-based onboarding.
/// </summary>
/// <remarks>
/// Read endpoints require <see cref="AuthorizationCodes.EmployeesRead"/>; mutation endpoints require
/// <see cref="AuthorizationCodes.EmployeesManage"/>. Assigning the SYSTEM_ADMIN role additionally
/// requires <see cref="AuthorizationCodes.IdentityAdministratorsManage"/> which is enforced inside
/// the service (Owner-only). Invitation verify/complete endpoints are intentionally unauthenticated —
/// the invitation token itself is the bearer credential.
/// </remarks>
[ApiController]
[Route("api/v1/admin/employees")]
[Tags("Employees")]
[Produces("application/json")]
public sealed class EmployeeController(IEmployeeService employeeService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "permission:" + AuthorizationCodes.EmployeesRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EmployeeResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<EmployeeResult>>>> List(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<EmployeeResult>>.Ok(
            await employeeService.ListAsync(cancellationToken)));

    /// <summary>
    /// Branch options for the Create Employee screen. Unlike the catalogue endpoint this exposes the
    /// internal numeric <c>Id</c> required by <see cref="CreateEmployeeRequest.BranchId"/>.
    /// </summary>
    [HttpGet("branches")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.EmployeesRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EmployeeBranchOption>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<EmployeeBranchOption>>>> BranchOptions(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<EmployeeBranchOption>>.Ok(
            await employeeService.GetBranchOptionsAsync(cancellationToken)));

    [HttpGet("{employeeId:long}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.EmployeesRead)]
    [ProducesResponseType(typeof(ApiResponse<EmployeeResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<EmployeeResult>>> Get(
        long employeeId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<EmployeeResult>.Ok(
            await employeeService.GetAsync(employeeId, cancellationToken)));

    [HttpPost]
    [Authorize(Policy = "permission:" + AuthorizationCodes.EmployeesManage)]
    [ProducesResponseType(typeof(ApiResponse<CreateEmployeeResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CreateEmployeeResult>>> Create(
        [FromBody] CreateEmployeeRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<CreateEmployeeResult>.Ok(
            await employeeService.CreateAsync(
                request,
                RequireUserId(),
                cancellationToken)));

    [HttpPut("{employeeId:long}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.EmployeesManage)]
    [ProducesResponseType(typeof(ApiResponse<EmployeeResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<EmployeeResult>>> Update(
        long employeeId,
        [FromBody] UpdateEmployeeRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<EmployeeResult>.Ok(
            await employeeService.UpdateAsync(
                employeeId,
                request,
                RequireUserId(),
                cancellationToken)));

    [HttpPost("{employeeId:long}/invitations/{invitationId:long}/resend")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.EmployeesManage)]
    [ProducesResponseType(typeof(ApiResponse<EmployeeInvitationResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<EmployeeInvitationResult>>> ResendInvitation(
        long employeeId,
        long invitationId,
        [FromBody] ResendEmployeeInvitationRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<EmployeeInvitationResult>.Ok(
            await employeeService.ResendInvitationAsync(
                employeeId,
                invitationId,
                RequireUserId(),
                cancellationToken,
                request.InvitationExpiresAt)));

    [HttpPost("{employeeId:long}/invitations/{invitationId:long}/cancel")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.EmployeesManage)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<object>>> CancelInvitation(
        long employeeId,
        long invitationId,
        CancellationToken cancellationToken)
    {
        await employeeService.CancelInvitationAsync(
            employeeId,
            invitationId,
            RequireUserId(),
            cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "Invitation cancelled."));
    }

    private long RequireUserId()
    {
        var value = User.FindFirstValue("user_id");
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : throw new UnauthorizedAppException();
    }
}

/// <summary>Invitation verification and registration — token is the bearer credential, no session required.</summary>
[ApiController]
[Route("api/v1/employee-invitations")]
[Tags("Employee invitations")]
[Produces("application/json")]
[AllowAnonymous]
public sealed class EmployeeInvitationController(IEmployeeService employeeService) : ControllerBase
{
    [HttpGet("{token}/verify")]
    [ProducesResponseType(typeof(ApiResponse<EmployeeInvitationVerificationResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<EmployeeInvitationVerificationResult>>> Verify(
        string token,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<EmployeeInvitationVerificationResult>.Ok(
            await employeeService.VerifyInvitationAsync(token, cancellationToken)));

    [HttpPost("complete")]
    [ProducesResponseType(typeof(ApiResponse<CompleteEmployeeRegistrationResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CompleteEmployeeRegistrationResult>>> Complete(
        [FromBody] CompleteEmployeeRegistrationRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<CompleteEmployeeRegistrationResult>.Ok(
            await employeeService.CompleteRegistrationAsync(request, cancellationToken)));
}
