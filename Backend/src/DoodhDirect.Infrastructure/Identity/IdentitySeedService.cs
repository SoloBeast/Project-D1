using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Identity;

public sealed class IdentitySeedService(DoodhDirectDbContext dbContext)
{
    private static readonly IReadOnlyDictionary<string, string[]> RolePermissions =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [AuthorizationCodes.Customer] =
            [
                AuthorizationCodes.ProfileReadOwn,
                AuthorizationCodes.ProfileUpdateOwn,
                AuthorizationCodes.SessionsManageOwn
            ],
            [AuthorizationCodes.DeliveryStaff] =
            [
                AuthorizationCodes.ProfileReadOwn,
                AuthorizationCodes.ProfileUpdateOwn,
                AuthorizationCodes.SessionsManageOwn,
                AuthorizationCodes.BranchAccess
            ],
            [AuthorizationCodes.DairyManager] =
            [
                AuthorizationCodes.ProfileReadOwn,
                AuthorizationCodes.ProfileUpdateOwn,
                AuthorizationCodes.SessionsManageOwn,
                AuthorizationCodes.UsersRead,
                AuthorizationCodes.BranchAccess
            ],
            [AuthorizationCodes.DeliveryManager] =
            [
                AuthorizationCodes.ProfileReadOwn,
                AuthorizationCodes.ProfileUpdateOwn,
                AuthorizationCodes.SessionsManageOwn,
                AuthorizationCodes.UsersRead,
                AuthorizationCodes.BranchAccess
            ],
            [AuthorizationCodes.CustomerSupport] =
            [
                AuthorizationCodes.ProfileReadOwn,
                AuthorizationCodes.ProfileUpdateOwn,
                AuthorizationCodes.SessionsManageOwn,
                AuthorizationCodes.UsersRead,
                AuthorizationCodes.CustomerProfilesRead
            ],
            [AuthorizationCodes.Accountant] =
            [
                AuthorizationCodes.ProfileReadOwn,
                AuthorizationCodes.ProfileUpdateOwn,
                AuthorizationCodes.SessionsManageOwn
            ],
            [AuthorizationCodes.SystemAdmin] =
            [
                AuthorizationCodes.ProfileReadOwn,
                AuthorizationCodes.ProfileUpdateOwn,
                AuthorizationCodes.SessionsManageOwn,
                AuthorizationCodes.UsersRead,
                AuthorizationCodes.UsersManage,
                AuthorizationCodes.RolesRead,
                AuthorizationCodes.RolesManage,
                AuthorizationCodes.CustomerProfilesRead,
                AuthorizationCodes.CustomerProfilesManage
            ],
            [AuthorizationCodes.Owner] = AuthorizationCodes.Permissions.Keys.ToArray()
        };

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();

        await executionStrategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);

            var roles = await dbContext.Roles.ToDictionaryAsync(x => x.Code, StringComparer.Ordinal, cancellationToken);
            foreach (var definition in AuthorizationCodes.Roles)
            {
                if (roles.ContainsKey(definition.Key))
                    continue;

                var role = new Role(definition.Key, definition.Value);
                dbContext.Roles.Add(role);
                roles.Add(definition.Key, role);
            }

            var permissions = await dbContext.Permissions.ToDictionaryAsync(x => x.Code, StringComparer.Ordinal, cancellationToken);
            foreach (var definition in AuthorizationCodes.Permissions)
            {
                if (permissions.ContainsKey(definition.Key))
                    continue;

                var permission = new Permission(definition.Key, definition.Value);
                dbContext.Permissions.Add(permission);
                permissions.Add(definition.Key, permission);
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            var existingAssignments = await dbContext.RolePermissions
                .Select(x => new { x.RoleId, x.PermissionId })
                .ToListAsync(cancellationToken);
            var assignmentKeys = existingAssignments
                .Select(x => (x.RoleId, x.PermissionId))
                .ToHashSet();

            foreach (var roleDefinition in RolePermissions)
            {
                var role = roles[roleDefinition.Key];
                foreach (var permissionCode in roleDefinition.Value)
                {
                    var permission = permissions[permissionCode];
                    if (assignmentKeys.Add((role.Id, permission.Id)))
                        dbContext.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }
}
