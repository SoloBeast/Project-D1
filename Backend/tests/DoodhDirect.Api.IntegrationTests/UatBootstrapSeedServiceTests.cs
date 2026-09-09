using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class UatBootstrapSeedServiceTests
{
    private const string OwnerEmail = "owner@doodhdirect.uat";
    private const string OwnerPassword = "Owner.Uat!Password1";
    private const string SystemAdminEmail = "system.admin@doodhdirect.uat";
    private const string SystemAdminPassword = "SysAdmin.Uat!Password1";

    [Fact]
    public async Task SeedAsync_OutsideDevelopment_CreatesOwnerAndSystemAdminFromConfiguration()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
        var passwordHasher = new Pbkdf2PasswordHasher(Options.Create(new IdentityOptions()));
        var seed = new UatBootstrapSeedService(
            db,
            passwordHasher,
            new TestClock(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Unspecified)),
            CreateConfiguration(),
            new TestHostEnvironment("UAT"));

        await new IdentitySeedService(db).SeedAsync(CancellationToken.None);
        await seed.SeedAsync(CancellationToken.None);

        var owner = Assert.Single(
            await db.Users
                .Include(item => item.UserRoles)
                .ThenInclude(item => item.Role)
                .Where(item => item.Email == OwnerEmail)
                .ToListAsync());
        var systemAdmin = Assert.Single(
            await db.Users
                .Include(item => item.UserRoles)
                .ThenInclude(item => item.Role)
                .Where(item => item.Email == SystemAdminEmail)
                .ToListAsync());

        AssertUser(owner, UserType.Owner, AuthorizationCodes.Owner, passwordHasher, OwnerPassword);
        AssertUser(systemAdmin, UserType.SystemAdministrator, AuthorizationCodes.SystemAdmin, passwordHasher, SystemAdminPassword);

        Assert.Equal(1, await db.Users.CountAsync(item => item.UserType == UserType.Owner));
        Assert.Equal(1, await db.Users.CountAsync(item => item.UserType == UserType.SystemAdministrator));
    }

    [Fact]
    public async Task SeedAsync_OutsideDevelopment_IsIdempotent()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
        var passwordHasher = new Pbkdf2PasswordHasher(Options.Create(new IdentityOptions()));
        var seed = new UatBootstrapSeedService(
            db,
            passwordHasher,
            new TestClock(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Unspecified)),
            CreateConfiguration(),
            new TestHostEnvironment("UAT"));

        await new IdentitySeedService(db).SeedAsync(CancellationToken.None);
        await seed.SeedAsync(CancellationToken.None);
        await seed.SeedAsync(CancellationToken.None);

        Assert.Equal(1, await db.Users.CountAsync(item => item.UserType == UserType.Owner));
        Assert.Equal(1, await db.Users.CountAsync(item => item.UserType == UserType.SystemAdministrator));
    }

    [Fact]
    public async Task SeedAsync_InDevelopment_DoesNothing()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
        var passwordHasher = new Pbkdf2PasswordHasher(Options.Create(new IdentityOptions()));
        var seed = new UatBootstrapSeedService(
            db,
            passwordHasher,
            new TestClock(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Unspecified)),
            CreateConfiguration(),
            new TestHostEnvironment("Development"));

        await new IdentitySeedService(db).SeedAsync(CancellationToken.None);
        await seed.SeedAsync(CancellationToken.None);

        Assert.Empty(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task SeedAsync_EnabledWithoutCredentials_Throws()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
        var passwordHasher = new Pbkdf2PasswordHasher(Options.Create(new IdentityOptions()));
        var enabledConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UatBootstrap:Enabled"] = "true"
            })
            .Build();
        var seed = new UatBootstrapSeedService(
            db,
            passwordHasher,
            new TestClock(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Unspecified)),
            enabledConfiguration,
            new TestHostEnvironment("UAT"));

        await new IdentitySeedService(db).SeedAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => seed.SeedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SeedAsync_NotEnabled_DoesNothing()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
        var passwordHasher = new Pbkdf2PasswordHasher(Options.Create(new IdentityOptions()));
        var emptyConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var seed = new UatBootstrapSeedService(
            db,
            passwordHasher,
            new TestClock(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Unspecified)),
            emptyConfiguration,
            new TestHostEnvironment("UAT"));

        await new IdentitySeedService(db).SeedAsync(CancellationToken.None);
        await seed.SeedAsync(CancellationToken.None);

        Assert.Empty(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task SeedAsync_EnabledFalse_DoesNothing()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
        var passwordHasher = new Pbkdf2PasswordHasher(Options.Create(new IdentityOptions()));
        var disabledConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UatBootstrap:Enabled"] = "false"
            })
            .Build();
        var seed = new UatBootstrapSeedService(
            db,
            passwordHasher,
            new TestClock(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Unspecified)),
            disabledConfiguration,
            new TestHostEnvironment("UAT"));

        await new IdentitySeedService(db).SeedAsync(CancellationToken.None);
        await seed.SeedAsync(CancellationToken.None);

        Assert.Empty(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task SeedAsync_ExistingUserWithWrongUserType_Throws()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
        var passwordHasher = new Pbkdf2PasswordHasher(Options.Create(new IdentityOptions()));
        var seed = new UatBootstrapSeedService(
            db,
            passwordHasher,
            new TestClock(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Unspecified)),
            CreateConfiguration(),
            new TestHostEnvironment("UAT"));

        await new IdentitySeedService(db).SeedAsync(CancellationToken.None);

        var customerRole = await db.Roles.SingleAsync(item => item.Code == AuthorizationCodes.Customer);
        var customer = new User(UserType.Customer);
        customer.SetProfile("Existing Customer");
        customer.SetContact(null, OwnerEmail);
        customer.SetPasswordHash(passwordHasher.Hash("Customer@123"));
        customer.AssignRole(customerRole);
        db.Users.Add(customer);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => seed.SeedAsync(CancellationToken.None));
    }

    private static void AssertUser(
        User user,
        UserType expectedUserType,
        string expectedRoleCode,
        IPasswordHasher passwordHasher,
        string expectedPassword)
    {
        Assert.Equal(expectedUserType, user.UserType);
        Assert.True(user.IsActive);
        Assert.True(user.HasVerifiedEmail);
        var assignment = Assert.Single(user.UserRoles);
        Assert.Equal(expectedRoleCode, assignment.Role.Code);
        Assert.Null(assignment.BranchId);
        Assert.True(passwordHasher.Verify(user.PasswordHash!, expectedPassword));
    }

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UatBootstrap:Owner:Email"] = OwnerEmail,
                ["UatBootstrap:Owner:Password"] = OwnerPassword,
                ["UatBootstrap:Owner:DisplayName"] = "UAT Owner",
                ["UatBootstrap:SystemAdmin:Email"] = SystemAdminEmail,
                ["UatBootstrap:SystemAdmin:Password"] = SystemAdminPassword,
                ["UatBootstrap:SystemAdmin:DisplayName"] = "UAT System Administrator",
                ["UatBootstrap:Enabled"] = "true"
            })
            .Build();

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DoodhDirectDbContext>(options => options
            .UseInMemoryDatabase($"uat-bootstrap-seed-tests-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        return services.BuildServiceProvider();
    }

    private sealed class TestHostEnvironment(string environmentName) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = nameof(UatBootstrapSeedServiceTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
