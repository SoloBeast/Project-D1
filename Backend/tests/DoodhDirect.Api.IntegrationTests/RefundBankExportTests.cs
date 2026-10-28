using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Application.Setup;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Dairy;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Customer;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Reports;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Stage 3: manual-bank refund export batches. Batches claim approved
/// ManualBankTransfer tickets and freeze beneficiary plus company snapshots
/// without moving money; settlement happens only through per-item
/// confirmation via the Stage 2 service after the real bank transfer.
/// </summary>
public sealed class RefundBankExportTests
{
    private const string CustomerHolder = "Asha Customer";
    private const string CustomerBank = "Example National Bank";
    private const string CustomerAccount = "4111111111111111";
    private const string CustomerLast4 = "1111";
    private const string CustomerIfsc = "EXMP0001234";

    private const string CompanyHolder = "DoodhDirect Foods Pvt Ltd";
    private const string CompanyBank = "State Bank of India";
    private const string CompanyAccount = "123456789012";
    private const string CompanyIfsc = "SBIN0001234";

    // 1. An eligible ManualBankTransfer refund appears in the candidate list.
    [Fact]
    public async Task EligibleManualBankTicket_AppearsInCandidateList()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");
        await SeedExportPrerequisitesAsync(harness);

        var service = CreateExportService(harness);
        var candidates = await service.ListEligibleAsync(StaffActor(harness), CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal(ticketId, candidate.TicketId);
        Assert.Equal("RR-ONETIME-1", candidate.RequestNumber);
        Assert.Equal("ORD-ONETIME-1", candidate.OrderNumber);
        Assert.Equal(200m, candidate.ReservedAmount);
        Assert.Equal("INR", candidate.ReservedCurrency);
        Assert.Equal(1, candidate.ReservationCount);
        Assert.True(candidate.CompanyBankConfigured);
    }

