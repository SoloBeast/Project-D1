using DoodhDirect.Domain.Common;

namespace DoodhDirect.Domain.RefundReplacement;

/// <summary>
/// Stage 3: lifecycle of a manual-bank refund export batch. A batch claims
/// one or more approved ManualBankTransfer tickets (reusing the Stage 2
/// payment-level reservations — claiming never moves money), freezes the
/// company source-account snapshot once, and tracks generation, bank
/// submission and terminal closure. Money moves only through the linked
/// export-item confirmations, which settle through the Stage 2 service.
/// </summary>
public enum RefundBankExportBatchStatus
{
    Claimed,
    Generated,
    Submitted,
    Closed,
    Voided
}

/// <summary>
/// Stage 3: lifecycle of a single refund inside an export batch. The item
/// freezes the beneficiary and source snapshots plus the Stage 2
/// reservation references at claim time, so later customer or company
/// bank-detail edits cannot change a historical batch. Payment happens
/// only via <c>Paid</c> (through the Stage 2 settlement); <c>Voided</c>
/// never releases the reservation, and <c>Released</c> records an explicit
/// safe release executed exactly once through the Stage 2 release path.
/// </summary>
public enum RefundBankExportItemStatus
{
    Claimed,
    Generated,
    Submitted,
    Paid,
    Failed,
    Voided,
    Released
}

/// <summary>
/// Stage 3: a bank-transfer export batch. The company source-account
/// snapshot (including the DataProtection-encrypted full account number)
/// is frozen at claim time; re-downloads regenerate identical bytes from
/// these immutable snapshots. Full account numbers never appear in logs,
/// ordinary APIs or audit JSON — only last-4 masks travel there.
/// </summary>
public sealed class RefundBankExportBatch : AuditableEntity
{
    private RefundBankExportBatch() { }

    public RefundBankExportBatch(
        string batchNumber,
        long createdByUserId,
        string sourceAccountHolderName,
        string sourceBankName,
        string sourceProtectedAccountNumber,
        string sourceAccountLast4,
        string sourceIfsc,
        DateTime indiaLocalNow)
    {
        BatchNumber = Required(batchNumber, nameof(batchNumber));
        if (createdByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(createdByUserId));
        CreatedByUserId = createdByUserId;
        SourceAccountHolderName = Required(sourceAccountHolderName, nameof(sourceAccountHolderName));
        SourceBankName = Required(sourceBankName, nameof(sourceBankName));
        SourceProtectedAccountNumber = Required(sourceProtectedAccountNumber, nameof(sourceProtectedAccountNumber));
        SourceAccountLast4 = Required(sourceAccountLast4, nameof(sourceAccountLast4));
        SourceIfsc = Required(sourceIfsc, nameof(sourceIfsc));
        Status = RefundBankExportBatchStatus.Claimed;
        SetCreated(indiaLocalNow);
    }

    public string BatchNumber { get; private set; } = string.Empty;
    public long CreatedByUserId { get; private set; }
    public RefundBankExportBatchStatus Status { get; private set; }
    public string SourceAccountHolderName { get; private set; } = string.Empty;
    public string SourceBankName { get; private set; } = string.Empty;
    public string SourceProtectedAccountNumber { get; private set; } = string.Empty;
    public string SourceAccountLast4 { get; private set; } = string.Empty;
    public string SourceIfsc { get; private set; } = string.Empty;
    public DateTime? GeneratedAt { get; private set; }
    public DateTime? SubmittedAt { get; private set; }
    public long? SubmittedByUserId { get; private set; }
    public DateTime? ClosedAt { get; private set; }
    public DateTime? VoidedAt { get; private set; }
    public long? VoidedByUserId { get; private set; }
    public string? VoidReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public void MarkGenerated(DateTime indiaLocalNow)
    {
        EnsureStatus(RefundBankExportBatchStatus.Claimed, nameof(MarkGenerated));
        GeneratedAt = indiaLocalNow;
        Status = RefundBankExportBatchStatus.Generated;
        SetUpdated(indiaLocalNow);
    }

