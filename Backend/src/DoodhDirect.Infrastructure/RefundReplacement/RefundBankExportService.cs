using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Application.Setup;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Reports;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// Stage 3: manual-bank refund export batches.
///
/// A batch claims approved <c>ManualBankTransfer</c> tickets and freezes
/// the beneficiary plus company source-account snapshots, so later
/// customer or company bank-detail edits cannot change a historical file.
/// Claiming reuses the Stage 2 payment-level reservations (pending
/// <c>Refund</c> rows) and never creates a second financial reservation or
/// debit: the reservation rows are the money, the batch is paperwork.
///
/// Money moves only through <see cref="ConfirmItemPaidAsync"/>, which
/// settles exactly once through the existing Stage 2 confirmation after the
/// real bank transfer is made. Generation, download and submission never
/// move money; voids never release reservations automatically.
///
/// Full account numbers exist only inside the downloaded CSV bytes served
/// to <c>REFUNDS.BANK_EXPORT</c> holders. Lists, results, audits and logs
/// carry last-4 masks only.
/// </summary>
public sealed class RefundBankExportService(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider,
    INumberSeriesService numberSeriesService,
    IRefundReplacementService refundReplacementService,
    ICompanyBankService companyBankService,
    ManualBankBeneficiaryProtector beneficiaryProtector,
    CompanyBankDetailProtector companyBankProtector) : IRefundBankExportService
{
    public const string NumberSeriesCode = "REFUNDBANKEXPORT";
    public const int MaxTicketsPerBatch = 200;

    public const string Disclaimer =
        "Generic bank-transfer file. This format has not been verified against any particular " +
        "bank's upload specification — confirm the layout with your bank before uploading.";

    public async Task<IReadOnlyList<RefundBankExportCandidateResult>> ListEligibleAsync(
        RefundReplacementActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var tickets = await dbContext.RefundReplacementRequests
            .AsNoTracking()
            .Include(x => x.Order)
            .Include(x => x.Delivery)
            .Include(x => x.Customer)
            .Where(x => x.Type == RefundReplacementType.Refund
                && x.Status == RefundReplacementStatus.Approved
                && x.SettlementMethod == RefundSettlementMethod.ManualBankTransfer
                && (actor.HasGlobalAccess || actor.BranchIds.Contains(x.BranchId)))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var ticketIds = tickets.Select(x => x.Id).ToList();
        var activeItems = await dbContext.RefundBankExportItems
            .AsNoTracking()
            .Where(x => ticketIds.Contains(x.TicketId)
                && (x.Status == RefundBankExportItemStatus.Claimed
                    || x.Status == RefundBankExportItemStatus.Generated
                    || x.Status == RefundBankExportItemStatus.Submitted))
            .Select(x => x.TicketId)
            .ToListAsync(cancellationToken);
        var claimed = new HashSet<long>(activeItems);

        var companyConfigured = await dbContext.CompanyBankDetails
            .AsNoTracking()
            .AnyAsync(cancellationToken);

        // One batched reservation fetch for all candidates instead of one
        // query per ticket; per-ticket row sets match FindTicketReservationsAsync.
        var reservationsByTicket = await ManualBankTransferReservation.FindReservationsByTicketsAsync(
            dbContext, tickets.Select(x => x.PublicId).ToList(), cancellationToken);

        var result = new List<RefundBankExportCandidateResult>();
        foreach (var ticket in tickets)
        {
            if (claimed.Contains(ticket.Id))
            {
                continue;
            }

            if (!TryBuildCandidate(ticket, reservationsByTicket[ticket.PublicId], companyConfigured, out var candidate))
            {
                continue;
            }

            // Employee-origin policy: an employee-originated ticket without a
            // verifiable administrator approval is never export-eligible.
            // Skipped (not failed) so one ineligible ticket cannot block the
            // finance list; claiming still fails explicitly in CreateBatchAsync.
            if (EmployeeOriginGuard.IsEmployeeOriginated(ticket.Customer.UserType)
                && (ticket.DecidedByUserId is null
                    || !await EmployeeOriginGuard.IsAdministratorTierAsync(
                        dbContext, ticket.DecidedByUserId.Value, cancellationToken)))
            {
                continue;
            }

            result.Add(candidate);
        }

        return result;
    }

    public async Task<RefundBankExportBatchResult> CreateBatchAsync(
        RefundReplacementActor actor,
        CreateRefundBankExportBatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        var ticketIds = request.TicketIds?.Distinct().ToList()
            ?? throw new ValidationAppException("At least one ticket is required to create an export batch.", "ticketIds");
        if (ticketIds.Count == 0)
        {
            throw new ValidationAppException("At least one ticket is required to create an export batch.", "ticketIds");
        }

        if (ticketIds.Count > MaxTicketsPerBatch)
        {
            throw new ValidationAppException(
                $"An export batch holds at most {MaxTicketsPerBatch} tickets.",
                "ticketIds");
        }

        // The company source account must be complete before anything is
        // frozen; the snapshot call audit-logs the resolution.
        var source = await companyBankService.GetSnapshotForExportAsync(actor.UserId, null, null, cancellationToken);

        // Number allocation is independent of the claiming transaction; a
        // failed claim may leave a gap, consistent with other series.
        var batchNumber = await numberSeriesService.GetNextNumberAsync(
            NumberSeriesCode, actor.UserId, cancellationToken);

        RefundBankExportBatch? batch = null;
        var ticketPublicIds = new Dictionary<long, Guid>(ticketIds.Count);
        await ExecuteSerializableAsync(async () =>
        {
            var created = new RefundBankExportBatch(
                batchNumber,
                actor.UserId,
                source.AccountHolderName,
                source.BankName,
                source.ProtectedAccountNumber,
                source.AccountLast4,
                source.Ifsc,
                timeProvider.Now);
            dbContext.RefundBankExportBatches.Add(created);
            await SaveGuardedAsync(cancellationToken);

            // Batched claim reads: one query each for tickets (with
            // Order/Delivery/Customer), reservations and active items,
            // instead of three per ticket. Every authoritative check below
            // still runs inside this serializable transaction, in request
            // order, so NotFound/Conflict precedence is unchanged.
            var ticketsByPublicId = await dbContext.RefundReplacementRequests
                .Include(x => x.Order)
                .Include(x => x.Delivery)
                .Include(x => x.Customer)
                .Where(x => ticketIds.Contains(x.PublicId))
                .ToDictionaryAsync(x => x.PublicId, cancellationToken);
            var reservationsByTicket = await ManualBankTransferReservation.FindReservationsByTicketsAsync(
                dbContext, ticketIds, cancellationToken);
            var activelyClaimed = new HashSet<long>(await dbContext.RefundBankExportItems
                .AsNoTracking()
                .Where(x => ticketsByPublicId.Values.Select(t => t.Id).Contains(x.TicketId)
                    && (x.Status == RefundBankExportItemStatus.Claimed
                        || x.Status == RefundBankExportItemStatus.Generated
                        || x.Status == RefundBankExportItemStatus.Submitted))
                .Select(x => x.TicketId)
                .ToListAsync(cancellationToken));

            foreach (var ticketId in ticketIds)
            {
                if (!ticketsByPublicId.TryGetValue(ticketId, out var ticket))
                {
                    throw new NotFoundException("The refund request was not found.");
                }

                EnsureStaffAccess(actor, ticket);
                var reservations = reservationsByTicket[ticket.PublicId];
                await RequireEligibleAsync(ticket, reservations, companyConfigured: true, forClaim: true, cancellationToken);
                ticketPublicIds[ticket.Id] = ticket.PublicId;

                if (activelyClaimed.Contains(ticket.Id))
                {
                    throw new ConflictException(
                        $"Ticket {ticket.RequestNumber} is already claimed by another active export batch.");
                }

                var item = new RefundBankExportItem(
                    created.Id,
                    ticket.Id,
                    ticket.RequestNumber,
                    ticket.Order?.OrderNumber,
                    DisplayName(ticket),
                    ticket.ManualBankReservedAmount!.Value,
                    ticket.ManualBankReservedCurrency!,
                    ticket.ManualBankHolderNameSnapshot!,
                    ticket.ManualBankNameSnapshot!,
                    ticket.ManualBankProtectedAccountNumber!,
                    ticket.ManualBankAccountLast4Snapshot!,
                    ticket.ManualBankIfscSnapshot!,
                    source.AccountHolderName,
                    source.BankName,
                    source.ProtectedAccountNumber,
                    source.AccountLast4,
                    source.Ifsc,
                    actor.UserId,
                    timeProvider.Now);
                dbContext.RefundBankExportItems.Add(item);
            }

            await SaveGuardedAsync(cancellationToken);

            var items = await dbContext.RefundBankExportItems
                .Where(x => x.BatchId == created.Id)
                .OrderBy(x => x.Id)
                .ToListAsync(cancellationToken);
            // Reservation rows were already fetched above; linking reuses
            // them instead of re-querying one ticket at a time.
            foreach (var item in items)
            {
                foreach (var reservation in reservationsByTicket[ticketPublicIds[item.TicketId]])
                {
                    dbContext.RefundBankExportItemReservations.Add(
                        new RefundBankExportItemReservation(item.Id, reservation.Id, reservation.Amount));
                }
            }

            AddAudit(
                actor.UserId,
                "REFUND_BANK_EXPORT.BATCH_CLAIMED",
                nameof(RefundBankExportBatch),
                created.BatchNumber,
                new
                {
                    Source = MaskedSource(source.AccountHolderName, source.BankName, source.AccountLast4, source.Ifsc),
                    Tickets = items.Select(x => x.RequestNumber).ToList(),
                    TotalAmount = items.Sum(x => x.Amount)
                },
                timeProvider.Now);
            await SaveGuardedAsync(cancellationToken);

            batch = created;
        }, cancellationToken);

        return await LoadBatchResultAsync(batch!.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<RefundBankExportBatchResult>> ListBatchesAsync(
        RefundReplacementActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        // Finance-wide view: batches routinely span branches, so no branch
        // filter is applied. Visibility is governed by REFUNDS.BANK_EXPORT.
        // Four queries total regardless of batch count: batches, their
        // items, the linked ticket ids, and the reservation links.
        var batches = await dbContext.RefundBankExportBatches
            .AsNoTracking()
            .OrderByDescending(x => x.Id)
            .ToListAsync(cancellationToken);
        if (batches.Count == 0)
        {
            return [];
        }

        var itemsByBatch = await LoadItemsByBatchAsync(
            batches.Select(x => x.Id).ToList(), cancellationToken);
        var allItems = itemsByBatch.Values.SelectMany(x => x).ToList();
        var ticketMap = await LoadTicketMapAsync(
            allItems.Select(x => x.TicketId).Distinct().ToList(), cancellationToken);
        var linksByItem = await LoadLinksByItemAsync(
            allItems.Select(x => x.Id).ToList(), cancellationToken);

        var result = new List<RefundBankExportBatchResult>(batches.Count);
        foreach (var batch in batches)
        {
            var batchItems = itemsByBatch.TryGetValue(batch.Id, out var batchItemList)
                ? batchItemList
                : [];
            result.Add(BuildBatchResult(batch, batchItems, ticketMap, linksByItem));
        }

        return result;
    }

    public async Task<RefundBankExportBatchResult> GetBatchAsync(
        RefundReplacementActor actor,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var batch = await FindBatchAsync(batchId, cancellationToken);
        return await LoadBatchResultAsync(batch.Id, cancellationToken);
    }

    public async Task<RefundBankExportBatchResult> GenerateBatchAsync(
        RefundReplacementActor actor,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RefundBankExportBatch? batch = null;
        await ExecuteSerializableAsync(async () =>
        {
            batch = await FindBatchTrackedAsync(batchId, cancellationToken);
            var items = await BatchItemsTrackedAsync(batch.Id, cancellationToken);
            var claimable = items.Where(x => x.Status == RefundBankExportItemStatus.Claimed).ToList();
            if (claimable.Count == 0)
            {
                throw new ConflictException(
                    $"Batch {batch.BatchNumber} has no claimable items to generate.");
            }

            var now = timeProvider.Now;
            Mutate(() => batch.MarkGenerated(now));
            foreach (var item in claimable)
            {
                Mutate(() => item.MarkGenerated(now));
            }

            AddAudit(
                actor.UserId,
                "REFUND_BANK_EXPORT.BATCH_GENERATED",
                nameof(RefundBankExportBatch),
                batch.BatchNumber,
                new { Items = items.Count, batch.GeneratedAt },
                timeProvider.Now);
            await SaveGuardedAsync(cancellationToken);
        }, cancellationToken);

        return await LoadBatchResultAsync(batch!.Id, cancellationToken);
    }

    public async Task<RefundBankExportDownloadResult> DownloadBatchAsync(
        RefundReplacementActor actor,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var batch = await dbContext.RefundBankExportBatches
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.PublicId == batchId, cancellationToken)
            ?? throw new NotFoundException("The export batch was not found.");

        if (batch.GeneratedAt is null || batch.Status == RefundBankExportBatchStatus.Voided)
        {
            throw new ConflictException(
                $"Batch {batch.BatchNumber} has no generated file to download.");
        }

        var items = await dbContext.RefundBankExportItems
            .AsNoTracking()
            .Where(x => x.BatchId == batch.Id)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        // The company snapshot call audit-logs the resolution. Rows are
        // built from the frozen per-item snapshots (never the live tables),
        // so later company or customer bank-detail edits cannot change a
        // historical file. Neither plaintext ever leaves this method except
        // inside the returned bytes.
        await companyBankService.GetSnapshotForExportAsync(actor.UserId, null, null, cancellationToken);

        // Only bank-payable rows travel to the bank file. Failed, Voided
        // and Released items were explicitly kept out of payment, and
        // Claimed rows were never generated (unreachable here — generation
        // advances every claimed row in one transaction — but never
        // silently payable if that invariant ever changes, hence the
        // explicit allow-list plus the empty-file guard below).
        var payable = items.Where(x =>
                x.Status == RefundBankExportItemStatus.Generated
                || x.Status == RefundBankExportItemStatus.Submitted
                || x.Status == RefundBankExportItemStatus.Paid)
            .ToList();
        if (payable.Count == 0)
        {
            throw new ConflictException(
                $"Batch {batch.BatchNumber} has no payable items to download.");
        }

        var rows = new List<IReadOnlyList<string?>>(payable.Count);
        foreach (var item in payable)
        {
            var beneficiaryAccount = UnprotectBeneficiary(item);
            var sourceAccount = UnprotectCompany(item.SourceProtectedAccountNumber, batch.BatchNumber);
            rows.Add([
                batch.BatchNumber,
                item.RequestNumber,
                item.OrderNumber ?? string.Empty,
                item.CustomerDisplayName,
                item.BeneficiaryAccountHolderName,
                item.BeneficiaryBankName,
                beneficiaryAccount,
                item.BeneficiaryIfsc,
                item.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                item.Currency,
                item.SourceAccountHolderName,
                item.SourceBankName,
                sourceAccount,
                item.SourceIfsc,
                $"Refund {item.RequestNumber} via batch {batch.BatchNumber}",
                batch.GeneratedAt.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture)
            ]);
        }

        var content = ReportTabularExporter.CsvTable(
            [
                "BatchNumber", "RefundRequestNumber", "OrderNumber", "CustomerName",
                "BeneficiaryAccountHolder", "BeneficiaryBankName", "BeneficiaryAccountNumber", "BeneficiaryIfsc",
                "RefundAmount", "Currency",
                "SourceAccountHolder", "SourceBankName", "SourceAccountNumber", "SourceIfsc",
                "Narration", "ExportedAt"
            ],
            rows);

        dbContext.AddAuditLog(new AuditLog(
            actor.UserId,
            "REFUND_BANK_EXPORT.BATCH_DOWNLOADED",
            nameof(RefundBankExportBatch),
            batch.BatchNumber,
            null,
            JsonSerializer.Serialize(new
            {
                Items = payable.Count,
                TotalAmount = payable.Sum(x => x.Amount),
                batch.GeneratedAt
            }),
            null,
            null,
            $"Export file downloaded for batch {batch.BatchNumber} ({payable.Count} items).",
            timeProvider.Now));
        await dbContext.SaveChangesAsync(cancellationToken);

        return new RefundBankExportDownloadResult(
            $"refund-bank-export-{batch.BatchNumber.Replace("/", "-", StringComparison.Ordinal)}.csv",
            content,
            Disclaimer);
    }

    public async Task<RefundBankExportBatchResult> SubmitBatchAsync(
        RefundReplacementActor actor,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RefundBankExportBatch? batch = null;
        await ExecuteSerializableAsync(async () =>
        {
            batch = await FindBatchTrackedAsync(batchId, cancellationToken);
            var items = await BatchItemsTrackedAsync(batch.Id, cancellationToken);
            // Terminal dispositions (Paid through confirmation, Voided, or
            // Released) never travel to the bank, so they no longer block
            // submission; only un-generated Claimed rows do — submitting a
            // half-generated file would silently drop refunds.
            if (items.Any(x => x.Status == RefundBankExportItemStatus.Claimed))
            {
                throw new ConflictException(
                    $"Batch {batch.BatchNumber} can only be submitted when every claimed item is generated or explicitly dispositioned.");
            }

            var submittable = items.Where(x => x.Status == RefundBankExportItemStatus.Generated).ToList();
            if (submittable.Count == 0)
            {
                throw new ConflictException(
                    $"Batch {batch.BatchNumber} has no generated items to submit.");
            }

            var now = timeProvider.Now;
            Mutate(() => batch.MarkSubmitted(actor.UserId, now));
            foreach (var item in submittable)
            {
                Mutate(() => item.MarkSubmitted(now));
            }

            AddAudit(
                actor.UserId,
                "REFUND_BANK_EXPORT.BATCH_SUBMITTED",
                nameof(RefundBankExportBatch),
                batch.BatchNumber,
                new { Items = items.Count, SubmittedItems = submittable.Count, batch.SubmittedByUserId, batch.SubmittedAt },
                timeProvider.Now);
            await SaveGuardedAsync(cancellationToken);
        }, cancellationToken);

        return await LoadBatchResultAsync(batch!.Id, cancellationToken);
    }

    public async Task<RefundBankExportBatchResult> VoidBatchAsync(
        RefundReplacementActor actor,
        Guid batchId,
        RefundBankExportItemDispositionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        var reason = ValidateDispositionReason(request.Reason, "reason");
        RefundBankExportBatch? batch = null;
        await ExecuteSerializableAsync(async () =>
        {
            batch = await FindBatchTrackedAsync(batchId, cancellationToken);
            var items = await BatchItemsTrackedAsync(batch.Id, cancellationToken);
            if (items.Any(x => x.Status is RefundBankExportItemStatus.Submitted or RefundBankExportItemStatus.Paid))
            {
                throw new ConflictException(
                    $"Batch {batch.BatchNumber} cannot be voided while an item is submitted or paid: record per-item dispositions first.");
            }

            var now = timeProvider.Now;
            foreach (var item in items.Where(x =>
                x.Status is RefundBankExportItemStatus.Claimed or RefundBankExportItemStatus.Generated))
            {
                Mutate(() => item.Void(actor.UserId, reason, now));
            }

            Mutate(() => batch.Void(actor.UserId, reason, now));
            AddAudit(
                actor.UserId,
                "REFUND_BANK_EXPORT.BATCH_VOIDED",
                nameof(RefundBankExportBatch),
                batch.BatchNumber,
                new { batch.VoidReason, batch.VoidedByUserId, Items = items.Count },
                timeProvider.Now);
            await SaveGuardedAsync(cancellationToken);
        }, cancellationToken);

        return await LoadBatchResultAsync(batch!.Id, cancellationToken);
    }

    public async Task<RefundBankExportItemResult> ConfirmItemPaidAsync(
        RefundReplacementActor actor,
        Guid itemId,
        ConfirmRefundBankExportItemRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        var transferReference = ValidateTransferReference(request.TransferReference);

        var probe = await LoadItemResultAsync(await FindItemIdAsync(itemId, cancellationToken), cancellationToken);
        if (probe.Status is not (nameof(RefundBankExportItemStatus.Generated) or nameof(RefundBankExportItemStatus.Submitted)))
        {
            throw new ConflictException(
                $"Export item {probe.RequestNumber} cannot be confirmed paid from status {probe.Status}: only a generated or submitted item can be confirmed.");
        }

        // Settles the reservation and completes the ticket exactly once
        // through the Stage 2 service (idempotent replay when already
        // completed). The Stage 2 gate re-checks the export linkage inside
        // its own transaction, so a void racing this confirmation aborts
        // before any money moves.
        await refundReplacementService.ConfirmManualBankTransferAsync(
            actor,
            probe.TicketId,
            new ConfirmManualBankTransferRequest(transferReference, request.Remarks),
            cancellationToken);

        RefundBankExportItem? item = null;
        await ExecuteSerializableAsync(async () =>
        {
            item = await FindItemTrackedAsync(itemId, cancellationToken);
            if (item.Status == RefundBankExportItemStatus.Paid)
            {
                if (!string.Equals(item.TransferReference, transferReference, StringComparison.Ordinal))
                {
                    throw new ConflictException(
                        $"Export item {item.RequestNumber} is already paid under a different transfer reference.");
                }

                return;
            }

            if (item.Status is not RefundBankExportItemStatus.Generated and not RefundBankExportItemStatus.Submitted)
            {
                throw new ConflictException(
                    $"Export item {item.RequestNumber} changed while confirming (status {item.Status}). Refresh and reconcile before retrying.");
            }

            await RequireTicketSettledAsync(item.TicketId, cancellationToken);
            var now = timeProvider.Now;
            Mutate(() => item.MarkPaid(actor.UserId, transferReference, now));
            AddAudit(
                actor.UserId,
                "REFUND_BANK_EXPORT.ITEM_PAID",
                nameof(RefundBankExportItem),
                item.RequestNumber,
                new
                {
                    item.Amount,
                    item.Currency,
                    Destination = MaskedAccount(item.BeneficiaryBankName, item.BeneficiaryAccountLast4),
                    item.TransferReference,
                    item.PaidByUserId
                },
                timeProvider.Now);
            await SaveGuardedAsync(cancellationToken);
            await AutoCloseBatchAsync(item.BatchId, cancellationToken);
        }, cancellationToken);

        return await LoadItemResultAsync(item!.Id, cancellationToken);
    }

    public async Task<RefundBankExportItemResult> FailItemAsync(
        RefundReplacementActor actor,
        Guid itemId,
        RefundBankExportItemDispositionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        var reason = ValidateDispositionReason(request.Reason, "reason");
        RefundBankExportItem? item = null;
        await ExecuteSerializableAsync(async () =>
        {
            item = await FindItemTrackedAsync(itemId, cancellationToken);
            var now = timeProvider.Now;
            Mutate(() => item.MarkFailed(actor.UserId, reason, now));
            AddAudit(
                actor.UserId,
                "REFUND_BANK_EXPORT.ITEM_FAILED",
                nameof(RefundBankExportItem),
                item.RequestNumber,
                new { item.FailureReason, item.FailedByUserId },
                timeProvider.Now);
            await SaveGuardedAsync(cancellationToken);
            await AutoCloseBatchAsync(item.BatchId, cancellationToken);
        }, cancellationToken);

        return await LoadItemResultAsync(item!.Id, cancellationToken);
    }

    public async Task<RefundBankExportItemResult> VoidItemAsync(
        RefundReplacementActor actor,
        Guid itemId,
        RefundBankExportItemDispositionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        var reason = ValidateDispositionReason(request.Reason, "reason");
        RefundBankExportItem? item = null;
        await ExecuteSerializableAsync(async () =>
        {
            item = await FindItemTrackedAsync(itemId, cancellationToken);
            var now = timeProvider.Now;
            Mutate(() => item.Void(actor.UserId, reason, now));
            AddAudit(
                actor.UserId,
                "REFUND_BANK_EXPORT.ITEM_VOIDED",
                nameof(RefundBankExportItem),
                item.RequestNumber,
                new { item.VoidReason, item.VoidedByUserId },
                timeProvider.Now);
            await SaveGuardedAsync(cancellationToken);
            await AutoCloseBatchAsync(item.BatchId, cancellationToken);
        }, cancellationToken);

        return await LoadItemResultAsync(item!.Id, cancellationToken);
    }

    public async Task<RefundBankExportItemResult> ReleaseItemReservationAsync(
        RefundReplacementActor actor,
        Guid itemId,
        RefundBankExportItemDispositionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        var reason = ValidateDispositionReason(request.Reason, "reason");

        var probe = await LoadItemResultAsync(await FindItemIdAsync(itemId, cancellationToken), cancellationToken);
        if (probe.Status is not (nameof(RefundBankExportItemStatus.Claimed) or nameof(RefundBankExportItemStatus.Generated)))
        {
            throw new ConflictException(
                $"Export item {probe.RequestNumber} cannot be released from status {probe.Status}: only an item that never reached the bank can be released.");
        }

        // Executes the Stage 2 release exactly once: fails the pending
        // reservation rows and moves the ticket to terminal Rejected. A
        // second attempt fails there as a clean conflict.
        await refundReplacementService.ReleaseManualBankTransferAsync(
            actor,
            probe.TicketId,
            new ReleaseManualBankTransferRequest(reason, request.Remarks),
            cancellationToken);

        RefundBankExportItem? item = null;
        await ExecuteSerializableAsync(async () =>
        {
            item = await FindItemTrackedAsync(itemId, cancellationToken);
            if (item.Status == RefundBankExportItemStatus.Released)
            {
                return;
            }

            if (item.Status is not RefundBankExportItemStatus.Claimed and not RefundBankExportItemStatus.Generated)
            {
                throw new ConflictException(
                    $"Export item {item.RequestNumber} changed while releasing (status {item.Status}). Refresh and reconcile before retrying.");
            }

            await RequireTicketReleasedAsync(item.TicketId, cancellationToken);
            var now = timeProvider.Now;
            Mutate(() => item.MarkReleased(actor.UserId, reason, now));
            AddAudit(
                actor.UserId,
                "REFUND_BANK_EXPORT.ITEM_RELEASED",
                nameof(RefundBankExportItem),
                item.RequestNumber,
                new { item.ReleaseReason, item.ReleasedByUserId },
                timeProvider.Now);
            await SaveGuardedAsync(cancellationToken);
            await AutoCloseBatchAsync(item.BatchId, cancellationToken);
        }, cancellationToken);

        return await LoadItemResultAsync(item!.Id, cancellationToken);
    }

    private async Task RequireTicketSettledAsync(long ticketId, CancellationToken cancellationToken)
    {
        var ticket = await dbContext.RefundReplacementRequests
            .AsNoTracking()
            .SingleAsync(x => x.Id == ticketId, cancellationToken);
        if (ticket.Status != RefundReplacementStatus.Completed)
        {
            throw new ConflictException(
                $"Export item {ticket.RequestNumber} cannot be marked paid: the linked ticket is {ticket.Status}, not Completed.");
        }

        var reservations = await ManualBankTransferReservation.FindTicketReservationsAsync(
            dbContext, ticket.PublicId, cancellationToken);
        if (reservations.Count == 0 || reservations.Any(x => x.Status != RefundStatus.Succeeded))
        {
            throw new ConflictException(
                $"Export item {ticket.RequestNumber} cannot be marked paid: its reservations did not settle.");
        }
    }

    private async Task RequireTicketReleasedAsync(long ticketId, CancellationToken cancellationToken)
    {
        var ticket = await dbContext.RefundReplacementRequests
            .AsNoTracking()
            .SingleAsync(x => x.Id == ticketId, cancellationToken);
        if (ticket.Status != RefundReplacementStatus.Rejected)
        {
            throw new ConflictException(
                $"Export item {ticket.RequestNumber} cannot be marked released: the linked ticket is {ticket.Status}, not Rejected.");
        }

        var reservations = await ManualBankTransferReservation.FindTicketReservationsAsync(
            dbContext, ticket.PublicId, cancellationToken);
        if (reservations.Count == 0 || reservations.Any(x => x.Status == RefundStatus.Pending))
        {
            throw new ConflictException(
                $"Export item {ticket.RequestNumber} cannot be marked released: its reservation is not fully released.");
        }
    }

    private async Task AutoCloseBatchAsync(long batchId, CancellationToken cancellationToken)
    {
        var batch = await dbContext.RefundBankExportBatches
            .SingleAsync(x => x.Id == batchId, cancellationToken);
        if (batch.Status is RefundBankExportBatchStatus.Closed or RefundBankExportBatchStatus.Voided)
        {
            return;
        }

        var open = await dbContext.RefundBankExportItems
            .AsNoTracking()
            .AnyAsync(
                x => x.BatchId == batchId
                    && (x.Status == RefundBankExportItemStatus.Claimed
                        || x.Status == RefundBankExportItemStatus.Generated
                        || x.Status == RefundBankExportItemStatus.Submitted),
                cancellationToken);
        if (!open)
        {
            var now = timeProvider.Now;
            Mutate(() => batch.Close(now));
            await SaveGuardedAsync(cancellationToken);
        }
    }

    private bool TryBuildCandidate(
        RefundReplacementRequest ticket,
        List<Refund> reservations,
        bool companyConfigured,
        out RefundBankExportCandidateResult candidate)
    {
        candidate = null!;
        if (!IsEligible(ticket, reservations, out _))
        {
            return false;
        }

        candidate = new RefundBankExportCandidateResult(
            ticket.PublicId,
            ticket.RequestNumber,
            ticket.Order?.OrderNumber,
            DisplayName(ticket),
            ticket.ManualBankReservedAmount!.Value,
            ticket.ManualBankReservedCurrency!,
            reservations.Count,
            companyConfigured);
        return true;
    }

    private static bool IsEligible(
        RefundReplacementRequest ticket,
        List<Refund> reservations,
        out string? reason)
    {
        reason = null;
        if (ticket.Type != RefundReplacementType.Refund
            || ticket.Status != RefundReplacementStatus.Approved
            || ticket.SettlementMethod != RefundSettlementMethod.ManualBankTransfer)
        {
            reason = "Only approved manual-bank-transfer refund tickets can be exported.";
            return false;
        }

        if (ticket.ManualBankReservedAmount is null
            || ticket.ManualBankReservedAmount.Value <= 0
            || string.IsNullOrWhiteSpace(ticket.ManualBankReservedCurrency)
            || string.IsNullOrWhiteSpace(ticket.ManualBankHolderNameSnapshot)
            || string.IsNullOrWhiteSpace(ticket.ManualBankNameSnapshot)
            || string.IsNullOrWhiteSpace(ticket.ManualBankIfscSnapshot)
            || string.IsNullOrWhiteSpace(ticket.ManualBankAccountLast4Snapshot)
            || string.IsNullOrWhiteSpace(ticket.ManualBankProtectedAccountNumber))
        {
            reason = "The frozen beneficiary snapshot or reserved amount is missing.";
            return false;
        }

        if (reservations.Count == 0
            || reservations.Any(x => x.Status != RefundStatus.Pending)
            || reservations.Sum(x => x.Amount) != ticket.ManualBankReservedAmount.Value)
        {
            reason = "The payment-level reservation is missing or no longer pending.";
            return false;
        }

        var prefix = ManualBankTransferReservation.KeyPrefixFor(ticket.PublicId);
        foreach (var reservation in reservations)
        {
            var payment = reservation.Payment;
            if (payment.Status is not (PaymentStatus.Success or PaymentStatus.PartiallyRefunded or PaymentStatus.RefundPending))
            {
                reason = "A covering payment is no longer in a refundable state.";
                return false;
            }

            if (payment.Refunds.Any(x => x.Status == RefundStatus.Pending && !x.IdempotencyKey.StartsWith(prefix, StringComparison.Ordinal)))
            {
                reason = "Another refund is already in flight on a covering payment.";
                return false;
            }

            var outstanding = payment.Amount - payment.RefundedAmount;
            var ours = reservations.Where(x => x.PaymentId == payment.Id).Sum(x => x.Amount);
            if (ours > outstanding)
            {
                reason = "The reserved amount exceeds the remaining paid amount (another path settled first).";
                return false;
            }
        }

        return true;
    }

    private async Task RequireEligibleAsync(
        RefundReplacementRequest ticket,
        List<Refund> reservations,
        bool companyConfigured,
        bool forClaim,
        CancellationToken cancellationToken)
    {
        if (!IsEligible(ticket, reservations, out var reason))
        {
            throw new ConflictException(
                $"Ticket {ticket.RequestNumber} cannot be exported: {reason}");
        }

        if (forClaim && !companyConfigured)
        {
            throw new ConflictException(
                $"Ticket {ticket.RequestNumber} cannot be exported: the company bank details are not configured.");
        }

        // Employee-origin policy: claiming (and therefore every downstream
        // settlement step) requires a verifiable administrator approval on
        // the ticket. Pre-policy or non-admin-approved employee tickets are
        // blocked here with the reconciliation procedure; the approval
        // itself is never rewritten.
        if (EmployeeOriginGuard.IsEmployeeOriginated(ticket.Customer.UserType)
            && (ticket.DecidedByUserId is null
                || !await EmployeeOriginGuard.IsAdministratorTierAsync(
                    dbContext, ticket.DecidedByUserId.Value, cancellationToken)))
        {
            throw new ConflictException(
                $"Ticket {ticket.RequestNumber} cannot be exported: it has no verifiable administrator approval. " +
                "Ask an Owner or System Administrator to review and re-decide the request; " +
                "settlement and export remain blocked until a verifiable administrator approval is recorded.");
        }
    }

    private static string DisplayName(RefundReplacementRequest ticket) =>
        string.IsNullOrWhiteSpace(ticket.Customer.DisplayName)
            ? ticket.Delivery.CustomerNameSnapshot
            : ticket.Customer.DisplayName!;

    private string UnprotectBeneficiary(RefundBankExportItem item)
    {
        try
        {
            return beneficiaryProtector.Unprotect(item.BeneficiaryProtectedAccountNumber);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException(
                $"The beneficiary bank details for export item {item.RequestNumber} are currently unavailable.",
                exception);
        }
    }

    private string UnprotectCompany(string protectedAccountNumber, string batchNumber)
    {
        try
        {
            return companyBankProtector.Unprotect(protectedAccountNumber);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException(
                $"The company bank details for batch {batchNumber} are currently unavailable.",
                exception);
        }
    }

    private async Task<RefundBankExportBatch> FindBatchAsync(Guid batchId, CancellationToken cancellationToken) =>
        await dbContext.RefundBankExportBatches
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.PublicId == batchId, cancellationToken)
            ?? throw new NotFoundException("The export batch was not found.");

    private async Task<RefundBankExportBatch> FindBatchTrackedAsync(Guid batchId, CancellationToken cancellationToken) =>
        await dbContext.RefundBankExportBatches
            .SingleOrDefaultAsync(x => x.PublicId == batchId, cancellationToken)
            ?? throw new NotFoundException("The export batch was not found.");

    private Task<List<RefundBankExportItem>> BatchItemsTrackedAsync(long batchId, CancellationToken cancellationToken) =>
        dbContext.RefundBankExportItems
            .Where(x => x.BatchId == batchId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

    private async Task<long> FindItemIdAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var id = await dbContext.RefundBankExportItems
            .AsNoTracking()
            .Where(x => x.PublicId == itemId)
            .Select(x => x.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (id == 0)
        {
            throw new NotFoundException("The export item was not found.");
        }

        return id;
    }

    private async Task<RefundBankExportItem> FindItemTrackedAsync(Guid itemId, CancellationToken cancellationToken) =>
        await dbContext.RefundBankExportItems
            .SingleOrDefaultAsync(x => x.PublicId == itemId, cancellationToken)
            ?? throw new NotFoundException("The export item was not found.");

    private async Task<RefundBankExportBatchResult> LoadBatchResultAsync(long batchId, CancellationToken cancellationToken)
    {
        var batch = await dbContext.RefundBankExportBatches
            .AsNoTracking()
            .SingleAsync(x => x.Id == batchId, cancellationToken);
        var itemsByBatch = await LoadItemsByBatchAsync([batchId], cancellationToken);
        var items = itemsByBatch.TryGetValue(batchId, out var batchItems)
            ? batchItems
            : [];
        var ticketMap = await LoadTicketMapAsync(
            items.Select(x => x.TicketId).Distinct().ToList(), cancellationToken);
        var linksByItem = await LoadLinksByItemAsync(
            items.Select(x => x.Id).ToList(), cancellationToken);

        return BuildBatchResult(batch, items, ticketMap, linksByItem);
    }

    private async Task<Dictionary<long, List<RefundBankExportItem>>> LoadItemsByBatchAsync(
        IReadOnlyList<long> batchIds,
        CancellationToken cancellationToken)
    {
        var items = await dbContext.RefundBankExportItems
            .AsNoTracking()
            .Where(x => batchIds.Contains(x.BatchId))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return items
            .GroupBy(x => x.BatchId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    private async Task<Dictionary<long, Guid>> LoadTicketMapAsync(
        IReadOnlyList<long> ticketIds,
        CancellationToken cancellationToken)
    {
        if (ticketIds.Count == 0)
        {
            return new Dictionary<long, Guid>();
        }

        return await dbContext.RefundReplacementRequests
            .AsNoTracking()
            .Where(x => ticketIds.Contains(x.Id))
            .Select(x => new { x.Id, x.PublicId })
            .ToDictionaryAsync(x => x.Id, x => x.PublicId, cancellationToken);
    }

    private async Task<Dictionary<long, List<long>>> LoadLinksByItemAsync(
        IReadOnlyList<long> itemIds,
        CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new Dictionary<long, List<long>>();
        }

        var links = await dbContext.RefundBankExportItemReservations
            .AsNoTracking()
            .Where(x => itemIds.Contains(x.ItemId))
            .OrderBy(x => x.ItemId)
            .ThenBy(x => x.RefundId)
            .Select(x => new { x.ItemId, x.RefundId })
            .ToListAsync(cancellationToken);
        return links
            .GroupBy(x => x.ItemId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.RefundId).ToList());
    }

    private static RefundBankExportBatchResult BuildBatchResult(
        RefundBankExportBatch batch,
        IReadOnlyList<RefundBankExportItem> items,
        IReadOnlyDictionary<long, Guid> ticketMap,
        IReadOnlyDictionary<long, List<long>> linksByItem)
    {
        var itemResults = new List<RefundBankExportItemResult>(items.Count);
        foreach (var item in items)
        {
            var reservationIds = linksByItem.TryGetValue(item.Id, out var ids)
                ? ids
                : [];
            itemResults.Add(ToItemResult(item, ticketMap[item.TicketId], reservationIds));
        }

        return new RefundBankExportBatchResult(
            batch.PublicId,
            batch.BatchNumber,
            batch.Status.ToString(),
            batch.CreatedByUserId,
            batch.CreatedAt,
            batch.GeneratedAt,
            batch.SubmittedAt,
            batch.SubmittedByUserId,
            batch.ClosedAt,
            batch.VoidedAt,
            batch.VoidedByUserId,
            batch.VoidReason,
            batch.SourceAccountHolderName,
            batch.SourceBankName,
            MaskedAccount(batch.SourceBankName, batch.SourceAccountLast4),
            batch.SourceAccountLast4,
            batch.SourceIfsc,
            itemResults.Count,
            itemResults.Sum(x => x.Amount),
            itemResults.Select(x => x.Currency).Distinct().SingleOrDefault(),
            itemResults);
    }

    private async Task<RefundBankExportItemResult> LoadItemResultAsync(long itemId, CancellationToken cancellationToken)
    {
        var item = await dbContext.RefundBankExportItems
            .AsNoTracking()
            .SingleAsync(x => x.Id == itemId, cancellationToken);
        var ticketMap = await LoadTicketMapAsync([item.TicketId], cancellationToken);
        var linksByItem = await LoadLinksByItemAsync([item.Id], cancellationToken);
        var reservationIds = linksByItem.TryGetValue(item.Id, out var ids)
            ? ids
            : [];
        return ToItemResult(item, ticketMap[item.TicketId], reservationIds);
    }

    private static RefundBankExportItemResult ToItemResult(
        RefundBankExportItem item,
        Guid ticketPublicId,
        IReadOnlyList<long> reservationIds)
    {
        return new RefundBankExportItemResult(
            item.PublicId,
            ticketPublicId,
            item.RequestNumber,
            item.OrderNumber,
            item.CustomerDisplayName,
            item.Amount,
            item.Currency,
            item.Status.ToString(),
            item.BeneficiaryAccountHolderName,
            item.BeneficiaryBankName,
            MaskedAccount(item.BeneficiaryBankName, item.BeneficiaryAccountLast4),
            item.BeneficiaryAccountLast4,
            item.BeneficiaryIfsc,
            MaskedAccount(item.SourceBankName, item.SourceAccountLast4),
            item.ClaimedAt,
            item.ClaimedByUserId,
            item.GeneratedAt,
            item.SubmittedAt,
            item.PaidAt,
            item.PaidByUserId,
            item.TransferReference,
            item.FailureReason,
            item.FailedAt,
            item.VoidReason,
            item.VoidedAt,
            item.ReleaseReason,
            item.ReleasedAt,
            reservationIds);
    }

    private static void EnsureStaffAccess(RefundReplacementActor actor, RefundReplacementRequest request)
    {
        if (!actor.HasGlobalAccess && !actor.BranchIds.Contains(request.BranchId))
        {
            throw new NotFoundException("The refund request was not found.");
        }
    }

    private void AddAudit(long userId, string action, string entityType, string entityId, object after, DateTime now) =>
        dbContext.AddAuditLog(new AuditLog(
            userId,
            action,
            entityType,
            entityId,
            null,
            JsonSerializer.Serialize(after),
            null,
            null,
            null,
            now));

    private static string MaskedAccount(string bankName, string last4) =>
        $"{bankName} ···· {last4}";

    private static object MaskedSource(string holder, string bank, string last4, string ifsc) => new
    {
        holder,
        bank,
        MaskedAccountNumber = $"···· {last4}",
        last4,
        ifsc
    };

    private static string ValidateTransferReference(string? transferReference)
    {
        var normalized = transferReference?.Trim() ?? string.Empty;
        if (normalized.Length < 6 || normalized.Length > 100)
        {
            throw new ValidationAppException(
                "A bank transfer reference (UTR) between 6 and 100 characters is required to mark an export item paid.",
                "transferReference");
        }

        return normalized;
    }

    private static string ValidateDispositionReason(string? reason, string parameterName)
    {
        var normalized = reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ValidationAppException("A reason is required for this disposition.", parameterName);
        }

        if (normalized.Length > 1000)
        {
            throw new ValidationAppException("The reason cannot exceed 1000 characters.", parameterName);
        }

        return normalized;
    }

    private static void Mutate(Action operation)
    {
        try
        {
            operation();
        }
        catch (ArgumentException exception)
        {
            throw new ValidationAppException(exception.Message, exception.ParamName);
        }
        catch (InvalidOperationException exception)
        {
            throw new ConflictException(exception.Message);
        }
    }

    private async Task ExecuteSerializableAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private async Task SaveGuardedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConflictException(
                $"The export was changed by another operation. Refresh and try again. ({exception.GetType().Name})");
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new ConflictException(
                "The export conflicts with another operation: the same refund was already claimed by an active batch.");
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException
                && (sqlException.Number == 2601 || sqlException.Number == 2627))
            {
                return true;
            }

            if (current.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