    // 2. Wallet-settled and gateway-settled refunds are excluded.
    [Fact]
    public async Task WalletCompletedTicket_IsExcludedFromCandidates()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness, "1");
        await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet, "1");
        await SeedBankDetailAsync(harness, CustomerHolder, CustomerBank, CustomerAccount, CustomerLast4, CustomerIfsc);
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: false, WaiverReason: "Goodwill",
                SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        await service.CompleteAsync(
            StaffActor(harness), ticketId, new DecideRefundReplacementRequest("done"), CancellationToken.None);

        var exports = CreateExportService(harness);
        Assert.Empty(await exports.ListEligibleAsync(StaffActor(harness), CancellationToken.None));
    }

    [Fact]
    public async Task GatewaySettledPayment_RemovesTicketFromEligibility()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");

        // Another path settles the covering payment externally (e.g. a
        // gateway refund posted directly): outstanding drops to zero while
        // the reservation still claims 200.
        var payment = await harness.Db.Payments.SingleAsync();
        payment.CompleteRefund(200m);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
        await SeedExportPrerequisitesAsync(harness);

        var service = CreateExportService(harness);
        Assert.Empty(await service.ListEligibleAsync(StaffActor(harness), CancellationToken.None));

        await Assert.ThrowsAsync<ConflictException>(() => service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest([ticketId]),
            CancellationToken.None));
    }

    // 3. Missing/inconsistent reservation or frozen snapshots prevent export.
    [Fact]
    public async Task LegacyUnspecifiedTicket_CannotBeClaimed()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness, "1", preApprove: true);
        await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet, "1");
        await SeedExportPrerequisitesAsync(harness);

        var service = CreateExportService(harness);
        Assert.Empty(await service.ListEligibleAsync(StaffActor(harness), CancellationToken.None));

        var error = await Assert.ThrowsAsync<ConflictException>(() => service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest([ticketId]),
            CancellationToken.None));
        Assert.Contains("manual-bank", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeletedReservation_PreventsClaim()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");
        await SeedExportPrerequisitesAsync(harness);

        harness.Db.Refunds.RemoveRange(harness.Db.Refunds);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var service = CreateExportService(harness);
        Assert.Empty(await service.ListEligibleAsync(StaffActor(harness), CancellationToken.None));

        var error = await Assert.ThrowsAsync<ConflictException>(() => service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest([ticketId]),
            CancellationToken.None));
        Assert.Contains("reservation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // 4. Two concurrent exports cannot claim the same refund.
    [Fact]
    public async Task ConcurrentBatchClaims_ClaimExactlyOnce()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");
        await SeedExportPrerequisitesAsync(harness);

        var firstDb = harness.CreateContext();
        var secondDb = harness.CreateContext();
        await using var _ = firstDb;
        await using var __ = secondDb;
        var firstService = CreateExportServiceFor(firstDb, harness);
        var secondService = CreateExportServiceFor(secondDb, harness);

        var outcomes = await Task.WhenAll(
            new[]
            {
                firstService.CreateBatchAsync(
                    StaffActor(harness),
                    new CreateRefundBankExportBatchRequest([ticketId]),
                    CancellationToken.None),
                secondService.CreateBatchAsync(
                    StaffActor(harness),
                    new CreateRefundBankExportBatchRequest([ticketId]),
                    CancellationToken.None),
            }.Select(async attempt =>
            {
                try
                {
                    return (Result: await attempt, Error: (Exception?)null);
                }
                catch (Exception exception)
                {
                    return (Result: (RefundBankExportBatchResult?)null, Error: exception);
                }
            }));

        Assert.Single(outcomes, x => x.Result is not null);
        var failure = Assert.Single(outcomes, x => x.Result is null);
        Assert.IsType<ConflictException>(failure.Error);

        await using var verifyDb = harness.CreateContext();
        await using var ___ = verifyDb;
        Assert.Equal(1, await verifyDb.RefundBankExportBatches.AsNoTracking().CountAsync());
        Assert.Equal(1, await verifyDb.RefundBankExportItems.AsNoTracking().CountAsync());
        // Claiming moves no money: the reservation is still pending.
        Assert.Equal(RefundStatus.Pending, await verifyDb.Refunds.AsNoTracking().Select(x => x.Status).SingleAsync());
        Assert.Equal(0m, await verifyDb.Payments.AsNoTracking().Select(x => x.RefundedAmount).SingleAsync());
    }

    // 5. Re-downloading returns identical bytes and creates no new claims.
    [Fact]
    public async Task Redownload_ReturnsIdenticalBytesWithoutNewClaims()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");
        var batchId = await ClaimAndGenerateAsync(harness, ticketId);

        var service = CreateExportService(harness);
        var first = await service.DownloadBatchAsync(StaffActor(harness), batchId, CancellationToken.None);
        var second = await service.DownloadBatchAsync(StaffActor(harness), batchId, CancellationToken.None);

        Assert.Equal(first.Content, second.Content);
        Assert.Equal(first.FileName, second.FileName);
        Assert.False(string.IsNullOrWhiteSpace(first.Disclaimer));
        Assert.Contains("Generic", first.Disclaimer, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.RefundBankExportBatches.AsNoTracking().CountAsync());
        Assert.Equal(
            2,
            await harness.Db.AuditLogs.AsNoTracking()
                .CountAsync(x => x.Action == "REFUND_BANK_EXPORT.BATCH_DOWNLOADED"));
        Assert.Equal(
            RefundBankExportBatchStatus.Generated,
            (await harness.Db.RefundBankExportBatches.AsNoTracking().SingleAsync()).Status);
    }

    // 6. CSV quoting and formula-injection protection.
    [Fact]
    public void CsvTable_QuotesAndNeutralizesFormulas()
    {
        var content = ReportTabularExporter.CsvTable(
            ["Name", "Note"],
            [
                new string?[] { "=SUM(1,1)", "plain" },
                new string?[] { "+919999999999", "a\tb" },
                new string?[] { "-5", "@mention" },
                new string?[] { "  -trimmed", "line1\r\nline2" },
                new string?[] { "Comma, Bank", "Quote \"Q\"" },
            ]);
        var csv = Encoding.UTF8.GetString(content.AsSpan(3));

        Assert.StartsWith("Name,Note" + Environment.NewLine, csv, StringComparison.Ordinal);
        Assert.Contains("\"'=SUM(1,1)\"", csv, StringComparison.Ordinal);
        Assert.Contains("'+919999999999", csv, StringComparison.Ordinal);
        Assert.Contains("'-5", csv, StringComparison.Ordinal);
        Assert.Contains("'@mention", csv, StringComparison.Ordinal);
        Assert.Contains("'  -trimmed", csv, StringComparison.Ordinal);
        Assert.Contains("\"a\tb\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"Comma, Bank\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"Quote \"\"Q\"\"\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_NeutralizesFormulaHolderName()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1", holderName: "=HYPERLINK(\"evil\")");
        var batchId = await ClaimAndGenerateAsync(harness, ticketId);

        var service = CreateExportService(harness);
        var download = await service.DownloadBatchAsync(StaffActor(harness), batchId, CancellationToken.None);
        var csv = Encoding.UTF8.GetString(download.Content.AsSpan(3));

        Assert.Contains("\"'=HYPERLINK(\"\"evil\"\")\"", csv, StringComparison.Ordinal);
        Assert.DoesNotContain(",=HYPERLINK(", csv, StringComparison.Ordinal);
    }

    // 7. Full account numbers appear only in authorized downloads.
    [Fact]
    public async Task FullAccountNumbers_AppearOnlyInDownloadBytes()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");
        var batchId = await ClaimAndGenerateAsync(harness, ticketId);

        var service = CreateExportService(harness);
        var candidates = await service.ListEligibleAsync(StaffActor(harness), CancellationToken.None);
        Assert.Empty(candidates); // already claimed: proves list hides nothing either way
        var batch = await service.GetBatchAsync(StaffActor(harness), batchId, CancellationToken.None);

        Assert.DoesNotContain(CustomerAccount, JsonSerializer.Serialize(batch));
        Assert.DoesNotContain(CompanyAccount, JsonSerializer.Serialize(batch));
        Assert.Contains(CustomerLast4, JsonSerializer.Serialize(batch));
        Assert.Contains("9012", JsonSerializer.Serialize(batch));

        foreach (var audit in await harness.Db.AuditLogs.AsNoTracking().ToListAsync())
        {
            Assert.DoesNotContain(CustomerAccount, audit.OldValueJson ?? string.Empty);
            Assert.DoesNotContain(CustomerAccount, audit.NewValueJson ?? string.Empty);
            Assert.DoesNotContain(CompanyAccount, audit.OldValueJson ?? string.Empty);
            Assert.DoesNotContain(CompanyAccount, audit.NewValueJson ?? string.Empty);
        }

        var download = await service.DownloadBatchAsync(StaffActor(harness), batchId, CancellationToken.None);
        var csv = Encoding.UTF8.GetString(download.Content.AsSpan(3));
        Assert.Contains(CustomerAccount, csv, StringComparison.Ordinal);
        Assert.Contains(CompanyAccount, csv, StringComparison.Ordinal);
    }

    // 8. Company or customer bank detail edits do not change prior batches.
    [Fact]
    public async Task BankDetailEdits_DoNotChangePriorBatchBytes()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");
        var batchId = await ClaimAndGenerateAsync(harness, ticketId);

        var service = CreateExportService(harness);
        var before = await service.DownloadBatchAsync(StaffActor(harness), batchId, CancellationToken.None);

        var companyService = new CompanyBankService(
            harness.Db, harness.TimeProvider, new CompanyBankDetailProtector(SharedDataProtection));
        await companyService.UpdateAsync(
            new UpsertCompanyBankDetailRequest("Renamed Holder", "New Bank", "9999888877776666", "HDFC0001234"),
            harness.Manager.Id, null, null, CancellationToken.None);

        var customerDetail = await harness.Db.CustomerBankDetails.SingleAsync();
        customerDetail.Update(
            "Changed Name",
            "Changed Bank",
            new BankDetailProtector(SharedDataProtection).Protect("5555444433332222"),
            "2222",
            "CHNG0001234");
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var after = await service.DownloadBatchAsync(StaffActor(harness), batchId, CancellationToken.None);
        Assert.Equal(before.Content, after.Content);
        var csv = Encoding.UTF8.GetString(after.Content.AsSpan(3));
        Assert.Contains(CustomerAccount, csv, StringComparison.Ordinal);
        Assert.DoesNotContain("9999888877776666", csv, StringComparison.Ordinal);
    }

    // 9. Invalid/missing UTR and wrong-state confirmation are rejected.
    [Fact]
    public async Task ConfirmItem_WithInvalidUtr_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");
        var batchId = await ClaimAndGenerateAsync(harness, ticketId);

        var service = CreateExportService(harness);
        var itemId = await ItemIdAsync(harness, batchId);

        var error = await Assert.ThrowsAsync<ValidationAppException>(() => service.ConfirmItemPaidAsync(
            StaffActor(harness), itemId,
            new ConfirmRefundBankExportItemRequest("ABC", null),
            CancellationToken.None));
        Assert.Contains("6", error.Message, StringComparison.Ordinal);

        Assert.Equal(
            RefundBankExportItemStatus.Generated,
            (await harness.Db.RefundBankExportItems.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ConfirmItem_BeforeGenerate_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");
        await SeedExportPrerequisitesAsync(harness);
        var batch = await CreateExportService(harness).CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest([ticketId]),
            CancellationToken.None);
        var itemId = await ItemIdAsync(harness, batch.BatchId);

        var service = CreateExportService(harness);
        await Assert.ThrowsAsync<ConflictException>(() => service.ConfirmItemPaidAsync(
            StaffActor(harness), itemId,
            new ConfirmRefundBankExportItemRequest("UTR20260001", null),
            CancellationToken.None));

        // The Stage 2 direct endpoint is gated the same way: no export
        // linkage, no settlement.
        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            CreateRefundService(harness).ConfirmManualBankTransferAsync(
                StaffActor(harness), ticketId,
                new ConfirmManualBankTransferRequest("UTR20260001", null),
                CancellationToken.None));
        Assert.Contains("export", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            RefundStatus.Pending, (await harness.Db.Refunds.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task FailItem_BeforeSubmit_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");
        var batchId = await ClaimAndGenerateAsync(harness, ticketId);

        var service = CreateExportService(harness);
        var itemId = await ItemIdAsync(harness, batchId);
        await Assert.ThrowsAsync<ConflictException>(() => service.FailItemAsync(
            StaffActor(harness), itemId,
            new RefundBankExportItemDispositionRequest("Bank rejected", null),
            CancellationToken.None));
    }

    [Fact]
    public async Task VoidItem_AfterSubmit_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");
        var batchId = await ClaimAndGenerateAsync(harness, ticketId);

        var service = CreateExportService(harness);
        await service.SubmitBatchAsync(StaffActor(harness), batchId, CancellationToken.None);

        var voidItemId = await ItemIdAsync(harness, batchId);
        await Assert.ThrowsAsync<ConflictException>(() => service.VoidItemAsync(
            StaffActor(harness), voidItemId,
            new RefundBankExportItemDispositionRequest("Changed mind", null),
            CancellationToken.None));
    }

    // 10. One item can be paid exactly once and only its ticket completes.
    [Fact]
    public async Task ConfirmItem_PaysOnlyItsOwnTicketExactlyOnce()
    {
        await using var harness = await CreateHarnessAsync();
        var firstTicket = await SeedManualBankTicketAsync(harness, 200m, "1");
        var secondTicket = await SeedManualBankTicketAsync(harness, 150m, "2");
        await SeedExportPrerequisitesAsync(harness);

        var service = CreateExportService(harness);
        var batch = await service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest([firstTicket, secondTicket]),
            CancellationToken.None);
        await service.GenerateBatchAsync(StaffActor(harness), batch.BatchId, CancellationToken.None);
        await service.SubmitBatchAsync(StaffActor(harness), batch.BatchId, CancellationToken.None);

        var items = await harness.Db.RefundBankExportItems.AsNoTracking()
            .OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, items.Count);

        var paid = await service.ConfirmItemPaidAsync(
            StaffActor(harness), items[0].PublicId,
            new ConfirmRefundBankExportItemRequest("UTR20260001", "Paid via NEFT"),
            CancellationToken.None);
        Assert.Equal(nameof(RefundBankExportItemStatus.Paid), paid.Status);
        Assert.Equal("UTR20260001", paid.TransferReference);

        var tickets = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(RefundReplacementStatus.Completed, tickets[0].Status);
        Assert.Equal(RefundReplacementStatus.Approved, tickets[1].Status);

        var refunds = await harness.Db.Refunds.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(RefundStatus.Succeeded, refunds[0].Status);
        Assert.Equal(RefundStatus.Pending, refunds[1].Status);
        var payments = await harness.Db.Payments.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(200m, payments[0].RefundedAmount);
        Assert.Equal(0m, payments[1].RefundedAmount);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());

        // The batch stays open while the second item is unresolved.
        Assert.Equal(
            RefundBankExportBatchStatus.Submitted,
            (await harness.Db.RefundBankExportBatches.AsNoTracking().SingleAsync()).Status);
    }

    // 11. Duplicate confirmation cannot create another payment settlement.
    [Fact]
    public async Task DuplicateConfirm_WithSameUtr_IsCleanConflictWithoutMovement()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");
        var batchId = await ClaimAndGenerateAsync(harness, ticketId);

        var service = CreateExportService(harness);
        await service.SubmitBatchAsync(StaffActor(harness), batchId, CancellationToken.None);
        var itemId = await ItemIdAsync(harness, batchId);
        await service.ConfirmItemPaidAsync(
            StaffActor(harness), itemId,
            new ConfirmRefundBankExportItemRequest("UTR20260001", null),
            CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => service.ConfirmItemPaidAsync(
            StaffActor(harness), itemId,
            new ConfirmRefundBankExportItemRequest("UTR20260001", null),
            CancellationToken.None));

        Assert.Equal(200m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(RefundStatus.Succeeded, (await harness.Db.Refunds.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task DuplicateConfirm_WithDifferentUtr_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");
        var batchId = await ClaimAndGenerateAsync(harness, ticketId);

        var service = CreateExportService(harness);
        var itemId = await ItemIdAsync(harness, batchId);
        await service.ConfirmItemPaidAsync(
            StaffActor(harness), itemId,
            new ConfirmRefundBankExportItemRequest("UTR20260001", null),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<ConflictException>(() => service.ConfirmItemPaidAsync(
            StaffActor(harness), itemId,
            new ConfirmRefundBankExportItemRequest("UTR99999999", null),
            CancellationToken.None));
        Assert.Contains("from status Paid", error.Message, StringComparison.Ordinal);
    }

    // 12. Voiding a batch never automatically releases uncertain transfers.
    [Fact]
    public async Task VoidClaimedBatch_KeepsReservationPending()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");
        await SeedExportPrerequisitesAsync(harness);

        var service = CreateExportService(harness);
        var batch = await service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest([ticketId]),
            CancellationToken.None);

        var voided = await service.VoidBatchAsync(
            StaffActor(harness), batch.BatchId,
            new RefundBankExportItemDispositionRequest("Duplicate file", null),
            CancellationToken.None);
        Assert.Equal(nameof(RefundBankExportBatchStatus.Voided), voided.Status);
        Assert.Equal(nameof(RefundBankExportItemStatus.Voided), voided.Items.Single().Status);

        // Nothing released: the reservation is untouched and the ticket is
        // still approved, so finance must reconcile explicitly.
        Assert.Equal(RefundStatus.Pending, (await harness.Db.Refunds.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        Assert.Equal(
            RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.DownloadBatchAsync(StaffActor(harness), batch.BatchId, CancellationToken.None));
    }

    [Fact]
    public async Task VoidSubmittedBatch_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");
        var batchId = await ClaimAndGenerateAsync(harness, ticketId);

        var service = CreateExportService(harness);
        await service.SubmitBatchAsync(StaffActor(harness), batchId, CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => service.VoidBatchAsync(
            StaffActor(harness), batchId,
            new RefundBankExportItemDispositionRequest("Too late", null),
            CancellationToken.None));

        Assert.Equal(
            RefundBankExportItemStatus.Submitted,
            (await harness.Db.RefundBankExportItems.AsNoTracking().SingleAsync()).Status);
    }

    // 12b. Voiding a subset no longer strands the batch: the rest still
    // generate and submit, with audited per-item outcomes.
    [Fact]
    public async Task VoidSomeThenGenerateThenSubmit_Succeeds()
    {
        await using var harness = await CreateHarnessAsync();
        var first = await SeedManualBankTicketAsync(harness, 200m, "v1");
        var second = await SeedManualBankTicketAsync(harness, 200m, "v2");
        await SeedExportPrerequisitesAsync(harness);

        var service = CreateExportService(harness);
        var batch = await service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest([first, second]),
            CancellationToken.None);

        var itemIds = await BatchItemIdsAsync(harness, batch.BatchId);
        Assert.Equal(2, itemIds.Count);

        var voided = await service.VoidItemAsync(
            StaffActor(harness), itemIds[0],
            new RefundBankExportItemDispositionRequest("Duplicate claim", null),
            CancellationToken.None);
        Assert.Equal(nameof(RefundBankExportItemStatus.Voided), voided.Status);

        var generated = await service.GenerateBatchAsync(
            StaffActor(harness), batch.BatchId, CancellationToken.None);
        Assert.Equal(nameof(RefundBankExportBatchStatus.Generated), generated.Status);

        var submitted = await service.SubmitBatchAsync(
            StaffActor(harness), batch.BatchId, CancellationToken.None);
        Assert.Equal(nameof(RefundBankExportBatchStatus.Submitted), submitted.Status);
        Assert.Equal(nameof(RefundBankExportItemStatus.Voided), submitted.Items[0].Status);
        Assert.Equal(nameof(RefundBankExportItemStatus.Submitted), submitted.Items[1].Status);
    }

    // 12c. Paid and voided items do not block submitting the rest.
    [Fact]
    public async Task SubmitWithPaidAndVoidedItems_SubmitsOnlyGenerated()
    {
        await using var harness = await CreateHarnessAsync();
        await SeedManualBankTicketAsync(harness, 200m, "p1");
        await SeedManualBankTicketAsync(harness, 200m, "p2");
        await SeedManualBankTicketAsync(harness, 200m, "p3");
        await SeedExportPrerequisitesAsync(harness);

        var service = CreateExportService(harness);
        var tickets = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => x.PublicId)
            .ToListAsync();
        var batch = await service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest(tickets),
            CancellationToken.None);
        await service.GenerateBatchAsync(StaffActor(harness), batch.BatchId, CancellationToken.None);

        var itemIds = await BatchItemIdsAsync(harness, batch.BatchId);
        Assert.Equal(3, itemIds.Count);

        var paid = await service.ConfirmItemPaidAsync(
            StaffActor(harness), itemIds[0],
            new ConfirmRefundBankExportItemRequest("UTR123456", null),
            CancellationToken.None);
        Assert.Equal(nameof(RefundBankExportItemStatus.Paid), paid.Status);

        await service.VoidItemAsync(
            StaffActor(harness), itemIds[1],
            new RefundBankExportItemDispositionRequest("Duplicate claim", null),
            CancellationToken.None);

        var submitted = await service.SubmitBatchAsync(
            StaffActor(harness), batch.BatchId, CancellationToken.None);
        Assert.Equal(nameof(RefundBankExportBatchStatus.Submitted), submitted.Status);
        Assert.Equal(nameof(RefundBankExportItemStatus.Paid), submitted.Items[0].Status);
        Assert.Equal(nameof(RefundBankExportItemStatus.Voided), submitted.Items[1].Status);
        Assert.Equal(nameof(RefundBankExportItemStatus.Submitted), submitted.Items[2].Status);

        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync(x => x.Status == RefundStatus.Succeeded));
        Assert.Equal(2, await harness.Db.Refunds.AsNoTracking().CountAsync(x => x.Status == RefundStatus.Pending));
    }

    // 12d. Downloads never include Failed, Voided or Released rows.
    [Fact]
    public async Task Download_ExcludesTerminalRows()
    {
        await using var harness = await CreateHarnessAsync();
        await SeedManualBankTicketAsync(harness, 200m, "d1");
        await SeedManualBankTicketAsync(harness, 200m, "d2");
        await SeedManualBankTicketAsync(harness, 200m, "d3");
        await SeedExportPrerequisitesAsync(harness);

        var service = CreateExportService(harness);
        var tickets = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => x.PublicId)
            .ToListAsync();
        var batch = await service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest(tickets),
            CancellationToken.None);
        await service.GenerateBatchAsync(StaffActor(harness), batch.BatchId, CancellationToken.None);

        var itemIds = await BatchItemIdsAsync(harness, batch.BatchId);
        await service.VoidItemAsync(
            StaffActor(harness), itemIds[0],
            new RefundBankExportItemDispositionRequest("Duplicate claim", null),
            CancellationToken.None);
        await service.ReleaseItemReservationAsync(
            StaffActor(harness), itemIds[1],
            new RefundBankExportItemDispositionRequest("Wrong beneficiary account", null),
            CancellationToken.None);
        await service.SubmitBatchAsync(StaffActor(harness), batch.BatchId, CancellationToken.None);

        var download = await service.DownloadBatchAsync(
            StaffActor(harness), batch.BatchId, CancellationToken.None);
        var csv = Encoding.UTF8.GetString(download.Content);
        var requests = await harness.Db.RefundBankExportItems.AsNoTracking()
            .Where(x => x.BatchId == harness.Db.RefundBankExportBatches.AsNoTracking()
                .Where(b => b.PublicId == batch.BatchId).Select(b => b.Id).Single())
            .OrderBy(x => x.Id)
            .Select(x => x.RequestNumber)
            .ToListAsync();
        Assert.DoesNotContain(requests[0], csv, StringComparison.Ordinal);
        Assert.DoesNotContain(requests[1], csv, StringComparison.Ordinal);
        Assert.Contains(requests[2], csv, StringComparison.Ordinal);
    }

    // 12e. A batch with no payable rows left has nothing to download.
    [Fact]
    public async Task Download_WithNoPayableItems_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "e1");
        var batchId = await ClaimAndGenerateAsync(harness, ticketId);

        var service = CreateExportService(harness);
        await service.SubmitBatchAsync(StaffActor(harness), batchId, CancellationToken.None);
        await service.FailItemAsync(
            StaffActor(harness), await ItemIdAsync(harness, batchId),
            new RefundBankExportItemDispositionRequest("Bank rejected the transfer", null),
            CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => service.DownloadBatchAsync(
            StaffActor(harness), batchId, CancellationToken.None));
    }

    // 12f. Listing batches assembles every batch with its own items.
    [Fact]
    public async Task ListBatches_ReturnsEveryBatchWithItsOwnItems()
    {
        await using var harness = await CreateHarnessAsync();
        await SeedManualBankTicketAsync(harness, 200m, "m1");
        await SeedManualBankTicketAsync(harness, 200m, "m2");
        await SeedManualBankTicketAsync(harness, 200m, "m3");
        await SeedManualBankTicketAsync(harness, 200m, "m4");
        await SeedExportPrerequisitesAsync(harness);

        var service = CreateExportService(harness);
        var tickets = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => x.PublicId)
            .ToListAsync();
        var first = await service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest(tickets.Take(2).ToList()),
            CancellationToken.None);
        var second = await service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest(tickets.Skip(2).ToList()),
            CancellationToken.None);

        var listed = await service.ListBatchesAsync(StaffActor(harness), CancellationToken.None);
        Assert.Equal(2, listed.Count);
        // Newest batch first.
        Assert.Equal(second.BatchId, listed[0].BatchId);
        Assert.Equal(first.BatchId, listed[1].BatchId);
        Assert.Equal(2, listed[0].ItemCount);
        Assert.Equal(2, listed[1].ItemCount);
        Assert.Equal(400m, listed[0].TotalAmount);
        Assert.Equal(400m, listed[1].TotalAmount);
        Assert.Equal(
            ["RR-ONETIME-m3", "RR-ONETIME-m4"],
            listed[0].Items.Select(x => x.RequestNumber).OrderBy(x => x));
        Assert.Equal(
            ["RR-ONETIME-m1", "RR-ONETIME-m2"],
            listed[1].Items.Select(x => x.RequestNumber).OrderBy(x => x));
        Assert.All(listed.SelectMany(x => x.Items), x => Assert.NotEmpty(x.ReservationRefundIds));
    }

    // 12g. A failed item's ticket can be reclaimed into a new batch and
    // settles exactly once: the same reservation rows are linked, not duplicated.
    [Fact]
    public async Task FailThenReclaim_PaysExactlyOnce()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "f1");
        var firstBatchId = await ClaimAndGenerateAsync(harness, ticketId);

        var service = CreateExportService(harness);
        await service.SubmitBatchAsync(StaffActor(harness), firstBatchId, CancellationToken.None);
        await service.FailItemAsync(
            StaffActor(harness), await ItemIdAsync(harness, firstBatchId),
            new RefundBankExportItemDispositionRequest("Bank rejected the transfer", null),
            CancellationToken.None);

        // Failure is terminal for the item but not the ticket: the ticket
        // is eligible again with its reservation still pending.
        var candidates = await service.ListEligibleAsync(StaffActor(harness), CancellationToken.None);
        Assert.Equal(ticketId, Assert.Single(candidates).TicketId);
        Assert.Equal(
            RefundReplacementStatus.Approved,
            await harness.Db.RefundReplacementRequests.AsNoTracking().Select(x => x.Status).SingleAsync());
        Assert.Equal(
            RefundStatus.Pending,
            await harness.Db.Refunds.AsNoTracking().Select(x => x.Status).SingleAsync());

        var secondBatch = await service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest([ticketId]),
            CancellationToken.None);
        await service.GenerateBatchAsync(StaffActor(harness), secondBatch.BatchId, CancellationToken.None);
        await service.SubmitBatchAsync(StaffActor(harness), secondBatch.BatchId, CancellationToken.None);
        var paid = await service.ConfirmItemPaidAsync(
            StaffActor(harness), await ItemIdAsync(harness, secondBatch.BatchId),
            new ConfirmRefundBankExportItemRequest("UTR777001", null),
            CancellationToken.None);
        Assert.Equal(nameof(RefundBankExportItemStatus.Paid), paid.Status);

        // One reservation row, settled once, shared by both items: no double-pay.
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(
            RefundStatus.Succeeded,
            await harness.Db.Refunds.AsNoTracking().Select(x => x.Status).SingleAsync());
        Assert.Equal(200m, await harness.Db.Payments.AsNoTracking().Select(x => x.RefundedAmount).SingleAsync());
        var links = await harness.Db.RefundBankExportItemReservations.AsNoTracking().ToListAsync();
        Assert.Equal(2, links.Count);
        Assert.Equal(links[0].RefundId, links[1].RefundId);
        Assert.Equal(
            RefundBankExportBatchStatus.Closed,
            await harness.Db.RefundBankExportBatches.AsNoTracking()
                .Where(x => x.PublicId == firstBatchId).Select(x => x.Status).SingleAsync());
    }

    // 12h. A voided item's ticket can be reclaimed; an active claim still
    // blocks a second batch.
    [Fact]
    public async Task VoidThenReclaim_PaysExactlyOnce()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "g1");
        await SeedExportPrerequisitesAsync(harness);

        var service = CreateExportService(harness);
        var firstBatch = await service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest([ticketId]),
            CancellationToken.None);

        // Active claim blocks a second batch for the same ticket.
        await Assert.ThrowsAsync<ConflictException>(() => service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest([ticketId]),
            CancellationToken.None));

        await service.VoidItemAsync(
            StaffActor(harness), await ItemIdAsync(harness, firstBatch.BatchId),
            new RefundBankExportItemDispositionRequest("Duplicate claim", null),
            CancellationToken.None);

        var secondBatch = await service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest([ticketId]),
            CancellationToken.None);
        await service.GenerateBatchAsync(StaffActor(harness), secondBatch.BatchId, CancellationToken.None);
        await service.SubmitBatchAsync(StaffActor(harness), secondBatch.BatchId, CancellationToken.None);
        var paid = await service.ConfirmItemPaidAsync(
            StaffActor(harness), await ItemIdAsync(harness, secondBatch.BatchId),
            new ConfirmRefundBankExportItemRequest("UTR777002", null),
            CancellationToken.None);
        Assert.Equal(nameof(RefundBankExportItemStatus.Paid), paid.Status);

        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(200m, await harness.Db.Payments.AsNoTracking().Select(x => x.RefundedAmount).SingleAsync());
        Assert.Equal(
            RefundReplacementStatus.Completed,
            await harness.Db.RefundReplacementRequests.AsNoTracking().Select(x => x.Status).SingleAsync());
    }

    private static async Task<IReadOnlyList<Guid>> BatchItemIdsAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        Guid batchId)
    {
        var batchDbId = await harness.Db.RefundBankExportBatches.AsNoTracking()
            .Where(x => x.PublicId == batchId)
            .Select(x => x.Id)
            .SingleAsync();
        return await harness.Db.RefundBankExportItems.AsNoTracking()
            .Where(x => x.BatchId == batchDbId)
            .OrderBy(x => x.Id)
            .Select(x => x.PublicId)
            .ToListAsync();
    }

    // 13. Any safe release restores the reservation exactly once.
    [Fact]
    public async Task SafeRelease_RestoresReservationExactlyOnce()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");
        await SeedExportPrerequisitesAsync(harness);

        var service = CreateExportService(harness);
        var batch = await service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest([ticketId]),
            CancellationToken.None);
        var itemId = await ItemIdAsync(harness, batch.BatchId);

        var released = await service.ReleaseItemReservationAsync(
            StaffActor(harness), itemId,
            new RefundBankExportItemDispositionRequest("Wrong beneficiary account", null),
            CancellationToken.None);
        Assert.Equal(nameof(RefundBankExportItemStatus.Released), released.Status);

        Assert.Equal(RefundStatus.Failed, (await harness.Db.Refunds.AsNoTracking().SingleAsync()).Status);
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentStatus.Success, payment.Status);
        Assert.Equal(0m, payment.RefundedAmount);
        Assert.Equal(
            RefundReplacementStatus.Rejected,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(
            RefundBankExportBatchStatus.Closed,
            (await harness.Db.RefundBankExportBatches.AsNoTracking().SingleAsync()).Status);

        // A second release is a clean conflict: nothing moves again.
        await Assert.ThrowsAsync<ConflictException>(() => service.ReleaseItemReservationAsync(
            StaffActor(harness), itemId,
            new RefundBankExportItemDispositionRequest("Wrong beneficiary account", null),
            CancellationToken.None));
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(RefundStatus.Failed, (await harness.Db.Refunds.AsNoTracking().SingleAsync()).Status);
    }

    // 13b. An explicitly released ticket is terminal: it leaves the
    // eligible set and can never be reclaimed into another batch.
    [Fact]
    public async Task ReleaseThenReclaim_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "r1");
        await SeedExportPrerequisitesAsync(harness);

        var service = CreateExportService(harness);
        var batch = await service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest([ticketId]),
            CancellationToken.None);
        var itemId = await ItemIdAsync(harness, batch.BatchId);

        var released = await service.ReleaseItemReservationAsync(
            StaffActor(harness), itemId,
            new RefundBankExportItemDispositionRequest("Wrong beneficiary account", null),
            CancellationToken.None);
        Assert.Equal(nameof(RefundBankExportItemStatus.Released), released.Status);

        // Terminal ticket, released reservation, balance restored exactly once.
        Assert.Equal(
            RefundReplacementStatus.Rejected,
            await harness.Db.RefundReplacementRequests.AsNoTracking().Select(x => x.Status).SingleAsync());
        Assert.Equal(
            RefundStatus.Failed,
            await harness.Db.Refunds.AsNoTracking().Select(x => x.Status).SingleAsync());
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentStatus.Success, payment.Status);
        Assert.Equal(0m, payment.RefundedAmount);

        // Absent from the eligible set.
        Assert.Empty(await service.ListEligibleAsync(StaffActor(harness), CancellationToken.None));

        // Reclaiming the same ticket in a subsequent batch is rejected:
        // the reservation is no longer pending.
        await Assert.ThrowsAsync<ConflictException>(() => service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest([ticketId]),
            CancellationToken.None));
        Assert.Equal(
            RefundReplacementStatus.Rejected,
            await harness.Db.RefundReplacementRequests.AsNoTracking().Select(x => x.Status).SingleAsync());
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());

        // Repeating the release is a clean conflict: balances, reservation
        // state and financial effects do not change a second time.
        await Assert.ThrowsAsync<ConflictException>(() => service.ReleaseItemReservationAsync(
            StaffActor(harness), itemId,
            new RefundBankExportItemDispositionRequest("Wrong beneficiary account", null),
            CancellationToken.None));
        Assert.Equal(
            RefundStatus.Failed,
            await harness.Db.Refunds.AsNoTracking().Select(x => x.Status).SingleAsync());
        payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentStatus.Success, payment.Status);
        Assert.Equal(0m, payment.RefundedAmount);
        Assert.Equal(
            nameof(RefundBankExportItemStatus.Released),
            await harness.Db.RefundBankExportItems.AsNoTracking().Select(x => x.Status.ToString()).SingleAsync());
    }

    [Fact]
    public async Task ReleaseSubmittedItem_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m, "1");
        var batchId = await ClaimAndGenerateAsync(harness, ticketId);

        var service = CreateExportService(harness);
        await service.SubmitBatchAsync(StaffActor(harness), batchId, CancellationToken.None);

        var releaseItemId = await ItemIdAsync(harness, batchId);
        await Assert.ThrowsAsync<ConflictException>(() => service.ReleaseItemReservationAsync(
            StaffActor(harness), releaseItemId,
            new RefundBankExportItemDispositionRequest("Changed mind", null),
            CancellationToken.None));
    }

    // 14. Legacy and wallet-settled tickets retain existing behavior.
    [Fact]
    public async Task LegacyTicket_CompletesThroughWalletAndStaysOutOfExport()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness, "1", preApprove: true);
        await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet, "1");

        var service = CreateRefundService(harness);
        var completed = await service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("legacy"),
            CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);
        Assert.Equal(200m, (await harness.Db.WalletTransactions.AsNoTracking().SingleAsync()).Amount);

        var exports = CreateExportService(harness);
        Assert.Empty(await exports.ListEligibleAsync(StaffActor(harness), CancellationToken.None));
    }

    private static async Task<DeliveryServiceTests.DeliveryHarness> CreateHarnessAsync()
    {
        var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await harness.Db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
        return harness;
    }

    private static RefundReplacementActor StaffActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Manager.Id, new HashSet<long> { harness.Branch.Id }, false);

    private static RefundReplacementService CreateRefundService(
        DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        harness.ConfigurationService,
        new NumberSeriesService(harness.Db, harness.TimeProvider),
        harness.ProofStorage,
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider),
        Options.Create(new PaymentOptions { Currency = "INR" }),
        new BankDetailProtector(SharedDataProtection),
        new ManualBankBeneficiaryProtector(SharedDataProtection));

    private static RefundBankExportService CreateExportService(
        DeliveryServiceTests.DeliveryHarness harness) =>
        CreateExportServiceFor(harness.Db, harness);

    private static RefundBankExportService CreateExportServiceFor(
        DoodhDirectDbContext db,
        DeliveryServiceTests.DeliveryHarness harness) => new(
        db,
        harness.TimeProvider,
        new NumberSeriesService(db, harness.TimeProvider),
        CreateRefundServiceFor(db, harness),
        new CompanyBankService(db, harness.TimeProvider, new CompanyBankDetailProtector(SharedDataProtection)),
        new ManualBankBeneficiaryProtector(SharedDataProtection),
        new CompanyBankDetailProtector(SharedDataProtection));

    private static RefundReplacementService CreateRefundServiceFor(
        DoodhDirectDbContext db,
        DeliveryServiceTests.DeliveryHarness harness) => new(
        db,
        harness.TimeProvider,
        harness.ConfigurationService,
        new NumberSeriesService(db, harness.TimeProvider),
        harness.ProofStorage,
        new TestNotificationEventWriter(db, harness.TimeProvider),
        Options.Create(new PaymentOptions { Currency = "INR" }),
        new BankDetailProtector(SharedDataProtection),
        new ManualBankBeneficiaryProtector(SharedDataProtection));

    private static readonly EphemeralDataProtectionProvider SharedDataProtection = new();

    private static async Task SeedExportPrerequisitesAsync(
        DeliveryServiceTests.DeliveryHarness harness)
    {
        if (!await harness.Db.CompanyBankDetails.AnyAsync())
        {
            var protector = new CompanyBankDetailProtector(SharedDataProtection);
            var detail = new CompanyBankDetail(
                CompanyHolder,
                CompanyBank,
                protector.Protect(CompanyAccount),
                "9012",
                CompanyIfsc);
            detail.SetCreated(harness.TimeProvider.Now);
            harness.Db.CompanyBankDetails.Add(detail);
        }

        if (!await harness.Db.NumberSeries.AnyAsync(x => x.Code == RefundBankExportService.NumberSeriesCode))
        {
            harness.Db.NumberSeries.Add(new NumberSeries(
                RefundBankExportService.NumberSeriesCode,
                "Manual-bank refund export batch numbers",
                "RBE/{NUMBER:000000}",
                1,
                1,
                NumberSeriesResetPolicy.Never));
        }

        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
    }

    private static async Task SeedBankDetailAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        string holder,
        string bank,
        string account,
        string last4,
        string ifsc)
    {
        if (await harness.Db.CustomerBankDetails.AnyAsync(x => x.UserId == harness.Customer.Id))
        {
            return;
        }

        var protector = new BankDetailProtector(SharedDataProtection);
        harness.Db.CustomerBankDetails.Add(new CustomerBankDetail(
            harness.Customer.Id,
            holder,
            bank,
            protector.Protect(account),
            last4,
            ifsc));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
    }

    private static async Task<Guid> SeedManualBankTicketAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        decimal paymentAmount,
        string suffix,
        string? holderName = null)
    {
        var ticketId = await SeedOneTimeTicketAsync(harness, suffix);
        await SeedOrderPaymentAsync(harness, ticketId, paymentAmount, PaymentMethod.Wallet, suffix);
        await SeedBankDetailAsync(
            harness,
            holderName ?? CustomerHolder,
            CustomerBank,
            CustomerAccount,
            CustomerLast4,
            CustomerIfsc);
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: false, WaiverReason: "Goodwill",
                SettlementMethod: RefundSettlementMethod.ManualBankTransfer),
            CancellationToken.None);
        harness.Db.ChangeTracker.Clear();
        return ticketId;
    }

    private static async Task<Guid> SeedOneTimeTicketAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        string suffix,
        bool preApprove = false)
    {
        var db = harness.Db;
        var now = harness.TimeProvider.Now;
        var order = new Order(
            harness.Customer.Id,
            null,
            harness.Branch.Id,
            $"one-time-order-{suffix}-{Guid.NewGuid():N}",
            $"ORD-ONETIME-{suffix}",
            200m,
            0m,
            harness.Branch.Code,
            harness.Branch.Name,
            "Home",
            "1 Main Road",
            null,
            "Central",
            "Bengaluru",
            "Karnataka",
            "560001",
            null,
            null,
            "Customer",
            "9999999999",
            12.9716m,
            77.5946m);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var delivery = Delivery.ForOrder(
            order.Id,
            harness.Customer.Id,
            harness.Branch.Id,
            DateOnly.FromDateTime(now),
            $"REF-ONETIME-{suffix}",
            "Customer",
            "9999999999",
            "Home, 1 Main Road, Central, Bengaluru",
            null,
            12.9716m,
            77.5946m);
        delivery.AssignDeliveryNumber($"DLV-ONETIME-{suffix}");
        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync();

        delivery.Assign(harness.Staff.Id, harness.Staff.Id, now, null);
        delivery.PickUp(harness.Staff.Id, now, null);
        delivery.Start(harness.Staff.Id, now);
        delivery.Arrive(harness.Staff.Id, now);
        delivery.RecordOtpVerified(harness.Staff.Id, now);
        delivery.Complete(harness.Staff.Id, now, null);
        await db.SaveChangesAsync();

        var ticket = new RefundReplacementRequest(
            order.Id,
            delivery.Id,
            harness.Customer.Id,
            harness.Branch.Id,
            null,
            RefundReplacementType.Refund,
            RefundReplacementSource.CustomerPostDelivery,
            "Item was not satisfactory",
            null,
            now,
            now.AddHours(48));
        ticket.AssignRequestNumber($"RR-ONETIME-{suffix}");
        if (preApprove)
        {
            ticket.RecordDeductionDecision(false, 0m, "Legacy waiver recorded before Stage 3.");
            ticket.Approve(harness.Manager.Id, now, "Legacy approval.");
        }

        db.RefundReplacementRequests.Add(ticket);
        await db.SaveChangesAsync();
        ticket.AddImage(new RefundReplacementImage(
            ticket.Id,
            $"test/one-time/{suffix}/proof.jpg",
            "proof.jpg",
            "image/jpeg",
            5,
            harness.Customer.Id,
            now));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return ticket.PublicId;
    }

    private static async Task SeedOrderPaymentAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        Guid ticketId,
        decimal amount,
        PaymentMethod method,
        string keySuffix)
    {
        var db = harness.Db;
        var now = harness.TimeProvider.Now;
        var ticket = await db.RefundReplacementRequests.AsNoTracking().SingleAsync(x => x.PublicId == ticketId);
        var payment = new Payment(
            ticket.OrderId!.Value,
            harness.Customer.Id,
            method,
            amount,
            "INR",
            $"one-time-pay-{keySuffix}-{Guid.NewGuid():N}",
            now.AddMinutes(15));
        payment.Succeed(
            method == PaymentMethod.Wallet ? null : $"gw-seed-{keySuffix}",
            method == PaymentMethod.Wallet ? "WALLET" : "captured",
            now);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<Guid> ClaimAndGenerateAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        Guid ticketId)
    {
        await SeedExportPrerequisitesAsync(harness);
        var service = CreateExportService(harness);
        var batch = await service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest([ticketId]),
            CancellationToken.None);
        await service.GenerateBatchAsync(StaffActor(harness), batch.BatchId, CancellationToken.None);
        return batch.BatchId;
    }

    // 12i. Claiming issues a bounded number of SELECTs regardless of item
    // count: no per-ticket reads in the claim path. INSERT..RETURNING row
    // writes are excluded — one row per item is inherent to the write.
    [Fact]
    public async Task CreateBatch_ReadQueryCountIsBounded()
    {
        var two = await CreateCountedBatchAsync(2);
        var four = await CreateCountedBatchAsync(4);
        Assert.Equal(two.ReaderCommands, four.ReaderCommands);
    }

    private static async Task<CommandCountingInterceptor> CreateCountedBatchAsync(int ticketCount)
    {
        await using var harness = await CreateHarnessAsync();
        var ticketIds = new List<Guid>(ticketCount);
        for (var i = 0; i < ticketCount; i++)
        {
            ticketIds.Add(await SeedManualBankTicketAsync(harness, 200m, $"n{i}"));
        }

        await SeedExportPrerequisitesAsync(harness);

        var counting = new CommandCountingInterceptor();
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseSqlite(harness.Db.Database.GetConnectionString(), sqlite => sqlite.CommandTimeout(10))
            .AddInterceptors(counting)
            .Options;
        await using var db = new DoodhDirectDbContext(options);
        var service = CreateExportServiceFor(db, harness);
        await service.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest(ticketIds),
            CancellationToken.None);
        return counting;
    }

    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        public int ReaderCommands;

        public override DbDataReader ReaderExecuted(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            CountSelect(command);
            return base.ReaderExecuted(command, eventData, result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            CountSelect(command);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        private void CountSelect(DbCommand command)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                ReaderCommands++;
            }
        }
    }

    private static async Task<Guid> ItemIdAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        Guid batchId) =>
        (await BatchItemIdsAsync(harness, batchId)).Single();
}