    public void MarkSubmitted(long submittedByUserId, DateTime indiaLocalNow)
    {
        EnsureStatus(RefundBankExportBatchStatus.Generated, nameof(MarkSubmitted));
        if (submittedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(submittedByUserId));
        SubmittedByUserId = submittedByUserId;
        SubmittedAt = indiaLocalNow;
        Status = RefundBankExportBatchStatus.Submitted;
        SetUpdated(indiaLocalNow);
    }

    public void Close(DateTime indiaLocalNow)
    {
        if (Status is RefundBankExportBatchStatus.Closed or RefundBankExportBatchStatus.Voided)
        {
            throw new InvalidOperationException($"The export batch cannot be closed from status {Status}.");
        }

        // Terminality of the items is enforced by the service (auto-close
        // only fires when no item is claimed, generated or submitted), so
        // a batch that never reached the bank can still close here once
        // every item carries an explicit terminal disposition.
        ClosedAt = indiaLocalNow;
        Status = RefundBankExportBatchStatus.Closed;
        SetUpdated(indiaLocalNow);
    }

    public void Void(long voidedByUserId, string reason, DateTime indiaLocalNow)
    {
        if (Status is RefundBankExportBatchStatus.Submitted
            or RefundBankExportBatchStatus.Closed
            or RefundBankExportBatchStatus.Voided)
        {
            throw new InvalidOperationException(
                $"The export batch cannot be voided from status {Status}: submitted batches need per-item dispositions.");
        }

        if (voidedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(voidedByUserId));
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A void reason is required.", nameof(reason));
        }

        VoidedByUserId = voidedByUserId;
        VoidReason = reason.Trim();
        VoidedAt = indiaLocalNow;
        Status = RefundBankExportBatchStatus.Voided;
        SetUpdated(indiaLocalNow);
    }

    private void EnsureStatus(RefundBankExportBatchStatus expected, string operation)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(
                $"The export batch cannot be {operation} from status {Status}; expected {expected}.");
        }
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();
}

/// <summary>
/// Stage 3: one approved ManualBankTransfer ticket claimed into a batch,
/// with the beneficiary and company source snapshots plus the exact Stage 2
/// reservation <c>Refund</c> rows frozen at claim time.
/// </summary>
public sealed class RefundBankExportItem : AuditableEntity
{
    private RefundBankExportItem() { }

    public RefundBankExportItem(
        long batchId,
        long ticketId,
        string requestNumber,
        string? orderNumber,
        string customerDisplayName,
        decimal amount,
        string currency,
        string beneficiaryAccountHolderName,
        string beneficiaryBankName,
        string beneficiaryProtectedAccountNumber,
        string beneficiaryAccountLast4,
        string beneficiaryIfsc,
        string sourceAccountHolderName,
        string sourceBankName,
        string sourceProtectedAccountNumber,
        string sourceAccountLast4,
        string sourceIfsc,
        long claimedByUserId,
        DateTime indiaLocalNow)
    {
        if (batchId <= 0) throw new ArgumentOutOfRangeException(nameof(batchId));
        if (ticketId <= 0) throw new ArgumentOutOfRangeException(nameof(ticketId));
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "The export amount must be positive.");
        if (claimedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(claimedByUserId));

