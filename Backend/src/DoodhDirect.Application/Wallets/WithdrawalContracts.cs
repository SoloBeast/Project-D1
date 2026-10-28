using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Wallets;

namespace DoodhDirect.Application.Wallets;

/// <summary>
/// Customer-visible withdrawal request. Bank destination is a masked summary
/// only; full account details are never exposed here.
/// </summary>
public sealed record WithdrawalRequestResult(
    Guid PublicId,
    string RequestNumber,
    decimal Amount,
    string Currency,
    WithdrawalRequestStatus Status,
    string MaskedDestination,
    string BankNameSnapshot,
    string? DecisionRemarks,
    string? TransferReference,
    DateTime CreatedAt,
    DateTime? DecidedAt);

/// <summary>
/// Staff-visible withdrawal request. Includes the masked destination and the
/// bank-detail reference needed to open the authorized full-detail view;
/// the full account number is served only by <see cref="WithdrawalBankDetailResult"/>.
/// </summary>
public sealed record StaffWithdrawalRequestResult(
    Guid PublicId,
    string RequestNumber,
    Guid CustomerPublicId,
    string CustomerName,
    string CustomerMobile,
    decimal Amount,
    string Currency,
    WithdrawalRequestStatus Status,
    Guid BankDetailPublicId,
    string MaskedDestination,
    string BankNameSnapshot,
    string AccountHolderNameSnapshot,
    string IfscSnapshot,
    string AccountLast4Snapshot,
    string? DecisionRemarks,
    string? TransferReference,
    DateTime CreatedAt,
    DateTime? DecidedAt,
    /// <summary>
    /// Server-derived origin indicator: true when the authoritative owning
    /// account is a non-customer (employee/owner/administrator) account.
    /// Never accepted from client input.
    /// </summary>
    bool IsEmployeeOriginated = false);

/// <summary>
/// Authorized full bank destination for a withdrawal request. Served only to
/// staff holding the withdrawal-processing permission; never logged.
/// </summary>
public sealed record WithdrawalBankDetailResult(
    Guid WithdrawalPublicId,
    string AccountHolderName,
    string BankName,
    string AccountNumber,
    string Ifsc);

public sealed record CreateWithdrawalRequest(
    decimal Amount,
    string IdempotencyKey);

public sealed record DecideWithdrawalRequest(
    string? Remarks);

/// <summary>
/// Completion requires the verifiable bank transfer reference (UTR) recorded
/// when staff confirm the transfer. Rejection uses <see cref="DecideWithdrawalRequest"/>.
/// </summary>
public sealed record CompleteWithdrawalRequest(
    string TransferReference,
    string? Remarks);

public interface IWithdrawalService
{
    Task<WithdrawalRequestResult> SubmitAsync(
        long customerId,
        CreateWithdrawalRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WithdrawalRequestResult>> ListForCustomerAsync(
        long customerId,
        CancellationToken cancellationToken);

    Task<WithdrawalRequestResult> GetForCustomerAsync(
        long customerId,
        Guid withdrawalId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StaffWithdrawalRequestResult>> ListForStaffAsync(
        CancellationToken cancellationToken);

    Task<StaffWithdrawalRequestResult> GetForStaffAsync(
        Guid withdrawalId,
        CancellationToken cancellationToken);

    Task<WithdrawalBankDetailResult> GetBankDetailForStaffAsync(
        long staffUserId,
        Guid withdrawalId,
        CancellationToken cancellationToken);

    Task<StaffWithdrawalRequestResult> CompleteAsync(
        long staffUserId,
        Guid withdrawalId,
        CompleteWithdrawalRequest request,
        CancellationToken cancellationToken);

    Task<StaffWithdrawalRequestResult> RejectAsync(
        long staffUserId,
        Guid withdrawalId,
        DecideWithdrawalRequest request,
        CancellationToken cancellationToken);
}

public static class WithdrawalMappings
{
    public static WithdrawalRequestResult ToResult(this WithdrawalRequest request) => new(
        request.PublicId,
        request.RequestNumber,
        request.Amount,
        request.Currency,
        request.Status,
        MaskedDestination(request.BankNameSnapshot, request.AccountLast4Snapshot),
        request.BankNameSnapshot,
        request.DecisionRemarks,
        request.TransferReference,
        request.CreatedAt,
        request.DecidedAt);

    public static StaffWithdrawalRequestResult ToStaffResult(
        this WithdrawalRequest request,
        Guid customerPublicId,
        string customerName,
        string customerMobile,
        Guid bankDetailPublicId,
        bool isEmployeeOriginated = false) => new(
        request.PublicId,
        request.RequestNumber,
        customerPublicId,
        customerName,
        customerMobile,
        request.Amount,
        request.Currency,
        request.Status,
        bankDetailPublicId,
        MaskedDestination(request.BankNameSnapshot, request.AccountLast4Snapshot),
        request.BankNameSnapshot,
        request.AccountHolderNameSnapshot,
        request.IfscSnapshot,
        request.AccountLast4Snapshot,
        request.DecisionRemarks,
        request.TransferReference,
        request.CreatedAt,
        request.DecidedAt,
        isEmployeeOriginated);

    public static string MaskedDestination(string bankName, string last4) =>
        $"{bankName} ···· {last4}";
}