/// <summary>
/// Stage 3 permission proof: every export endpoint requires
/// <c>REFUNDS.BANK_EXPORT</c>. Support staff (refund approval permission
/// but no export grant) are denied; accountants are admitted. Auth runs
/// before any data access, so denials need no seeded tickets.
/// </summary>
public sealed class RefundBankExportHttpTests
{
    private static int _counter;

    [Fact]
    public async Task Support_WithoutExportGrant_IsDeniedEverywhere()
    {
        await using var factory = new FoundationApiFactory();
        await using var setup = await ExportUserSetupAsync(
            factory, [AuthorizationCodes.RefundReplacementManageBranch]);
        using var client = BearerClient(factory, setup.Token);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/v1/refund-bank-exports/eligible", CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/v1/refund-bank-exports/batches", CancellationToken.None)).StatusCode);
        using var post = new StringContent("{}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync("/api/v1/refund-bank-exports/batches", post, CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync(
                $"/api/v1/refund-bank-exports/batches/{Guid.NewGuid():D}/download",
                CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_IsUnauthorized()
    {
        await using var factory = new FoundationApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/refund-bank-exports/eligible", CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Accountant_WithExportGrant_ReachesServiceLayer()
    {
        await using var factory = new FoundationApiFactory();
        await using var setup = await ExportUserSetupAsync(
            factory, [AuthorizationCodes.RefundBankExport]);
        using var client = BearerClient(factory, setup.Token);

        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/api/v1/refund-bank-exports/eligible", CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/api/v1/refund-bank-exports/batches", CancellationToken.None)).StatusCode);

        // Empty ticket list fails service validation (400), proving the
        // request passed authorization and reached the export workflow.
        using var empty = new StringContent(
            "{\"ticketIds\":[]}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync("/api/v1/refund-bank-exports/batches", empty, CancellationToken.None)).StatusCode);

        // Unknown batch fails lookup (404), not authorization.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync(
                $"/api/v1/refund-bank-exports/batches/{Guid.NewGuid():D}/download",
                CancellationToken.None)).StatusCode);
    }

    private static HttpClient BearerClient(FoundationApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private sealed record ExportSetup(FoundationApiFactory Factory, IAsyncDisposable Scope, long UserId, string Token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Scope.DisposeAsync();
    }

    private static async Task<ExportSetup> ExportUserSetupAsync(
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

            var branch = new Branch($"EXP{n}", $"Export Branch {n}", "Bengaluru", "Karnataka", 12.9m, 77.5m);
            db.Branches.Add(branch);
            var role = new Role($"ExportFinance{n}", $"Export Finance {n}");
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            foreach (var code in permissions.Distinct())
            {
                var permission = new Permission(code, code);
                db.Permissions.Add(permission);
                await db.SaveChangesAsync();
                db.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
            }

            var user = new User(UserType.Employee);
            user.SetProfile($"Export Finance {n}");
            user.SetContact($"+918{n:D9}".Substring(0, 13), null);
            user.AssignRole(role, branch.Id);
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var now = time.Now;
            var authUser = await user.ToAuthUserResultAsync(db, CancellationToken.None);
            var token = jwt.Create(
                user,
                new UserSession(user.Id, "export-device", null, null, null, null, now),
                authUser.Roles,
                authUser.Permissions,
                authUser.BranchIds,
                now,
                user.AuthorizationVersion,
                authUser.BranchHistoryIds).AccessToken;
            db.ChangeTracker.Clear();

            return new ExportSetup(factory, scope, user.Id, token);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }
}
