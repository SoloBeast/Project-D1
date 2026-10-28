using System.Data.Common;
using System.Reflection;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Customer;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Application.Setup;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Customer;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using DoodhDirect.Infrastructure.Wallets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Employee-origin approval policy: any request associated with an
/// employee's own account must receive its approval/rejection decision from
/// an authorized Owner or System Administrator — never from an ordinary
/// employee — and self-approval is never permitted. These tests pin the
/// behavior at the service layer across refund/replacement, vacation
/// refund, wallet withdrawal, and manual-bank export/settlement workflows.
/// </summary>
public sealed class EmployeeOriginPolicyTests
{
    private static readonly DateTime FrozenNow =
        new(2026, 8, 17, 9, 30, 0, DateTimeKind.Unspecified);

    // ---------- Refund/replacement decisions ----------

    [Fact]
    public async Task Owner_CanApprove_EmployeeOriginated_RefundTicket()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var ticketId = await harness.SubmitEmployeeTicketAsync();

        var decided = await harness.RefundService.ApproveAsync(
            harness.GlobalActor(harness.OwnerUser.Id),
            ticketId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: false, WaiverReason: "Goodwill",
                SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Approved, decided.Status);
        Assert.Equal(harness.OwnerUser.Id, decided.DecidedByUserId);
        Assert.True(decided.IsEmployeeOriginated);
    }

    [Fact]
    public async Task SystemAdmin_CanReject_EmployeeOriginated_RefundTicket()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var ticketId = await harness.SubmitEmployeeTicketAsync();

        var decided = await harness.RefundService.RejectAsync(
            harness.GlobalActor(harness.SysAdminUser.Id),
            ticketId,
            new DecideRefundReplacementRequest("Duplicate request"),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Rejected, decided.Status);
        Assert.Equal(harness.SysAdminUser.Id, decided.DecidedByUserId);
        Assert.True(decided.IsEmployeeOriginated);
    }

    [Fact]
    public async Task DairyManager_And_CustomerSupport_CannotApprove_EmployeeOriginated_RefundTicket()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var firstId = await harness.SubmitEmployeeTicketAsync();
        var secondId = await harness.SubmitEmployeeTicketAsync("Slow delivery, items missing");

        var dairyError = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.RefundService.ApproveAsync(
                harness.BranchActor(harness.DairyManager.Id),
                firstId,
                new ApproveRefundReplacementRequest(
                    null, ApplyDeduction: false, WaiverReason: "Goodwill",
                    SettlementMethod: RefundSettlementMethod.Wallet),
                CancellationToken.None));
        Assert.Contains("Owner or System Administrator", dairyError.Message, StringComparison.Ordinal);

        var supportError = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.RefundService.RejectAsync(
                harness.BranchActor(harness.Support.Id),
                secondId,
                new DecideRefundReplacementRequest("No"),
                CancellationToken.None));
        Assert.Contains("Owner or System Administrator", supportError.Message, StringComparison.Ordinal);

        Assert.Equal(
            [RefundReplacementStatus.Pending, RefundReplacementStatus.Pending],
            await harness.Db.RefundReplacementRequests
                .AsNoTracking()
                .OrderBy(x => x.Id)
                .Select(x => x.Status)
                .ToListAsync());
    }

    [Fact]
    public async Task Administrators_CannotApprove_TheirOwn_EmployeeOriginated_Requests()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var ownerTicketId = await harness.SubmitTicketForAsync(harness.OwnerUser);
        var adminTicketId = await harness.SubmitTicketForAsync(harness.SysAdminUser);

        var ownerError = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.RefundService.ApproveAsync(
                harness.GlobalActor(harness.OwnerUser.Id),
                ownerTicketId,
                new ApproveRefundReplacementRequest(
                    null, ApplyDeduction: false, WaiverReason: "Goodwill",
                    SettlementMethod: RefundSettlementMethod.Wallet),
                CancellationToken.None));
        Assert.Contains("self-approval", ownerError.Message, StringComparison.OrdinalIgnoreCase);

        var adminError = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.RefundService.RejectAsync(
                harness.GlobalActor(harness.SysAdminUser.Id),
                adminTicketId,
                new DecideRefundReplacementRequest("No"),
                CancellationToken.None));
        Assert.Contains("self-approval", adminError.Message, StringComparison.OrdinalIgnoreCase);

        // A different administrator may still decide: no self-dealing, valid tier.
        var decided = await harness.RefundService.ApproveAsync(
            harness.GlobalActor(harness.SysAdminUser.Id),
            ownerTicketId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: false, WaiverReason: "Goodwill",
                SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Approved, decided.Status);
    }

    [Fact]
    public async Task CustomerOriginated_RefundTicket_KeepsExistingBranchRules()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var ticketId = await harness.SubmitCustomerTicketAsync();

        var decided = await harness.RefundService.ApproveAsync(
            harness.BranchActor(harness.DairyManager.Id),
            ticketId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: false, WaiverReason: "Goodwill",
                SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Approved, decided.Status);
        Assert.False(decided.IsEmployeeOriginated);
    }

    [Fact]
    public async Task DeniedDecision_LeavesRequestWalletAndLedger_Unchanged()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var ticketId = await harness.SubmitEmployeeTicketAsync();

        await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.RefundService.ApproveAsync(
                harness.BranchActor(harness.Accountant.Id),
                ticketId,
                new ApproveRefundReplacementRequest(
                    null, ApplyDeduction: false, WaiverReason: "Goodwill",
                    SettlementMethod: RefundSettlementMethod.Wallet),
                CancellationToken.None));

        var ticket = await harness.Db.RefundReplacementRequests
            .AsNoTracking()
            .SingleAsync(x => x.PublicId == ticketId);
        Assert.Equal(RefundReplacementStatus.Pending, ticket.Status);
        Assert.Null(ticket.DecidedByUserId);
        Assert.Null(ticket.DecidedAt);
        Assert.Empty(await harness.Db.Refunds.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Empty(
            await harness.Db.AuditLogs
                .AsNoTracking()
                .Where(x => x.Action == "REFUND_REPLACEMENT.DECIDED")
                .ToListAsync());
    }

    [Fact]
    public async Task SpoofedClientOriginFlag_CannotExistOnDecisionContracts()
    {
        var spoofNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "RequesterType", "IsEmployeeRequest", "EmployeeOrigin",
            "IsEmployeeOriginated", "Origin", "RequestOrigin", "IsEmployee"
        };
        var requestTypes = new[]
        {
            typeof(SubmitRefundReplacementRequest),
            typeof(DecideRefundReplacementRequest),
            typeof(ApproveRefundReplacementRequest),
            typeof(ConfirmManualBankTransferRequest),
            typeof(ReleaseManualBankTransferRequest),
            typeof(RemediateLegacyReplacementRequest),
            typeof(DecideVacationRefundRequest),
            typeof(CreateWithdrawalRequest),
            typeof(DecideWithdrawalRequest),
            typeof(CompleteWithdrawalRequest),
            typeof(CreateRefundBankExportBatchRequest),
            typeof(ConfirmRefundBankExportItemRequest),
            typeof(RefundBankExportItemDispositionRequest),
        };
        foreach (var type in requestTypes)
        {
            var spoofed = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => spoofNames.Contains(p.Name))
                .Select(p => p.Name)
                .ToList();
            Assert.True(
                spoofed.Count == 0,
                $"{type.Name} must not accept a client-provided request-origin flag: {string.Join(",", spoofed)}");
        }

        // The origin indicator on results is server-computed, never bound
        // from client input: it has no setter path through the request DTOs
        // above, and the service derives it from the persisted user type.
        Assert.True(EmployeeOriginGuard.IsEmployeeOriginated(UserType.Employee));
        Assert.True(EmployeeOriginGuard.IsEmployeeOriginated(UserType.Owner));
        Assert.True(EmployeeOriginGuard.IsEmployeeOriginated(UserType.SystemAdministrator));
        Assert.False(EmployeeOriginGuard.IsEmployeeOriginated(UserType.Customer));
    }

    // ---------- Vacation-refund decisions ----------

    [Fact]
    public async Task Owner_CanApprove_EmployeeOriginated_VacationRefund()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var requestId = await harness.SeedEmployeeVacationRequestAsync();

        var decided = await harness.VacationService.ApproveAsync(
            harness.GlobalActor(harness.OwnerUser.Id),
            requestId,
            new DecideVacationRefundRequest("Verified against subscription"),
            CancellationToken.None);

        Assert.Equal(VacationRefundStatus.Approved, decided.Status);
        Assert.True(decided.IsEmployeeOriginated);
    }

    [Fact]
    public async Task Support_CannotApprove_EmployeeOriginated_VacationRefund()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var requestId = await harness.SeedEmployeeVacationRequestAsync();

        var error = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.VacationService.ApproveAsync(
                harness.BranchActor(harness.Support.Id),
                requestId,
                new DecideVacationRefundRequest("Looks fine"),
                CancellationToken.None));
        Assert.Contains("Owner or System Administrator", error.Message, StringComparison.Ordinal);

        var persisted = await harness.Db.VacationRefundRequests
            .AsNoTracking()
            .SingleAsync(x => x.PublicId == requestId);
        Assert.Equal(VacationRefundStatus.Pending, persisted.Status);
        Assert.Null(persisted.DecidedByUserId);
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Administrator_CannotApprove_Own_VacationRefund()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var requestId = await harness.SeedVacationRequestForAsync(harness.OwnerUser);

        var error = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.VacationService.ApproveAsync(
                harness.GlobalActor(harness.OwnerUser.Id),
                requestId,
                new DecideVacationRefundRequest("Mine"),
                CancellationToken.None));
        Assert.Contains("self-approval", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CustomerOriginated_VacationRefund_KeepsExistingRules()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var requestId = await harness.SeedVacationRequestForAsync(harness.Customer);

        var decided = await harness.VacationService.ApproveAsync(
            harness.BranchActor(harness.DairyManager.Id),
            requestId,
            new DecideVacationRefundRequest("Verified"),
            CancellationToken.None);

        Assert.Equal(VacationRefundStatus.Approved, decided.Status);
        Assert.False(decided.IsEmployeeOriginated);
    }

    [Fact]
    public async Task Settlement_OfValidlyApproved_EmployeeVacationRefund_WorksForAuthorizedRole()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var requestId = await harness.SeedEmployeeVacationRequestAsync();
        await harness.VacationService.ApproveAsync(
            harness.GlobalActor(harness.OwnerUser.Id),
            requestId,
            new DecideVacationRefundRequest("Verified"),
            CancellationToken.None);

        // Settlement (not a decision) stays with the authorized workflow:
        // a branch-scoped non-admin actor completes the validly approved ticket.
        var completed = await harness.VacationService.CompleteAsync(
            harness.BranchActor(harness.Support.Id),
            requestId,
            new DecideVacationRefundRequest("Wallet credited"),
            CancellationToken.None);

        Assert.Equal(VacationRefundStatus.Completed, completed.Status);
        var wallet = await harness.Db.Wallets
            .AsNoTracking()
            .SingleAsync(x => x.CustomerId == harness.Requester.Id);
        Assert.Equal(200m, wallet.Balance);
    }

    [Fact]
    public async Task Complete_WithoutVerifiableAdminApproval_IsBlocked()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var requestId = await harness.SeedEmployeeVacationRequestAsync();
        await harness.VacationService.ApproveAsync(
            harness.GlobalActor(harness.OwnerUser.Id),
            requestId,
            new DecideVacationRefundRequest("Verified"),
            CancellationToken.None);

        // Simulate a pre-policy approval whose decider cannot be verified
        // as an administrator (e.g. approvals recorded before the policy).
        // Historical rows are never rewritten; consequential processing stops.
        harness.Db.UserRoles.RemoveRange(
            harness.Db.UserRoles.Where(x => x.UserId == harness.OwnerUser.Id));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            harness.VacationService.CompleteAsync(
                harness.BranchActor(harness.Support.Id),
                requestId,
                new DecideVacationRefundRequest("Wallet credited"),
                CancellationToken.None));
        Assert.Contains("verifiable administrator approval", error.Message, StringComparison.Ordinal);

        var persisted = await harness.Db.VacationRefundRequests
            .AsNoTracking()
            .SingleAsync(x => x.PublicId == requestId);
        Assert.Equal(VacationRefundStatus.Approved, persisted.Status);
        Assert.Empty(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
    }

    // ---------- Wallet withdrawal decisions ----------

    [Fact]
    public async Task Owner_CanComplete_EmployeeOriginated_Withdrawal()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var withdrawalId = await harness.SubmitEmployeeWithdrawalAsync(50m, "emp-wd-1");

        var completed = await harness.WithdrawalService.CompleteAsync(
            harness.OwnerUser.Id,
            withdrawalId,
            new CompleteWithdrawalRequest("UTR12345678", "Paid"),
            CancellationToken.None);

        Assert.Equal(WithdrawalRequestStatus.Completed, completed.Status);
        Assert.Equal("UTR12345678", completed.TransferReference);
        Assert.True(completed.IsEmployeeOriginated);
    }

    [Fact]
    public async Task SystemAdmin_CanReject_EmployeeOriginated_Withdrawal()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var withdrawalId = await harness.SubmitEmployeeWithdrawalAsync(50m, "emp-wd-2");

        var rejected = await harness.WithdrawalService.RejectAsync(
            harness.SysAdminUser.Id,
            withdrawalId,
            new DecideWithdrawalRequest("Wrong account"),
            CancellationToken.None);

        Assert.Equal(WithdrawalRequestStatus.Rejected, rejected.Status);
        var wallet = await harness.Db.Wallets
            .AsNoTracking()
            .SingleAsync(x => x.CustomerId == harness.Requester.Id);
        Assert.Equal(500m, wallet.Balance);
    }

    [Fact]
    public async Task Accountant_CannotComplete_EmployeeOriginated_Withdrawal()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var withdrawalId = await harness.SubmitEmployeeWithdrawalAsync(50m, "emp-wd-3");

        var error = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.WithdrawalService.CompleteAsync(
                harness.Accountant.Id,
                withdrawalId,
                new CompleteWithdrawalRequest("UTR12345678", "Paid"),
                CancellationToken.None));
        Assert.Contains("Owner or System Administrator", error.Message, StringComparison.Ordinal);

        var persisted = await harness.Db.WithdrawalRequests
            .AsNoTracking()
            .SingleAsync(x => x.PublicId == withdrawalId);
        Assert.Equal(WithdrawalRequestStatus.Pending, persisted.Status);
        Assert.Null(persisted.TransferReference);
        var wallet = await harness.Db.Wallets
            .AsNoTracking()
            .SingleAsync(x => x.CustomerId == harness.Requester.Id);
        Assert.Equal(450m, wallet.Balance);
        Assert.Empty(
            await harness.Db.AuditLogs
                .AsNoTracking()
                .Where(x => x.Action == "WithdrawalCompleted")
                .ToListAsync());
    }

    [Fact]
    public async Task Administrator_CannotComplete_Own_EmployeeOriginated_Withdrawal()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var withdrawalId = await harness.SubmitWithdrawalForAsync(harness.OwnerUser, 40m, "owner-wd-1");

        var error = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.WithdrawalService.CompleteAsync(
                harness.OwnerUser.Id,
                withdrawalId,
                new CompleteWithdrawalRequest("UTR12345678", "Paid"),
                CancellationToken.None));
        Assert.Contains("self-approval", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmployeeOriginated_WithdrawalBankDetail_IsAdministratorOnly()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var employeeWithdrawalId = await harness.SubmitEmployeeWithdrawalAsync(50m, "emp-wd-4");
        var customerWithdrawalId = await harness.SubmitCustomerWithdrawalAsync(60m, "cust-wd-1");

        await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.WithdrawalService.GetBankDetailForStaffAsync(
                harness.Accountant.Id, employeeWithdrawalId, CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.WithdrawalService.GetBankDetailForStaffAsync(
                harness.Support.Id, employeeWithdrawalId, CancellationToken.None));

        var adminView = await harness.WithdrawalService.GetBankDetailForStaffAsync(
            harness.OwnerUser.Id, employeeWithdrawalId, CancellationToken.None);
        Assert.Equal("4111111111111111", adminView.AccountNumber);

        // Customer-originated withdrawals keep the existing audited access.
        var accountantView = await harness.WithdrawalService.GetBankDetailForStaffAsync(
            harness.Accountant.Id, customerWithdrawalId, CancellationToken.None);
        Assert.Equal("4222222222222222", accountantView.AccountNumber);
    }

    [Fact]
    public async Task Accountant_CanComplete_CustomerOriginated_Withdrawal()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var withdrawalId = await harness.SubmitCustomerWithdrawalAsync(60m, "cust-wd-2");

        var completed = await harness.WithdrawalService.CompleteAsync(
            harness.Accountant.Id,
            withdrawalId,
            new CompleteWithdrawalRequest("UTR87654321", "Paid"),
            CancellationToken.None);

        Assert.Equal(WithdrawalRequestStatus.Completed, completed.Status);
        Assert.False(completed.IsEmployeeOriginated);
    }

    // ---------- Manual-bank export and settlement ----------

    [Fact]
    public async Task EmployeeOriginated_ManualBankTicket_WithoutAdminApproval_BlockedFromExportAndSettlement()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var ticketId = await harness.SeedEmployeeManualBankTicketAsync(200m, "mb-1");
        await harness.SeedExportPrerequisitesAsync();
        await harness.RefundService.ApproveAsync(
            harness.GlobalActor(harness.OwnerUser.Id),
            ticketId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: false, WaiverReason: "Goodwill",
                SettlementMethod: RefundSettlementMethod.ManualBankTransfer),
            CancellationToken.None);

        // Simulate a pre-policy (or non-admin) approval: the decider can no
        // longer be verified as an administrator. The row itself is kept.
        harness.Db.UserRoles.RemoveRange(
            harness.Db.UserRoles.Where(x => x.UserId == harness.OwnerUser.Id));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var exports = harness.CreateExportService();
        Assert.Empty(await exports.ListEligibleAsync(
            harness.GlobalActor(harness.OwnerUser.Id), CancellationToken.None));

        var claimError = await Assert.ThrowsAsync<ConflictException>(() =>
            exports.CreateBatchAsync(
                harness.GlobalActor(harness.OwnerUser.Id),
                new CreateRefundBankExportBatchRequest([ticketId]),
                CancellationToken.None));
        Assert.Contains("verifiable administrator approval", claimError.Message, StringComparison.Ordinal);

        var confirmError = await Assert.ThrowsAsync<ConflictException>(() =>
            harness.RefundService.ConfirmManualBankTransferAsync(
                harness.BranchActor(harness.Accountant.Id),
                ticketId,
                new ConfirmManualBankTransferRequest("UTR99990000", "Paid"),
                CancellationToken.None));
        Assert.Contains("verifiable administrator approval", confirmError.Message, StringComparison.Ordinal);

        var releaseError = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.RefundService.ReleaseManualBankTransferAsync(
                harness.BranchActor(harness.Support.Id),
                ticketId,
                new ReleaseManualBankTransferRequest("Wrong details", null),
                CancellationToken.None));
        Assert.Contains("Owner or System Administrator", releaseError.Message, StringComparison.Ordinal);

        var ticket = await harness.Db.RefundReplacementRequests
            .AsNoTracking()
            .SingleAsync(x => x.PublicId == ticketId);
        Assert.Equal(RefundReplacementStatus.Approved, ticket.Status);
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(0m, payment.RefundedAmount);
        Assert.All(
            await harness.Db.Refunds.AsNoTracking().ToListAsync(),
            x => Assert.Equal(RefundStatus.Pending, x.Status));
    }

    [Fact]
    public async Task EmployeeOriginated_ManualBankTicket_WithAdminApproval_SettlesForFinanceRole()
    {
        await using var harness = await PolicyHarness.CreateAsync();
        var ticketId = await harness.SeedEmployeeManualBankTicketAsync(200m, "mb-2");
        await harness.SeedExportPrerequisitesAsync();
        await harness.RefundService.ApproveAsync(
            harness.GlobalActor(harness.OwnerUser.Id),
            ticketId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: false, WaiverReason: "Goodwill",
                SettlementMethod: RefundSettlementMethod.ManualBankTransfer),
            CancellationToken.None);

        // A non-admin cannot decide, but the authorized finance role settles
        // the validly approved ticket through the export workflow.
        var exports = harness.CreateExportService();
        var candidate = Assert.Single(await exports.ListEligibleAsync(
            harness.GlobalActor(harness.OwnerUser.Id), CancellationToken.None));
        Assert.Equal(ticketId, candidate.TicketId);

        var batch = await exports.CreateBatchAsync(
            harness.GlobalActor(harness.OwnerUser.Id),
            new CreateRefundBankExportBatchRequest([ticketId]),
            CancellationToken.None);
        await exports.GenerateBatchAsync(
            harness.GlobalActor(harness.OwnerUser.Id), batch.BatchId, CancellationToken.None);

        var paid = await exports.ConfirmItemPaidAsync(
            harness.BranchActor(harness.Accountant.Id),
            batch.Items.Single().ItemId,
            new ConfirmRefundBankExportItemRequest("UTR55550000", "Paid"),
            CancellationToken.None);
        Assert.Equal(nameof(RefundBankExportItemStatus.Paid), paid.Status);

        var ticket = await harness.Db.RefundReplacementRequests
            .AsNoTracking()
            .SingleAsync(x => x.PublicId == ticketId);
        Assert.Equal(RefundReplacementStatus.Completed, ticket.Status);
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(160m, payment.RefundedAmount);
        Assert.All(
            await harness.Db.Refunds.AsNoTracking().ToListAsync(),
            x => Assert.Equal(RefundStatus.Succeeded, x.Status));
    }

    // ---------- Approval-policy coverage inventory ----------

    [Fact]
    public void ApprovalPolicyInventory_Covers_AllRequestDecisionWorkflows()
    {
        var guardedDecisions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [typeof(IRefundReplacementService).FullName!] = new(StringComparer.Ordinal)
            {
                nameof(IRefundReplacementService.ApproveAsync),
                nameof(IRefundReplacementService.RejectAsync),
                nameof(IRefundReplacementService.ReleaseManualBankTransferAsync),
                nameof(IRefundReplacementService.RemediateLegacyReplacementAsync),
            },
            [typeof(IVacationRefundService).FullName!] = new(StringComparer.Ordinal)
            {
                nameof(IVacationRefundService.ApproveAsync),
                nameof(IVacationRefundService.RejectAsync),
            },
            [typeof(IWithdrawalService).FullName!] = new(StringComparer.Ordinal)
            {
                nameof(IWithdrawalService.CompleteAsync),
                nameof(IWithdrawalService.RejectAsync),
            },
        };
        var guardedSettlements = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [typeof(IRefundReplacementService).FullName!] = new(StringComparer.Ordinal)
            {
                nameof(IRefundReplacementService.CompleteAsync),
                nameof(IRefundReplacementService.ConfirmManualBankTransferAsync),
            },
            [typeof(IVacationRefundService).FullName!] = new(StringComparer.Ordinal)
            {
                nameof(IVacationRefundService.CompleteAsync),
            },
            [typeof(IRefundBankExportService).FullName!] = new(StringComparer.Ordinal)
            {
                nameof(IRefundBankExportService.ConfirmItemPaidAsync),
            },
        };
        var guardedBankDetail = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [typeof(IWithdrawalService).FullName!] = new(StringComparer.Ordinal)
            {
                nameof(IWithdrawalService.GetBankDetailForStaffAsync),
            },
        };
        var guardedClaims = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [typeof(IRefundBankExportService).FullName!] = new(StringComparer.Ordinal)
            {
                nameof(IRefundBankExportService.ListEligibleAsync),
                nameof(IRefundBankExportService.CreateBatchAsync),
            },
        };

        // Explicitly reviewed and intentionally unguarded: customer
        // self-service (own eligibility/submission/choice/evidence), masked
        // or own reads, advisory previews, batch paperwork operating only on
        // already-claimed items, non-settling failure/void dispositions, and
        // automated system workers (delivery-fail timeout, vacation
        // processing). Adding a human decision method to any of these
        // services without updating this inventory fails the test below.
        var intentionallyUnguarded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [typeof(IRefundReplacementService).FullName!] = new(StringComparer.Ordinal)
            {
                nameof(IRefundReplacementService.GetEligibilityAsync),
                nameof(IRefundReplacementService.SubmitAsync),
                nameof(IRefundReplacementService.ListForCustomerAsync),
                nameof(IRefundReplacementService.GetForCustomerAsync),
                nameof(IRefundReplacementService.ListForStaffAsync),
                nameof(IRefundReplacementService.GetForStaffAsync),
                nameof(IRefundReplacementService.ChooseAsync),
                nameof(IRefundReplacementService.ProposeReplacementDateAsync),
                nameof(IRefundReplacementService.PreviewOrderRefundAsync),
                nameof(IRefundReplacementService.UploadImageAsync),
                nameof(IRefundReplacementService.OpenImageAsync),
            },
            [typeof(IVacationRefundService).FullName!] = new(StringComparer.Ordinal)
            {
                nameof(IVacationRefundService.ProcessDueAsync),
                nameof(IVacationRefundService.ProcessSubscriptionAsync),
                nameof(IVacationRefundService.GetForStaffAsync),
                nameof(IVacationRefundService.GetForCustomerAsync),
                nameof(IVacationRefundService.GetStatusForCustomerAsync),
                nameof(IVacationRefundService.ListForStaffAsync),
                nameof(IVacationRefundService.ListForCustomerAsync),
            },
            [typeof(IWithdrawalService).FullName!] = new(StringComparer.Ordinal)
            {
                nameof(IWithdrawalService.SubmitAsync),
                nameof(IWithdrawalService.ListForCustomerAsync),
                nameof(IWithdrawalService.GetForCustomerAsync),
                nameof(IWithdrawalService.ListForStaffAsync),
                nameof(IWithdrawalService.GetForStaffAsync),
            },
            [typeof(IRefundBankExportService).FullName!] = new(StringComparer.Ordinal)
            {
                nameof(IRefundBankExportService.ListBatchesAsync),
                nameof(IRefundBankExportService.GetBatchAsync),
                nameof(IRefundBankExportService.GenerateBatchAsync),
                nameof(IRefundBankExportService.DownloadBatchAsync),
                nameof(IRefundBankExportService.SubmitBatchAsync),
                nameof(IRefundBankExportService.VoidBatchAsync),
                nameof(IRefundBankExportService.FailItemAsync),
                nameof(IRefundBankExportService.VoidItemAsync),
                nameof(IRefundBankExportService.ReleaseItemReservationAsync),
            },
        };

        var inspected = new[]
        {
            typeof(IRefundReplacementService),
            typeof(IVacationRefundService),
            typeof(IWithdrawalService),
            typeof(IRefundBankExportService),
        };
        foreach (var service in inspected)
        {
            var key = service.FullName!;
            var actual = service
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => m.Name)
                .ToHashSet(StringComparer.Ordinal);
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bucket in new[] { guardedDecisions, guardedSettlements, guardedBankDetail, guardedClaims, intentionallyUnguarded })
            {
                if (bucket.TryGetValue(key, out var names))
                {
                    expected.UnionWith(names);
                }
            }

            var missing = actual.Except(expected, StringComparer.Ordinal).OrderBy(x => x).ToList();
            var stale = expected.Except(actual, StringComparer.Ordinal).OrderBy(x => x).ToList();
            Assert.True(
                missing.Count == 0 && stale.Count == 0,
                service.Name + " inventory drift — new decision workflow not reviewed: [" +
                string.Join(",", missing) + "]; removed: [" + string.Join(",", stale) +
                "]. Update EmployeeOriginGuard coverage and this inventory together.");
        }
    }

    // ---------- Shared policy fixture ----------

    private sealed class PolicyHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly TestClock _clock;

        private PolicyHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            TestClock clock,
            TestIndiaTimeProvider time,
            User customer,
            User requester,
            User ownerUser,
            User sysAdminUser,
            User dairyManager,
            User support,
            User accountant,
            Branch branch,
            Dictionary<string, byte[]> storage,
            IDataProtectionProvider protection)
        {
            _connection = connection;
            Db = db;
            _clock = clock;
            Time = time;
            Customer = customer;
            Requester = requester;
            OwnerUser = ownerUser;
            SysAdminUser = sysAdminUser;
            DairyManager = dairyManager;
            Support = support;
            Accountant = accountant;
            Branch = branch;
            Storage = storage;
            Protection = protection;
            RefundService = new RefundReplacementService(
                db,
                clock,
                new RefundReplacementConfigurationService(db, clock),
                new NumberSeriesService(db, clock),
                new CapturingMediaStorage(storage),
                new TestNotificationEventWriter(db, clock),
                Options.Create(new PaymentOptions { Currency = "INR" }),
                new BankDetailProtector(protection),
                new ManualBankBeneficiaryProtector(protection));
            VacationService = new VacationRefundService(
                db,
                time,
                new NumberSeriesService(db, time),
                new TestNotificationEventWriter(db, time));
            var numberSeries = new NumberSeriesService(db, time);
            var bankProtector = new BankDetailProtector(protection);
            CustomerService = new CustomerService(db, numberSeries, time, bankProtector);
            WithdrawalService = new WithdrawalService(
                db,
                time,
                Options.Create(new PaymentOptions
                {
                    Provider = "Mock",
                    Currency = "INR",
                    PaymentExpiryMinutes = 15,
                    MockSigningSecret = "test-signing-secret"
                }),
                numberSeries,
                bankProtector,
                new TestNotificationEventWriter(db, time));
        }

        public DoodhDirectDbContext Db { get; }
        public TestIndiaTimeProvider Time { get; }
        public User Customer { get; }
        public User Requester { get; }
        public User OwnerUser { get; }
        public User SysAdminUser { get; }
        public User DairyManager { get; }
        public User Support { get; }
        public User Accountant { get; }
        public Branch Branch { get; }
        public Dictionary<string, byte[]> Storage { get; }
        private int _ticketSequence;
        public IDataProtectionProvider Protection { get; }
        public RefundReplacementService RefundService { get; }
        public VacationRefundService VacationService { get; }
        public CustomerService CustomerService { get; }
        public WithdrawalService WithdrawalService { get; }

        public RefundReplacementActor GlobalActor(long userId) =>
            new(userId, new HashSet<long>(), true);

        public RefundReplacementActor BranchActor(long userId) =>
            new(userId, new HashSet<long> { Branch.Id }, false);

        public static async Task<PolicyHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var customer = CreateUser(UserType.Customer, "Customer", "9999999999");
            var requester = CreateUser(UserType.Employee, "Staff Shopper", "9888888888");
            var ownerUser = CreateUser(UserType.Employee, "Owner Operator", "9777777777");
            var sysAdminUser = CreateUser(UserType.SystemAdministrator, "System Admin", "9666666666");
            var dairyManager = CreateUser(UserType.Employee, "Dairy Manager", "9555555555");
            var support = CreateUser(UserType.Employee, "Support Agent", "9444444444");
            var accountant = CreateUser(UserType.Employee, "Accountant", "9333333333");
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            db.AddRange(customer, requester, ownerUser, sysAdminUser, dairyManager, support, accountant, branch);
            db.Roles.AddRange(
                new Role(AuthorizationCodes.Owner, "Owner"),
                new Role(AuthorizationCodes.SystemAdmin, "System Administrator"),
                new Role(AuthorizationCodes.DairyManager, "Dairy Manager"),
                new Role(AuthorizationCodes.CustomerSupport, "Customer Support"),
                new Role(AuthorizationCodes.Accountant, "Accountant"));
            db.NumberSeries.AddRange(
                new NumberSeries(
                    RefundReplacementService.SeriesCode,
                    "Refund/replacement request numbers",
                    "RR/{NUMBER:000000}",
                    1, 1, NumberSeriesResetPolicy.Never),
                new NumberSeries(
                    NumberSeriesSeedService.WithdrawalCode,
                    "Customer wallet withdrawal request numbers",
                    "WD/{NUMBER:000000}",
                    1, 1, NumberSeriesResetPolicy.Never));
            await db.SaveChangesAsync();

            AssignRole(db, ownerUser.Id, AuthorizationCodes.Owner);
            AssignRole(db, sysAdminUser.Id, AuthorizationCodes.SystemAdmin);
            AssignRole(db, dairyManager.Id, AuthorizationCodes.DairyManager);
            AssignRole(db, support.Id, AuthorizationCodes.CustomerSupport);
            AssignRole(db, accountant.Id, AuthorizationCodes.Accountant);
            await db.SaveChangesAsync();

            var clock = new TestClock(FrozenNow);
            var storage = new Dictionary<string, byte[]>();
            var protection = new EphemeralDataProtectionProvider();
            var time = new TestIndiaTimeProvider(clock);
            return new PolicyHarness(
                connection, db, clock, time,
                customer, requester, ownerUser, sysAdminUser,
                dairyManager, support, accountant, branch,
                storage, protection);
        }

        public async Task<Guid> SubmitEmployeeTicketAsync(string reason = "The delivered items were unsatisfactory") =>
            await SubmitTicketForAsync(Requester, reason);

        public async Task<Guid> SubmitCustomerTicketAsync() =>
            await SubmitTicketForAsync(Customer, "The delivered items were unsatisfactory");

        public async Task<Guid> SubmitTicketForAsync(User user, string reason = "The delivered items were unsatisfactory")
        {
            _ticketSequence++;
            var sequence = _ticketSequence;
            var order = await AddOrderAsync(
                Db, user, Branch, $"policy-order-{user.Id}-{sequence}", $"ORD-POL-{user.Id}-{sequence}", _clock.Now);
            var delivery = await AddDeliveryAsync(
                Db, order, user, Support, Branch,
                $"REF-POL-{user.Id}-{sequence}", $"DLV-POL-{user.Id}-{sequence}", _clock.Now);
            await FailAsync(delivery, Support.Id);
            var result = await RefundService.SubmitAsync(
                new RefundReplacementActor(user.Id, new HashSet<long>(), false),
                new SubmitRefundReplacementRequest(
                    delivery.PublicId, RefundReplacementType.Refund, reason, null),
                CancellationToken.None,
                ProofImage());
            return result.RequestId;
        }

        public async Task<Guid> SubmitEmployeeWithdrawalAsync(decimal amount, string key) =>
            await SubmitWithdrawalForAsync(Requester, amount, key);

        public async Task<Guid> SubmitCustomerWithdrawalAsync(decimal amount, string key) =>
            await SubmitWithdrawalForAsync(Customer, amount, key);

        public async Task<Guid> SubmitWithdrawalForAsync(User user, decimal amount, string key)
        {
            var account = user.Id == Customer.Id ? "4222222222222222" : "4111111111111111";
            await CustomerService.SaveBankDetailAsync(
                user.Id,
                new UpsertCustomerBankDetailRequest(
                    $"{user.DisplayName} Holder", "Example National Bank", account, "EXMP0001234"),
                CancellationToken.None);
            var wallet = new Wallet(user.Id, "INR");
            wallet.Credit(
                WalletTransactionType.TopUp, 500m, $"policy-topup-{user.Id}-{key}",
                "Policy test top-up", _clock.Now);
            Db.Wallets.Add(wallet);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            var result = await WithdrawalService.SubmitAsync(
                user.Id, new CreateWithdrawalRequest(amount, key), CancellationToken.None);
            return result.PublicId;
        }

        public async Task<Guid> SeedEmployeeVacationRequestAsync() =>
            await SeedVacationRequestForAsync(Requester);

        public async Task<Guid> SeedVacationRequestForAsync(User user)
        {
            var category = new ProductCategory("MILK", "Milk");
            category.Activate();
            Db.ProductCategories.Add(category);
            await Db.SaveChangesAsync();
            var product = new Product(category.Id, $"MILK-POL-{user.Id}", "Fresh Milk", null, "litre", 100m);
            product.Activate();
            var address = new CustomerAddress(
                user.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka",
                "560001", user.DisplayName ?? "Customer", "9999999999", 12.9716m, 77.5946m);
            Db.AddRange(product, address);
            await Db.SaveChangesAsync();

            var start = new DateOnly(2026, 7, 1);
            var end = new DateOnly(2026, 7, 2);
            var subscription = new Subscription(
                user.Id, product.Id, address.Id, Branch.Id, $"POL-VAC-{user.Id}-{Guid.NewGuid():N}",
                start, end, 1m, 100m, 2,
                product.Sku, product.Name, product.UnitOfMeasure,
                Branch.Code, Branch.Name,
                "1 Main Road, Central, Bengaluru, Karnataka 560001");
            subscription.AddDelivery(start);
            subscription.AddDelivery(end);
            subscription.Activate(Time.Now);
            Db.Subscriptions.Add(subscription);
            await Db.SaveChangesAsync();

            var payment = Payment.CreateForSubscription(
                subscription.Id, user.Id, PaymentMethod.Wallet, subscription.PayableAmount,
                "INR", $"policy-vac-pay-{subscription.Id}", Time.Now.AddMinutes(15));
            payment.Succeed(null, "WALLET", Time.Now);
            Db.Payments.Add(payment);
            await Db.SaveChangesAsync();

            var occurrenceIds = await Db.SubscriptionDeliveries
                .AsNoTracking()
                .Where(x => x.SubscriptionId == subscription.Id)
                .OrderBy(x => x.ScheduledDate)
                .Select(x => x.Id)
                .ToListAsync();
            var gross = decimal.Round(subscription.PayableAmount / subscription.TotalEntitlement, 2);
            var request = new VacationRefundRequest(
                subscription.Id, user.Id, Branch.Id, gross, gross * occurrenceIds.Count,
                occurrenceIds.Count, subscription.PayableAmount, subscription.TotalEntitlement,
                "INR", payment.Id, "{\"seeded\":\"policy\"}", Time.Now);
            request.AssignRequestNumber($"VR/POL/{subscription.Id}");
            foreach (var occurrenceId in occurrenceIds)
            {
                request.Occurrences.Add(new VacationRefundOccurrence(
                    0, occurrenceId, start, 1m, gross));
                start = start.AddDays(1);
            }

            Db.VacationRefundRequests.Add(request);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();

            var total = await Db.VacationRefundRequests
                .AsNoTracking()
                .Where(x => x.Id == request.Id)
                .Select(x => x.TotalAmount)
                .SingleAsync();
            Assert.Equal(200m, total);
            return request.PublicId;
        }

        public async Task<Guid> SeedEmployeeManualBankTicketAsync(decimal paymentAmount, string suffix)
        {
            var ticketId = await SubmitTicketForAsync(Requester, $"Manual-bank refund {suffix}");
            var ticket = await Db.RefundReplacementRequests
                .AsNoTracking()
                .SingleAsync(x => x.PublicId == ticketId);
            var payment = new Payment(
                ticket.OrderId!.Value, Requester.Id, PaymentMethod.Wallet, paymentAmount,
                "INR", $"policy-mb-pay-{suffix}-{Guid.NewGuid():N}", Time.Now.AddMinutes(15));
            payment.Succeed(null, "WALLET", Time.Now);
            Db.Payments.Add(payment);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();

            await CustomerService.SaveBankDetailAsync(
                Requester.Id,
                new UpsertCustomerBankDetailRequest(
                    "Staff Shopper", "Example National Bank",
                    "4111111111111111", "EXMP0001234"),
                CancellationToken.None);
            return ticketId;
        }

        public async Task SeedExportPrerequisitesAsync()
        {
            if (!await Db.CompanyBankDetails.AnyAsync())
            {
                var protector = new CompanyBankDetailProtector(Protection);
                var detail = new CompanyBankDetail(
                    "DoodhDirect Foods Pvt Ltd", "State Bank of India",
                    protector.Protect("123456789012"), "9012", "SBIN0001234");
                detail.SetCreated(Time.Now);
                Db.CompanyBankDetails.Add(detail);
            }

            if (!await Db.NumberSeries.AnyAsync(x => x.Code == RefundBankExportService.NumberSeriesCode))
            {
                Db.NumberSeries.Add(new NumberSeries(
                    RefundBankExportService.NumberSeriesCode,
                    "Manual-bank refund export batch numbers",
                    "RBE/{NUMBER:000000}",
                    1, 1, NumberSeriesResetPolicy.Never));
            }

            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public RefundBankExportService CreateExportService() => new(
            Db,
            Time,
            new NumberSeriesService(Db, Time),
            RefundService,
            new CompanyBankService(Db, Time, new CompanyBankDetailProtector(Protection)),
            new ManualBankBeneficiaryProtector(Protection),
            new CompanyBankDetailProtector(Protection));

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static User CreateUser(UserType type, string name, string mobile)
        {
            var user = new User(type);
            user.SetProfile(name);
            user.SetContact(mobile, null);
            return user;
        }

        private static void AssignRole(DoodhDirectDbContext db, long userId, string roleCode)
        {
            var roleId = db.Roles.Single(x => x.Code == roleCode).Id;
            db.UserRoles.Add(new UserRole(userId, roleId));
        }

        private static async Task<Order> AddOrderAsync(
            DoodhDirectDbContext db, User customer, Branch branch,
            string idempotencyKey, string orderNumber, DateTime now)
        {
            var order = new Order(
                customer.Id, null, branch.Id, idempotencyKey, orderNumber,
                160m, 0m, branch.Code, branch.Name,
                "Home", "1 Main Road", null, "Central", "Bengaluru", "Karnataka", "560001",
                null, null, "Customer", customer.Mobile ?? "9999999999", 12.9716m, 77.5946m);
            order.ConfirmPayment();
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            return order;
        }

        private static async Task<Delivery> AddDeliveryAsync(
            DoodhDirectDbContext db, Order order, User customer, User staff, Branch branch,
            string reference, string deliveryNumber, DateTime now)
        {
            var delivery = Delivery.ForOrder(
                order.Id, customer.Id, branch.Id, DateOnly.FromDateTime(now), reference,
                "Customer", customer.Mobile ?? "9999999999",
                "1 Main Road, Central, Bengaluru, Karnataka 560001", null, 12.9716m, 77.5946m);
            delivery.AssignDeliveryNumber(deliveryNumber);
            db.Deliveries.Add(delivery);
            await db.SaveChangesAsync();
            return delivery;
        }

        private async Task FailAsync(Delivery delivery, long staffId)
        {
            delivery.Assign(staffId, staffId, _clock.Now, null);
            delivery.PickUp(staffId, _clock.Now, null);
            delivery.Start(staffId, _clock.Now);
            delivery.Arrive(staffId, _clock.Now);
            _clock.Advance(TimeSpan.FromMinutes(1));
            delivery.Fail(staffId, _clock.Now, "Customer not available", null, null, null);
            await Db.SaveChangesAsync();
        }

        private SubmitRefundReplacementImage ProofImage() =>
            new(new MemoryStream(ImageBytes, writable: false), "proof.jpg", "image/jpeg", ImageBytes.Length);

        private static readonly byte[] ImageBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x01];

        private sealed class CapturingMediaStorage(Dictionary<string, byte[]> files) : IMediaStorage
        {
            public async Task<StoredMediaResult> SaveAsync(
                string storageKey, Stream content, string contentType, CancellationToken cancellationToken)
            {
                using var buffered = new MemoryStream();
                await content.CopyToAsync(buffered, cancellationToken);
                files[storageKey] = buffered.ToArray();
                return new StoredMediaResult(storageKey, contentType, buffered.Length);
            }

            public Task<StoredMediaContent> OpenReadAsync(string storageKey, CancellationToken cancellationToken) =>
                files.TryGetValue(storageKey, out var content)
                    ? Task.FromResult(new StoredMediaContent(
                        new MemoryStream(content, writable: false), "image/jpeg", content.Length))
                    : throw new NotFoundException("The media was not found.");

            public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken)
            {
                files.Remove(storageKey);
                return Task.CompletedTask;
            }
        }
    }
}
