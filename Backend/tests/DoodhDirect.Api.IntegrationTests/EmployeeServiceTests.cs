using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Integrations;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Integration tests for invitation-based employee onboarding, RBAC guards (OWNER vs SYSTEM_ADMIN),
/// audit attribution and single-use token concurrency. The harness uses SQLite in-memory shared
/// connections (not the InMemory provider) because <see cref="EmployeeService.CompleteRegistrationAsync"/>
/// executes inside a serializable transaction, which the InMemory provider does not support.
/// </summary>
public sealed class EmployeeServiceTests
{
    private static readonly DeviceInfo Device = new(
        "employee-device-1",
        "Employee test device",
        "test",
        "127.0.0.1",
        "DoodhDirect.Tests");

    [Fact]
    public async Task CreateEmployeeWithInvitation_ReturnsTokenExactlyOnce_AndAuditsRealActor()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Ravi Kumar",
                "+919876543210",
                "ravi@example.com",
                AuthorizationCodes.DeliveryStaff,
                harness.MainBranch.Id,
                SendInvitation: true),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        Assert.NotNull(created.Invitation);
        Assert.Null(harness.Delivery.LastReqId);
        Assert.Equal(0, await harness.Db.OtpChallenges.CountAsync());
        Assert.Equal(harness.MainBranch.Id, created.Employee.BranchId);
        Assert.Equal(AuthorizationCodes.DeliveryStaff, created.Employee.RoleCode);
        Assert.Equal(created.Invitation.InvitationId, created.Employee.InvitationId);
        Assert.False(string.IsNullOrWhiteSpace(created.Invitation.Token));

        // The raw token is returned exactly once; only its SHA-256 hash is persisted.
        var stored = await harness.Db.EmployeeInvitations
            .SingleAsync(x => x.Id == created.Invitation.InvitationId);
        Assert.Equal(new SecureTokenGenerator().Hash(created.Invitation.Token), stored.TokenHash);
        Assert.NotEqual(created.Invitation.Token, stored.TokenHash);
        Assert.Equal(EmployeeInvitationStatus.Invited, stored.Status);
        Assert.Equal(harness.SystemAdmin.Id, stored.CreatedByUserId);

        // Audit must record the real authenticated actor, never a generic "System".
        var audit = await harness.Db.AuditLogs.SingleAsync(x => x.Action == EmployeeService.ActionInvited);
        Assert.Equal(harness.SystemAdmin.Id, audit.UserId);
        Assert.Equal("Employee", audit.EntityType);
        Assert.NotNull(audit.NewValueJson);
    }

    [Fact]
    public async Task CreateEmployeeWithoutInvitation_AuditsCreated()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Ravi Kumar",
                "+919876543210",
                "ravi@example.com",
                AuthorizationCodes.DeliveryStaff,
                harness.MainBranch.Id,
                SendInvitation: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        Assert.Null(created.Invitation);
        Assert.Null(created.Employee.InvitationId);
        Assert.Equal(0, await harness.Db.EmployeeInvitations.CountAsync());
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == EmployeeService.ActionCreated && x.UserId == harness.SystemAdmin.Id);
    }

    [Fact]
    public async Task VerifyInvitation_ReturnsInviteeWithRoleAndBranch()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        var verification = await harness.Employees.VerifyInvitationAsync(
            created.Invitation!.Token, CancellationToken.None);

        Assert.True(verification.IsValid);
        Assert.Equal("Ravi Kumar", verification.DisplayName);
        Assert.Equal("+919876543210", verification.Mobile);
        Assert.Equal("ravi@example.com", verification.Email);
        Assert.Equal(AuthorizationCodes.DeliveryStaff, verification.RoleCode);
        Assert.Equal(harness.MainBranch.Id, verification.BranchId);
        Assert.Equal(harness.MainBranch.Code, verification.BranchCode);
        Assert.Equal(harness.MainBranch.Name, verification.BranchName);
        Assert.Null(verification.Reason);
    }

    [Fact]
    public async Task VerifyInvitation_WithUnknownToken_IsInvalid()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        var verification = await harness.Employees.VerifyInvitationAsync(
            "definitely-not-a-real-token", CancellationToken.None);

        Assert.False(verification.IsValid);
        Assert.Equal("The invitation link is not recognized.", verification.Reason);
    }

    [Fact]
    public async Task CompleteRegistration_ActivatesAccount_WithInvitationRoleAndBranch()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        var completed = await SendOtpAndCompleteAsync(
            harness, created.Invitation!.Token, "Ravi Kumar", "+919876543210", "ravi@example.com");

        Assert.Equal(EmployeeInvitationStatus.Registered, completed.InvitationStatus);
        Assert.Equal("ravi@example.com", completed.Session.User.Email);
        Assert.Contains(AuthorizationCodes.DeliveryStaff, completed.Session.User.Roles);
        Assert.Contains(harness.MainBranch.Id, completed.Session.User.BranchIds);

        // The authenticated session resolves the assigned branch into display metadata so
        // dashboards render the real branch name instead of a numeric fallback.
        Assert.NotNull(completed.Session.User.BranchDetails);
        var assignedBranch = Assert.Single(completed.Session.User.BranchDetails!);
        Assert.Equal(harness.MainBranch.Id, assignedBranch.Id);
        Assert.Equal(harness.MainBranch.Code, assignedBranch.Code);
        Assert.Equal(harness.MainBranch.Name, assignedBranch.Name);
        Assert.False(string.IsNullOrWhiteSpace(completed.Session.Tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(completed.Session.Tokens.RefreshToken));

        // Role and branch are authoritative values from the invitation — the request carries
        // no role or branch fields, so the client cannot influence the assignment.
        var user = await harness.Db.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .SingleAsync(x => x.Mobile == "+919876543210");
        Assert.True(user.IsActive);
        Assert.Equal(UserType.Employee, user.UserType);
        Assert.Single(user.UserRoles);
        Assert.Equal(AuthorizationCodes.DeliveryStaff, user.UserRoles.Single().Role.Code);
        Assert.Equal(harness.MainBranch.Id, user.UserRoles.Single().BranchId);
        Assert.Equal("test-hash:StrongPass!1", user.PasswordHash);

        var invitation = await harness.Db.EmployeeInvitations
            .SingleAsync(x => x.Id == created.Invitation.InvitationId);
        Assert.Equal(EmployeeInvitationStatus.Registered, invitation.Status);
        Assert.Equal(user.Id, invitation.RegisteredByUserId);
        Assert.NotNull(invitation.RegisteredAt);

        // Registration + activation are audited with the employee as the actor.
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == EmployeeService.ActionRegistered && x.UserId == user.Id);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == EmployeeService.ActionActivated && x.UserId == user.Id);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == "EMPLOYEE_REGISTRATION" && x.EntityType == "UserSession");
        Assert.Equal(1, await harness.Db.UserSessions.CountAsync());
        Assert.Equal(1, await harness.Db.RefreshTokens.CountAsync());
        Assert.Equal(2, await harness.Db.NotificationEvents.CountAsync());
    }

    [Fact]
    public async Task CompleteRegistration_SecondUseOfSameToken_IsRejected()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        var completed = await SendOtpAndCompleteAsync(
            harness, created.Invitation!.Token, "Ravi Kumar", "+919876543210", "ravi@example.com");
        Assert.Equal(EmployeeInvitationStatus.Registered, completed.InvitationStatus);

        // The invitation is single-use: a second completion must fail.
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Employees.CompleteRegistrationAsync(
                new CompleteEmployeeRegistrationRequest(
                    created.Invitation!.Token,
                    "Ravi Kumar",
                    "ravi@example.com",
                    "+919876543210",
                    "StrongPass!1",
                    harness.Delivery.LastCode!,
                    Device),
                CancellationToken.None));

        Assert.Equal(1, await harness.Db.UserSessions.CountAsync());
        Assert.Equal(1, await harness.Db.RefreshTokens.CountAsync());
    }

    [Fact]
    public async Task CompleteRegistration_RequiresEmployeeInvitationOtpPurpose()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        // Invitation creation does not send an OTP. The only outstanding OTP below is the Login
        // one, proving it cannot satisfy EmployeeInvitation registration (purpose-binding in
        // CompleteRegistrationAsync).
        var invitee = await harness.Db.EmployeeInvitations
            .SingleAsync(x => x.Id == created.Invitation!.InvitationId);
        Assert.Equal(0, await harness.Db.OtpChallenges.CountAsync());
        Assert.Null(harness.Delivery.LastReqId);

        // Send a Login OTP instead of an EmployeeInvitation OTP.
        await harness.Otp.SendAsync(
            new SendOtpRequest(invitee.InviteeMobile, OtpPurpose.Login, "127.0.0.1"),
            CancellationToken.None);

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Employees.CompleteRegistrationAsync(
                new CompleteEmployeeRegistrationRequest(
                    created.Invitation!.Token,
                    "Ravi Kumar",
                    "ravi@example.com",
                    "+919876543210",
                    "StrongPass!1",
                    harness.Delivery.LastCode!,
                    Device),
                CancellationToken.None));

        // The invitation must remain usable — nothing was consumed.
        var invitation = await harness.Db.EmployeeInvitations
            .SingleAsync(x => x.Id == created.Invitation!.InvitationId);
        Assert.Equal(EmployeeInvitationStatus.Invited, invitation.Status);
        Assert.Equal(0, await harness.Db.UserSessions.CountAsync());
    }

    [Fact]
    public async Task CompleteRegistration_WrongOtp_IsRejected()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");
        await harness.Otp.SendAsync(
            new SendOtpRequest("+919876543210", OtpPurpose.EmployeeInvitation, "127.0.0.1"),
            CancellationToken.None);

        await Assert.ThrowsAsync<OtpProviderRejectedException>(() =>
            harness.Employees.CompleteRegistrationAsync(
                new CompleteEmployeeRegistrationRequest(
                    created.Invitation!.Token,
                    "Ravi Kumar",
                    "ravi@example.com",
                    "+919876543210",
                    "StrongPass!1",
                    "000000",
                    Device),
                CancellationToken.None));

        Assert.Equal(EmployeeInvitationStatus.Invited,
            (await harness.Db.EmployeeInvitations.SingleAsync(x => x.Id == created.Invitation!.InvitationId)).Status);
        Assert.Equal(0, await harness.Db.UserSessions.CountAsync());
    }

    [Fact]
    public async Task ResendInvitation_InvalidatesOldToken_AndAuditsActor()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");
        var originalToken = created.Invitation!.Token;

        var resent = await harness.Employees.ResendInvitationAsync(
            created.Employee.Id,
            created.Invitation.InvitationId,
            harness.SystemAdmin.Id,
            CancellationToken.None);

        Assert.NotEqual(originalToken, resent.Token);

        var oldInvitation = await harness.Db.EmployeeInvitations
            .SingleAsync(x => x.Id == created.Invitation.InvitationId);
        Assert.Equal(EmployeeInvitationStatus.Cancelled, oldInvitation.Status);

        var freshInvitation = await harness.Db.EmployeeInvitations
            .SingleAsync(x => x.Id == resent.InvitationId);
        Assert.Equal(EmployeeInvitationStatus.Invited, freshInvitation.Status);
        Assert.Equal(harness.SystemAdmin.Id, freshInvitation.CreatedByUserId);
        Assert.Equal(harness.SystemAdmin.Id, freshInvitation.LastResentByUserId);
        Assert.NotNull(freshInvitation.LastResentAt);

        // The previously delivered link is dead.
        var oldVerification = await harness.Employees.VerifyInvitationAsync(originalToken, CancellationToken.None);
        Assert.False(oldVerification.IsValid);
        Assert.Equal("This invitation has been cancelled.", oldVerification.Reason);

        // The fresh token still works.
        var newVerification = await harness.Employees.VerifyInvitationAsync(resent.Token, CancellationToken.None);
        Assert.True(newVerification.IsValid);

        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == EmployeeService.ActionInvitationResent && x.UserId == harness.SystemAdmin.Id);
    }

    [Fact]
    public async Task CreateInvitation_WhenSmtpAcceptsEmail_ReturnsSentOutcomeAndAuditsDelivery()
    {
        var sender = new TestEmailSender(new EmailSendResult(true, null));
        await using var harness = await EmployeeHarness.CreateAsync(
            emailSender: sender,
            integrationSettings: new TestIntegrationSettingsProvider("https://app.example.test"));

        var created = await CreateDeliveryStaffAsync(
            harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        Assert.NotNull(created.Invitation);
        Assert.Equal(InvitationEmailDeliveryStatus.Sent, created.Invitation.EmailDeliveryStatus);
        Assert.Equal("ravi@example.com", created.Invitation.EmailRecipient);
        Assert.Contains($"/invite/{created.Invitation.Token}", sender.LastMessage!.PlainTextBody);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == EmployeeService.ActionInvitationEmailSent &&
            x.EntityId == created.Invitation.InvitationPublicId.ToString());
    }

    [Fact]
    public async Task CreateInvitation_WhenSmtpReturnsNotSent_ReturnsSkippedOutcome()
    {
        await using var harness = await EmployeeHarness.CreateAsync(
            emailSender: new TestEmailSender(
                new EmailSendResult(false, "SMTP delivery is not configured.")),
            integrationSettings: new TestIntegrationSettingsProvider("https://app.example.test"));

        var created = await CreateDeliveryStaffAsync(
            harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        Assert.Equal(InvitationEmailDeliveryStatus.Skipped, created.Invitation!.EmailDeliveryStatus);
        Assert.Contains("SMTP delivery is not configured.", created.Invitation.EmailDeliveryReason);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == EmployeeService.ActionInvitationEmailSkipped);
    }

    [Fact]
    public async Task CreateInvitation_WhenSmtpThrows_ReturnsFailedOutcome()
    {
        await using var harness = await EmployeeHarness.CreateAsync(
            emailSender: new TestEmailSender(new InvalidOperationException("SMTP authentication failed.")),
            integrationSettings: new TestIntegrationSettingsProvider("https://app.example.test"));

        var created = await CreateDeliveryStaffAsync(
            harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        Assert.Equal(InvitationEmailDeliveryStatus.Failed, created.Invitation!.EmailDeliveryStatus);
        Assert.Contains("SMTP authentication failed.", created.Invitation.EmailDeliveryReason);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == EmployeeService.ActionInvitationEmailFailed);
    }

    [Fact]
    public async Task CancelInvitation_InvalidatesToken_AndAuditsActor()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        await harness.Employees.CancelInvitationAsync(
            created.Employee.Id,
            created.Invitation!.InvitationId,
            harness.SystemAdmin.Id,
            CancellationToken.None);

        var invitation = await harness.Db.EmployeeInvitations
            .SingleAsync(x => x.Id == created.Invitation.InvitationId);
        Assert.Equal(EmployeeInvitationStatus.Cancelled, invitation.Status);
        Assert.Equal(harness.SystemAdmin.Id, invitation.CancelledByUserId);
        Assert.NotNull(invitation.CancelledAt);

        var verification = await harness.Employees.VerifyInvitationAsync(
            created.Invitation.Token, CancellationToken.None);
        Assert.False(verification.IsValid);
        Assert.Equal("This invitation has been cancelled.", verification.Reason);

        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == EmployeeService.ActionInvitationCancelled && x.UserId == harness.SystemAdmin.Id);
    }

    [Fact]
    public async Task UpdateEmployee_RoleAndBranchChange_AuditsAndUpdatesPendingInvitation()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        var updated = await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest(
                "Ravi Kumar",
                "ravi@example.com",
                AuthorizationCodes.Accountant,
                harness.NorthBranch.Id,
                IsActive: true),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        Assert.Equal(AuthorizationCodes.Accountant, updated.RoleCode);
        Assert.Equal(harness.NorthBranch.Id, updated.BranchId);

        // A pending invitation follows the new role and branch so registration activates
        // with exactly the administrator's latest intent.
        var pending = await harness.Db.EmployeeInvitations
            .SingleAsync(x => x.Id == created.Invitation!.InvitationId);
        Assert.Equal(AuthorizationCodes.Accountant, pending.RoleCode);
        Assert.Equal(harness.NorthBranch.Id, pending.BranchId);
        Assert.Equal(EmployeeInvitationStatus.Invited, pending.Status);

        var audits = await harness.Db.AuditLogs
            .Where(x => x.Action == EmployeeService.ActionRoleChanged || x.Action == EmployeeService.ActionBranchChanged)
            .ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, x => Assert.Equal(harness.SystemAdmin.Id, x.UserId));
    }

    [Fact]
    public async Task UpdateEmployee_DeactivateAndReactivate_AreAudited()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        var deactivated = await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Ravi Kumar", "ravi@example.com", null, null, IsActive: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        Assert.False(deactivated.IsActive);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == EmployeeService.ActionDeactivated && x.UserId == harness.SystemAdmin.Id);

        var reactivated = await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Ravi Kumar", "ravi@example.com", null, null, IsActive: true),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        Assert.True(reactivated.IsActive);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == EmployeeService.ActionReactivated && x.UserId == harness.SystemAdmin.Id);
    }

    [Fact]
    public async Task UpdateEmployee_CanDeactivateAfterAssignedBranchIsDeactivated()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        harness.MainBranch.Deactivate();
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var deactivated = await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Ravi Kumar", "ravi@example.com", null, null, IsActive: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        Assert.False(deactivated.IsActive);
        Assert.Equal(harness.MainBranch.Id, deactivated.BranchId);
    }

    [Fact]
    public async Task UpdateEmployee_CannotReactivateOnInactiveAssignedBranch()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        var employee = await harness.Db.Users
            .Include(user => user.UserRoles)
            .ThenInclude(userRole => userRole.Role)
            .SingleAsync(user => user.Id == created.Employee.Id);
        employee.Deactivate();
        var assignedBranchId = employee.UserRoles.Single().BranchId;
        var assignedBranch = await harness.Db.Branches.SingleAsync(branch => branch.Id == assignedBranchId);
        assignedBranch.Deactivate();
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() => harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Ravi Kumar", "ravi@example.com", null, null, IsActive: true),
            harness.SystemAdmin.Id,
            CancellationToken.None));

        Assert.Equal("The selected branch is inactive.", exception.Message);
    }

    [Fact]
    public async Task Owner_CanCreateSystemAdministrator()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "IT Admin",
                "+919877770001",
                "itadmin@example.com",
                AuthorizationCodes.SystemAdmin,
                BranchId: null,
                SendInvitation: true),
            harness.Owner.Id,
            CancellationToken.None);

        Assert.NotNull(created.Invitation);
        Assert.Null(created.Employee.BranchId);

        var user = await harness.Db.Users.SingleAsync(x => x.Mobile == "+919877770001");
        Assert.Equal(UserType.SystemAdministrator, user.UserType);
    }

    [Fact]
    public async Task SystemAdministrator_CannotCreateSystemAdministrator()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        var exception = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.Employees.CreateAsync(
                new CreateEmployeeRequest(
                    "Rogue Admin",
                    "+919877770002",
                    "rogue@example.com",
                    AuthorizationCodes.SystemAdmin,
                    BranchId: null,
                    SendInvitation: true),
                harness.SystemAdmin.Id,
                CancellationToken.None));

        Assert.Equal("Only the owner can create or manage system administrators.", exception.Message);
        Assert.Equal(0, await harness.Db.EmployeeInvitations.CountAsync());
    }

    [Fact]
    public async Task OwnerRole_IsRejectedForEmployeeCreation()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Employees.CreateAsync(
                new CreateEmployeeRequest(
                    "Owner Clone",
                    "+919877770003",
                    "owner-clone@example.com",
                    AuthorizationCodes.Owner,
                    harness.MainBranch.Id,
                    SendInvitation: false),
                harness.Owner.Id,
                CancellationToken.None));

        Assert.Equal("The selected role cannot be assigned to an employee.", exception.Message);
    }

    [Fact]
    public async Task ExpiredInvitation_IsRejectedAndMarkedExpired()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Ravi Kumar",
                "+919876543210",
                "ravi@example.com",
                AuthorizationCodes.DeliveryStaff,
                harness.MainBranch.Id,
                SendInvitation: true,
                InvitationExpiresAt: harness.Clock.Now.AddHours(1)),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        var token = created.Invitation!.Token;

        harness.Clock.Advance(TimeSpan.FromHours(2));

        var verification = await harness.Employees.VerifyInvitationAsync(token, CancellationToken.None);
        Assert.False(verification.IsValid);
        Assert.Equal("This invitation has expired.", verification.Reason);

        var invitation = await harness.Db.EmployeeInvitations
            .SingleAsync(x => x.Id == created.Invitation.InvitationId);
        Assert.Equal(EmployeeInvitationStatus.Expired, invitation.Status);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Employees.CompleteRegistrationAsync(
                new CompleteEmployeeRegistrationRequest(
                    token,
                    "Ravi Kumar",
                    "ravi@example.com",
                    "+919876543210",
                    "StrongPass!1",
                    "123456",
                    Device),
                CancellationToken.None));
    }

    [Fact]
    public async Task GetBranchOptions_ReturnsActiveBranchesOrderedByName()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        var options = await harness.Employees.GetBranchOptionsAsync(CancellationToken.None);

        Assert.Equal(2, options.Count);
        Assert.Contains(options, x => x.Id == harness.MainBranch.Id && x.Name == "Main Branch");
        Assert.Contains(options, x => x.Id == harness.NorthBranch.Id && x.Name == "North Branch");
        Assert.Equal("Main Branch", options[0].Name);
        Assert.Equal("North Branch", options[1].Name);
    }

    [Fact]
    public async Task ListAndGet_IncludeInvitationStatusAndInvitationId()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        var list = await harness.Employees.ListAsync(CancellationToken.None);
        var listed = Assert.Single(list, x => x.Id == created.Employee.Id);
        Assert.Equal(EmployeeInvitationStatus.Invited, listed.InvitationStatus);
        Assert.Equal(created.Invitation!.InvitationId, listed.InvitationId);
        Assert.Equal(AuthorizationCodes.DeliveryStaff, listed.RoleCode);
        Assert.Equal("Main Branch", listed.BranchName);

        var got = await harness.Employees.GetAsync(created.Employee.Id, CancellationToken.None);
        Assert.Equal(created.Invitation.InvitationId, got.InvitationId);
        Assert.Equal(EmployeeInvitationStatus.Invited, got.InvitationStatus);
    }

    [Fact]
    public async Task OwnerAndSystemAdministrator_PermissionBoundaries()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        var ownerPermissions = await harness.Db.Roles
            .Where(x => x.Code == AuthorizationCodes.Owner)
            .SelectMany(x => x.RolePermissions)
            .Select(x => x.Permission.Code)
            .ToListAsync();
        Assert.Contains(AuthorizationCodes.IdentityAdministratorsManage, ownerPermissions);
        Assert.Contains(AuthorizationCodes.EmployeesRead, ownerPermissions);
        Assert.Contains(AuthorizationCodes.EmployeesManage, ownerPermissions);

        var systemAdminPermissions = await harness.Db.Roles
            .Where(x => x.Code == AuthorizationCodes.SystemAdmin)
            .SelectMany(x => x.RolePermissions)
            .Select(x => x.Permission.Code)
            .ToListAsync();
        // SYSTEM_ADMIN manages employees but must never hold the ownership-level guard.
        Assert.DoesNotContain(AuthorizationCodes.IdentityAdministratorsManage, systemAdminPermissions);
        Assert.Contains(AuthorizationCodes.EmployeesRead, systemAdminPermissions);
        Assert.Contains(AuthorizationCodes.EmployeesManage, systemAdminPermissions);
    }

    [Fact]
    public async Task ConcurrentRegistration_ExactlyOneCompletes()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");
        await harness.Otp.SendAsync(
            new SendOtpRequest("+919876543210", OtpPurpose.EmployeeInvitation, "127.0.0.1"),
            CancellationToken.None);
        var otpCode = harness.Delivery.LastCode!;
        var request = new CompleteEmployeeRegistrationRequest(
            created.Invitation!.Token,
            "Ravi Kumar",
            "ravi@example.com",
            "+919876543210",
            "StrongPass!1",
            otpCode,
            Device);

        // EF Core DbContext is not thread-safe, so each contender uses its own context over a
        // separate connection into the same shared-cache in-memory SQLite database.
        var context1 = harness.CreateContext();
        var context2 = harness.CreateContext();
        var successes = 0;
        Exception? failure = null;
        var gate = new object();

        await Task.WhenAll(RunAsync(context1), RunAsync(context2));

        async Task RunAsync(DoodhDirectDbContext context)
        {
            try
            {
                await harness.CreateService(context)
                    .CompleteRegistrationAsync(request, CancellationToken.None);
                lock (gate) { successes++; }
            }
            catch (Exception ex)
            {
                lock (gate) { failure = ex; }
            }
            finally
            {
                await context.DisposeAsync();
            }
        }

        // Exactly one contender wins; the single-use token defeats the race.
        Assert.Equal(1, successes);
        Assert.NotNull(failure);

        harness.Db.ChangeTracker.Clear();
        var invitation = await harness.Db.EmployeeInvitations
            .SingleAsync(x => x.Id == created.Invitation.InvitationId);
        Assert.Equal(EmployeeInvitationStatus.Registered, invitation.Status);
        Assert.Equal(1, await harness.Db.UserSessions.CountAsync());
        Assert.Equal(1, await harness.Db.RefreshTokens.CountAsync());
    }

    [Fact]
    public async Task CompleteRegistration_NorthBranchInvitation_CarriesBranchMetadataIntoSession()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        // A Dairy Manager invitation to the NORTH branch — the harness analog of a Dabua/NIT3
        // invitation. The branch must come from the invitation, never from the role or from
        // any default/first branch (no Branch 4, no MAIN substitution).
        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Priya Singh",
                "+919876543211",
                "priya@example.com",
                AuthorizationCodes.DairyManager,
                harness.NorthBranch.Id,
                SendInvitation: true),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        Assert.Equal(harness.NorthBranch.Id, created.Employee.BranchId);

        var verification = await harness.Employees.VerifyInvitationAsync(
            created.Invitation!.Token, CancellationToken.None);
        Assert.True(verification.IsValid);
        Assert.Equal(AuthorizationCodes.DairyManager, verification.RoleCode);
        Assert.Equal(harness.NorthBranch.Id, verification.BranchId);
        Assert.Equal(harness.NorthBranch.Code, verification.BranchCode);
        Assert.Equal(harness.NorthBranch.Name, verification.BranchName);

        var completed = await SendOtpAndCompleteAsync(
            harness, created.Invitation!.Token, "Priya Singh", "+919876543211", "priya@example.com");
        Assert.Equal(EmployeeInvitationStatus.Registered, completed.InvitationStatus);
        Assert.Contains(AuthorizationCodes.DairyManager, completed.Session.User.Roles);
        Assert.Contains(harness.NorthBranch.Id, completed.Session.User.BranchIds);

        // The session exposes the resolved branch (id + code + name) so the dashboard can
        // render "North Branch" — and never MAIN or a numeric fallback.
        Assert.NotNull(completed.Session.User.BranchDetails);
        var sessionBranch = Assert.Single(completed.Session.User.BranchDetails!);
        Assert.Equal(harness.NorthBranch.Id, sessionBranch.Id);
        Assert.Equal(harness.NorthBranch.Code, sessionBranch.Code);
        Assert.Equal(harness.NorthBranch.Name, sessionBranch.Name);
    }

    [Fact]
    public async Task VerifyInvitation_SystemAdminWithoutBranch_HasNoBranchMetadata()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        // Only the owner may create a SYSTEM_ADMIN. The invitation has no branch by design,
        // proving the role never invents a branch assignment.
        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Sita Devi",
                "+919876543212",
                "sita@example.com",
                AuthorizationCodes.SystemAdmin,
                null,
                SendInvitation: true),
            harness.Owner.Id,
            CancellationToken.None);

        var verification = await harness.Employees.VerifyInvitationAsync(
            created.Invitation!.Token, CancellationToken.None);
        Assert.True(verification.IsValid);
        Assert.Equal(AuthorizationCodes.SystemAdmin, verification.RoleCode);
        Assert.Null(verification.BranchId);
        Assert.Null(verification.BranchCode);
        Assert.Null(verification.BranchName);
    }

    [Fact]
    public async Task VerifyAndComplete_WhenInvitationBranchDeleted_FailsSafely()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        // Dedicated branch so MAIN/NORTH stay intact; then delete it to reproduce an
        // invitation whose persisted BranchId points at a branch that no longer exists.
        var deletedBranch = new Branch("TEMP", "Temp Branch", "Bengaluru", "Karnataka", 12.9m, 77.5m);
        harness.Db.Branches.Add(deletedBranch);
        await harness.Db.SaveChangesAsync();

        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Ravi Kumar",
                "+919876543210",
                "ravi@example.com",
                AuthorizationCodes.DeliveryStaff,
                deletedBranch.Id,
                SendInvitation: true),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        harness.Db.Branches.Remove(deletedBranch);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        // Verification stops with a clear business reason instead of silently defaulting.
        var verification = await harness.Employees.VerifyInvitationAsync(
            created.Invitation!.Token, CancellationToken.None);
        Assert.False(verification.IsValid);
        Assert.Equal(created.Employee.BranchId, verification.BranchId);
        Assert.Null(verification.BranchCode);
        Assert.Null(verification.BranchName);
        Assert.Equal(
            "The branch for this invitation is no longer available. Ask your administrator to create a new invitation.",
            verification.Reason);

        // Registration also stops inside the atomic transaction; the OTP stays live.
        await harness.Otp.SendAsync(
            new SendOtpRequest("+919876543210", OtpPurpose.EmployeeInvitation, "127.0.0.1"),
            CancellationToken.None);
        var otpCode = harness.Delivery.LastCode!;
        var ex = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Employees.CompleteRegistrationAsync(
                new CompleteEmployeeRegistrationRequest(
                    created.Invitation!.Token,
                    "Ravi Kumar",
                    "ravi@example.com",
                    "+919876543210",
                    "StrongPass!1",
                    otpCode,
                    Device),
                CancellationToken.None));
        Assert.Equal("The selected branch was not found.", ex.Message);

        harness.Db.ChangeTracker.Clear();
        var invitation = await harness.Db.EmployeeInvitations
            .SingleAsync(x => x.Id == created.Invitation.InvitationId);
        Assert.Equal(EmployeeInvitationStatus.Invited, invitation.Status);
        var challenge = await harness.Db.OtpChallenges.SingleAsync();
        Assert.Null(challenge.ConsumedAt);
        Assert.Equal(0, await harness.Db.UserSessions.CountAsync());
        Assert.Equal(0, await harness.Db.RefreshTokens.CountAsync());
    }

    [Fact]
    public async Task VerifyAndComplete_WhenInvitationBranchInactive_FailsSafely()
    {
        await using var harness = await EmployeeHarness.CreateAsync();

        // Create the invitation while the branch is active, then deactivate the branch to
        // reproduce an invitation whose persisted branch has been turned off.
        var deactivatedBranch = new Branch("TEMP2", "Temp Branch Two", "Bengaluru", "Karnataka", 12.91m, 77.51m);
        harness.Db.Branches.Add(deactivatedBranch);
        await harness.Db.SaveChangesAsync();

        var created = await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                "Ravi Kumar",
                "+919876543210",
                "ravi@example.com",
                AuthorizationCodes.DeliveryStaff,
                deactivatedBranch.Id,
                SendInvitation: true),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        deactivatedBranch.Deactivate();
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var verification = await harness.Employees.VerifyInvitationAsync(
            created.Invitation!.Token, CancellationToken.None);
        Assert.False(verification.IsValid);
        Assert.Null(verification.BranchCode);
        Assert.Null(verification.BranchName);
        Assert.Equal(
            "The branch for this invitation is inactive. Ask your administrator to create a new invitation.",
            verification.Reason);

        await harness.Otp.SendAsync(
            new SendOtpRequest("+919876543210", OtpPurpose.EmployeeInvitation, "127.0.0.1"),
            CancellationToken.None);
        var otpCode = harness.Delivery.LastCode!;
        var ex = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Employees.CompleteRegistrationAsync(
                new CompleteEmployeeRegistrationRequest(
                    created.Invitation!.Token,
                    "Ravi Kumar",
                    "ravi@example.com",
                    "+919876543210",
                    "StrongPass!1",
                    otpCode,
                    Device),
                CancellationToken.None));
        Assert.Equal("The selected branch is inactive.", ex.Message);

        harness.Db.ChangeTracker.Clear();
        var invitation = await harness.Db.EmployeeInvitations
            .SingleAsync(x => x.Id == created.Invitation.InvitationId);
        Assert.Equal(EmployeeInvitationStatus.Invited, invitation.Status);
        var challenge = await harness.Db.OtpChallenges.SingleAsync();
        Assert.Null(challenge.ConsumedAt);
        Assert.Equal(0, await harness.Db.UserSessions.CountAsync());
        Assert.Equal(0, await harness.Db.RefreshTokens.CountAsync());
    }

    [Fact]
    public void CompleteRegistrationRequest_CannotCarryRoleOrBranchOverride()
    {
        // The registration contract has no role or branch fields — the invitation is the only
        // source of authority. This reflection guard fails the moment anyone adds an override.
        var names = typeof(CompleteEmployeeRegistrationRequest)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { "Device", "DisplayName", "Email", "Mobile", "OtpCode", "Password", "Token" },
            names);
    }

    // ---- delete-or-archive lifecycle ------------------------------------------------
    // An archived employee is never removed: the row is retained with IsArchived set so
    // historical and operational records keep a stable identity. The server decides whether
    // a delete request permanently removes the account (no dependencies) or archives it
    // (dependencies exist) — the client never influences that decision.

    [Fact]
    public async Task DeleteAsync_ThrowsBusinessRuleForActiveEmployee()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Employees.DeleteAsync(created.Employee.Id, harness.SystemAdmin.Id, CancellationToken.None));

        Assert.Contains("deactivate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await harness.Db.Users.CountAsync(x => x.Id == created.Employee.Id));
    }

    [Fact]
    public async Task DeleteAsync_PermanentlyDeletesInactiveEmployeeWithoutDependencies()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Ravi Kumar", "ravi@example.com", null, null, IsActive: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        var result = await harness.Employees.DeleteAsync(
            created.Employee.Id,
            harness.SystemAdmin.Id,
            CancellationToken.None);

        Assert.True(result.IsDeleted);
        Assert.False(result.IsArchived);
        Assert.Null(result.Employee);
        Assert.Equal(0, await harness.Db.Users.CountAsync(x => x.Id == created.Employee.Id));

        var audit = await harness.Db.AuditLogs
            .SingleAsync(item => item.Action == EmployeeService.ActionDeleted
                                 && item.EntityId == created.Employee.PublicId.ToString());
        Assert.Equal(EmployeeService.ActionDeleted, audit.Action);
        Assert.Equal("Employee", audit.EntityType);
        Assert.Equal(harness.SystemAdmin.Id, audit.UserId);
        Assert.NotNull(audit.OldValueJson);
        Assert.Null(audit.NewValueJson);
    }

    [Fact]
    public async Task DeleteAsync_ArchivesInactiveEmployeeWithDependency()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Ravi Kumar", "ravi@example.com", null, null, IsActive: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        // A shared operational record — a customer profile with a Restrict FK back to the
        // user — blocks the permanent delete at the database, so the server archives instead.
        harness.Db.CustomerProfiles.Add(new CustomerProfile(created.Employee.Id));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var result = await harness.Employees.DeleteAsync(
            created.Employee.Id,
            harness.SystemAdmin.Id,
            CancellationToken.None);

        Assert.False(result.IsDeleted);
        Assert.True(result.IsArchived);
        Assert.NotNull(result.Employee);
        Assert.True(result.Employee!.IsArchived);
        Assert.False(result.Employee.IsActive);
        Assert.NotNull(result.Employee.ArchivedAt);

        // The employee is retained in the database so historical records keep identifying it.
        var stored = await harness.Db.Users.SingleAsync(x => x.Id == created.Employee.Id);
        Assert.True(stored.IsArchived);
        Assert.NotNull(stored.ArchivedAt);
        Assert.Equal(1, await harness.Db.CustomerProfiles.CountAsync());

        var audit = await harness.Db.AuditLogs
            .SingleAsync(item => item.Action == EmployeeService.ActionArchived
                                 && item.EntityId == created.Employee.PublicId.ToString());
        Assert.Equal("Employee", audit.EntityType);
        Assert.Equal(harness.SystemAdmin.Id, audit.UserId);
        Assert.NotNull(audit.OldValueJson);
        Assert.NotNull(audit.NewValueJson);
    }

    [Fact]
    public async Task DeleteAsync_IsIdempotentForArchivedEmployee()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Ravi Kumar", "ravi@example.com", null, null, IsActive: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);

        harness.Db.CustomerProfiles.Add(new CustomerProfile(created.Employee.Id));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var first = await harness.Employees.DeleteAsync(
            created.Employee.Id,
            harness.SystemAdmin.Id,
            CancellationToken.None);
        var again = await harness.Employees.DeleteAsync(
            created.Employee.Id,
            harness.SystemAdmin.Id,
            CancellationToken.None);

        Assert.True(first.IsArchived);
        Assert.False(first.IsDeleted);
        Assert.NotNull(first.Employee);
        // The second call observes the archive without rewriting the archive timestamp.
        Assert.True(again.IsArchived);
        Assert.False(again.IsDeleted);
        Assert.Equal(first.Employee!.ArchivedAt, again.Employee!.ArchivedAt);

        // Only one EMPLOYEE.ARCHIVED audit entry is written.
        var archivedAudits = await harness.Db.AuditLogs
            .CountAsync(item => item.Action == EmployeeService.ActionArchived
                                && item.EntityId == created.Employee.PublicId.ToString());
        Assert.Equal(1, archivedAudits);
    }

    [Fact]
    public async Task ListAsync_ExcludesArchivedEmployees()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var active = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");
        var doomed = await CreateDeliveryStaffAsync(harness, "Suresh Verma", "+919876543211", "suresh@example.com");

        await harness.Employees.UpdateAsync(
            doomed.Employee.Id,
            new UpdateEmployeeRequest("Suresh Verma", "suresh@example.com", null, null, IsActive: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        harness.Db.CustomerProfiles.Add(new CustomerProfile(doomed.Employee.Id));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
        await harness.Employees.DeleteAsync(doomed.Employee.Id, harness.SystemAdmin.Id, CancellationToken.None);

        var list = await harness.Employees.ListAsync(CancellationToken.None);

        Assert.DoesNotContain(list, x => x.Id == doomed.Employee.Id);
        Assert.Contains(list, x => x.Id == active.Employee.Id);
        Assert.All(list, x => Assert.False(x.IsArchived));
    }

    [Fact]
    public async Task GetAsync_StillReturnsArchivedEmployeeForHistory()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Ravi Kumar", "ravi@example.com", null, null, IsActive: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        harness.Db.CustomerProfiles.Add(new CustomerProfile(created.Employee.Id));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
        await harness.Employees.DeleteAsync(created.Employee.Id, harness.SystemAdmin.Id, CancellationToken.None);

        // Archived employees remain retrievable (read-only view) and stay in the database.
        var result = await harness.Employees.GetAsync(created.Employee.Id, CancellationToken.None);

        Assert.Equal(created.Employee.Id, result.Id);
        Assert.True(result.IsArchived);
        Assert.NotNull(result.ArchivedAt);
        Assert.Equal(1, await harness.Db.Users.CountAsync(x => x.Id == created.Employee.Id));
    }

    [Fact]
    public async Task UpdateAsync_ThrowsWhenReactivatingArchivedEmployee()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        var created = await CreateDeliveryStaffAsync(harness, "Ravi Kumar", "+919876543210", "ravi@example.com");

        await harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Ravi Kumar", "ravi@example.com", null, null, IsActive: false),
            harness.SystemAdmin.Id,
            CancellationToken.None);
        harness.Db.CustomerProfiles.Add(new CustomerProfile(created.Employee.Id));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
        await harness.Employees.DeleteAsync(created.Employee.Id, harness.SystemAdmin.Id, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Employees.UpdateAsync(
            created.Employee.Id,
            new UpdateEmployeeRequest("Ravi Kumar", "ravi@example.com", null, null, IsActive: true),
            harness.SystemAdmin.Id,
            CancellationToken.None));

        Assert.Contains("cannot be updated or reactivated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CreateEmployeeResult> CreateDeliveryStaffAsync(
        EmployeeHarness harness,
        string name,
        string mobile,
        string? email)
    {
        return await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                name,
                mobile,
                email,
                AuthorizationCodes.DeliveryStaff,
                harness.MainBranch.Id,
                SendInvitation: true),
            harness.SystemAdmin.Id,
            CancellationToken.None);
    }

    private static async Task<CompleteEmployeeRegistrationResult> SendOtpAndCompleteAsync(
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
                Device),
            CancellationToken.None);
    }
}

