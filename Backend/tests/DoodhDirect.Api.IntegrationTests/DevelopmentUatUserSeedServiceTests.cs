using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Catalogue;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class DevelopmentUatUserSeedServiceTests
{
    [Fact]
    public async Task SeedAsync_InDevelopment_IsIdempotentAndAssignsExpectedRolesAndScopes()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
        var passwordHasher = new Pbkdf2PasswordHasher(Options.Create(new IdentityOptions()));
        var identitySeed = new IdentitySeedService(db);
        var timeProvider = new TestClock(new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "BRANCH", "Branch Number", "BR/{NUMBER:000}", 1, 1, NumberSeriesResetPolicy.Never));
        await db.SaveChangesAsync();
        var catalogueSeed = new CatalogueSeedService(
            db,
            new NumberSeriesService(db, timeProvider),
            new NumberSeriesSeedService(db));
        var developmentSeed = new DevelopmentUatUserSeedService(
            db,
            passwordHasher,
            new TestHostEnvironment("Development"));

        await identitySeed.SeedAsync(CancellationToken.None);
        await catalogueSeed.SeedAsync(CancellationToken.None);
        await developmentSeed.SeedAsync(CancellationToken.None);
        await developmentSeed.SeedAsync(CancellationToken.None);

        var branch = await db.Branches.SingleAsync(item => item.Code == DevelopmentUatUserSeedService.BranchCode);
        var users = await db.Users
            .Include(item => item.UserRoles)
            .ThenInclude(item => item.Role)
            .Where(item => DevelopmentEmails.Contains(item.Email!))
            .ToListAsync();

        Assert.Equal(DevelopmentEmails.Count, users.Count);

        AssertUser(users, DevelopmentUatUserSeedService.OwnerEmail, AuthorizationCodes.Owner, null, UserType.Owner, passwordHasher);
        AssertUser(users, DevelopmentUatUserSeedService.SystemAdminEmail, AuthorizationCodes.SystemAdmin, null, UserType.SystemAdministrator, passwordHasher);
        AssertUser(users, DevelopmentUatUserSeedService.DeliveryManagerEmail, AuthorizationCodes.DeliveryManager, branch.Id, UserType.Employee, passwordHasher);
        AssertUser(users, DevelopmentUatUserSeedService.CustomerSupportEmail, AuthorizationCodes.CustomerSupport, branch.Id, UserType.Employee, passwordHasher);
        AssertUser(users, DevelopmentUatUserSeedService.AccountantEmail, AuthorizationCodes.Accountant, branch.Id, UserType.Employee, passwordHasher);
    }

    [Fact]
    public async Task SeedAsync_OutsideDevelopment_DoesNotCreateUsers()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
        var passwordHasher = new Pbkdf2PasswordHasher(Options.Create(new IdentityOptions()));
        var developmentSeed = new DevelopmentUatUserSeedService(
            db,
            passwordHasher,
            new TestHostEnvironment("Production"));

        await developmentSeed.SeedAsync(CancellationToken.None);

        Assert.Empty(await db.Users
            .Where(item => DevelopmentEmails.Contains(item.Email!))
            .ToListAsync());
    }

    private static readonly IReadOnlySet<string> DevelopmentEmails = new HashSet<string>(StringComparer.Ordinal)
    {
        DevelopmentUatUserSeedService.OwnerEmail,
        DevelopmentUatUserSeedService.SystemAdminEmail,
        DevelopmentUatUserSeedService.DeliveryManagerEmail,
        DevelopmentUatUserSeedService.CustomerSupportEmail,
        DevelopmentUatUserSeedService.AccountantEmail
    };

    private static void AssertUser(
        IReadOnlyCollection<User> users,
        string email,
        string roleCode,
        long? branchId,
        UserType userType,
        IPasswordHasher passwordHasher)
    {
        var user = Assert.Single(users, item => item.Email == email);
        var assignment = Assert.Single(user.UserRoles);

        Assert.Equal(userType, user.UserType);
        Assert.Equal(roleCode, assignment.Role.Code);
        Assert.Equal(branchId, assignment.BranchId);
        Assert.True(passwordHasher.Verify(user.PasswordHash!, DevelopmentUatUserSeedService.Password));
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DoodhDirectDbContext>(options => options
            .UseInMemoryDatabase($"development-uat-seed-tests-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        return services.BuildServiceProvider();
    }

    private sealed class TestHostEnvironment(string environmentName) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = nameof(DevelopmentUatUserSeedServiceTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