        BatchId = batchId;
        TicketId = ticketId;
        RequestNumber = Required(requestNumber, nameof(requestNumber));
        OrderNumber = string.IsNullOrWhiteSpace(orderNumber) ? null : orderNumber.Trim();
        CustomerDisplayName = Required(customerDisplayName, nameof(customerDisplayName));
        Amount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        Currency = Required(currency, nameof(currency)).ToUpperInvariant();
        BeneficiaryAccountHolderName = Required(beneficiaryAccountHolderName, nameof(beneficiaryAccountHolderName));
        BeneficiaryBankName = Required(beneficiaryBankName, nameof(beneficiaryBankName));
        BeneficiaryProtectedAccountNumber =
            Required(beneficiaryProtectedAccountNumber, nameof(beneficiaryProtectedAccountNumber));
        BeneficiaryAccountLast4 = Required(beneficiaryAccountLast4, nameof(beneficiaryAccountLast4));
        BeneficiaryIfsc = Required(beneficiaryIfsc, nameof(beneficiaryIfsc));
        SourceAccountHolderName = Required(sourceAccountHolderName, nameof(sourceAccountHolderName));
        SourceBankName = Required(sourceBankName, nameof(sourceBankName));
        SourceProtectedAccountNumber = Required(sourceProtectedAccountNumber, nameof(sourceProtectedAccountNumber));
        SourceAccountLast4 = Required(sourceAccountLast4, nameof(sourceAccountLast4));
        SourceIfsc = Required(sourceIfsc, nameof(sourceIfsc));
        ClaimedByUserId = claimedByUserId;
        ClaimedAt = indiaLocalNow;
        Status = RefundBankExportItemStatus.Claimed;
        SetCreated(indiaLocalNow);
    }

    public long BatchId { get; private set; }
    public long TicketId { get; private set; }
    public string RequestNumber { get; private set; } = string.Empty;
    public string? OrderNumber { get; private set; }
    public string CustomerDisplayName { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public string BeneficiaryAccountHolderName { get; private set; } = string.Empty;
    public string BeneficiaryBankName { get; private set; } = string.Empty;
    public string BeneficiaryProtectedAccountNumber { get; private set; } = string.Empty;
    public string BeneficiaryAccountLast4 { get; private set; } = string.Empty;
    public string BeneficiaryIfsc { get; private set; } = string.Empty;
    public string SourceAccountHolderName { get; private set; } = string.Empty;
    public string SourceBankName { get; private set; } = string.Empty;
    public string SourceProtectedAccountNumber { get; private set; } = string.Empty;
    public string SourceAccountLast4 { get; private set; } = string.Empty;
    public string SourceIfsc { get; private set; } = string.Empty;
    public RefundBankExportItemStatus Status { get; private set; }
    public long ClaimedByUserId { get; private set; }
    public DateTime ClaimedAt { get; private set; }
    public DateTime? GeneratedAt { get; private set; }
    public DateTime? SubmittedAt { get; private set; }
    public DateTime? PaidAt { get; private set; }
    public long? PaidByUserId { get; private set; }
    public string? TransferReference { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTime? FailedAt { get; private set; }
    public long? FailedByUserId { get; private set; }
    public string? VoidReason { get; private set; }
    public DateTime? VoidedAt { get; private set; }
    public long? VoidedByUserId { get; private set; }
    public string? ReleaseReason { get; private set; }
    public DateTime? ReleasedAt { get; private set; }
    public long? ReleasedByUserId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsActive =>
        Status is RefundBankExportItemStatus.Claimed
            or RefundBankExportItemStatus.Generated
            or RefundBankExportItemStatus.Submitted;

    public bool IsTerminal =>
        Status is RefundBankExportItemStatus.Paid
            or RefundBankExportItemStatus.Failed
            or RefundBankExportItemStatus.Voided
            or RefundBankExportItemStatus.Released;

    public void MarkGenerated(DateTime indiaLocalNow)
    {
        EnsureStatus(RefundBankExportItemStatus.Claimed, nameof(MarkGenerated));
        GeneratedAt = indiaLocalNow;
        Status = RefundBankExportItemStatus.Generated;
        SetUpdated(indiaLocalNow);
    }

    public void MarkSubmitted(DateTime indiaLocalNow)
    {
        EnsureStatus(RefundBankExportItemStatus.Generated, nameof(MarkSubmitted));
        SubmittedAt = indiaLocalNow;
        Status = RefundBankExportItemStatus.Submitted;
        SetUpdated(indiaLocalNow);
    }

    public void MarkPaid(long paidByUserId, string transferReference, DateTime indiaLocalNow)
    {
        if (Status is not RefundBankExportItemStatus.Generated and not RefundBankExportItemStatus.Submitted)
        {
            throw new InvalidOperationException(
                $"The export item cannot be marked paid from status {Status}: only a generated or submitted item can be confirmed paid.");
        }

        if (paidByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(paidByUserId));
        if (string.IsNullOrWhiteSpace(transferReference))
        {
            throw new ArgumentException("A transfer reference is required.", nameof(transferReference));
        }

        PaidByUserId = paidByUserId;
        TransferReference = transferReference.Trim();
        PaidAt = indiaLocalNow;
        Status = RefundBankExportItemStatus.Paid;
        SetUpdated(indiaLocalNow);
    }

    public void MarkFailed(long failedByUserId, string reason, DateTime indiaLocalNow)
    {
        EnsureStatus(RefundBankExportItemStatus.Submitted, nameof(MarkFailed));
        if (failedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(failedByUserId));
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A failure reason is required.", nameof(reason));
        }

        FailedByUserId = failedByUserId;
        FailureReason = reason.Trim();
        FailedAt = indiaLocalNow;
        Status = RefundBankExportItemStatus.Failed;
        SetUpdated(indiaLocalNow);
    }

    public void Void(long voidedByUserId, string reason, DateTime indiaLocalNow)
    {
        if (Status is not RefundBankExportItemStatus.Claimed and not RefundBankExportItemStatus.Generated)
        {
            throw new InvalidOperationException(
                $"The export item cannot be voided from status {Status}: submitted items need a paid or failed disposition first.");
        }

        if (voidedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(voidedByUserId));
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A void reason is required.", nameof(reason));
        }

        VoidedByUserId = voidedByUserId;
        VoidReason = reason.Trim();
        VoidedAt = indiaLocalNow;
        Status = RefundBankExportItemStatus.Voided;
        SetUpdated(indiaLocalNow);
    }

    public void MarkReleased(long releasedByUserId, string reason, DateTime indiaLocalNow)
    {
        if (Status is not RefundBankExportItemStatus.Claimed and not RefundBankExportItemStatus.Generated)
        {
            throw new InvalidOperationException(
                $"The export item reservation cannot be released from status {Status}: only an item that never reached the bank can be released.");
        }

        if (releasedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(releasedByUserId));
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A release reason is required.", nameof(reason));
        }

        ReleasedByUserId = releasedByUserId;
        ReleaseReason = reason.Trim();
        ReleasedAt = indiaLocalNow;
        Status = RefundBankExportItemStatus.Released;
        SetUpdated(indiaLocalNow);
    }

    private void EnsureStatus(RefundBankExportItemStatus expected, string operation)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(
                $"The export item cannot be {operation} from status {Status}; expected {expected}.");
        }
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();
}

/// <summary>
/// Stage 3: links an export item to the exact Stage 2 reservation
/// <c>Refund</c> rows frozen at claim time. A reservation row may be linked
/// from more than one item over time (a failed or voided item leaves the
/// reservation pending, so a later batch can reclaim the same ticket), but
/// the filtered unique index on active items guarantees a ticket is never
/// claimed by two conflicting batches at once.
/// </summary>
public sealed class RefundBankExportItemReservation : Entity
{
    private RefundBankExportItemReservation() { }

    public RefundBankExportItemReservation(long itemId, long refundId, decimal amount)
    {
        if (itemId <= 0) throw new ArgumentOutOfRangeException(nameof(itemId));
        if (refundId <= 0) throw new ArgumentOutOfRangeException(nameof(refundId));
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));

        ItemId = itemId;
        RefundId = refundId;
        Amount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
    }

    public long ItemId { get; private set; }
    public long RefundId { get; private set; }
    public decimal Amount { get; private set; }
}