internal sealed class EmployeeHarness : IAsyncDisposable
{
    private const string MainBranchCode = "MAIN";
    private const string NorthBranchCode = "NORTH";

    private static readonly string[] SystemAdministratorPermissions =
    [
        AuthorizationCodes.GlobalAccess,
        AuthorizationCodes.ProfileReadOwn,
        AuthorizationCodes.ProfileUpdateOwn,
        AuthorizationCodes.SessionsManageOwn,
        AuthorizationCodes.UsersRead,
        AuthorizationCodes.UsersManage,
        AuthorizationCodes.EmployeesRead,
        AuthorizationCodes.EmployeesManage,
        AuthorizationCodes.CustomerProfilesRead,
        AuthorizationCodes.CustomerProfilesManage,
        AuthorizationCodes.CatalogueRead,
        AuthorizationCodes.CatalogueManage,
        AuthorizationCodes.OrdersRead,
        AuthorizationCodes.DairyRead,
        AuthorizationCodes.DairyManage,
        AuthorizationCodes.CamerasRead,
        AuthorizationCodes.CamerasManage,
        AuthorizationCodes.NotificationTemplatesRead,
        AuthorizationCodes.NotificationTemplatesManage,
        AuthorizationCodes.DeliveriesReadBranch,
        AuthorizationCodes.DeliveriesAssignBranch,
        AuthorizationCodes.PaymentsRefund,
        AuthorizationCodes.WalletAdjust,
        AuthorizationCodes.ReportsDashboardRead,
        AuthorizationCodes.ReportsAdministrationRead,
        AuthorizationCodes.ReportsFinancialRead,
        AuthorizationCodes.ReportsOperationsRead,
        AuthorizationCodes.ReportsMilkTestsRead,
        AuthorizationCodes.ReportsAuditRead,
        AuthorizationCodes.ReportsExport,
        AuthorizationCodes.SetupNumberSeriesRead,
        AuthorizationCodes.SetupNumberSeriesManage
    ];

