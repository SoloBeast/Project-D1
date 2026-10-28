using DoodhDirect.Domain.RefundReplacement;

namespace DoodhDirect.Application.RefundReplacement;

/// <summary>
/// Stage 3: one approved ManualBankTransfer ticket that currently qualifies
/// for export-batch claiming. Informative only: claiming re-evaluates every
/// rule inside its own serializable transaction.
/// </summary>
public sealed record RefundBankExportCandidateResult(
    Guid TicketId,
    string RequestNumber,
    string? OrderNumber,
    string CustomerDisplayName,
    decimal ReservedAmount,
    string ReservedCurrency,
    int ReservationCount,
    bool CompanyBankConfigured);

/// <summary>
/// Stage 3: an export batch with masked account details. Full account
/// numbers are never present on this result — they exist only in the
/// downloaded CSV bytes served to authorized finance users.
/// </summary>
public sealed record RefundBankExportBatchResult(
    Guid BatchId,
    string BatchNumber,
    string Status,
    long CreatedByUserId,
    DateTime CreatedAt,
    DateTime? GeneratedAt,
    DateTime? SubmittedAt,
    long? SubmittedByUserId,
    DateTime? ClosedAt,
    DateTime? VoidedAt,
    long? VoidedByUserId,
    string? VoidReason,
    string SourceAccountHolderName,
    string SourceBankName,
    string SourceMaskedAccountNumber,
    string SourceAccountLast4,
    string SourceIfsc,
    int ItemCount,
    decimal TotalAmount,
    string? Currency,
    IReadOnlyList<RefundBankExportItemResult> Items);

/// <summary>
/// Stage 3: one claimed refund inside a batch. Beneficiary and source
/// accounts are masked; the full numbers they were frozen from appear only
/// in the authorized CSV download.
/// </summary>
public sealed record RefundBankExportItemResult(
    Guid ItemId,
    Guid TicketId,
    string RequestNumber,
    string? OrderNumber,
    string CustomerDisplayName,
    decimal Amount,
    string Currency,
    string Status,
    string BeneficiaryAccountHolderName,
    string BeneficiaryBankName,
    string BeneficiaryMaskedAccountNumber,
    string BeneficiaryAccountLast4,
    string BeneficiaryIfsc,
    string SourceMaskedAccountNumber,
    DateTime ClaimedAt,
    long ClaimedByUserId,
    DateTime? GeneratedAt,
    DateTime? SubmittedAt,
    DateTime? PaidAt,
    long? PaidByUserId,
    string? TransferReference,
    string? FailureReason,
    DateTime? FailedAt,
    string? VoidReason,
    DateTime? VoidedAt,
    string? ReleaseReason,
    DateTime? ReleasedAt,
    IReadOnlyList<long> ReservationRefundIds);

/// <summary>
/// Stage 3: deterministic CSV bytes for a generated batch plus the
/// generic-format disclaimer. Re-downloading returns identical bytes
/// without creating claims, reservations, debits or payment attempts.
/// </summary>
public sealed record RefundBankExportDownloadResult(
    string FileName,
    byte[] Content,
    string Disclaimer);

/// <summary>
/// Stage 3: explicit batch creation over approved ManualBankTransfer
/// tickets. Every ticket id is re-validated inside the claiming
/// transaction; unknown, ineligible or already-claimed tickets abort the
/// whole batch.
/// </summary>
public sealed record CreateRefundBankExportBatchRequest(IReadOnlyList<Guid> TicketIds);

/// <summary>
/// Stage 3: confirmation that the bank transfer for one export item was
/// actually made. The reference (UTR) is required (6–100 characters, same
/// convention as wallet withdrawals); only after this confirmation does
/// the linked ticket complete and its reserved payment allocations settle,
/// exactly once through the existing Stage 2 service. Generation or
/// download of an export file never confirms payment.
/// </summary>
public sealed record ConfirmRefundBankExportItemRequest(
    string TransferReference,
    string? Remarks);

/// <summary>
/// Stage 3: explicit audited disposition recording a bank-reported failure
/// for a submitted item, or a void / safe-release decision. Voids never
/// release the Stage 2 reservation; a release is allowed only when the item
/// never reached the bank and executes the Stage 2 release exactly once.
/// </summary>
public sealed record RefundBankExportItemDispositionRequest(
    string Reason,
    string? Remarks);

public interface IRefundBankExportService
{
    /// <summary>
    /// Lists approved ManualBankTransfer tickets that currently qualify for
    /// claiming, scoped to the actor's branches.
    /// </summary>
    Task<IReadOnlyList<RefundBankExportCandidateResult>> ListEligibleAsync(
        RefundReplacementActor actor,
        CancellationToken cancellationToken);

    Task<RefundBankExportBatchResult> CreateBatchAsync(
        RefundReplacementActor actor,
        CreateRefundBankExportBatchRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RefundBankExportBatchResult>> ListBatchesAsync(
        RefundReplacementActor actor,
        CancellationToken cancellationToken);

    Task<RefundBankExportBatchResult> GetBatchAsync(
        RefundReplacementActor actor,
        Guid batchId,
        CancellationToken cancellationToken);

    Task<RefundBankExportBatchResult> GenerateBatchAsync(
        RefundReplacementActor actor,
        Guid batchId,
        CancellationToken cancellationToken);

    Task<RefundBankExportDownloadResult> DownloadBatchAsync(
        RefundReplacementActor actor,
        Guid batchId,
        CancellationToken cancellationToken);

    Task<RefundBankExportBatchResult> SubmitBatchAsync(
        RefundReplacementActor actor,
        Guid batchId,
        CancellationToken cancellationToken);

    Task<RefundBankExportBatchResult> VoidBatchAsync(
        RefundReplacementActor actor,
        Guid batchId,
        RefundBankExportItemDispositionRequest request,
        CancellationToken cancellationToken);

    Task<RefundBankExportItemResult> ConfirmItemPaidAsync(
        RefundReplacementActor actor,
        Guid itemId,
        ConfirmRefundBankExportItemRequest request,
        CancellationToken cancellationToken);

    Task<RefundBankExportItemResult> FailItemAsync(
        RefundReplacementActor actor,
        Guid itemId,
        RefundBankExportItemDispositionRequest request,
        CancellationToken cancellationToken);

    Task<RefundBankExportItemResult> VoidItemAsync(
        RefundReplacementActor actor,
        Guid itemId,
        RefundBankExportItemDispositionRequest request,
        CancellationToken cancellationToken);

    Task<RefundBankExportItemResult> ReleaseItemReservationAsync(
        RefundReplacementActor actor,
        Guid itemId,
        RefundBankExportItemDispositionRequest request,
        CancellationToken cancellationToken);
}
