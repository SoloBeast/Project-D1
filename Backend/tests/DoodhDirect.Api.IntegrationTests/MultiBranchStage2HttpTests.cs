using System.Net;
using System.Net.Http.Headers;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Branches;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Stage 2 HTTP proof over the real API: a Dairy Manager holding two branches under
/// one role reaches both (and only both) branches, removal preserves the remaining
/// branch after reauthentication, assignment changes through the real EmployeeService
/// take effect immediately, and archiving denies operations on the archived branch
/// while preserving the other.
/// </summary>
public sealed class MultiBranchStage2HttpTests
{
    private static int _counter;

    [Fact]
    public async Task MultiBranchManager_ReachesAssignedBranches_DeniedElsewhere()
    {
        await using var factory = new FoundationApiFactory();
        await using var setup = await DairyManagerSetupAsync(
            factory, [AuthorizationCodes.DairyRead]);
        await MutateAsync(factory, async db =>
        {
            var role = await db.Roles.SingleAsync(x => x.Code == AuthorizationCodes.DairyManager);
            var user = await db.Users
                .Include(x => x.UserRoles)
                .SingleAsync(x => x.Id == setup.ManagerId);
            user.AssignRole(role, setup.BranchBId);
            BranchScopeGuard.EnsureSingleBranchedRole(
                user.UserRoles.Select(x => (x.Role.Code, x.BranchId)));
            user.BumpAuthorizationVersion();
            await db.SaveChangesAsync();
        });
        long branchCId = await MutateAndReturnAsync(factory, async db =>
        {
            var tag = setup.N;
            var branch = new Branch($"S2{tag:00}C", $"Stage2 Branch C{tag}", "Bengaluru", "Karnataka", 13.1m, 77.6m);
            db.Branches.Add(branch);
            await db.SaveChangesAsync();
            return branch.Id;
        });

        var fresh = await MintAsync(factory, setup.ManagerId);
        using var client = BearerClient(factory, fresh);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync(UsagePath(setup.BranchAId), CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync(UsagePath(setup.BranchBId), CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync(UsagePath(branchCId), CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task RemoveOneBranch_StaleDies_PreservesRemainingAfterReauth()
    {
        await using var factory = new FoundationApiFactory();
        await using var setup = await DairyManagerSetupAsync(
            factory, [AuthorizationCodes.DairyRead]);
        await MutateAsync(factory, async db =>
        {
            var role = await db.Roles.SingleAsync(x => x.Code == AuthorizationCodes.DairyManager);
            var user = await db.Users
                .Include(x => x.UserRoles)
                .SingleAsync(x => x.Id == setup.ManagerId);
            user.AssignRole(role, setup.BranchBId);
            user.BumpAuthorizationVersion();
            await db.SaveChangesAsync();
        });
        var both = await MintAsync(factory, setup.ManagerId);
        using var bothClient = BearerClient(factory, both);
        Assert.Equal(HttpStatusCode.OK,
            (await bothClient.GetAsync(UsagePath(setup.BranchBId), CancellationToken.None)).StatusCode);

        // Remove branch B through the real Stage 2 service path as a global admin.
        await MutateWithServicesAsync(factory, async (db, services) =>
        {
            var employees = services.GetRequiredService<IEmployeeService>();
            var adminId = await db.Users
                .Where(x => x.UserType == UserType.SystemAdministrator)
                .Select(x => x.Id)
                .SingleAsync();
            var manager = await db.Users.SingleAsync(x => x.Id == setup.ManagerId);
            await employees.UpdateAsync(
                setup.ManagerId,
                new UpdateEmployeeRequest(
                    manager.DisplayName!, manager.Email, null, null, true,
                    BranchIds: new[] { setup.BranchAId }),
                adminId,
                CancellationToken.None);
        });

        using var stale = BearerClient(factory, both);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await stale.GetAsync(UsagePath(setup.BranchAId), CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await stale.GetAsync(UsagePath(setup.BranchBId), CancellationToken.None)).StatusCode);

        var fresh = await MintAsync(factory, setup.ManagerId);
        using var freshClient = BearerClient(factory, fresh);
        Assert.Equal(HttpStatusCode.OK,
            (await freshClient.GetAsync(UsagePath(setup.BranchAId), CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await freshClient.GetAsync(UsagePath(setup.BranchBId), CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task ArchiveSecondBranch_OperationalDeniedOnArchived_OtherPreserved()
    {
        await using var factory = new FoundationApiFactory();
        await using var setup = await DairyManagerSetupAsync(
            factory, [AuthorizationCodes.DairyRead]);
        await MutateAsync(factory, async db =>
        {
            var role = await db.Roles.SingleAsync(x => x.Code == AuthorizationCodes.DairyManager);
            var user = await db.Users
                .Include(x => x.UserRoles)
                .SingleAsync(x => x.Id == setup.ManagerId);
            user.AssignRole(role, setup.BranchBId);
            user.BumpAuthorizationVersion();
            await db.SaveChangesAsync();
        });
        Guid branchBPublicId = Guid.Empty;
        await MutateAsync(factory, async db =>
        {
            var time = factory.Services.GetRequiredService<IIndiaTimeProvider>();
            var branches = new BranchService(db, new NumberSeriesSeedService(db), time);
            var branch = await db.Branches.SingleAsync(x => x.Id == setup.BranchBId);
            branchBPublicId = branch.PublicId;
            await branches.SetActiveAsync(setup.ManagerId, branchBPublicId, false, CancellationToken.None);
        });
        await MutateAsync(factory, async db =>
        {
            var time = factory.Services.GetRequiredService<IIndiaTimeProvider>();
            var branches = new BranchService(db, new NumberSeriesSeedService(db), time);
            var deleted = await branches.DeleteAsync(setup.ManagerId, branchBPublicId, CancellationToken.None);
            Assert.True(deleted.IsArchived);
        });

        var fresh = await MintAsync(factory, setup.ManagerId);
        using var client = BearerClient(factory, fresh);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync(UsagePath(setup.BranchAId), CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync(UsagePath(setup.BranchBId), CancellationToken.None)).StatusCode);

        // Assignment history is retained for reporting.
        await MutateAsync(factory, async db =>
        {
            var assignments = await db.UserRoles
                .Where(x => x.UserId == setup.ManagerId)
                .Select(x => x.BranchId)
                .ToListAsync();
            Assert.Contains(setup.BranchAId, assignments);
            Assert.Contains(setup.BranchBId, assignments);
        });
    }

    // -----------------------------------------------------------------
    // Harness (mirrors the Stage 1 HTTP proof).
    // -----------------------------------------------------------------

    private sealed record DairySetup(
        FoundationApiFactory Factory,
        AsyncServiceScope Scope,
        int N,
        long ManagerId,
        long BranchAId,
        long BranchBId,
        string Token) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Scope.DisposeAsync();
    }

    private static string UsagePath(long branchId) =>
        $"/api/v1/dairy/branches/{branchId}/usage";

    private static HttpClient BearerClient(FoundationApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task MutateAsync(
        FoundationApiFactory factory, Func<DoodhDirectDbContext, Task> mutate)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
        await mutate(db);
        db.ChangeTracker.Clear();
    }

    private static async Task MutateWithServicesAsync(
        FoundationApiFactory factory, Func<DoodhDirectDbContext, IServiceProvider, Task> mutate)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
        await mutate(db, scope.ServiceProvider);
        db.ChangeTracker.Clear();
    }

    private static async Task<T> MutateAndReturnAsync<T>(
        FoundationApiFactory factory, Func<DoodhDirectDbContext, Task<T>> mutate)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
        var result = await mutate(db);
        db.ChangeTracker.Clear();
        return result;
    }

    private static async Task<string> MintAsync(
        FoundationApiFactory factory, long userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<DoodhDirectDbContext>();
        var jwt = services.GetRequiredService<ITokenService>();
        var time = services.GetRequiredService<IIndiaTimeProvider>();
        var user = await db.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
                .ThenInclude(x => x.RolePermissions)
                .ThenInclude(x => x.Permission)
            .SingleAsync(x => x.Id == userId);
        var authUser = await user.ToAuthUserResultAsync(db, CancellationToken.None);
        var now = time.Now;
        return jwt.Create(
            user,
            new UserSession(user.Id, "sec-device", null, null, null, null, now),
            authUser.Roles,
            authUser.Permissions,
            authUser.BranchIds,
            now,
            user.AuthorizationVersion,
            authUser.BranchHistoryIds).AccessToken;
    }

    private static async Task<DairySetup> DairyManagerSetupAsync(
        FoundationApiFactory factory, string[] permissions)
    {
        var n = Interlocked.Increment(ref _counter);
        var scope = factory.Services.CreateAsyncScope();
        try
        {
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<DoodhDirectDbContext>();
            var jwt = services.GetRequiredService<ITokenService>();
            var time = services.GetRequiredService<IIndiaTimeProvider>();

            var branchA = new Branch($"S2{n}A", $"Stage2 Branch A{n}", "Bengaluru", "Karnataka", 12.9m, 77.5m);
            var branchB = new Branch($"S2{n}B", $"Stage2 Branch B{n}", "Bengaluru", "Karnataka", 13.0m, 77.6m);
            db.Branches.AddRange(branchA, branchB);
            var role = new Role(AuthorizationCodes.DairyManager, "Dairy Manager");
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            foreach (var code in permissions.Distinct())
            {
                var permission = new Permission(code, code);
                db.Permissions.Add(permission);
                await db.SaveChangesAsync();
                db.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
            }

            var adminRole = new Role($"S2ADMIN{n}", "Stage2 Admin");
            db.Roles.Add(adminRole);
            await db.SaveChangesAsync();
            foreach (var code in new[] { AuthorizationCodes.GlobalAccess, AuthorizationCodes.EmployeesManage })
            {
                var permission = await db.Permissions.SingleOrDefaultAsync(x => x.Code == code);
                if (permission is null)
                {
                    permission = new Permission(code, code);
                    db.Permissions.Add(permission);
                    await db.SaveChangesAsync();
                }

                db.RolePermissions.Add(new RolePermission(adminRole.Id, permission.Id));
            }

            var admin = new User(UserType.SystemAdministrator);
            admin.SetProfile($"Stage2 Admin {n}");
            admin.SetContact($"+916{n:D9}".Substring(0, 13), null);
            admin.AssignRole(adminRole);
            db.Users.Add(admin);

            var manager = new User(UserType.Employee);
            manager.SetProfile($"Stage2 Manager {n}");
            manager.SetContact($"+917{n:D9}".Substring(0, 13), null);
            manager.AssignRole(role, branchA.Id);
            db.Users.Add(manager);
            await db.SaveChangesAsync();

            var now = time.Now;
            var token = jwt.Create(
                manager,
                new UserSession(manager.Id, "sec-device", null, null, null, null, now),
                [AuthorizationCodes.DairyManager],
                permissions,
                [branchA.Id],
                now,
                manager.AuthorizationVersion).AccessToken;
            db.ChangeTracker.Clear();

            return new DairySetup(factory, scope, n, manager.Id, branchA.Id, branchB.Id, token);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }
}

/// <summary>
/// Stage 2 concurrency proof on real SQL Server: two writers replacing the same
/// Dairy Manager's branch set serialize — the loser re-validates against the
/// winner's committed state instead of silently merging, dropping, or duplicating
/// assignments. Requires the local SQLEXPRESS instance like the other SQL Server
/// concurrency suites in this project.
/// </summary>
public sealed class EmployeeBranchAssignmentConcurrencyTests
{
    private const string SqlServerInstance = @".\SQLEXPRESS";

    [Fact]
    public async Task ConcurrentBranchUpdatesOnSqlServer_SerializeWithoutLossOrDuplication()
    {
        await using var harness = await EmployeeBranchSqlServerHarness.CreateAsync();
        var initialVersion = await VersionOfAsync(harness, harness.EmployeeId);

        var first = Task.Run(async () =>
        {
            await using var db = harness.CreateDb();
            var service = harness.CreateService(db);
            await service.UpdateAsync(
                harness.EmployeeId,
                new UpdateEmployeeRequest(
                    "Race Manager", null, null, null, true,
                    BranchIds: new[] { harness.BranchAId, harness.BranchBId }),
                harness.AdminId,
                CancellationToken.None);
        });
        var second = Task.Run(async () =>
        {
            await using var db = harness.CreateDb();
            var service = harness.CreateService(db);
            await service.UpdateAsync(
                harness.EmployeeId,
                new UpdateEmployeeRequest(
                    "Race Manager", null, null, null, true,
                    BranchIds: new[] { harness.BranchBId, harness.BranchCId }),
                harness.AdminId,
                CancellationToken.None);
        });
        await Task.WhenAll(first, second);

        await using var verify = harness.CreateDb();
        var persisted = await verify.UserRoles
            .Where(x => x.UserId == harness.EmployeeId)
            .Select(x => x.BranchId)
            .ToListAsync();
        var ordered = persisted.Where(x => x.HasValue).Select(x => x!.Value).OrderBy(x => x).ToArray();

        // Exactly one writer's complete intent survived: no union of both sets,
        // no partial application, no duplicates (the unique index guards too).
        var firstSet = new[] { harness.BranchAId, harness.BranchBId }.OrderBy(x => x).ToArray();
        var secondSet = new[] { harness.BranchBId, harness.BranchCId }.OrderBy(x => x).ToArray();
        Assert.True(
            ordered.SequenceEqual(firstSet) || ordered.SequenceEqual(secondSet),
            "Persisted branches were neither writer's complete set: [" + string.Join(", ", ordered) + "]");
        Assert.Equal(ordered.Length, ordered.Distinct().Count());

        // Both writers applied (a deadlock-victim retry still converges; the epoch
        // may advance by more than one per logical update on retry).
        Assert.True(
            await VersionOfAsync(harness, harness.EmployeeId) >= initialVersion + 2,
            "Both concurrent updates should have bumped the authorization epoch.");
    }

    private static async Task<long> VersionOfAsync(EmployeeBranchSqlServerHarness harness, long userId)
    {
        await using var db = harness.CreateDb();
        return await db.Users
            .Where(x => x.Id == userId)
            .Select(x => x.AuthorizationVersion)
            .SingleAsync();
    }

    private sealed class EmployeeBranchSqlServerHarness : IAsyncDisposable
    {
        private readonly DbContextOptions<DoodhDirectDbContext> _options;
        private readonly DoodhDirectDbContext _db;
        private readonly string _databaseName;
        private readonly TestClock _clock;
        private readonly TestIndiaTimeProvider _time;
        private readonly TestPasswordHasher _hasher;
        private readonly TestTokenService _tokens;
        private readonly CapturingOtpDelivery _delivery;
        private readonly IdentityOptions _identityOptions;

        private EmployeeBranchSqlServerHarness(
            DbContextOptions<DoodhDirectDbContext> options,
            DoodhDirectDbContext db,
            string databaseName,
            TestClock clock,
            TestIndiaTimeProvider time,
            TestPasswordHasher hasher,
            TestTokenService tokens,
            CapturingOtpDelivery delivery,
            IdentityOptions identityOptions)
        {
            _options = options;
            _db = db;
            _databaseName = databaseName;
            _clock = clock;
            _time = time;
            _hasher = hasher;
            _tokens = tokens;
            _delivery = delivery;
            _identityOptions = identityOptions;
        }

        public long AdminId { get; private set; }

        public long EmployeeId { get; private set; }

        public long BranchAId { get; private set; }

        public long BranchBId { get; private set; }

        public long BranchCId { get; private set; }

        public static async Task<EmployeeBranchSqlServerHarness> CreateAsync()
        {
            var databaseName = $"DoodhDirect_Test_EmployeeBranchRace_{Guid.NewGuid():N}";
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = SqlServerInstance,
                InitialCatalog = databaseName,
                IntegratedSecurity = true,
                Encrypt = true,
                TrustServerCertificate = true,
                MultipleActiveResultSets = true,
            };

            // Mirrors the production DbContext registration: serializable deadlock
            // victims MUST be retried so the retry re-validates against the
            // winner's committed state instead of surfacing SqlException 1205.
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlServer(builder.ConnectionString, sql =>
                {
                    sql.EnableRetryOnFailure(maxRetryCount: 5);
                    sql.CommandTimeout(60);
                })
                .Options;
            var db = new DoodhDirectDbContext(options);

            try
            {
                await db.Database.EnsureCreatedAsync();
            }
            catch (SqlException ex) when (ex.Number is -2 or 2 or 40 or 53)
            {
                await db.DisposeAsync();
                throw new InvalidOperationException(
                    $"This concurrency test requires the local SQL Server Express instance ('{SqlServerInstance}'). " +
                    "Start the SQLEXPRESS service and re-run dotnet test.",
                    ex);
            }

            var clock = new TestClock(new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Unspecified));
            var time = new TestIndiaTimeProvider(clock);
            var hasher = new TestPasswordHasher();
            var tokens = new TestTokenService();
            var delivery = new CapturingOtpDelivery();
            var identityOptions = new IdentityOptions();

            var harness = new EmployeeBranchSqlServerHarness(
                options, db, databaseName, clock, time, hasher, tokens, delivery, identityOptions);

            var dairyManager = new Role(AuthorizationCodes.DairyManager, "Dairy Manager");
            var adminRole = new Role("RACE_ADMIN", "Race Admin");
            var globalAccess = new Permission(AuthorizationCodes.GlobalAccess, "Global access");
            db.Roles.AddRange(dairyManager, adminRole);
            db.Permissions.Add(globalAccess);
            await db.SaveChangesAsync();
            db.RolePermissions.Add(new RolePermission(adminRole.Id, globalAccess.Id));

            var branchA = new Branch("RACEA", "Race Branch A", "Bengaluru", "Karnataka", 12.9m, 77.5m);
            var branchB = new Branch("RACEB", "Race Branch B", "Bengaluru", "Karnataka", 13.0m, 77.6m);
            var branchC = new Branch("RACEC", "Race Branch C", "Bengaluru", "Karnataka", 13.1m, 77.6m);
            db.Branches.AddRange(branchA, branchB, branchC);
            await db.SaveChangesAsync();

            var admin = new User(UserType.SystemAdministrator);
            admin.SetProfile("Race Admin");
            admin.SetContact("+919999000091", "race-admin@example.com");
            admin.AssignRole(adminRole);
            db.Users.Add(admin);

            var employee = new User(UserType.Employee);
            employee.SetProfile("Race Manager");
            employee.SetContact("+919999000092", "race-manager@example.com");
            employee.AssignRole(dairyManager, branchA.Id);
            db.Users.Add(employee);
            await db.SaveChangesAsync();

            harness.AdminId = admin.Id;
            harness.EmployeeId = employee.Id;
            harness.BranchAId = branchA.Id;
            harness.BranchBId = branchB.Id;
            harness.BranchCId = branchC.Id;
            return harness;
        }

        public DoodhDirectDbContext CreateDb() => new(_options);

        public EmployeeService CreateService(DoodhDirectDbContext db) => new(
            db,
            _hasher,
            _tokens,
            _time,
            new TestNotificationEventWriter(db, _clock),
            new SecureTokenGenerator(),
            _delivery,
            Options.Create(_identityOptions));

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            // Best-effort cleanup of the unique race database; never fail a green
            // test because cleanup hit a transient server state.
            try
            {
                await using var master = new DoodhDirectDbContext(
                    new DbContextOptionsBuilder<DoodhDirectDbContext>()
                        .UseSqlServer(
                            new SqlConnectionStringBuilder
                            {
                                DataSource = SqlServerInstance,
                                InitialCatalog = "master",
                                IntegratedSecurity = true,
                                Encrypt = true,
                                TrustServerCertificate = true,
                            }.ConnectionString)
                        .Options);
                await master.Database.ExecuteSqlRawAsync(
                    $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];");
                await master.DisposeAsync();
            }
            catch
            {
            }
        }
    }
}