    private static readonly string[] DeliveryStaffPermissions =
    [
        AuthorizationCodes.ProfileReadOwn,
        AuthorizationCodes.ProfileUpdateOwn,
        AuthorizationCodes.SessionsManageOwn,
        AuthorizationCodes.BranchAccess,
        AuthorizationCodes.DeliveriesOperateAssigned,
        AuthorizationCodes.DeliveriesTrackAssigned,
        AuthorizationCodes.MilkTestsOperateAssigned
    ];

    private static readonly string[] DeliveryManagerPermissions =
    [
        AuthorizationCodes.ProfileReadOwn,
        AuthorizationCodes.ProfileUpdateOwn,
        AuthorizationCodes.SessionsManageOwn,
        AuthorizationCodes.UsersRead,
        AuthorizationCodes.BranchAccess,
        AuthorizationCodes.DeliveriesReadBranch,
        AuthorizationCodes.DeliveriesAssignBranch,
        AuthorizationCodes.ReportsDashboardRead,
        AuthorizationCodes.ReportsOperationsRead,
        AuthorizationCodes.ReportsMilkTestsRead,
        AuthorizationCodes.ReportsExport
    ];

    private static readonly string[] AccountantPermissions =
    [
        AuthorizationCodes.ProfileReadOwn,
        AuthorizationCodes.ProfileUpdateOwn,
        AuthorizationCodes.SessionsManageOwn,
        AuthorizationCodes.UsersRead,
        AuthorizationCodes.CustomerProfilesRead,
        AuthorizationCodes.OrdersRead,
        AuthorizationCodes.PaymentsRefund,
        AuthorizationCodes.WalletAdjust,
        AuthorizationCodes.ReportsDashboardRead,
        AuthorizationCodes.ReportsAdministrationRead,
        AuthorizationCodes.ReportsFinancialRead,
        AuthorizationCodes.ReportsExport
    ];

