using System.Net;
using System.Net.Http.Headers;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Dairy;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Branches;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Stage 1 multi-branch security foundation: cross-role scope confinement,
/// operational/history branch partitioning, immediate token invalidation, and
/// archived-branch semantics. Multi-branch assignment itself stays disabled;
/// these tests pin the guardrails Stage 2 will rely on.
/// </summary>
public sealed class BranchScopeSecurityStage1Tests
{
    // -----------------------------------------------------------------
    // BranchScopeGuard: pure invariant, no database required.
    // -----------------------------------------------------------------

    [Fact]
    public void Guard_AllowsSingleRoleAcrossManyBranches()
    {
        BranchScopeGuard.EnsureSingleBranchedRole(new[]
        {
            (AuthorizationCodes.DairyManager, (long?)11),
            (AuthorizationCodes.DairyManager, (long?)22),
        });
    }

    [Fact]
    public void Guard_AllowsGlobalAssignmentsAlongsideBranchedRole()
    {
        BranchScopeGuard.EnsureSingleBranchedRole(new[]
        {
            (AuthorizationCodes.SystemAdmin, (long?)null),
            (AuthorizationCodes.DairyManager, (long?)11),
        });
    }

    [Fact]
    public void Guard_AllowsEmptyAssignments()
    {
        BranchScopeGuard.EnsureSingleBranchedRole(
            Array.Empty<(string, long?)>());
    }

    [Fact]
    public void Guard_RejectsTwoBranchedRoles()
    {
        var ex = Assert.Throws<BusinessRuleException>(() =>
            BranchScopeGuard.EnsureSingleBranchedRole(new[]
            {
                (AuthorizationCodes.DairyManager, (long?)11),
                (AuthorizationCodes.DeliveryManager, (long?)22),
            }));
        Assert.Contains(AuthorizationCodes.DairyManager, ex.Message);
        Assert.Contains(AuthorizationCodes.DeliveryManager, ex.Message);
    }

    [Fact]
    public void Guard_RejectsSameBranchUnderTwoRoles()
    {
        Assert.Throws<BusinessRuleException>(() =>
            BranchScopeGuard.EnsureSingleBranchedRole(new[]
            {
                (AuthorizationCodes.DairyManager, (long?)11),
                (AuthorizationCodes.DeliveryManager, (long?)11),
            }));
    }

