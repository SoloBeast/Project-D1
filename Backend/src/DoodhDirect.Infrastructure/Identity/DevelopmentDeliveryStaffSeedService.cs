using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Identity;

public sealed class DevelopmentDeliveryStaffSeedService(
    DoodhDirectDbContext dbContext,
    IPasswordHasher passwordHasher)
{
    public const string Email = "delivery@doodhdirect.local";
    public const string Password = "DoodhDirect@123";
    public const string BranchCode = "MAIN";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();

        await executionStrategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);

            var deliveryStaffRole = await dbContext.Roles
                .SingleAsync(
                    role => role.Code == AuthorizationCodes.DeliveryStaff,
                    cancellationToken);
            var branch = await dbContext.Branches
                .SingleAsync(item => item.Code == BranchCode, cancellationToken);
            var user = await dbContext.Users
                .Include(item => item.UserRoles)
                .SingleOrDefaultAsync(item => item.Email == Email, cancellationToken);

            if (user is null)
            {
                user = new User(UserType.Employee);
                user.SetProfile("Development Delivery Staff");
                user.SetContact("9000000001", Email);
                user.SetPasswordHash(passwordHasher.Hash(Password));
                user.AssignRole(deliveryStaffRole, branch.Id);
                dbContext.Users.Add(user);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            else if (!user.UserRoles.Any(assignment =>
                         assignment.RoleId == deliveryStaffRole.Id
                         && assignment.BranchId == branch.Id))
            {
                user.AssignRole(deliveryStaffRole, branch.Id);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        });
    }
}