    private static readonly string[] DairyManagerPermissions =
    [
        AuthorizationCodes.ProfileReadOwn,
        AuthorizationCodes.ProfileUpdateOwn,
        AuthorizationCodes.SessionsManageOwn,
        AuthorizationCodes.UsersRead,
        AuthorizationCodes.BranchAccess,
        AuthorizationCodes.DeliveriesReadBranch,
        AuthorizationCodes.DeliveriesAssignBranch,
        AuthorizationCodes.DairyRead,
        AuthorizationCodes.DairyManage,
        AuthorizationCodes.CamerasRead,
        AuthorizationCodes.CamerasManage,
        AuthorizationCodes.ReportsDashboardRead,
        AuthorizationCodes.ReportsOperationsRead,
        AuthorizationCodes.ReportsMilkTestsRead,
        AuthorizationCodes.ReportsExport
    ];

    private EmployeeHarness(
        SqliteConnection connection,
        string connectionString,
        DoodhDirectDbContext db,
        TestClock clock,
        TestIndiaTimeProvider timeProvider,
        TestPasswordHasher hasher,
        TestTokenService tokens,
        CapturingOtpDelivery delivery,
        IdentityOptions identityOptions,
        EmployeeService employees,
        OtpService otp,
        User owner,
        User systemAdmin,
        Branch mainBranch,
        Branch northBranch)
    {
        Connection = connection;
        ConnectionString = connectionString;
        Db = db;
        Clock = clock;
        TimeProvider = timeProvider;
        Hasher = hasher;
        Tokens = tokens;
        Delivery = delivery;
        IdentityOptions = identityOptions;
        Employees = employees;
        Otp = otp;
        Owner = owner;
        SystemAdmin = systemAdmin;
        MainBranch = mainBranch;
        NorthBranch = northBranch;
    }

