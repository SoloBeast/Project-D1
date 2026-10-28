using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Identity;

/// <summary>
/// Service-layer enforcement of the employee-origin approval policy: any
/// request associated with an employee's own account (requester is a
/// non-customer user) must receive its approval/rejection decision from an
/// authorized Owner or System Administrator — never from an ordinary
/// employee — and self-approval is never permitted, even between two
/// administrators.
///
/// The guard resolves both the requester's persisted user type and the
/// actor's persisted role membership (<c>UserRoles</c>/<c>Roles</c>) from the
/// database. It never trusts client-provided origin flags and never relies
/// on JWT permission claims alone to establish administrator tier. Call it
/// explicitly at decision points (approve/reject and equivalent
/// disposition transitions); do not place it in shared read-access helpers.
/// </summary>
public static class EmployeeOriginGuard
{
    /// <summary>
    /// The persisted requester/beneficiary is employee-originated when their
    /// authoritative user type is anything other than <see cref="UserType.Customer"/>.
    /// </summary>
    public static bool IsEmployeeOriginated(UserType requesterType) =>
        requesterType != UserType.Customer;

    /// <summary>
    /// True when the user holds persisted <c>OWNER</c> or
    /// <c>SYSTEM_ADMIN</c> role membership. Fails closed (false) for unknown
    /// users and for roles that are not active.
    /// </summary>
    public static Task<bool> IsAdministratorTierAsync(
        DoodhDirectDbContext dbContext,
        long userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return dbContext.UserRoles
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .Join(
                dbContext.Roles.AsNoTracking(),
                userRole => userRole.RoleId,
                role => role.Id,
                (_, role) => role)
            .AnyAsync(
                role => role.IsActive
                    && (role.Code == AuthorizationCodes.Owner
                        || role.Code == AuthorizationCodes.SystemAdmin),
                cancellationToken);
    }

    /// <summary>
    /// Enforces who may make an approval/rejection (or equivalent
    /// disposition) decision on a request. Customer-originated requests pass
    /// through untouched so existing authorization rules keep applying.
    /// Employee-originated decisions require a persisted Owner or System
    /// Administrator actor, and the actor may never be the requester
    /// themselves. Fails closed when the requester identity, user type, or
    /// role information is missing or inconsistent.
    /// </summary>
    public static async Task RequireDecisionAuthorityAsync(
        DoodhDirectDbContext dbContext,
        long requesterUserId,
        long actorUserId,
        string actionDescription,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionDescription);

        var requesterType = await dbContext.Users
            .AsNoTracking()
            .Where(x => x.Id == requesterUserId)
            .Select(x => (UserType?)x.UserType)
            .SingleOrDefaultAsync(cancellationToken);
        if (requesterType is null)
        {
            throw new ConflictException(
                $"The request cannot be {actionDescription}: the requesting account is missing or inconsistent. " +
                "Resolve it with staff support first.");
        }

        if (!IsEmployeeOriginated(requesterType.Value))
        {
            return;
        }

        if (requesterUserId == actorUserId)
        {
            throw new ForbiddenAppException(
                $"An employee-originated request cannot be {actionDescription} by the requesting account itself: " +
                "self-approval is never permitted, including between administrators. " +
                "Route it to a different Owner or System Administrator.");
        }

        if (!await IsAdministratorTierAsync(dbContext, actorUserId, cancellationToken))
        {
            throw new ForbiddenAppException(
                $"Only an Owner or System Administrator may {actionDescription} an employee-originated request.");
        }
    }

    /// <summary>
    /// Verifies that an employee-originated request carries a verifiable
    /// administrator approval before consequential processing (settlement,
    /// export, or completion) may continue: the recorded decider must exist
    /// and hold persisted Owner or System Administrator membership.
    /// Customer-originated requests pass through untouched. Throws a
    /// <see cref="ConflictException"/> carrying the explicit reconciliation
    /// procedure when no valid administrator approval can be established
    /// (for example pre-policy or non-admin-approved in-flight tickets);
    /// historical approvals are never rewritten by this check.
    /// </summary>
    public static async Task RequireVerifiableAdminApprovalAsync(
        DoodhDirectDbContext dbContext,
        long requesterUserId,
        long? decidedByUserId,
        string ticketNumber,
        string actionDescription,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionDescription);

        var requesterType = await dbContext.Users
            .AsNoTracking()
            .Where(x => x.Id == requesterUserId)
            .Select(x => (UserType?)x.UserType)
            .SingleOrDefaultAsync(cancellationToken);
        if (requesterType is null)
        {
            throw new ConflictException(
                $"Ticket {ticketNumber} cannot be {actionDescription}: the requesting account is missing or inconsistent. " +
                "Resolve it with staff support first.");
        }

        if (!IsEmployeeOriginated(requesterType.Value))
        {
            return;
        }

        if (decidedByUserId is null
            || !await IsAdministratorTierAsync(dbContext, decidedByUserId.Value, cancellationToken))
        {
            throw new ConflictException(
                $"Ticket {ticketNumber} cannot be {actionDescription}: it has no verifiable administrator approval. " +
                "Ask an Owner or System Administrator to review and re-decide the request; " +
                "settlement and export remain blocked until a verifiable administrator approval is recorded.");
        }
    }
}