    // -----------------------------------------------------------------
    // Scope computation: issuance is fail-closed and partitions history.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Issuance_FailsClosedOnConflictingBranchedRoles()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var user = await SeedEmployeeAsync(
            harness, AuthorizationCodes.DairyManager, harness.MainBranch.Id);
        var deliveryManager = await harness.Db.Roles
            .SingleAsync(x => x.Code == AuthorizationCodes.DeliveryManager);
        harness.Db.UserRoles.Add(new UserRole(user.Id, deliveryManager.Id, harness.NorthBranch.Id));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var loaded = await harness.Db.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .SingleAsync(x => x.Id == user.Id);
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            loaded.ToAuthUserResultAsync(harness.Db, CancellationToken.None));
    }

    [Fact]
    public async Task Issuance_AllowsSameRoleOnTwoBranches()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var user = await SeedEmployeeAsync(
            harness, AuthorizationCodes.DairyManager, harness.MainBranch.Id);
        var dairyManager = await harness.Db.Roles
            .SingleAsync(x => x.Code == AuthorizationCodes.DairyManager);
        harness.Db.UserRoles.Add(new UserRole(user.Id, dairyManager.Id, harness.NorthBranch.Id));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var loaded = await harness.Db.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .SingleAsync(x => x.Id == user.Id);
        var authUser = await loaded.ToAuthUserResultAsync(harness.Db, CancellationToken.None);
        Assert.Equal(
            new[] { harness.MainBranch.Id, harness.NorthBranch.Id }.OrderBy(x => x),
            authUser.BranchIds.OrderBy(x => x));
        Assert.Empty(authUser.BranchHistoryIds ?? []);
    }

    [Fact]
    public async Task Issuance_PartitionsArchivedBranchIntoHistoryOnly()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var user = await SeedEmployeeAsync(
            harness, AuthorizationCodes.DairyManager, harness.MainBranch.Id);
        var branch = await harness.Db.Branches.SingleAsync(x => x.Id == harness.MainBranch.Id);
        branch.Archive(harness.TimeProvider.Now);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var loaded = await harness.Db.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .SingleAsync(x => x.Id == user.Id);
        var authUser = await loaded.ToAuthUserResultAsync(harness.Db, CancellationToken.None);
        Assert.Empty(authUser.BranchIds);
        Assert.Equal(new[] { harness.MainBranch.Id }, authUser.BranchHistoryIds);
        Assert.Equal(harness.MainBranch.Code, authUser.BranchDetails!.Single().Code);
    }

    // -----------------------------------------------------------------
    // AuthorizationVersion bumps on every effective-authorization change.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Update_BumpsVersionOnBranchChange_NotOnProfileEdit()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Meera", "+919876543211", "meera@example.com",
                AuthorizationCodes.DeliveryStaff, harness.MainBranch.Id, SendInvitation: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        Assert.Equal(0, await VersionOfAsync(harness, created.Employee.Id));

        var renamed = await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Meera Devi", null, null, null, true),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        Assert.Equal(0, await VersionOfAsync(harness, renamed.Id));

        var moved = await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Meera Devi", null, null, harness.NorthBranch.Id, true),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        Assert.Equal(harness.NorthBranch.Id, moved.BranchId);
        Assert.Equal(1, await VersionOfAsync(harness, moved.Id));

        var deactivated = await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Meera Devi", null, null, null, false),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        Assert.False(deactivated.IsActive);
        Assert.Equal(2, await VersionOfAsync(harness, deactivated.Id));
    }

    [Fact]
    public async Task BranchArchive_BumpsAssignedUsers_AndRetainsAssignment()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Archie", "+919876543212", "archie@example.com",
                AuthorizationCodes.DairyManager, harness.MainBranch.Id, SendInvitation: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        var branches = new BranchService(
            harness.Db, new NumberSeriesSeedService(harness.Db), harness.TimeProvider);

        await branches.SetActiveAsync(
            harness.SystemAdmin.Id, harness.MainBranch.PublicId, false, CancellationToken.None);
        Assert.Equal(1, await VersionOfAsync(harness, created.Employee.Id));

        await branches.DeleteAsync(
            harness.SystemAdmin.Id, harness.MainBranch.PublicId, CancellationToken.None);
        Assert.Equal(2, await VersionOfAsync(harness, created.Employee.Id));

        // Assignment history is retained for reporting; operational scope is
        // derived from branch eligibility at issuance, not row deletion.
        var assignments = await harness.Db.UserRoles
            .Where(x => x.UserId == created.Employee.Id)
            .ToListAsync();
        Assert.Single(assignments);
        Assert.Equal(harness.MainBranch.Id, assignments[0].BranchId);
    }

    // -----------------------------------------------------------------
    // Per-request revalidation matrix.
    // -----------------------------------------------------------------

    [Fact]
    public async Task ScopeValidator_AcceptsCurrent_RejectsStaleInactiveAndMissing()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Veda", "+919876543213", "veda@example.com",
                AuthorizationCodes.DeliveryStaff, harness.MainBranch.Id, SendInvitation: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        var validator = new AuthorizationScopeValidator(harness.Db);

        Assert.True(await validator.IsTokenScopeCurrentAsync(
            created.Employee.Id, 0, CancellationToken.None));
        Assert.False(await validator.IsTokenScopeCurrentAsync(
            created.Employee.Id, 7, CancellationToken.None));
        Assert.False(await validator.IsTokenScopeCurrentAsync(
            987654321, 0, CancellationToken.None));

        await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Veda", null, null, null, false),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        Assert.False(await validator.IsTokenScopeCurrentAsync(
            created.Employee.Id, 0, CancellationToken.None));
    }

    // -----------------------------------------------------------------
    // Helpers.
    // -----------------------------------------------------------------

    private static async Task<User> SeedEmployeeAsync(
        EmployeeHarness harness, string roleCode, long branchId)
    {
        var role = await harness.Db.Roles.SingleAsync(x => x.Code == roleCode);
        var user = new User(UserType.Employee);
        user.SetProfile($"Stage1 {roleCode}");
        user.SetContact($"+919000{Math.Abs(HashCode.Combine(roleCode, branchId)) % 900000 + 100000}", null);
        user.AssignRole(role, branchId);
        harness.Db.Users.Add(user);
        await harness.Db.SaveChangesAsync();
        return user;
    }

    private static async Task<long> VersionOfAsync(EmployeeHarness harness, long userId)
    {
        await using var context = harness.CreateContext();
        return await context.Users
            .Where(x => x.Id == userId)
            .Select(x => x.AuthorizationVersion)
            .SingleAsync();
    }
}
