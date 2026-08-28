using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Setup;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Identity;

public sealed class DevelopmentCustomerSeedService(
    DoodhDirectDbContext dbContext,
    INumberSeriesService numberSeriesService,
    IPasswordHasher passwordHasher)
{
    public const string Email = "customer@doodhdirect.local";
    public const string Password = "DoodhDirect@123";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();

        await executionStrategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);

            var customerRole = await dbContext.Roles
                .SingleAsync(role => role.Code == AuthorizationCodes.Customer, cancellationToken);
            var user = await dbContext.Users
                .Include(item => item.UserRoles)
                .SingleOrDefaultAsync(item => item.Email == Email, cancellationToken);

            if (user is null)
            {
                user = new User(UserType.Customer);
                user.SetProfile("Development Customer");
                user.SetContact("9000000000", Email);
                user.SetPasswordHash(passwordHasher.Hash(Password));
                user.AssignRole(customerRole);
                dbContext.Users.Add(user);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            else if (!user.UserRoles.Any(assignment => assignment.RoleId == customerRole.Id))
            {
                user.AssignRole(customerRole);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            if (!await dbContext.CustomerProfiles.AnyAsync(
                    profile => profile.UserId == user.Id,
                    cancellationToken))
            {
                var profile = new CustomerProfile(user.Id);
                profile.AssignCustomerNumber(
                    await numberSeriesService.GetNextNumberAsync("CUSTOMER", user.Id, cancellationToken));
                profile.Update("Development", "Customer", null, null, null);
                dbContext.CustomerProfiles.Add(profile);
            }

            if (!await dbContext.CustomerAddresses.AnyAsync(
                    address => address.UserId == user.Id && address.IsActive,
                    cancellationToken))
            {
                var address = new CustomerAddress(
                    user.Id,
                    "Home",
                    "1 Development Road",
                    "Central Bengaluru",
                    "Bengaluru",
                    "Karnataka",
                    "560001",
                    "Development Customer",
                    "9000000000",
                    12.9716m,
                    77.5946m);
                address.SetDefault();
                dbContext.CustomerAddresses.Add(address);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }
}
