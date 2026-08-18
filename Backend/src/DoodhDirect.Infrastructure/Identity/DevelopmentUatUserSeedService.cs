using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace DoodhDirect.Infrastructure.Identity;

public sealed class DevelopmentUatUserSeedService(
    DoodhDirectDbContext dbContext,
    IPasswordHasher passwordHasher,
    Microsoft.Extensions.Hosting.IHostEnvironment environment)
{
    public const string Password = "DoodhDirect@123";
    public const string BranchCode = "MAIN";

    public const string OwnerEmail = "owner@doodhdirect.local";
    public const string SystemAdminEmail = "system.admin@doodhdirect.local";
    public const string DeliveryManagerEmail = "delivery.manager@doodhdirect.local";
    public const string CustomerSupportEmail = "support@doodhdirect.local";
    public const string AccountantEmail = "accountant@doodhdirect.local";

    private static readonly IReadOnlyCollection<DevelopmentUserDefinition> Users =
    [
        new(OwnerEmail, "Development Owner", "9000000010", AuthorizationCodes.Owner, UserType.Owner, UsesGlobalAccess: true),
        new(SystemAdminEmail, "Development System Admin", "9000000011", AuthorizationCodes.SystemAdmin, UserType.SystemAdministrator, UsesGlobalAccess: true),
        new(DeliveryManagerEmail, "Development Delivery Manager", "9000000012", AuthorizationCodes.DeliveryManager, UserType.Employee, UsesGlobalAccess: false),
        new(CustomerSupportEmail, "Development Customer Support", "9000000013", AuthorizationCodes.CustomerSupport, UserType.Employee, UsesGlobalAccess: false),
        new(AccountantEmail, "Development Accountant", "9000000014", AuthorizationCodes.Accountant, UserType.Employee, UsesGlobalAccess: false)
    ];

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return;
        }

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();

        await executionStrategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);

            var roleCodes = Users.Select(user => user.RoleCode).ToArray();
            var roles = await dbContext.Roles
                .Where(role => roleCodes.Contains(role.Code))
                .ToDictionaryAsync(role => role.Code, StringComparer.Ordinal, cancellationToken);
            var branch = await dbContext.Branches
                .SingleAsync(item => item.Code == BranchCode, cancellationToken);

            foreach (var definition in Users)
            {
                var role = roles[definition.RoleCode];
                long? branchId = definition.UsesGlobalAccess ? null : branch.Id;
                var user = await dbContext.Users
                    .Include(item => item.UserRoles)
                    .SingleOrDefaultAsync(item => item.Email == definition.Email, cancellationToken);

                if (user is null)
                {
                    user = new User(definition.UserType);
                    user.SetProfile(definition.DisplayName);
                    user.SetContact(definition.Mobile, definition.Email);
                    user.SetPasswordHash(passwordHasher.Hash(Password));
                    user.AssignRole(role, branchId);
                    dbContext.Users.Add(user);
                }
                else if (!user.UserRoles.Any(assignment =>
                             assignment.RoleId == role.Id
                             && assignment.BranchId == branchId))
                {
                    user.AssignRole(role, branchId);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private sealed record DevelopmentUserDefinition(
        string Email,
        string DisplayName,
        string Mobile,
        string RoleCode,
        UserType UserType,
        bool UsesGlobalAccess);
}
