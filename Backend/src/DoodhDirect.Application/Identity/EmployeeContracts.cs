using DoodhDirect.Domain.Identity;

namespace DoodhDirect.Application.Identity;

/// <summary>
/// One branch assignment within an employee's (single-role) assignment set.
/// Stage 2 multi-branch Dairy Manager: a Dairy Manager holds one such row per
/// assigned branch under the same role; every other employee role holds at most one.
/// </summary>
public sealed record EmployeeBranchAssignment(
    long BranchId,
    string? BranchName);

/// <summary>
/// A single employee (or pending invite) visible to the caller.
/// <see cref="RoleCode"/> and the branch set are authoritative values set by the
/// administrator when creating the invitation; the invitee can never change them from
/// the client. <see cref="BranchAssignments"/> is the complete assignment collection —
/// never a truncated sample. The legacy <see cref="BranchId"/>/<see cref="BranchName"/>
/// pair carries the first assignment (ordered by branch id) for backward compatibility
/// and is null when the employee holds no branch assignment.
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
    bool IsArchived,
    DateTime? ArchivedAt,
    EmployeeInvitationStatus? InvitationStatus,
    DateTime? InvitationExpiresAt,
    DateTime? RegisteredAt,
    DateTime? CreatedAt,
    IReadOnlyList<EmployeeBranchAssignment>? BranchAssignments = null);

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

/// <summary>
/// Creates an employee and, when <see cref="SendInvitation"/> is true, a secure single-use invitation.
/// Stage 2 multi-branch Dairy Manager: <see cref="BranchIds"/>, when supplied (non-null), is the
/// authoritative branch set — every id must exist, be active, be distinct, and lie within the
/// administrator's authority, otherwise the entire request is rejected. When <see cref="BranchIds"/>
/// is null the legacy <see cref="BranchId"/> carries the single assignment. Supplying both is
/// allowed only when <see cref="BranchId"/> is contained in <see cref="BranchIds"/>; any other
/// combination is rejected rather than silently reinterpreted. Only DAIRY_MANAGER accepts more
/// than one branch; every other branched role accepts exactly one; SYSTEM_ADMIN accepts none.
/// </summary>
public sealed record CreateEmployeeRequest(
    string DisplayName,
    string Mobile,
    string Email,
    string RoleCode,
    long? BranchId,
    bool SendInvitation = true,
    DateTime? InvitationExpiresAt = null,
    IReadOnlyList<long>? BranchIds = null);

/// <summary>
/// Updates permitted employee attributes. Role and branch changes are audited.
/// Branch-set semantics mirror <see cref="CreateEmployeeRequest"/>: a non-null
/// <see cref="BranchIds"/> replaces the full assignment set atomically, while null leaves
/// assignments untouched unless <see cref="BranchId"/> (or the role) changes them.
/// </summary>
public sealed record UpdateEmployeeRequest(
    string DisplayName,
    string? Email,
    string? RoleCode,
    long? BranchId,
    bool IsActive,
    IReadOnlyList<long>? BranchIds = null);

/// <summary>
/// Resends an invitation. The invitation is invalidated and a fresh token is issued so the
/// previously delivered link can no longer be used.
/// </summary>
public sealed record ResendEmployeeInvitationRequest(
    long EmployeeId,
    long InvitationId,
    DateTime? InvitationExpiresAt = null);

public enum InvitationEmailDeliveryStatus
{
    Sent,
    Skipped,
    Failed
}

/// <summary>
/// A fresh invitation token. The raw <see cref="Token"/> is returned exactly once; only its
/// SHA-256 hash is stored. <see cref="EmailDeliveryStatus"/> reports the application's SMTP
/// attempt; <c>Sent</c> means the SMTP relay accepted the message, not guaranteed inbox delivery.
/// </summary>
public sealed record EmployeeInvitationResult(
    long InvitationId,
    Guid InvitationPublicId,
    long EmployeeId,
    string Token,
    DateTime ExpiresAt,
    InvitationEmailDeliveryStatus EmailDeliveryStatus = InvitationEmailDeliveryStatus.Skipped,
    string? EmailDeliveryReason = null,
    string? EmailRecipient = null);

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
    string? Reason,
    string? BranchCode = null,
    string? BranchName = null,
    IReadOnlyList<EmployeeBranchAssignment>? BranchAssignments = null);

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
/// Outcome of an administrative delete request for an employee. The server — never the client —
/// decides whether the account was permanently deleted (<see cref="IsDeleted"/>) because nothing
/// references it, or archived (<see cref="IsArchived"/> with the retained <see cref="Employee"/>)
/// because operational or historical records still reference it.
/// </summary>
public sealed record EmployeeDeleteResult(
    bool IsDeleted,
    bool IsArchived,
    EmployeeResult? Employee);

/// <summary>
/// Employee management service. All privileged operations require an authenticated actor
/// <paramref name="actorUserId"/> which is recorded verbatim on every audit event — the
/// employee lifecycle never attributes changes to a generic "System" actor.
/// </summary>
public interface IEmployeeService
{
    /// <summary>
    /// Lists employees visible to <paramref name="actorUserId"/>: existing employee-view
    /// permissions apply, and each returned employee shares at least one assigned branch
    /// with the caller's operational scope. Owner / System Administrator callers retain
    /// global visibility. Returned employees are deduplicated by account.
    /// </summary>
    Task<IReadOnlyList<EmployeeResult>> ListAsync(long actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns one employee when <paramref name="actorUserId"/> is authorized to view
    /// them (same scope rule as <see cref="ListAsync"/>); otherwise not found.
    /// </summary>
    Task<EmployeeResult> GetAsync(long actorUserId, long employeeId, CancellationToken cancellationToken);

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

    /// <summary>
    /// Deletes or archives a deactivated employee (admin/owner operation). The server decides
    /// which action is taken: when no operational history references the account it is permanently
    /// deleted — role assignments, sessions, refresh tokens, devices and notification preferences
    /// are removed together with the account; when operational history (deliveries, notifications,
    /// orders and similar) still references the employee the account is archived instead and kept
    /// in the database so history continues to identify the employee.
    /// </summary>
    Task<EmployeeDeleteResult> DeleteAsync(
        long employeeId,
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
