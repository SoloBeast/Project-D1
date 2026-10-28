using DoodhDirect.Application.Common;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// Stage 2: payment-level reservation accounting for manual bank transfers.
/// A reservation is a <see cref="Refund"/> row in <c>Pending</c> status with
/// a <c>manual-bank:</c> idempotency key. Creating it moves the covering
/// payment to <c>RefundPending</c> (via <see cref="Payment.StartRefund"/>)
/// without touching <c>RefundedAmount</c>, so every competing refund
/// calculation — which only counts <c>Success</c>/<c>PartiallyRefunded</c>
/// payments and the <c>Amount - RefundedAmount</c> balance — automatically
/// excludes reserved funds. The row stays <c>Pending</c> (distinguishable
/// from a successful settlement) until the transfer is confirmed
/// (<see cref="SettleAsync"/>) or explicitly released
/// (<see cref="ReleaseAsync"/>). Committed rows are never duplicated: the
/// unique index on <c>(PaymentId, IdempotencyKey)</c> plus the serializable
/// caller transaction make concurrent reservations fail safe instead of
/// over-reserving.
/// </summary>
internal static class ManualBankTransferReservation
{
    public static string ReservationKeyFor(Guid ticketPublicId, long paymentId) =>
        $"manual-bank:{ticketPublicId:N}:payment:{paymentId}";

    public static string KeyPrefixFor(Guid ticketPublicId) =>
        $"manual-bank:{ticketPublicId:N}:";

    private const string ManualBankKeyPrefix = "manual-bank:";

    /// <summary>
    /// Single owner of the Stage 3 linkage rule: a manual-bank ticket can
    /// only be marked paid when it is linked to a generated or submitted
    /// export item. Called by the Stage 2 confirmation inside its own
    /// transaction, before any reservation settles, so a concurrent void
    /// aborts before money moves.
    /// </summary>
    public static async Task RequireExportItemLinkageAsync(
        DoodhDirectDbContext dbContext,
        long ticketId,
        string requestNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var linked = await dbContext.RefundBankExportItems
            .AsNoTracking()
            .AnyAsync(
                x => x.TicketId == ticketId
                    && (x.Status == RefundBankExportItemStatus.Generated
                        || x.Status == RefundBankExportItemStatus.Submitted),
                cancellationToken);
        if (!linked)
        {
            throw new ConflictException(
                $"Ticket {requestNumber} cannot be confirmed: it has not been claimed by a generated manual-bank export batch. Claim it through the export workflow first.");
        }
    }

    /// <summary>
    /// Reserves <paramref name="amount"/> on <paramref name="payment"/> for a
    /// manual bank transfer. Leaves the row <c>Pending</c> and the payment in
    /// <c>RefundPending</c>: reserved, not paid. A committed row with the same
    /// key is authoritative — a non-succeeded row means another operation is
    /// already in progress and this reservation must lose as a conflict.
    /// </summary>
    public static Refund Reserve(
        Payment payment,
        decimal amount,
        string idempotencyKey,
        long requestedByUserId)
    {
        ArgumentNullException.ThrowIfNull(payment);
        var existing = payment.Refunds.SingleOrDefault(x => x.IdempotencyKey == idempotencyKey);
        if (existing is not null)
        {
            throw new ConflictException(
                "A manual bank transfer reservation with the same reference is already in progress.");
        }

        try
        {
            return payment.StartRefund(
                amount,
                "Manual bank transfer reservation: funds held for bank transfer, not yet paid.",
                idempotencyKey,
                requestedByUserId);
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

    /// <summary>
    /// Settles a pending reservation after the real transfer is confirmed:
    /// marks the refund <c>Succeeded</c> and posts it against the payment's
    /// refunded total exactly once. Replaying an already-succeeded row is a
    /// no-op for the row itself; the caller must still guard the ticket
    /// transition so <c>CompleteRefund</c> is never applied twice.
    /// </summary>
    public static void Settle(Refund reservation, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (reservation.Status == RefundStatus.Succeeded)
        {
            return;
        }

        if (reservation.Status != RefundStatus.Pending)
        {
            throw new ConflictException(
                "The manual bank transfer reservation is no longer pending and cannot be settled.");
        }

        try
        {
            reservation.Succeed(now);
        }
        catch (InvalidOperationException exception)
        {
            throw new ConflictException(exception.Message);
        }

        try
        {
            reservation.Payment.CompleteRefund(reservation.Amount);
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

    /// <summary>
    /// Explicitly releases a pending reservation before any transfer reached
    /// the bank: fails the refund row and restores the payment to
    /// <c>Success</c>/<c>PartiallyRefunded</c>. Only pending rows can be
    /// released; settled rows are final.
    /// </summary>
    public static void Release(Refund reservation, string reason, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (reservation.Status == RefundStatus.Pending)
        {
            try
            {
                reservation.Fail("MANUAL_BANK_RELEASED", reason, now);
            }
            catch (InvalidOperationException exception)
            {
                throw new ConflictException(exception.Message);
            }

            try
            {
                reservation.Payment.FailRefund();
            }
            catch (InvalidOperationException exception)
            {
                throw new ConflictException(exception.Message);
            }

            return;
        }

        throw new ConflictException(
            "The manual bank transfer reservation is no longer pending and cannot be released.");
    }

    public static Task<List<Refund>> FindTicketReservationsAsync(
        DoodhDirectDbContext dbContext,
        Guid ticketPublicId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var prefix = KeyPrefixFor(ticketPublicId);
        return dbContext.Refunds
            .Include(x => x.Payment)
            .Where(x => x.IdempotencyKey.StartsWith(prefix))
            .OrderBy(x => x.PaymentId)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Batched reservation fetch for read paths (eligibility lists): one
    /// scan of manual-bank rows instead of one query per ticket. The key
    /// embeds the ticket id (see <see cref="ReservationKeyFor"/>), so
    /// grouping is an in-memory parse with no schema change. Same row set
    /// and ordering per ticket as <see cref="FindTicketReservationsAsync"/>.
    /// </summary>
    public static async Task<Dictionary<Guid, List<Refund>>> FindReservationsByTicketsAsync(
        DoodhDirectDbContext dbContext,
        IReadOnlyCollection<Guid> ticketPublicIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(ticketPublicIds);
        var result = ticketPublicIds.Distinct().ToDictionary(id => id, _ => new List<Refund>());
        if (result.Count == 0)
        {
            return result;
        }

        var rows = await dbContext.Refunds
            .Include(x => x.Payment)
            .Where(x => x.IdempotencyKey.StartsWith(ManualBankKeyPrefix))
            .OrderBy(x => x.PaymentId)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            if (TryParseTicketId(row.IdempotencyKey) is { } ticketId
                && result.TryGetValue(ticketId, out var list))
            {
                list.Add(row);
            }
        }

        return result;
    }

    private static Guid? TryParseTicketId(string idempotencyKey)
    {
        if (!idempotencyKey.StartsWith(ManualBankKeyPrefix, StringComparison.Ordinal)
            || idempotencyKey.Length < ManualBankKeyPrefix.Length + 32)
        {
            return null;
        }

        return Guid.TryParseExact(
            idempotencyKey.Substring(ManualBankKeyPrefix.Length, 32), "N", out var ticketId)
            ? ticketId
            : null;
    }
}
