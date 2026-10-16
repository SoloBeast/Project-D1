using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Dairy;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Branches;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Stage 1 HTTP proof: branch assignment changes take effect on existing
/// access tokens immediately, single-branch and global behavior is unchanged,
/// archived branches deny operations but keep history, and out-of-scope
/// branches cannot be reached through direct API requests.
/// </summary>
public sealed class BranchScopeHttpSecurityTests
{
    private static int _counter;

    [Fact]
    public async Task OldToken_RejectedImmediately_AfterBranchRemoved()
    {
        await using var factory = new FoundationApiFactory();
        await using var setup = await DairyManagerSetupAsync(
            factory, [AuthorizationCodes.DairyRead]);

        using var client = BearerClient(factory, setup.Token);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync(UsagePath(setup.BranchAId), CancellationToken.None)).StatusCode);

        await MutateAsync(factory, async db =>
        {
            var user = await db.Users
                .Include(x => x.UserRoles)
                .SingleAsync(x => x.Id == setup.ManagerId);
            db.UserRoles.RemoveRange(user.UserRoles);
            user.BumpAuthorizationVersion();
            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync(UsagePath(setup.BranchAId), CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task OldToken_CannotReachNewBranch_UntilRefreshed()
    {
        await using var factory = new FoundationApiFactory();
        await using var setup = await DairyManagerSetupAsync(
            factory, [AuthorizationCodes.DairyRead]);

        // Second branch granted under the same role, then the old token must
        // fail everywhere (its scope version is stale) until re-issued.
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

        using var stale = BearerClient(factory, setup.Token);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await stale.GetAsync(UsagePath(setup.BranchAId), CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await stale.GetAsync(UsagePath(setup.BranchBId), CancellationToken.None)).StatusCode);

        var fresh = await MintAsync(factory, setup.ManagerId);
        using var freshClient = BearerClient(factory, fresh);
        Assert.Equal(HttpStatusCode.OK,
            (await freshClient.GetAsync(UsagePath(setup.BranchAId), CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await freshClient.GetAsync(UsagePath(setup.BranchBId), CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task SingleBranchScope_Unchanged_OutOfScopeBranchForbidden()
    {
        await using var factory = new FoundationApiFactory();
        await using var setup = await DairyManagerSetupAsync(
            factory, [AuthorizationCodes.DairyRead]);

        using var client = BearerClient(factory, setup.Token);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync(UsagePath(setup.BranchAId), CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync(UsagePath(setup.BranchBId), CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task GlobalAccess_Unchanged_AfterScopeHardening()
    {
        await using var factory = new FoundationApiFactory();
        var n = Interlocked.Increment(ref _counter);
        var adminToken = string.Empty;
        long branchAId = 0;
        await MutateAsync(factory, async db =>
        {
            var branch = new Branch($"SEC{n}G", $"Security Branch G{n}", "Bengaluru", "Karnataka", 12.9m, 77.5m);
            db.Branches.Add(branch);
            var adminRole = new Role(AuthorizationCodes.SystemAdmin, "System Administrator");
            db.Roles.Add(adminRole);
            var global = new Permission(AuthorizationCodes.GlobalAccess, "Global access");
            var dairyRead = new Permission(AuthorizationCodes.DairyRead, "Read dairy");
            db.Permissions.AddRange(global, dairyRead);
            await db.SaveChangesAsync();
            db.RolePermissions.AddRange(
                new RolePermission(adminRole.Id, global.Id),
                new RolePermission(adminRole.Id, dairyRead.Id));
            var admin = new User(UserType.SystemAdministrator);
            admin.SetProfile("Security Admin");
            admin.SetContact($"+918{n:D9}".Substring(0, 13), null);
            admin.AssignRole(adminRole, null);
            db.Users.Add(admin);
            await db.SaveChangesAsync();
            branchAId = branch.Id;
            db.ChangeTracker.Clear();
        });
        await MutateAsync(factory, async db =>
        {
            var admin = await db.Users.SingleAsync(x => x.UserType == UserType.SystemAdministrator);
            var jwt = factory.Services.GetRequiredService<ITokenService>();
            var time = factory.Services.GetRequiredService<IIndiaTimeProvider>();
            var now = time.Now;
            adminToken = jwt.Create(
                admin,
                new UserSession(admin.Id, "sec-device", null, null, null, null, now),
                ["SYSTEM_ADMIN"],
                [AuthorizationCodes.GlobalAccess, AuthorizationCodes.DairyRead],
                [],
                now,
                admin.AuthorizationVersion).AccessToken;
            await db.SaveChangesAsync();
        });

        using var client = BearerClient(factory, adminToken);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync(UsagePath(branchAId), CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task ArchivedBranch_OperationalDenied_HistoryPreserved()
    {
        await using var factory = new FoundationApiFactory();
        await using var setup = await DairyManagerSetupAsync(
            factory, [AuthorizationCodes.DairyRead, AuthorizationCodes.ReportsOperationsRead]);

        // Seed operational history, then archive through the real service so
        // assignment bumps flow exactly as in production.
        Guid branchPublicId = Guid.Empty;
        await MutateAsync(factory, async db =>
        {
            var time = factory.Services.GetRequiredService<IIndiaTimeProvider>();
            var branch = await db.Branches.SingleAsync(x => x.Id == setup.BranchAId);
            branchPublicId = branch.PublicId;
            db.MilkProductions.Add(new MilkProduction(
                branch.Id, time.Now, 4, 40m, "L", setup.ManagerId, "Morning", null));
            await db.SaveChangesAsync();
        });
        await MutateAsync(factory, async db =>
        {
            var time = factory.Services.GetRequiredService<IIndiaTimeProvider>();
            var branches = new BranchService(
                db, new NumberSeriesSeedService(db), time);
            await branches.SetActiveAsync(setup.ManagerId, branchPublicId, false, CancellationToken.None);
        });
        await MutateAsync(factory, async db =>
        {
            var time = factory.Services.GetRequiredService<IIndiaTimeProvider>();
            var branches = new BranchService(
                db, new NumberSeriesSeedService(db), time);
            var deleted = await branches.DeleteAsync(setup.ManagerId, branchPublicId, CancellationToken.None);
            Assert.True(deleted.IsArchived);
        });

        // The pre-archive token dies immediately; the archived branch grants
        // no operational access even with freshly resolved (history-only) scope.
        using var stale = BearerClient(factory, setup.Token);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await stale.GetAsync(UsagePath(setup.BranchAId), CancellationToken.None)).StatusCode);

        var fresh = await MintAsync(factory, setup.ManagerId);
        using var freshClient = BearerClient(factory, fresh);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await freshClient.GetAsync(UsagePath(setup.BranchAId), CancellationToken.None)).StatusCode);

        // ...but authorized historical reporting still sees the production row.
        using var history = await freshClient.GetAsync(
            $"/api/v1/admin/reports/dairy?BranchIds={branchPublicId:D}",
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var body = JsonDocument.Parse(await history.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        Assert.True(body.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32() >= 1);
    }

    // -----------------------------------------------------------------
    // Harness.
    // -----------------------------------------------------------------

    private sealed record DairySetup(
        FoundationApiFactory Factory,
        AsyncServiceScope Scope,
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

            var branchA = new Branch($"SEC{n}A", $"Security Branch A{n}", "Bengaluru", "Karnataka", 12.9m, 77.5m);
            var branchB = new Branch($"SEC{n}B", $"Security Branch B{n}", "Bengaluru", "Karnataka", 13.0m, 77.6m);
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

            var manager = new User(UserType.Employee);
            manager.SetProfile($"Security Manager {n}");
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

            return new DairySetup(factory, scope, manager.Id, branchA.Id, branchB.Id, token);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }
}
