using DoodhDirect.Domain.Identity;

namespace DoodhDirect.Application.Identity;

/// <summary>
/// A single employee (or pending invite) visible to Owner / System Administrator.
/// <see cref="RoleCode"/> and <see cref="BranchId"/> are authoritative values set by the
/// administrator when creating the invitation; the invitee can never change them from
/// the client.
/// </summary>
public sealed record EmployeeResult(
    long Id,
    Guid PublicId,
    long? InvitationId,
    string DisplayName,
    string? Mobile,
    string? Email,
    string RoleCode,
    string? RoleName,
    long? BranchId,
    string? BranchName,
    bool IsActive,
    EmployeeInvitationStatus? InvitationStatus,
    DateTime? InvitationExpiresAt,
    DateTime? RegisteredAt,
    DateTime? CreatedAt);

/// <summary>
/// A branch option for the Create Employee screen. Exposes the internal <see cref="Id"/> required
/// by <see cref="CreateEmployeeRequest.BranchId"/> (the public catalogue endpoint only returns the
/// Guid <c>PublicId</c>, which is insufficient for employee assignment).
/// </summary>
public sealed record EmployeeBranchOption(
    long Id,
    Guid PublicId,
    string Code,
    string Name,
    string City,
    string State,
    bool IsActive);

/// <summary>Creates an employee and, when <see cref="SendInvitation"/> is true, a secure single-use invitation.</summary>
public sealed record CreateEmployeeRequest(
    string DisplayName,
    string Mobile,
    string? Email,
    string RoleCode,
    long? BranchId,
    bool SendInvitation = true,
    DateTime? InvitationExpiresAt = null);

/// <summary>Updates permitted employee attributes. Role and branch changes are audited.</summary>
public sealed record UpdateEmployeeRequest(
    string DisplayName,
    string? Email,
    string? RoleCode,
    long? BranchId,
    bool IsActive);

/// <summary>
/// Resends an invitation. The invitation is invalidated and a fresh token is issued so the
/// previously delivered link can no longer be used.
/// </summary>
public sealed record ResendEmployeeInvitationRequest(
    long EmployeeId,
    long InvitationId,
    DateTime? InvitationExpiresAt = null);

/// <summary>
/// A fresh invitation token. The raw <see cref="Token"/> is returned exactly once; only its
/// SHA-256 hash is stored.
/// </summary>
public sealed record EmployeeInvitationResult(
    long InvitationId,
    Guid InvitationPublicId,
    long EmployeeId,
    string Token,
    DateTime ExpiresAt);

/// <summary>
/// The outcome of creating an employee. When the administrator opted to send an invitation,
/// <see cref="Invitation"/> carries the raw token so the invitation link can be surfaced to the
/// administrator exactly once — the token is never included on list/get results.
/// </summary>
public sealed record CreateEmployeeResult(
    EmployeeResult Employee,
    EmployeeInvitationResult? Invitation);

/// <summary>Verifies an invitation token before allowing the invitee to complete registration.</summary>
public sealed record EmployeeInvitationVerificationResult(
    bool IsValid,
    string? DisplayName,
    string? Mobile,
    string? Email,
    string RoleCode,
    long? BranchId,
    string? Reason);

public sealed record CompleteEmployeeRegistrationRequest(
    string Token,
    string DisplayName,
    string? Email,
    string Mobile,
    string Password,
    string OtpCode,
    DeviceInfo Device);

public sealed record CompleteEmployeeRegistrationResult(
    AuthSessionResult Session,
    EmployeeInvitationStatus InvitationStatus);

/// <summary>
/// Employee management service. All privileged operations require an authenticated actor
/// <paramref name="actorUserId"/> which is recorded verbatim on every audit event — the
/// employee lifecycle never attributes changes to a generic "System" actor.
/// </summary>
public interface IEmployeeService
{
    Task<IReadOnlyList<EmployeeResult>> ListAsync(CancellationToken cancellationToken);

    Task<EmployeeResult> GetAsync(long employeeId, CancellationToken cancellationToken);

    Task<CreateEmployeeResult> CreateAsync(
        CreateEmployeeRequest request,
        long actorUserId,
        CancellationToken cancellationToken);

    Task<EmployeeResult> UpdateAsync(
        long employeeId,
        UpdateEmployeeRequest request,
        long actorUserId,
        CancellationToken cancellationToken);

    Task<EmployeeInvitationResult> ResendInvitationAsync(
        long employeeId,
        long invitationId,
        long actorUserId,
        CancellationToken cancellationToken,
        DateTime? invitationExpiresAt = null);

    Task CancelInvitationAsync(
        long employeeId,
        long invitationId,
        long actorUserId,
        CancellationToken cancellationToken);

    Task<EmployeeInvitationVerificationResult> VerifyInvitationAsync(
        string token,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EmployeeBranchOption>> GetBranchOptionsAsync(
        CancellationToken cancellationToken);

    Task<CompleteEmployeeRegistrationResult> CompleteRegistrationAsync(
        CompleteEmployeeRegistrationRequest request,
        CancellationToken cancellationToken);
}