    public SqliteConnection Connection { get; }
    public string ConnectionString { get; }
    public DoodhDirectDbContext Db { get; }
    public TestClock Clock { get; }
    public TestIndiaTimeProvider TimeProvider { get; }
    public TestPasswordHasher Hasher { get; }
    public TestTokenService Tokens { get; }
    public CapturingOtpDelivery Delivery { get; }
    public IdentityOptions IdentityOptions { get; }
    public EmployeeService Employees { get; }
    public OtpService Otp { get; }
    public User Owner { get; }
    public User SystemAdmin { get; }
    public Branch MainBranch { get; }
    public Branch NorthBranch { get; }

    public static async Task<EmployeeHarness> CreateAsync(
        int otpLifetimeMinutes = 5,
        int otpMaxAttempts = 5,
        int otpRequestsPerWindow = 3,
        IEmailSender? emailSender = null,
        IIntegrationSettingsProvider? integrationSettings = null)
    {
        var clock = new TestClock(new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Unspecified));
        var timeProvider = new TestIndiaTimeProvider(clock);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"employee-tests-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 10
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseSqlite(connection, sqlite => sqlite.CommandTimeout(10))
            .Options;
        var db = new DoodhDirectDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // Seed every permission code so Owner can be granted the full set.
        foreach (var permission in AuthorizationCodes.Permissions)
        {
            db.Permissions.Add(new Permission(permission.Key, permission.Value));
        }

