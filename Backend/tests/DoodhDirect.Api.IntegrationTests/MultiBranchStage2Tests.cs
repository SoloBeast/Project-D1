using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Branches;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Stage 2 multi-branch Dairy Manager: assignments under one role, invitation branch
/// sets, atomic full-set validation, stale-token invalidation, and scoped visibility.
/// SQLite-backed (shared in-memory connections); true concurrent-writer contention is
/// covered separately against SQL Server below.
/// </summary>
public sealed class MultiBranchStage2Tests
{
    private static readonly DeviceInfo Stage2Device = new(
        "stage2-device-1",
        "Stage 2 test device",
        "test",
        "127.0.0.1",
        "DoodhDirect.Tests");

    // -----------------------------------------------------------------
    // Assignment under one role.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Create_DairyManager_WithTwoBranches_PersistsFullSetAndMapsCompletely()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Meera Nair",
                "+919876543301",
                "meera@example.com",
                AuthorizationCodes.DairyManager,
                null,
                SendInvitation: true,
                BranchIds: new[] { harness.NorthBranch.Id, harness.MainBranch.Id }),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        Assert.NotNull(created.Invitation);
        var employee = created.Employee;
        Assert.Equal(
            new[] { harness.MainBranch.Id, harness.NorthBranch.Id }.OrderBy(x => x),
            employee.BranchAssignments!.Select(x => x.BranchId).OrderBy(x => x));
        // Legacy scalar compatibility: first assignment ordered by branch id.
        Assert.Equal(harness.MainBranch.Id, employee.BranchId);
        Assert.Equal("Main Branch", employee.BranchName);
        Assert.Equal(
            new[] { "Main Branch", "North Branch" }.OrderBy(x => x),
            employee.BranchAssignments.Select(x => x.BranchName).OrderBy(x => x));

        var assignments = await harness.Db.UserRoles
            .Where(x => x.UserId == employee.Id)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        var roleCodes = await harness.Db.Roles
            .Where(r => assignments.Select(a => a.RoleId).Contains(r.Id))
            .Select(r => r.Code)
            .ToListAsync();
        Assert.All(roleCodes, code => Assert.Equal(AuthorizationCodes.DairyManager, code));

        // The invitation carries the full set in link rows — no truncation.
        var invitation = await harness.Db.EmployeeInvitations
            .SingleAsync(x => x.Id == created.Invitation.InvitationId);
        Assert.Equal(
            new[] { harness.MainBranch.Id, harness.NorthBranch.Id }.OrderBy(x => x),
            invitation.GetBranchIds().OrderBy(x => x));
        Assert.Equal(2, await harness.Db.EmployeeInvitationBranches
            .CountAsync(x => x.InvitationId == invitation.Id));
    }

    [Fact]
    public async Task Create_DairyManager_LegacySingleBranch_PreservesPriorBehavior()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Ravi Kumar",
                "+919876543302",
                "ravi@example.com",
                AuthorizationCodes.DeliveryStaff,
                harness.MainBranch.Id,
                SendInvitation: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        Assert.Single(created.Employee.BranchAssignments!);
        Assert.Equal(harness.MainBranch.Id, created.Employee.BranchId);
        var assignment = await harness.Db.UserRoles.SingleAsync(x => x.UserId == created.Employee.Id);
        Assert.Equal(harness.MainBranch.Id, assignment.BranchId);
    }

    [Fact]
    public async Task Create_NonDairyManager_WithTwoBranches_RejectedAtomically()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        var ex = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Employees.CreateAsync(
                new CreateEmployeeRequest(
                    "Arjun Rao",
                    "+919876543303",
                    "arjun@example.com",
                    AuthorizationCodes.DeliveryStaff,
                    harness.MainBranch.Id,
                    SendInvitation: false,
                    BranchIds: new[] { harness.MainBranch.Id, harness.NorthBranch.Id }),
                harness.SystemAdmin.Id,
                CancellationToken.None));

        Assert.Contains("Dairy Manager", ex.Message);
        Assert.Equal(0, await harness.Db.Users.CountAsync(x => x.Mobile == "+919876543303"));
        Assert.Equal(0, await harness.Db.EmployeeInvitations.CountAsync());
    }

    [Fact]
    public async Task Update_AddAndRemoveBranches_MapsCompleteSetAndAuditsDiff()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Meera Nair",
                "+919876543304",
                "meera@example.com",
                AuthorizationCodes.DairyManager,
                harness.MainBranch.Id,
                SendInvitation: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        var expanded = await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest(
                "Meera Nair", "meera@example.com", null, null, true,
                BranchIds: new[] { harness.MainBranch.Id, harness.NorthBranch.Id }),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        Assert.Equal(2, expanded.BranchAssignments!.Count);
        Assert.Equal(1, await VersionOfAsync(harness, expanded.Id));

        var narrowed = await harness.Employees.UpdateAsync(
            expanded.Id,
            new UpdateEmployeeRequest(
                "Meera Nair", "meera@example.com", null, null, true,
                BranchIds: new[] { harness.NorthBranch.Id }),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        Assert.Single(narrowed.BranchAssignments!);
        Assert.Equal(harness.NorthBranch.Id, narrowed.BranchId);
        Assert.Equal(harness.NorthBranch.Id, narrowed.BranchAssignments[0].BranchId);
        Assert.Equal(2, await VersionOfAsync(harness, narrowed.Id));

        // The persisted set matches exactly — no leftovers, no duplicates.
        var persisted = await harness.Db.UserRoles
            .Where(x => x.UserId == narrowed.Id)
            .Select(x => x.BranchId)
            .ToListAsync();
        Assert.Equal(new long?[] { harness.NorthBranch.Id }, persisted);

        var branchAudits = await harness.Db.AuditLogs
            .Where(x => x.Action == EmployeeService.ActionBranchChanged)
            .OrderBy(x => x.Id)
            .ToListAsync();
        Assert.Equal(2, branchAudits.Count);
        Assert.Contains(harness.NorthBranch.Id.ToString(), branchAudits[0].Reason ?? string.Empty);
        Assert.Contains(harness.MainBranch.Id.ToString(), branchAudits[1].Reason ?? string.Empty);
    }

    [Fact]
    public async Task Update_PendingInvitationSyncedToNewBranchSet()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Invitee Devi",
                "+919876543305",
                "invitee@example.com",
                AuthorizationCodes.DairyManager,
                harness.MainBranch.Id,
                SendInvitation: true),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest(
                "Invitee Devi", "invitee@example.com", null, null, true,
                BranchIds: new[] { harness.MainBranch.Id, harness.NorthBranch.Id }),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        var invitation = await harness.Db.EmployeeInvitations
            .SingleAsync(x => x.Id == created.Invitation!.InvitationId);
        Assert.Equal(EmployeeInvitationStatus.Invited, invitation.Status);
        Assert.Equal(AuthorizationCodes.DairyManager, invitation.RoleCode);
        Assert.Equal(
            new[] { harness.MainBranch.Id, harness.NorthBranch.Id }.OrderBy(x => x),
            invitation.GetBranchIds().OrderBy(x => x));
        Assert.Equal(2, await harness.Db.EmployeeInvitationBranches
            .CountAsync(x => x.InvitationId == invitation.Id));
    }

    [Fact]
    public async Task Roles_RetainSingleBranchSemantics_OnUpdate()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Single Singh",
                "+919876543306",
                "single@example.com",
                AuthorizationCodes.DeliveryStaff,
                harness.MainBranch.Id,
                SendInvitation: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        // Explicit single-element list works for non-manager roles.
        var kept = await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest(
                "Single Singh", "single@example.com", null, null, true,
                BranchIds: new[] { harness.MainBranch.Id }),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        Assert.Single(kept.BranchAssignments!);

        // A second branch is rejected and the persisted set is untouched.
        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Employees.UpdateAsync(
                created.Employee.Id,
                new UpdateEmployeeRequest(
                    "Single Singh", "single@example.com", null, null, true,
                    BranchIds: new[] { harness.MainBranch.Id, harness.NorthBranch.Id }),
                harness.SystemAdmin.Id,
                CancellationToken.None));
        Assert.Equal(
            new long?[] { harness.MainBranch.Id },
            await harness.Db.UserRoles
                .Where(x => x.UserId == created.Employee.Id)
                .Select(x => x.BranchId)
                .ToListAsync());
    }

    // -----------------------------------------------------------------
    // Atomic full-set validation matrix.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Create_RejectsDuplicateBranches_Atomically()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Employees.CreateAsync(
                new CreateEmployeeRequest(
                    "Dup Devi",
                    "+919876543311",
                    "dup@example.com",
                    AuthorizationCodes.DairyManager,
                    null,
                    SendInvitation: false,
                    BranchIds: new[] { harness.MainBranch.Id, harness.MainBranch.Id }),
                harness.SystemAdmin.Id,
                CancellationToken.None));

        Assert.Equal(0, await harness.Db.Users.CountAsync(x => x.Mobile == "+919876543311"));
    }

    [Fact]
    public async Task Create_RejectsMissingBranch_Atomically()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Employees.CreateAsync(
                new CreateEmployeeRequest(
                    "Ghost Devi",
                    "+919876543312",
                    "ghost@example.com",
                    AuthorizationCodes.DairyManager,
                    null,
                    SendInvitation: false,
                    BranchIds: new[] { harness.MainBranch.Id, 987654321 }),
                harness.SystemAdmin.Id,
                CancellationToken.None));

        Assert.Equal(0, await harness.Db.Users.CountAsync(x => x.Mobile == "+919876543312"));
    }

    [Fact]
    public async Task Create_RejectsInactiveBranch_Atomically()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        (await harness.Db.Branches.SingleAsync(x => x.Id == harness.NorthBranch.Id)).Deactivate();
        await harness.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Employees.CreateAsync(
                new CreateEmployeeRequest(
                    "Inactive Devi",
                    "+919876543313",
                    "inactive@example.com",
                    AuthorizationCodes.DairyManager,
                    null,
                    SendInvitation: false,
                    BranchIds: new[] { harness.MainBranch.Id, harness.NorthBranch.Id }),
                harness.SystemAdmin.Id,
                CancellationToken.None));

        Assert.Contains("inactive", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await harness.Db.Users.CountAsync(x => x.Mobile == "+919876543313"));
    }

    [Fact]
    public async Task Create_RejectsArchivedBranch_Atomically()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        (await harness.Db.Branches.SingleAsync(x => x.Id == harness.NorthBranch.Id))
            .Archive(harness.TimeProvider.Now);
        await harness.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Employees.CreateAsync(
                new CreateEmployeeRequest(
                    "Archived Devi",
                    "+919876543314",
                    "archived@example.com",
                    AuthorizationCodes.DairyManager,
                    null,
                    SendInvitation: false,
                    BranchIds: new[] { harness.MainBranch.Id, harness.NorthBranch.Id }),
                harness.SystemAdmin.Id,
                CancellationToken.None));

        Assert.Contains("archived", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await harness.Db.Users.CountAsync(x => x.Mobile == "+919876543314"));
    }

    [Fact]
    public async Task Create_RejectsMismatchedBranchIdAndBranchIds()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Employees.CreateAsync(
                new CreateEmployeeRequest(
                    "Mismatch Devi",
                    "+919876543315",
                    "mismatch@example.com",
                    AuthorizationCodes.DairyManager,
                    harness.MainBranch.Id,
                    SendInvitation: false,
                    BranchIds: new[] { harness.NorthBranch.Id }),
                harness.SystemAdmin.Id,
                CancellationToken.None));

        Assert.Equal(0, await harness.Db.Users.CountAsync(x => x.Mobile == "+919876543315"));
    }

    [Fact]
    public async Task Create_RejectsBranchesOutsideActorAuthority()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var scoped = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Scoped Manager",
                "+919876543316",
                "scoped@example.com",
                AuthorizationCodes.DairyManager,
                harness.MainBranch.Id,
                SendInvitation: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        // The scoped manager's own scope (main only) cannot grant north.
        await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.Employees.CreateAsync(
                new CreateEmployeeRequest(
                    "Outsider Devi",
                    "+919876543317",
                    "outsider@example.com",
                    AuthorizationCodes.DairyManager,
                    null,
                    SendInvitation: false,
                    BranchIds: new[] { harness.NorthBranch.Id }),
                scoped.Employee.Id,
                CancellationToken.None));
        Assert.Equal(0, await harness.Db.Users.CountAsync(x => x.Mobile == "+919876543317"));

        // ...but can assign inside its own scope.
        var inside = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Insider Devi",
                "+919876543318",
                "insider@example.com",
                AuthorizationCodes.DeliveryStaff,
                harness.MainBranch.Id,
                SendInvitation: false),
            scoped.Employee.Id,
            CancellationToken.None);
        Assert.Equal(harness.MainBranch.Id, inside.Employee.BranchId);
    }

    [Fact]
    public async Task Create_SystemAdminWithBranch_Rejected()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Employees.CreateAsync(
                new CreateEmployeeRequest(
                    "Admin Devi",
                    "+919876543319",
                    "adminx@example.com",
                    AuthorizationCodes.SystemAdmin,
                    harness.MainBranch.Id,
                    SendInvitation: false),
                harness.Owner.Id,
                CancellationToken.None));
    }

    // -----------------------------------------------------------------
    // Multi-branch invitations.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Invitation_MultiBranch_VerifyCompleteAndRetry()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Multi Meera",
                "+919876543321",
                "multi@example.com",
                AuthorizationCodes.DairyManager,
                null,
                SendInvitation: true,
                BranchIds: new[] { harness.MainBranch.Id, harness.NorthBranch.Id }),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        var verification = await harness.Employees.VerifyInvitationAsync(
            created.Invitation!.Token, CancellationToken.None);
        Assert.True(verification.IsValid);
        Assert.Equal(2, verification.BranchAssignments!.Count);
        Assert.Equal(
            new[] { harness.MainBranch.Id, harness.NorthBranch.Id }.OrderBy(x => x),
            verification.BranchAssignments.Select(x => x.BranchId).OrderBy(x => x));
        Assert.Equal(harness.MainBranch.Id, verification.BranchId);

        var completed = await CompleteAsync(
            harness, created.Invitation.Token, "Multi Meera", "+919876543321", "multi@example.com");
        Assert.Equal(EmployeeInvitationStatus.Registered, completed.InvitationStatus);
        Assert.Equal(
            new[] { harness.MainBranch.Id, harness.NorthBranch.Id }.OrderBy(x => x),
            completed.Session.User.BranchIds.OrderBy(x => x));

        var roles = await harness.Db.UserRoles
            .Where(x => x.UserId == created.Employee.Id)
            .Select(x => x.BranchId)
            .ToListAsync();
        Assert.Equal(2, roles.Count);

        // Retrying acceptance cannot duplicate assignments or re-issue scope.
        await harness.Otp.SendAsync(
            new SendOtpRequest("+919876543321", OtpPurpose.EmployeeInvitation, "127.0.0.1"),
            CancellationToken.None);
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Employees.CompleteRegistrationAsync(
                new CompleteEmployeeRegistrationRequest(
                    created.Invitation.Token,
                    "Multi Meera",
                    "multi@example.com",
                    "+919876543321",
                    "StrongPass!1",
                    harness.Delivery.LastCode!,
                    Stage2Device),
                CancellationToken.None));
        Assert.Equal(2, await harness.Db.UserRoles.CountAsync(x => x.UserId == created.Employee.Id));
    }

    [Fact]
    public async Task Invitation_ResendPreservesBranchSet()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Resend Meera",
                "+919876543322",
                "resend@example.com",
                AuthorizationCodes.DairyManager,
                null,
                SendInvitation: true,
                BranchIds: new[] { harness.MainBranch.Id, harness.NorthBranch.Id }),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        var resent = await harness.Employees.ResendInvitationAsync(
            created.Employee.Id,
            created.Invitation!.InvitationId,
            harness.SystemAdmin.Id,
            CancellationToken.None);

        Assert.NotEqual(created.Invitation.Token, resent.Token);
        var fresh = await harness.Db.EmployeeInvitations
            .SingleAsync(x => x.Id == resent.InvitationId);
        Assert.Equal(
            new[] { harness.MainBranch.Id, harness.NorthBranch.Id }.OrderBy(x => x),
            fresh.GetBranchIds().OrderBy(x => x));

        var old = await harness.Db.EmployeeInvitations
            .SingleAsync(x => x.Id == created.Invitation.InvitationId);
        Assert.Equal(EmployeeInvitationStatus.Cancelled, old.Status);
    }

    // -----------------------------------------------------------------
    // Stale-token invalidation across assignment changes.
    // -----------------------------------------------------------------

    [Fact]
    public async Task BranchChanges_BumpVersion_AddRemoveRoleChange()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Version Vikram",
                "+919876543331",
                "version@example.com",
                AuthorizationCodes.DairyManager,
                harness.MainBranch.Id,
                SendInvitation: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        var validator = new AuthorizationScopeValidator(harness.Db);
        Assert.True(await validator.IsTokenScopeCurrentAsync(created.Employee.Id, 0, CancellationToken.None));

        // Addition invalidates.
        await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Version Vikram", "version@example.com", null, null, true,
                BranchIds: new[] { harness.MainBranch.Id, harness.NorthBranch.Id }),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        Assert.False(await validator.IsTokenScopeCurrentAsync(created.Employee.Id, 0, CancellationToken.None));
        Assert.True(await validator.IsTokenScopeCurrentAsync(created.Employee.Id, 1, CancellationToken.None));

        // Removal invalidates.
        await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Version Vikram", "version@example.com", null, null, true,
                BranchIds: new[] { harness.NorthBranch.Id }),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        Assert.False(await validator.IsTokenScopeCurrentAsync(created.Employee.Id, 1, CancellationToken.None));
        Assert.True(await validator.IsTokenScopeCurrentAsync(created.Employee.Id, 2, CancellationToken.None));

        // Role change invalidates.
        await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Version Vikram", "version@example.com",
                AuthorizationCodes.DeliveryManager, harness.NorthBranch.Id, true),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        Assert.False(await validator.IsTokenScopeCurrentAsync(created.Employee.Id, 2, CancellationToken.None));
        Assert.True(await validator.IsTokenScopeCurrentAsync(created.Employee.Id, 3, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveOneBranch_PreservesRemainingScopeAfterReauth()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Remaining Rita",
                "+919876543332",
                "remaining@example.com",
                AuthorizationCodes.DairyManager,
                null,
                SendInvitation: false,
                BranchIds: new[] { harness.MainBranch.Id, harness.NorthBranch.Id }),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Remaining Rita", "remaining@example.com", null, null, true,
                BranchIds: new[] { harness.NorthBranch.Id }),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        harness.Db.ChangeTracker.Clear();

        var loaded = await harness.Db.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
                .ThenInclude(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .SingleAsync(x => x.Id == created.Employee.Id);
        var authUser = await loaded.ToAuthUserResultAsync(harness.Db, CancellationToken.None);
        Assert.Equal(new[] { harness.NorthBranch.Id }, authUser.BranchIds);
        Assert.DoesNotContain(harness.MainBranch.Id, authUser.BranchIds);
    }

    [Fact]
    public async Task DeactivateBranch_RemovesOperationalScope_KeepsHistory()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "History Hari",
                "+919876543333",
                "history@example.com",
                AuthorizationCodes.DairyManager,
                null,
                SendInvitation: false,
                BranchIds: new[] { harness.MainBranch.Id, harness.NorthBranch.Id }),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        var branches = new BranchService(
            harness.Db,
            new NumberSeriesSeedService(harness.Db),
            harness.TimeProvider);
        await branches.SetActiveAsync(
            harness.SystemAdmin.Id, harness.MainBranch.PublicId, false, CancellationToken.None);
        harness.Db.ChangeTracker.Clear();

        // Assignment history is retained; the deactivated branch leaves scope.
        Assert.Equal(2, await harness.Db.UserRoles.CountAsync(x => x.UserId == created.Employee.Id));
        var loaded = await harness.Db.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
                .ThenInclude(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .SingleAsync(x => x.Id == created.Employee.Id);
        var authUser = await loaded.ToAuthUserResultAsync(harness.Db, CancellationToken.None);
        Assert.Equal(new[] { harness.NorthBranch.Id }, authUser.BranchIds);
        Assert.Equal(new[] { harness.MainBranch.Id }, authUser.BranchHistoryIds);

        // The deactivation bumped the version, killing stale tokens.
        var validator = new AuthorizationScopeValidator(harness.Db);
        Assert.False(await validator.IsTokenScopeCurrentAsync(created.Employee.Id, 0, CancellationToken.None));
    }

    // -----------------------------------------------------------------
    // Visibility: scoped callers, dedup, globals.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Visibility_ScopedCallerSeesOnlySharedBranchEmployees_Deduplicated()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var scoped = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Scoped Sam",
                "+919876543341",
                "scoped@example.com",
                AuthorizationCodes.DairyManager,
                harness.MainBranch.Id,
                SendInvitation: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        var multi = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Multi Mia",
                "+919876543342",
                "mia@example.com",
                AuthorizationCodes.DairyManager,
                null,
                SendInvitation: false,
                BranchIds: new[] { harness.MainBranch.Id, harness.NorthBranch.Id }),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        var other = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "North Nina",
                "+919876543343",
                "nina@example.com",
                AuthorizationCodes.DeliveryStaff,
                harness.NorthBranch.Id,
                SendInvitation: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        var list = await harness.Employees.ListAsync(scoped.Employee.Id, CancellationToken.None);
        var ids = list.Select(x => x.Id).ToArray();
        // Sees self and the multi-branch employee (once), but not the north-only one.
        Assert.Contains(scoped.Employee.Id, ids);
        Assert.Single(ids, x => x == multi.Employee.Id);
        Assert.DoesNotContain(other.Employee.Id, ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        // Branch-less global accounts are outside a scoped caller's visibility.
        Assert.DoesNotContain(harness.SystemAdmin.Id, ids);

        var multiListed = Assert.Single(list, x => x.Id == multi.Employee.Id);
        Assert.Equal(2, multiListed.BranchAssignments!.Count);
    }

    [Fact]
    public async Task Visibility_GetOutOfScope_NotFound()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var scoped = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Scoped Sam",
                "+919876543344",
                "scoped2@example.com",
                AuthorizationCodes.DairyManager,
                harness.MainBranch.Id,
                SendInvitation: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        var other = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "North Nina",
                "+919876543345",
                "nina2@example.com",
                AuthorizationCodes.DeliveryStaff,
                harness.NorthBranch.Id,
                SendInvitation: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Employees.GetAsync(scoped.Employee.Id, other.Employee.Id, CancellationToken.None));

        var got = await harness.Employees.GetAsync(
            harness.SystemAdmin.Id, other.Employee.Id, CancellationToken.None);
        Assert.Equal(other.Employee.Id, got.Id);
    }

    [Fact]
    public async Task Visibility_ArchivedBranchGrantsNoVisibility()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var northScoped = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "North Manager",
                "+919876543346",
                "northmgr@example.com",
                AuthorizationCodes.DairyManager,
                harness.NorthBranch.Id,
                SendInvitation: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        Assert.NotEmpty(await harness.Employees.ListAsync(northScoped.Employee.Id, CancellationToken.None));

        (await harness.Db.Branches.SingleAsync(x => x.Id == harness.NorthBranch.Id))
            .Archive(harness.TimeProvider.Now);
        await harness.Db.SaveChangesAsync();

        // The caller's only branch is archived: operational visibility is empty,
        // while the assignment rows (history) are retained.
        Assert.Empty(await harness.Employees.ListAsync(northScoped.Employee.Id, CancellationToken.None));
        Assert.NotEmpty(await harness.Db.UserRoles.Where(x => x.UserId == northScoped.Employee.Id).ToListAsync());
    }

    // -----------------------------------------------------------------
    // Helpers.
    // -----------------------------------------------------------------

    private static async Task<CompleteEmployeeRegistrationResult> CompleteAsync(
        EmployeeHarness harness,
        string token,
        string name,
        string mobile,
        string? email)
    {
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.EmployeeInvitation, "127.0.0.1"),
            CancellationToken.None);
        return await harness.Employees.CompleteRegistrationAsync(
            new CompleteEmployeeRegistrationRequest(
                token,
                name,
                email,
                mobile,
                "StrongPass!1",
                harness.Delivery.LastCode!,
                Stage2Device),
            CancellationToken.None);
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
