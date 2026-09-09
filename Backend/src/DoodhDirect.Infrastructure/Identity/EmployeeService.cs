using System.Data;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Integrations;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Identity;

/// <summary>
/// Employee management and invitation-based onboarding service.
/// </summary>
/// <remarks>
/// Every privileged operation receives the real authenticated <paramref name="actorUserId"/>
/// which is recorded verbatim on each audit event — the employee lifecycle never attributes
/// changes to a generic "System" actor. Role and branch are authoritative values taken from
/// the invitation; the invitee can never change them from the client.
/// </remarks>
public sealed class EmployeeService(
    DoodhDirectDbContext dbContext,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    IIndiaTimeProvider timeProvider,
    INotificationEventWriter notificationEventWriter,
    SecureTokenGenerator tokenGenerator,
    IMsg91OtpProvider otpProvider,
    IOptions<IdentityOptions> options,
    IEmailSender? emailSender = null,
    IIntegrationSettingsProvider? integrationSettings = null,
    ILogger<EmployeeService>? logger = null) : IEmployeeService
{
    private readonly IdentityOptions _identityOptions = options.Value;
    public const string ActionCreated = "EMPLOYEE.CREATED";
    public const string ActionInvited = "EMPLOYEE.INVITED";
    public const string ActionRegistered = "EMPLOYEE.REGISTERED";
    public const string ActionActivated = "EMPLOYEE.ACTIVATED";
    public const string ActionRoleChanged = "EMPLOYEE.ROLE_CHANGED";
    public const string ActionBranchChanged = "EMPLOYEE.BRANCH_CHANGED";
    public const string ActionDeactivated = "EMPLOYEE.DEACTIVATED";
    public const string ActionReactivated = "EMPLOYEE.REACTIVATED";
    public const string ActionInvitationResent = "EMPLOYEE.INVITATION_RESENT";
    public const string ActionInvitationCancelled = "EMPLOYEE.INVITATION_CANCELLED";
    public const string ActionInvitationEmailSent = "EMPLOYEE.INVITATION_EMAIL_SENT";
    public const string ActionInvitationEmailSkipped = "EMPLOYEE.INVITATION_EMAIL_SKIPPED";
    public const string ActionInvitationEmailFailed = "EMPLOYEE.INVITATION_EMAIL_FAILED";
    public const string ActionDeleted = "EMPLOYEE.DELETED";
    public const string ActionArchived = "EMPLOYEE.ARCHIVED";

    private static readonly TimeSpan DefaultInvitationLifetime = TimeSpan.FromDays(7);

    private static readonly HashSet<string> EmployeeRoleCodes = new(StringComparer.Ordinal)
    {
        AuthorizationCodes.DeliveryStaff,
        AuthorizationCodes.DeliveryManager,
        AuthorizationCodes.Accountant,
        AuthorizationCodes.DairyManager,
        AuthorizationCodes.SystemAdmin
    };

    public async Task<IReadOnlyList<EmployeeResult>> ListAsync(CancellationToken cancellationToken)
    {
        var users = await dbContext.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .Where(x => (x.UserType == UserType.Employee || x.UserType == UserType.SystemAdministrator) && !x.IsArchived)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);

        var mobiles = users.Select(x => x.Mobile).Where(x => x is not null).Distinct(StringComparer.Ordinal).ToArray();
        IReadOnlyList<EmployeeInvitation> invitations = mobiles.Length == 0
            ? Array.Empty<EmployeeInvitation>()
            : await dbContext.EmployeeInvitations
                .Where(x => mobiles.Contains(x.InviteeMobile))
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync(cancellationToken);
        var latestByMobile = invitations
            .GroupBy(x => x.InviteeMobile)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var branchNames = await LoadBranchNamesAsync(
            users.SelectMany(x => x.UserRoles)
                .Select(x => x.BranchId)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .Distinct()
                .ToArray(),
            cancellationToken);

        return users
            .Select(x => ToEmployeeResult(
                x,
                x.Mobile is not null && latestByMobile.TryGetValue(x.Mobile, out var invitation) ? invitation : null,
                branchNames))
            .ToList();
    }

    public async Task<EmployeeResult> GetAsync(long employeeId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .SingleOrDefaultAsync(x => x.Id == employeeId, cancellationToken)
            ?? throw new NotFoundException("The employee was not found.");
        EnsureEmployeeUser(user);

        var invitation = await dbContext.EmployeeInvitations
            .Where(x => x.InviteeMobile == user.Mobile)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var branchNames = await LoadBranchNamesAsync(
            user.UserRoles
                .Select(x => x.BranchId)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .Distinct()
                .ToArray(),
            cancellationToken);

        return ToEmployeeResult(user, invitation, branchNames);
    }

    public async Task<CreateEmployeeResult> CreateAsync(
        CreateEmployeeRequest request,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var displayName = Require(request.DisplayName, "Display name is required.", nameof(request.DisplayName));
        var mobile = NormalizeMobile(request.Mobile)
            ?? throw new ValidationAppException("Mobile number is required.", nameof(request.Mobile));
        var email = RequireEmail(request.Email);
        var roleCode = Require(request.RoleCode, "Role is required.", nameof(request.RoleCode));
        EnsureEmployeeRole(roleCode);
        await EnsureRoleAssignableAsync(roleCode, actorUserId, cancellationToken);
        await EnsureContactIsAvailableAsync(email, mobile, null, cancellationToken);

        var role = await GetRoleAsync(roleCode, cancellationToken);
        long? branchId = request.BranchId;
        if (roleCode == AuthorizationCodes.SystemAdmin)
        {
            branchId = null;
        }
        else
        {
            if (branchId is null)
                throw new ValidationAppException("A branch is required for this role.", nameof(request.BranchId));
            await EnsureBranchAsync(branchId.Value, cancellationToken);
        }

        var now = timeProvider.Now;
        var user = new User(roleCode == AuthorizationCodes.SystemAdmin ? UserType.SystemAdministrator : UserType.Employee);
        user.SetProfile(displayName);
        user.SetContact(mobile, email);
        user.AssignRole(role, branchId);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);

        EmployeeInvitation? invitation = null;
        string? token = null;
        InvitationEmailDelivery? emailDelivery = null;
        if (request.SendInvitation)
        {
            (invitation, token, emailDelivery) = await CreateInvitationAsync(user, roleCode, branchId, request.InvitationExpiresAt, actorUserId, now, cancellationToken);
        }

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            invitation is null ? ActionCreated : ActionInvited,
            "Employee",
            user.PublicId.ToString(),
            null,
            EmployeeSnapshot(user),
            null,
            null,
            $"Created employee with role {roleCode}." + (invitation is null ? string.Empty : " A secure invitation was issued."),
            now));
        await dbContext.SaveChangesAsync(cancellationToken);

        var branchNames = await LoadBranchNamesAsync(branchId is null ? Array.Empty<long>() : new[] { branchId.Value }, cancellationToken);
        var employee = ToEmployeeResult(user, invitation, branchNames);
        var invitationResult = invitation is null || token is null || emailDelivery is null
            ? null
            : ToInvitationResult(invitation, user.Id, token, emailDelivery);
        return new CreateEmployeeResult(employee, invitationResult);
    }

    public async Task<EmployeeResult> UpdateAsync(
        long employeeId,
        UpdateEmployeeRequest request,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .SingleOrDefaultAsync(x => x.Id == employeeId, cancellationToken)
            ?? throw new NotFoundException("The employee was not found.");
        EnsureEmployeeUser(user);
        if (user.IsArchived)
            throw new BusinessRuleException("An archived employee cannot be updated or reactivated.");

        var displayName = Require(request.DisplayName, "Display name is required.", nameof(request.DisplayName));
        var email = NormalizeEmail(request.Email);
        var currentAssignment = user.UserRoles.FirstOrDefault(x => x.Role is not null);
        var currentRoleCode = currentAssignment?.Role.Code ?? string.Empty;
        var currentBranchId = currentAssignment?.BranchId;
        var newRoleCode = string.IsNullOrWhiteSpace(request.RoleCode) ? currentRoleCode : request.RoleCode.Trim();
        var newBranchId = request.BranchId ?? currentBranchId;
        var wasActive = user.IsActive;

        EnsureEmployeeRole(newRoleCode);
        await EnsureRoleAssignableAsync(newRoleCode, actorUserId, cancellationToken);
        if (newRoleCode == AuthorizationCodes.SystemAdmin)
        {
            newBranchId = null;
        }
        else
        {
            if (newBranchId is null)
                throw new ValidationAppException("A branch is required for this role.", nameof(request.BranchId));

            // Deactivating an employee must remain possible after their branch has
            // been deactivated. The branch is still a valid historical assignment.
            // A branch must be active when the employee is being activated or when
            // the administrator explicitly changes the assignment/role.
            var assignmentChanged = currentBranchId != newBranchId;
            var assignmentRoleChanged = !string.Equals(currentRoleCode, newRoleCode, StringComparison.Ordinal);
            var assignedBranch = await GetBranchAsync(newBranchId.Value, cancellationToken);
            if (!assignedBranch.IsActive &&
                (assignmentChanged || assignmentRoleChanged || request.IsActive))
                throw new ValidationAppException("The selected branch is inactive.", nameof(request.BranchId));
        }

        var now = timeProvider.Now;
        var oldSnapshot = EmployeeSnapshot(user);
        var roleChanged = !string.Equals(currentRoleCode, newRoleCode, StringComparison.Ordinal);
        var branchChanged = currentBranchId != newBranchId;

        if (roleChanged || branchChanged)
        {
            var role = await GetRoleAsync(newRoleCode, cancellationToken);
            var existing = user.UserRoles.ToList();
            dbContext.UserRoles.RemoveRange(existing);
            user.UserRoles.Clear();
            user.AssignRole(role, newBranchId);

            var pendingInvitation = await dbContext.EmployeeInvitations
                .Where(x => x.InviteeMobile == user.Mobile && x.Status == EmployeeInvitationStatus.Invited)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            pendingInvitation?.ChangeRoleAndBranch(newRoleCode, newBranchId);
        }

        user.SetProfile(displayName);
        user.SetContact(user.Mobile, email);
        if (request.IsActive && !wasActive && newRoleCode != AuthorizationCodes.SystemAdmin)
            await EnsureBranchAsync(newBranchId!.Value, cancellationToken);
        if (wasActive && !request.IsActive)
            user.Deactivate();
        if (!wasActive && request.IsActive)
            user.Activate();

        if (roleChanged)
        {
            dbContext.AddAuditLog(new AuditLog(actorUserId, ActionRoleChanged, "Employee", user.PublicId.ToString(), oldSnapshot, EmployeeSnapshot(user), null, null, $"Role changed from {currentRoleCode} to {newRoleCode}.", now));
        }

        if (branchChanged)
        {
            dbContext.AddAuditLog(new AuditLog(actorUserId, ActionBranchChanged, "Employee", user.PublicId.ToString(), oldSnapshot, EmployeeSnapshot(user), null, null, "Assigned branch changed.", now));
        }

        if (wasActive && !request.IsActive)
        {
            dbContext.AddAuditLog(new AuditLog(actorUserId, ActionDeactivated, "Employee", user.PublicId.ToString(), oldSnapshot, EmployeeSnapshot(user), null, null, "Employee deactivated.", now));
        }

        if (!wasActive && request.IsActive)
        {
            dbContext.AddAuditLog(new AuditLog(actorUserId, ActionReactivated, "Employee", user.PublicId.ToString(), oldSnapshot, EmployeeSnapshot(user), null, null, "Employee reactivated.", now));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToEmployeeResult(user, null, await LoadBranchNamesAsync(newBranchId is null ? Array.Empty<long>() : new[] { newBranchId.Value }, cancellationToken));
    }

    public async Task<EmployeeInvitationResult> ResendInvitationAsync(
        long employeeId,
        long invitationId,
        long actorUserId,
        CancellationToken cancellationToken,
        DateTime? invitationExpiresAt = null)
    {
        var user = await GetEmployeeUserAsync(employeeId, cancellationToken);
        var invitation = await dbContext.EmployeeInvitations
            .SingleOrDefaultAsync(x => x.Id == invitationId, cancellationToken)
            ?? throw new NotFoundException("The employee invitation was not found.");
        if (!string.Equals(invitation.InviteeMobile, user.Mobile, StringComparison.Ordinal))
            throw new BusinessRuleException("The invitation does not belong to this employee.");

        var now = timeProvider.Now;
        if (!invitation.IsUsable(now))
            throw new BusinessRuleException("Only an active invitation can be resent.");

        invitation.Cancel(actorUserId, now);
        var (fresh, token, emailDelivery) = await CreateInvitationAsync(
            user,
            invitation.RoleCode,
            invitation.BranchId,
            invitationExpiresAt,
            actorUserId,
            now,
            cancellationToken);
        fresh.RecordResend(actorUserId, now);
        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            ActionInvitationResent,
            "EmployeeInvitation",
            fresh.PublicId.ToString(),
            null,
            InvitationSnapshot(fresh),
            null,
            null,
            "Invitation resent with a fresh single-use token.",
            now));
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToInvitationResult(fresh, user.Id, token, emailDelivery);
    }

    public async Task CancelInvitationAsync(
        long employeeId,
        long invitationId,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var user = await GetEmployeeUserAsync(employeeId, cancellationToken);
        var invitation = await dbContext.EmployeeInvitations
            .SingleOrDefaultAsync(x => x.Id == invitationId, cancellationToken)
            ?? throw new NotFoundException("The employee invitation was not found.");
        if (!string.Equals(invitation.InviteeMobile, user.Mobile, StringComparison.Ordinal))
            throw new BusinessRuleException("The invitation does not belong to this employee.");

        var now = timeProvider.Now;
        invitation.Cancel(actorUserId, now);
        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            ActionInvitationCancelled,
            "EmployeeInvitation",
            invitation.PublicId.ToString(),
            InvitationSnapshot(invitation),
            null,
            null,
            null,
            "Invitation cancelled.",
            now));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes or archives a deactivated employee. The employee's owned identity rows (role
    /// assignments, sessions, refresh tokens, devices, notification preferences and the
    /// employee's own notification events/inbox) are removed and any pending invitation is
    /// cancelled, but shared operational history — deliveries, orders and records where the
    /// employee was the acting user — is never cascaded. If such records exist the database
    /// blocks the delete and the employee is archived instead so historical records continue
    /// to identify the account.
    /// </summary>
    public async Task<EmployeeDeleteResult> DeleteAsync(
        long employeeId,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        if (employeeId == actorUserId)
            throw new BusinessRuleException("You cannot delete your own employee account.");

        var user = await GetEmployeeUserAsync(employeeId, cancellationToken);
        if (user.IsArchived)
            return new EmployeeDeleteResult(false, true, await ToEmployeeResultAsync(user, cancellationToken));

        if (user.IsActive)
            throw new BusinessRuleException("Only a deactivated employee can be deleted or archived. Deactivate the employee first.");

        var roleCode = user.UserRoles.FirstOrDefault(x => x.Role is not null)?.Role.Code ?? string.Empty;
        await EnsureRoleAssignableAsync(roleCode, actorUserId, cancellationToken);

        var now = timeProvider.Now;
        var oldSnapshot = EmployeeSnapshot(user);
        EmployeeDeleteResult? result = null;

        await ExecuteAtomicAsync(async () =>
        {
            var sessions = await dbContext.UserSessions
                .Where(x => x.UserId == employeeId)
                .ToListAsync(cancellationToken);
            var sessionIds = sessions.Select(x => x.Id).ToArray();

            var refreshTokens = await dbContext.RefreshTokens
                .Where(x => x.UserId == employeeId || (x.SessionId.HasValue && sessionIds.Contains(x.SessionId.Value)))
                .ToListAsync(cancellationToken);
            dbContext.RefreshTokens.RemoveRange(refreshTokens);
            dbContext.UserSessions.RemoveRange(sessions);

            var roleAssignments = await dbContext.UserRoles
                .Where(x => x.UserId == employeeId)
                .ToListAsync(cancellationToken);
            dbContext.UserRoles.RemoveRange(roleAssignments);

            var preferences = await dbContext.NotificationPreferences
                .Where(x => x.UserId == employeeId)
                .ToListAsync(cancellationToken);
            dbContext.NotificationPreferences.RemoveRange(preferences);

            // The employee's own notification graph (events, inbox, deliveries and attempts)
            // is removed with the account. Notifications are created for the employee on
            // onboarding and activity, so they belong to the account rather than to shared
            // operational history. Child rows are removed before parents (attempts ->
            // deliveries -> notifications -> events) and before user devices, because
            // deliveries can reference a device. Shared records (deliveries, orders, milk
            // production and similar) still block deletion at SaveChanges below.
            var notificationEvents = await dbContext.NotificationEvents
                .Where(x => x.UserId == employeeId)
                .ToListAsync(cancellationToken);
            var notificationEventIds = notificationEvents.Select(x => x.Id).ToArray();

            var inboxNotifications = await dbContext.Notifications
                .Where(x => x.UserId == employeeId || notificationEventIds.Contains(x.NotificationEventId))
                .ToListAsync(cancellationToken);
            var notificationIds = inboxNotifications.Select(x => x.Id).ToArray();

            var notificationDeliveries = await dbContext.NotificationDeliveries
                .Where(x => notificationIds.Contains(x.NotificationId))
                .ToListAsync(cancellationToken);
            var notificationDeliveryIds = notificationDeliveries.Select(x => x.Id).ToArray();

            var notificationAttempts = await dbContext.NotificationAttempts
                .Where(x => notificationDeliveryIds.Contains(x.NotificationDeliveryId))
                .ToListAsync(cancellationToken);
            dbContext.NotificationAttempts.RemoveRange(notificationAttempts);
            dbContext.NotificationDeliveries.RemoveRange(notificationDeliveries);
            dbContext.Notifications.RemoveRange(inboxNotifications);
            dbContext.NotificationEvents.RemoveRange(notificationEvents);

            var devices = await dbContext.UserDevices
                .Where(x => x.UserId == employeeId)
                .ToListAsync(cancellationToken);
            dbContext.UserDevices.RemoveRange(devices);

            if (user.Mobile is not null)
            {
                var pendingInvitations = await dbContext.EmployeeInvitations
                    .Where(x => x.InviteeMobile == user.Mobile && x.Status == EmployeeInvitationStatus.Invited)
                    .ToListAsync(cancellationToken);
                foreach (var invitation in pendingInvitations)
                {
                    invitation.Cancel(actorUserId, now);
                    dbContext.AddAuditLog(new AuditLog(
                        actorUserId,
                        ActionInvitationCancelled,
                        "EmployeeInvitation",
                        invitation.PublicId.ToString(),
                        InvitationSnapshot(invitation),
                        null,
                        null,
                        null,
                        "Invitation cancelled because the employee account was deleted.",
                        now));
                }
            }

            dbContext.Users.Remove(user);
            dbContext.AddAuditLog(new AuditLog(
                actorUserId,
                ActionDeleted,
                "Employee",
                user.PublicId.ToString(),
                oldSnapshot,
                null,
                null,
                null,
                "Employee deleted.",
                now));

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // The database is the authority: the delete is blocked because operational
                // history (deliveries, orders, milk records and similar) still references the
                // employee. Archive instead of erroring so those records keep a stable identity.
                dbContext.ChangeTracker.Clear();
                var archived = await GetEmployeeUserAsync(employeeId, cancellationToken);
                var archivedAt = timeProvider.Now;
                archived.Archive(archivedAt);
                dbContext.AddAuditLog(new AuditLog(
                    actorUserId,
                    ActionArchived,
                    "Employee",
                    archived.PublicId.ToString(),
                    oldSnapshot,
                    EmployeeSnapshot(archived),
                    null,
                    null,
                    "Employee archived because operational records reference the account.",
                    archivedAt));
                await dbContext.SaveChangesAsync(cancellationToken);
                result = new EmployeeDeleteResult(false, true, await ToEmployeeResultAsync(archived, cancellationToken));
                return;
            }

            result = new EmployeeDeleteResult(true, false, null);
        }, cancellationToken);

        return result!;
    }

    public async Task<EmployeeInvitationVerificationResult> VerifyInvitationAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var raw = Require(token, "Invitation token is required.", nameof(token));
        var tokenHash = tokenGenerator.Hash(raw);
        var invitation = await dbContext.EmployeeInvitations
            .SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
        if (invitation is null)
        {
            return new EmployeeInvitationVerificationResult(
                false, null, null, null, string.Empty, null, "The invitation link is not recognized.");
        }

        var now = timeProvider.Now;
        if (invitation.Status == EmployeeInvitationStatus.Invited && invitation.ExpiresAt <= now)
        {
            invitation.MarkExpired(now);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (!invitation.IsUsable(now))
        {
            var reason = invitation.Status == EmployeeInvitationStatus.Registered
                ? "This invitation has already been used."
                : invitation.Status == EmployeeInvitationStatus.Cancelled
                    ? "This invitation has been cancelled."
                    : "This invitation has expired.";
            return new EmployeeInvitationVerificationResult(
                false, invitation.InviteeName, invitation.InviteeMobile, invitation.InviteeEmail,
                invitation.RoleCode, invitation.BranchId, reason);
        }

        if (invitation.BranchId.HasValue)
        {
            var branch = await dbContext.Branches
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == invitation.BranchId.Value, cancellationToken);
            if (branch is null)
            {
                return new EmployeeInvitationVerificationResult(
                    false, invitation.InviteeName, invitation.InviteeMobile, invitation.InviteeEmail,
                    invitation.RoleCode, invitation.BranchId,
                    "The branch for this invitation is no longer available. Ask your administrator to create a new invitation.");
            }

            if (!branch.IsActive)
            {
                return new EmployeeInvitationVerificationResult(
                    false, invitation.InviteeName, invitation.InviteeMobile, invitation.InviteeEmail,
                    invitation.RoleCode, invitation.BranchId,
                    "The branch for this invitation is inactive. Ask your administrator to create a new invitation.");
            }

            return new EmployeeInvitationVerificationResult(
                true, invitation.InviteeName, invitation.InviteeMobile, invitation.InviteeEmail,
                invitation.RoleCode, invitation.BranchId, null, branch.Code, branch.Name);
        }

        // Invitations without a branch (e.g. SYSTEM_ADMIN) remain valid with no branch metadata.
        return new EmployeeInvitationVerificationResult(
            true, invitation.InviteeName, invitation.InviteeMobile, invitation.InviteeEmail,
            invitation.RoleCode, invitation.BranchId, null);
    }

    public async Task<CompleteEmployeeRegistrationResult> CompleteRegistrationAsync(
        CompleteEmployeeRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        ValidateDevice(request.Device);
        var token = Require(request.Token, "Invitation token is required.", nameof(request.Token));
        var otpCode = Require(request.OtpCode, "OTP code is required.", nameof(request.OtpCode));
        var displayName = Require(request.DisplayName, "Display name is required.", nameof(request.DisplayName));
        var password = Require(request.Password, "Password is required.", nameof(request.Password));
        var email = NormalizeEmail(request.Email);
        var requestedMobile = NormalizeMobile(request.Mobile)
            ?? throw new ValidationAppException("Mobile number is required.", nameof(request.Mobile));
        var tokenHash = tokenGenerator.Hash(token);
        var now = timeProvider.Now;

        CompleteEmployeeRegistrationResult result = null!;
        await ExecuteAtomicAsync(async () =>
        {
            var invitation = await dbContext.EmployeeInvitations
                .SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken)
                ?? throw new NotFoundException("The employee invitation was not found.");
            if (!invitation.IsUsable(now))
                throw new BusinessRuleException("This invitation is no longer valid. Ask your administrator to resend it.");

            // The invitation's branch is authoritative: it must still exist and be active at
            // registration time. Assigning an orphaned or inactive branch would silently
            // mis-route the employee, so the registration stops here (inside the atomic
            // operation) with a clear business error and the OTP challenge is never consumed.
            if (invitation.BranchId.HasValue)
                await EnsureBranchAsync(invitation.BranchId.Value, cancellationToken);

            // The invitation is the authoritative identity. The form mobile is accepted only
            // as a representation of that identity and can never replace it.
            var invitationMobile = IndiaMobileNumber.Canonicalize(invitation.InviteeMobile)
                ?? throw new BusinessRuleException("The invitation mobile number is invalid. Ask your administrator to create a new invitation.");
            var submittedMobile = IndiaMobileNumber.Canonicalize(requestedMobile)
                ?? throw new ValidationAppException("Enter a valid Indian mobile number.", nameof(request.Mobile));
            if (!string.Equals(submittedMobile, invitationMobile, StringComparison.Ordinal))
                throw new UnauthorizedAppException("The invitation mobile number does not match this registration.");

            var challengeDestinations = MobileLookupValues(invitationMobile);
            var challenge = await dbContext.OtpChallenges
                .Where(x => challengeDestinations.Contains(x.Destination) && x.Purpose == OtpPurpose.EmployeeInvitation)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (challenge is null || !challenge.CanAttempt(now))
                throw new UnauthorizedAppException("The OTP is invalid or has expired.");

            // Verify and validate with MSG91 using the canonical invitation identity. The
            // persisted request id and EmployeeInvitation purpose remain mandatory bindings.
            if (challenge.ReqId is null)
                throw new UnauthorizedAppException("The OTP is invalid or has expired.");
            Msg91OtpVerifyResult verifyResult;
            Msg91OtpValidationResult validationResult;
            try
            {
                verifyResult = await otpProvider.VerifyAsync(
                    new Msg91OtpVerifyRequest(challenge.ReqId, invitationMobile, otpCode, OtpPurpose.EmployeeInvitation.ToString()),
                    cancellationToken);
                validationResult = await otpProvider.ValidateAccessTokenAsync(
                    new Msg91OtpAccessTokenRequest(verifyResult.AccessToken, invitationMobile, OtpPurpose.EmployeeInvitation.ToString()),
                    cancellationToken);
            }
            catch (OtpProviderRejectedException)
            {
                challenge.RecordFailedAttempt();
                await dbContext.SaveChangesAsync(cancellationToken);
                throw;
            }

            // Compare provider-attested identity with the invitation, not with client input
            // or a legacy storage representation.
            var attestedIdentifier = IndiaMobileNumber.Canonicalize(validationResult.VerifiedIdentifier);
            if (!string.Equals(attestedIdentifier, invitationMobile, StringComparison.Ordinal))
                throw new OtpProviderRejectedException("The OTP verification could not be confirmed.");

            challenge.Consume(now);
            var userLookupValues = MobileLookupValues(invitationMobile);
            var user = await dbContext.Users
                .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x.RolePermissions).ThenInclude(x => x.Permission)
                .SingleOrDefaultAsync(x => userLookupValues.Contains(x.Mobile!), cancellationToken)
                ?? throw new BusinessRuleException("The invited employee account no longer exists. Ask your administrator to create a new invitation.");

            var role = await GetRoleAsync(invitation.RoleCode, cancellationToken);

            // Rebuild the assignment so the account activates with exactly the invitation's role and branch.
            var existing = user.UserRoles.ToList();
            dbContext.UserRoles.RemoveRange(existing);
            user.UserRoles.Clear();
            user.AssignRole(role, invitation.BranchId);
            user.SetProfile(displayName);
            user.SetContact(invitationMobile, email);
            user.SetPasswordHash(passwordHasher.Hash(password));
            user.Activate();

            invitation.MarkRegistered(user.Id, now);

            var session = new UserSession(
                user.Id,
                HashDevice(request.Device.DeviceIdentifier),
                request.Device.DeviceName,
                request.Device.Platform,
                request.Device.IpAddress,
                request.Device.UserAgent,
                now);
            dbContext.UserSessions.Add(session);
            user.RecordLogin(now);
            await dbContext.SaveChangesAsync(cancellationToken);

            var authUser = await user.ToAuthUserResultAsync(dbContext, cancellationToken);
            var tokens = tokenService.Create(
                user,
                session,
                authUser.Roles,
                authUser.Permissions,
                authUser.BranchIds,
                now);
            dbContext.RefreshTokens.Add(new RefreshToken(
                user.Id,
                tokenService.HashRefreshToken(tokens.RefreshToken),
                tokens.RefreshTokenExpiresAt,
                session.Id,
                now));
            dbContext.AddAuditLog(new AuditLog(
                user.Id,
                "EMPLOYEE_REGISTRATION",
                "UserSession",
                session.PublicId.ToString(),
                null,
                null,
                request.Device.IpAddress,
                request.Device.UserAgent,
                null,
                now));
            dbContext.AddAuditLog(new AuditLog(
                user.Id,
                ActionRegistered,
                "EmployeeInvitation",
                invitation.PublicId.ToString(),
                null,
                InvitationSnapshot(invitation),
                request.Device.IpAddress,
                request.Device.UserAgent,
                $"Employee completed the invitation for role {invitation.RoleCode}.",
                now));
            dbContext.AddAuditLog(new AuditLog(
                user.Id,
                ActionActivated,
                "Employee",
                user.PublicId.ToString(),
                null,
                EmployeeSnapshot(user),
                request.Device.IpAddress,
                request.Device.UserAgent,
                "Account activated through registration.",
                now));

            notificationEventWriter.Add(new NotificationEventRequest(
                user.Id,
                NotificationEventTypes.RegistrationCompleted,
                $"registration:{user.PublicId:N}:completed",
                new Dictionary<string, string>
                {
                    ["message"] = "Your DoodhDirect registration is complete."
                },
                "/"));
            notificationEventWriter.Add(new NotificationEventRequest(
                user.Id,
                NotificationEventTypes.AuthenticationSucceeded,
                $"authentication:{session.PublicId:N}:succeeded",
                new Dictionary<string, string>
                {
                    ["message"] = "You signed in successfully."
                },
                "/"));

            await dbContext.SaveChangesAsync(cancellationToken);
            result = new CompleteEmployeeRegistrationResult(new AuthSessionResult(authUser, tokens), invitation.Status);
        }, cancellationToken);

        return result;
    }

    private async Task<User> GetEmployeeUserAsync(long employeeId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .SingleOrDefaultAsync(x => x.Id == employeeId, cancellationToken)
            ?? throw new NotFoundException("The employee was not found.");
        EnsureEmployeeUser(user);
        return user;
    }

    private static void EnsureEmployeeUser(User user)
    {
        if (user.UserType != UserType.Employee && user.UserType != UserType.SystemAdministrator)
            throw new NotFoundException("The employee was not found.");
    }

    private static void EnsureEmployeeRole(string roleCode)
    {
        if (!EmployeeRoleCodes.Contains(roleCode))
            throw new ValidationAppException("The selected role cannot be assigned to an employee.", nameof(roleCode));
    }

    /// <summary>
    /// Assigning the SYSTEM_ADMIN role is an ownership-level operation. The actor must hold
    /// <see cref="AuthorizationCodes.IdentityAdministratorsManage"/> (Owner). This is the
    /// backend-authoritative guard that keeps System Administrators from creating or managing
    /// other System Administrators.
    /// </summary>
    private async Task EnsureRoleAssignableAsync(string roleCode, long actorUserId, CancellationToken cancellationToken)
    {
        if (roleCode != AuthorizationCodes.SystemAdmin)
            return;

        var actor = await dbContext.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .SingleOrDefaultAsync(x => x.Id == actorUserId, cancellationToken)
            ?? throw new UnauthorizedAppException();
        var auth = actor.ToAuthUserResult();
        if (!auth.Permissions.Contains(AuthorizationCodes.IdentityAdministratorsManage, StringComparer.Ordinal))
            throw new ForbiddenAppException("Only the owner can create or manage system administrators.");
    }

    private async Task<(EmployeeInvitation Invitation, string Token, InvitationEmailDelivery EmailDelivery)> CreateInvitationAsync(
        User user,
        string roleCode,
        long? branchId,
        DateTime? invitationExpiresAt,
        long actorUserId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var expiresAt = invitationExpiresAt ?? now.Add(DefaultInvitationLifetime);
        if (expiresAt.Kind != DateTimeKind.Unspecified)
            throw new ValidationAppException("Invitation expiry must be an India-local date-time.", nameof(invitationExpiresAt));
        if (expiresAt <= now)
            throw new ValidationAppException("Invitation expiry must be in the future.", nameof(invitationExpiresAt));

        var token = tokenGenerator.Create();
        var invitation = new EmployeeInvitation(
            user.DisplayName ?? string.Empty,
            user.Mobile ?? string.Empty,
            user.Email,
            roleCode,
            branchId,
            tokenGenerator.Hash(token),
            now,
            expiresAt,
            actorUserId);
        dbContext.EmployeeInvitations.Add(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);

        // The invitee requests the registration OTP on demand after opening the invitation link.
        // Do not send an SMS while the administrator creates or resends the invitation.

        // Email the single-use invitation link to the invitee when SMTP delivery is configured.
        // The outcome is audited and returned so the UI cannot confuse link generation with SMTP delivery.
        var emailDelivery = await SendInvitationEmailAsync(
            user, invitation, token, expiresAt, actorUserId, now, cancellationToken);

        return (invitation, token, emailDelivery);
    }

    private async Task<InvitationEmailDelivery> SendInvitationEmailAsync(
        User user,
        EmployeeInvitation invitation,
        string token,
        DateTime expiresAt,
        long actorUserId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var inviteeEmail = user.Email;
        string action;
        string reason;
        var status = InvitationEmailDeliveryStatus.Skipped;
        if (emailSender is null || integrationSettings is null)
        {
            action = ActionInvitationEmailSkipped;
            reason = "SMTP invitation delivery services are not available.";
        }
        else if (string.IsNullOrWhiteSpace(inviteeEmail))
        {
            action = ActionInvitationEmailSkipped;
            reason = "The invitee has no email address, so the invitation link email was skipped.";
        }
        else
        {
            var inviteUrlBase = (await integrationSettings.GetInviteUrlBaseAsync(cancellationToken))?.Trim();
            if (string.IsNullOrWhiteSpace(inviteUrlBase))
            {
                action = ActionInvitationEmailSkipped;
                reason = "No invitation base URL is configured, so the invitation link email was skipped.";
            }
            else
            {
                var link = $"{inviteUrlBase.TrimEnd('/')}/invite/{token}";
                var expiresText = expiresAt.ToString("d MMM yyyy, h:mm tt", CultureInfo.InvariantCulture);
                var displayName = string.IsNullOrWhiteSpace(user.DisplayName)
                    ? "there"
                    : user.DisplayName.Trim();
                var displayNameHtml = WebUtility.HtmlEncode(displayName);
                var linkHtml = WebUtility.HtmlEncode(link);

                var message = new EmailMessage(
                    inviteeEmail,
                    "Your DoodhDirect onboarding invitation",
                    string.Join(
                        "\n\n",
                        $"Hello {displayName},",
                        "You have been invited to join DoodhDirect. Complete your onboarding by opening this secure single-use invitation link before it expires:",
                        $"{expiresText}",
                        link,
                        "The link can be used only once. If you did not expect this invitation, you can safely ignore this email."),
                    string.Join(
                        "\n",
                        $"<p>Hello {displayNameHtml},</p>",
                        "<p>You have been invited to join <strong>DoodhDirect</strong>. Complete your onboarding by opening this secure single-use invitation link before it expires:</p>",
                        $"<p><em>{expiresText}</em></p>",
                        $"<p><a href=\"{link}\">{linkHtml}</a></p>",
                        "<p>The link can be used only once. If you did not expect this invitation, you can safely ignore this email.</p>"));

                try
                {
                    var result = await emailSender.SendAsync(message, cancellationToken);
                    if (result.Sent)
                    {
                        status = InvitationEmailDeliveryStatus.Sent;
                        action = ActionInvitationEmailSent;
                        reason = $"Invitation email accepted by the SMTP relay for {inviteeEmail}.";
                    }
                    else
                    {
                        action = ActionInvitationEmailSkipped;
                        reason = $"Invitation email to {inviteeEmail} was skipped: {result.Reason ?? "SMTP delivery is not configured."}";
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    status = InvitationEmailDeliveryStatus.Failed;
                    action = ActionInvitationEmailFailed;
                    reason = $"Invitation email to {inviteeEmail} failed: {exception.Message}";
                    logger?.LogError(
                        exception,
                        "Invitation email delivery failed for invitation {InvitationPublicId} to {ToAddress}.",
                        invitation.PublicId,
                        inviteeEmail);
                }
            }
        }

        logger?.LogInformation(
            "Invitation {InvitationPublicId} email outcome: {EmailDeliveryStatus}. Recipient: {ToAddress}. Reason: {Reason}",
            invitation.PublicId,
            status,
            inviteeEmail,
            reason);

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            action,
            "EmployeeInvitation",
            invitation.PublicId.ToString(),
            null,
            null,
            null,
            null,
            reason,
            now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new InvitationEmailDelivery(status, reason, inviteeEmail);
    }

    private static EmployeeInvitationResult ToInvitationResult(
        EmployeeInvitation invitation,
        long employeeId,
        string token,
        InvitationEmailDelivery emailDelivery) => new(
            invitation.Id,
            invitation.PublicId,
            employeeId,
            token,
            invitation.ExpiresAt,
            emailDelivery.Status,
            emailDelivery.Reason,
            emailDelivery.Recipient);

    private sealed record InvitationEmailDelivery(
        InvitationEmailDeliveryStatus Status,
        string Reason,
        string? Recipient);

    private async Task<Role> GetRoleAsync(string roleCode, CancellationToken cancellationToken) =>
        await dbContext.Roles
            .Include(x => x.RolePermissions)
                .ThenInclude(x => x.Permission)
            .SingleOrDefaultAsync(x => x.Code == roleCode && x.IsActive, cancellationToken)
        ?? throw new ValidationAppException("The selected role is not available.", nameof(roleCode));

    private async Task<Branch> GetBranchAsync(long branchId, CancellationToken cancellationToken) =>
        await dbContext.Branches
            .SingleOrDefaultAsync(x => x.Id == branchId, cancellationToken)
            ?? throw new ValidationAppException("The selected branch was not found.", nameof(branchId));

    private async Task EnsureBranchAsync(long branchId, CancellationToken cancellationToken)
    {
        var branch = await GetBranchAsync(branchId, cancellationToken);
        if (!branch.IsActive)
            throw new ValidationAppException("The selected branch is inactive.", nameof(branchId));
    }

    private async Task EnsureContactIsAvailableAsync(
        string? email,
        string? mobile,
        long? excludeUserId,
        CancellationToken cancellationToken)
    {
        if (email is not null && await dbContext.Users.AnyAsync(x => x.Email == email && x.Id != excludeUserId, cancellationToken))
            throw new ConflictException("An account already exists for this email.");
        if (mobile is not null)
        {
            // Match both the canonical E.164 form (+91XXXXXXXXXX) and the 10-digit national
            // form so a duplicate is detected regardless of how the existing account stores
            // its mobile (customers and registered employees are canonical, administrator-created
            // employees before registration are stored as entered).
            var mobileLookup = MobileLookupValues(mobile);
            if (mobileLookup.Count > 0 &&
                await dbContext.Users.AnyAsync(x => mobileLookup.Contains(x.Mobile!) && x.Id != excludeUserId, cancellationToken))
                throw new ConflictException("An account already exists for this mobile number.");
        }
    }

    public async Task<IReadOnlyList<EmployeeBranchOption>> GetBranchOptionsAsync(
        CancellationToken cancellationToken)
    {
        var branches = await dbContext.Branches
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name)
            .Select(x => new EmployeeBranchOption(
                x.Id,
                x.PublicId,
                x.Code,
                x.Name,
                x.City,
                x.State,
                x.IsActive))
            .ToListAsync(cancellationToken);
        return branches;
    }

    private async Task<IReadOnlyDictionary<long, string>> LoadBranchNamesAsync(
        long[] branchIds,
        CancellationToken cancellationToken)
    {
        if (branchIds.Length == 0)
            return new Dictionary<long, string>();

        var branches = await dbContext.Branches
            .Where(x => branchIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Name })
            .ToListAsync(cancellationToken);
        return branches.ToDictionary(x => x.Id, x => x.Name);
    }

    private async Task<EmployeeResult> ToEmployeeResultAsync(User user, CancellationToken cancellationToken)
    {
        var assignment = user.UserRoles.FirstOrDefault(x => x.Role is not null);
        var branchId = assignment?.BranchId;
        var branchNames = await LoadBranchNamesAsync(
            branchId is null ? Array.Empty<long>() : new[] { branchId.Value },
            cancellationToken);
        return ToEmployeeResult(user, null, branchNames);
    }

    private static EmployeeResult ToEmployeeResult(
        User user,
        EmployeeInvitation? invitation,
        IReadOnlyDictionary<long, string> branchNames)
    {
        var assignment = user.UserRoles.FirstOrDefault(x => x.Role is not null);
        var roleCode = assignment?.Role.Code ?? string.Empty;
        var branchId = assignment?.BranchId;
        return new EmployeeResult(
            user.Id,
            user.PublicId,
            invitation?.Id,
            user.DisplayName ?? string.Empty,
            user.Mobile,
            user.Email,
            roleCode,
            AuthorizationCodes.Roles.TryGetValue(roleCode, out var roleName) ? roleName : null,
            branchId,
            branchId.HasValue && branchNames.TryGetValue(branchId.Value, out var branchName) ? branchName : null,
            user.IsActive,
            user.IsArchived,
            user.ArchivedAt,
            invitation?.Status,
            invitation?.ExpiresAt,
            invitation?.RegisteredAt,
            user.CreatedAt);
    }

    private static string EmployeeSnapshot(User user) => JsonSerializer.Serialize(new
    {
        user.Id,
        user.UserType,
        user.DisplayName,
        user.Mobile,
        user.Email,
        user.IsActive,
        user.IsArchived,
        user.ArchivedAt,
        Assignments = user.UserRoles.Select(x => new
        {
            Role = x.Role?.Code,
            x.BranchId
        }).ToArray()
    });

    private static string InvitationSnapshot(EmployeeInvitation invitation) => JsonSerializer.Serialize(new
    {
        invitation.InviteeName,
        invitation.InviteeMobile,
        invitation.InviteeEmail,
        invitation.RoleCode,
        invitation.BranchId,
        invitation.Status,
        invitation.ExpiresAt,
        invitation.RegisteredAt,
        invitation.CancelledAt,
        invitation.LastResentAt
    });

    private async Task ExecuteAtomicAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            await operation();
            return;
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private static void ValidateDevice(DeviceInfo device)
    {
        if (string.IsNullOrWhiteSpace(device.DeviceIdentifier))
            throw new ValidationAppException("Device identifier is required.", nameof(device.DeviceIdentifier));
    }

    private static string Require(string? value, string message, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new ValidationAppException(message, field) : value.Trim();

    private static string RequireEmail(string? value)
    {
        var email = NormalizeEmail(value);
        return email is null
            ? throw new ValidationAppException("A valid email address is required.", nameof(CreateEmployeeRequest.Email))
            : email;
    }

    private static string? NormalizeEmail(string? value) => string.IsNullOrWhiteSpace(value) || !value.Contains('@') ? null : value.Trim().ToLowerInvariant();

    private static string? NormalizeMobile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Any(char.IsDigit) ? normalized : null;
    }

    private static IReadOnlyList<string> MobileLookupValues(string destination)
    {
        var canonical = IndiaMobileNumber.Canonicalize(destination);
        if (canonical is null) return Array.Empty<string>();
        var national = IndiaMobileNumber.ToNational(canonical);
        return national is null || national == canonical
            ? new[] { canonical }
            : new[] { canonical, national };
    }

    private static string HashDevice(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