        var ownerRole = new Role(AuthorizationCodes.Owner, "Owner");
        var systemAdminRole = new Role(AuthorizationCodes.SystemAdmin, "System Administrator");
        var deliveryStaffRole = new Role(AuthorizationCodes.DeliveryStaff, "Delivery Staff");
        var deliveryManagerRole = new Role(AuthorizationCodes.DeliveryManager, "Delivery Manager");
        var accountantRole = new Role(AuthorizationCodes.Accountant, "Accountant");
        var dairyManagerRole = new Role(AuthorizationCodes.DairyManager, "Dairy Manager");
        db.Roles.AddRange(
            ownerRole, systemAdminRole, deliveryStaffRole, deliveryManagerRole, accountantRole, dairyManagerRole);
        await db.SaveChangesAsync();

        await LinkPermissionsAsync(db, ownerRole, AuthorizationCodes.Permissions.Keys);
        await LinkPermissionsAsync(db, systemAdminRole, SystemAdministratorPermissions);
        await LinkPermissionsAsync(db, deliveryStaffRole, DeliveryStaffPermissions);
        await LinkPermissionsAsync(db, deliveryManagerRole, DeliveryManagerPermissions);
        await LinkPermissionsAsync(db, accountantRole, AccountantPermissions);
        await LinkPermissionsAsync(db, dairyManagerRole, DairyManagerPermissions);

        var mainBranch = new Branch(MainBranchCode, "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
        var northBranch = new Branch(NorthBranchCode, "North Branch", "Bengaluru", "Karnataka", 13.0358m, 77.5970m);
        db.Branches.AddRange(mainBranch, northBranch);
        await db.SaveChangesAsync();

        var owner = SeedUser(db, UserType.Owner, "Owner", "+919999000098", "owner@doodhdirect.in");
        var systemAdmin = SeedUser(db, UserType.SystemAdministrator, "System Admin", "+919999000099", "admin@doodhdirect.in");
        await db.SaveChangesAsync();

        owner.AssignRole(ownerRole);
        systemAdmin.AssignRole(systemAdminRole);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var hasher = new TestPasswordHasher();
        var tokens = new TestTokenService();
        var delivery = new CapturingOtpDelivery();
        var identityOptions = new IdentityOptions
        {
            OtpLifetimeMinutes = otpLifetimeMinutes,
            OtpMaxAttempts = otpMaxAttempts,
            OtpRequestsPerWindow = otpRequestsPerWindow,
            OtpRateLimitWindowMinutes = 15,
            PasswordIterations = 10_000
        };

        var employees = new EmployeeService(
            db,
            hasher,
            tokens,
            timeProvider,
            new TestNotificationEventWriter(db, clock),
            new SecureTokenGenerator(),
            delivery,
            Options.Create(identityOptions),
            emailSender,
            integrationSettings);
        var otp = new OtpService(
            db,
            delivery,
            timeProvider,
            tokens,
            Options.Create(identityOptions),
            new TestNotificationEventWriter(db, clock));

        return new EmployeeHarness(
            connection,
            connectionString,
            db,
            clock,
            timeProvider,
            hasher,
            tokens,
            delivery,
            identityOptions,
            employees,
            otp,
            owner,
            systemAdmin,
            mainBranch,
            northBranch);
    }

    /// <summary>Creates a fresh context over a new connection to the same shared in-memory database.</summary>
    public DoodhDirectDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseSqlite(ConnectionString, sqlite => sqlite.CommandTimeout(10))
            .Options;
        return new DoodhDirectDbContext(options);
    }

    public EmployeeService CreateService(DoodhDirectDbContext db) => new(
        db,
        Hasher,
        Tokens,
        TimeProvider,
        new TestNotificationEventWriter(db, Clock),
        new SecureTokenGenerator(),
        Delivery,
        Options.Create(IdentityOptions));

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await Connection.DisposeAsync();
    }

    private static User SeedUser(DoodhDirectDbContext db, UserType type, string name, string mobile, string email)
    {
        var user = new User(type);
        user.SetProfile(name);
        user.SetContact(mobile, email);
        db.Users.Add(user);
        return user;
    }

    private static async Task LinkPermissionsAsync(
        DoodhDirectDbContext db,
        Role role,
        IEnumerable<string> permissionCodes)
    {
        var permissionIds = await db.Permissions
            .Where(x => permissionCodes.Contains(x.Code))
            .Select(x => x.Id)
            .ToListAsync();
        foreach (var permissionId in permissionIds)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permissionId));
        }

        await db.SaveChangesAsync();
    }
}

internal sealed class TestIntegrationSettingsProvider(string? inviteUrlBase) : IIntegrationSettingsProvider
{
    public Task<EmailDeliverySettings> GetEmailAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new EmailDeliverySettings(
            "no-reply@example.test",
            "DoodhDirect",
            "smtp.example.test",
            587,
            "smtp-user",
            "smtp-password",
            true,
            true));

    public Task<RazorpayRuntimeSettings> GetRazorpayAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new RazorpayRuntimeSettings(null, null, null, false));

    public Task<GoogleMapsRuntimeSettings> GetGoogleMapsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new GoogleMapsRuntimeSettings(null, null, false));

    public Task<string?> GetInviteUrlBaseAsync(CancellationToken cancellationToken) =>
        Task.FromResult(inviteUrlBase);
}

internal sealed class TestEmailSender : IEmailSender
{
    private readonly EmailSendResult? result;
    private readonly Exception? exception;

    public TestEmailSender(EmailSendResult result) => this.result = result;

    public TestEmailSender(Exception exception) => this.exception = exception;

    public EmailMessage? LastMessage { get; private set; }

    public Task<EmailSendResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken)
    {
        LastMessage = message;
        return exception is null
            ? Task.FromResult(result!)
            : Task.FromException<EmailSendResult>(exception);
    }
}
