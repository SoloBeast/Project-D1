using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Setup;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Customer;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Wallets;

/// <summary>
/// Customer wallet-withdrawal request workflow. A submitted request debits
/// the wallet through the ledger exactly once (atomic with request creation
/// under serializable isolation) and acts as the support ticket for the
/// withdrawal: it stays <c>Pending</c> until staff confirm the bank transfer
/// (<c>CompleteAsync</c>, no money movement) or reject it
/// (<c>RejectAsync</c>, exactly one compensating reversal credit). This
/// service never touches Razorpay or the payment-refund (<c>Refund</c>)
/// flow: withdrawals are wallet-ledger operations only.
/// </summary>
public sealed class WithdrawalService(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider,
    IOptions<PaymentOptions> paymentOptions,
    INumberSeriesService numberSeriesService,
    BankDetailProtector bankDetailProtector,
    INotificationEventWriter notificationEventWriter) : IWithdrawalService
{
    private const int MaxConcurrencyAttempts = 3;
    private readonly PaymentOptions options = paymentOptions.Value;

    public async Task<WithdrawalRequestResult> SubmitAsync(
        long customerId,
        CreateWithdrawalRequest request,
        CancellationToken cancellationToken)
    {
        ValidatePositiveAmount(request.Amount, "amount");
        ValidateIdempotencyKey(request.IdempotencyKey);

        for (var attempt = 1; attempt <= MaxConcurrencyAttempts; attempt++)
        {
            try
            {
                Guid withdrawalId = Guid.Empty;
                await ExecuteAtomicAsync(async () =>
                {
                    await EnsureCustomerAsync(customerId, cancellationToken);

                    var bankDetail = await dbContext.CustomerBankDetails
                        .SingleOrDefaultAsync(x => x.UserId == customerId, cancellationToken)
                        ?? throw new ValidationAppException(
                            "Please check the bank details on profile section before withdraw request.",
                            "bankDetails");

                    var replayId = await dbContext.WithdrawalRequests
                        .Where(x => x.CustomerId == customerId && x.IdempotencyKey == request.IdempotencyKey.Trim())
                        .Select(x => (Guid?)x.PublicId)
                        .SingleOrDefaultAsync(cancellationToken);
                    if (replayId is not null)
                    {
                        withdrawalId = replayId.Value;
                        return;
                    }

                    var wallet = await GetOrCreateWalletAsync(customerId, cancellationToken);
                    var requestNumber = await numberSeriesService.GetNextNumberAsync(
                        NumberSeriesSeedService.WithdrawalCode,
                        customerId,
                        cancellationToken);
                    var now = timeProvider.Now;
                    var withdrawal = new WithdrawalRequest(
                        customerId,
                        requestNumber,
                        request.Amount,
                        wallet.Currency,
                        bankDetail.Id,
                        bankDetail.AccountHolderName,
                        bankDetail.BankName,
                        bankDetail.Ifsc,
                        bankDetail.AccountLast4,
                        request.IdempotencyKey.Trim());
                    dbContext.WithdrawalRequests.Add(withdrawal);

                    try
                    {
                        wallet.DebitWithdrawal(
                            request.Amount,
                            $"withdrawal:{withdrawal.PublicId:N}:debit",
                            $"Wallet withdrawal {requestNumber} to {bankDetail.BankName} ···· {bankDetail.AccountLast4}",
                            now);
                    }
                    catch (WalletBalanceInsufficientException exception)
                    {
                        throw new InsufficientWalletBalanceException(
                            exception.AvailableBalance,
                            exception.RequiredAmount,
                            exception.Shortfall,
                            exception.Currency);
                    }

                    // The masked destination identifies the verified account for
                    // staff without placing unmasked details in ticket text.
                    dbContext.AddAuditLog(new AuditLog(
                        customerId,
                        "WithdrawalSubmitted",
                        nameof(WithdrawalRequest),
                        requestNumber,
                        null,
                        JsonSerializer.Serialize(new
                        {
                            withdrawal.Amount,
                            withdrawal.Currency,
                            Status = WithdrawalRequestStatus.Pending.ToString(),
                            Destination = WithdrawalMappings.MaskedDestination(
                                bankDetail.BankName,
                                bankDetail.AccountLast4)
                        }),
                        null,
                        null,
                        null,
                        now));
                    notificationEventWriter.Add(new NotificationEventRequest(
                        customerId,
                        NotificationEventTypes.WalletUpdated,
                        $"withdrawal:{withdrawal.PublicId:N}:submitted",
                        new Dictionary<string, string>
                        {
                            ["message"] =
                                $"Your withdrawal request {requestNumber} for ₹{request.Amount:0.00} is pending review."
                        },
                        "/wallet"));
                    await dbContext.SaveChangesAsync(cancellationToken);
                    withdrawalId = withdrawal.PublicId;
                }, cancellationToken);

                return await GetForCustomerAsync(customerId, withdrawalId, cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyAttempts)
            {
                dbContext.ChangeTracker.Clear();
            }
            catch (DbUpdateConcurrencyException exception)
            {
                throw new ConflictException(
                    $"The wallet was changed by another operation. Retry the request. ({exception.GetType().Name})");
            }
            catch (DbUpdateException) when (attempt < MaxConcurrencyAttempts)
            {
                // A unique-index race (duplicate request row or duplicate
                // debit row) means the first attempt committed: recover by
                // returning it instead of creating a duplicate. A transient
                // failure with nothing committed (e.g. deadlock victim) is
                // retried by the loop; the idempotency-key check at the top
                // of the body makes the replay safe.
                dbContext.ChangeTracker.Clear();
                var replay = await FindByIdempotencyKeyAsync(customerId, request.IdempotencyKey.Trim(), cancellationToken);
                if (replay is not null)
                {
                    return replay.ToResult();
                }
            }
        }

        throw new ConflictException("The withdrawal request could not be completed.");
    }

    public async Task<IReadOnlyList<WithdrawalRequestResult>> ListForCustomerAsync(
        long customerId,
        CancellationToken cancellationToken)
    {
        await EnsureCustomerAsync(customerId, cancellationToken);
        return await dbContext.WithdrawalRequests
            .AsNoTracking()
            .Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => x.ToResult())
            .ToListAsync(cancellationToken);
    }

    public async Task<WithdrawalRequestResult> GetForCustomerAsync(
        long customerId,
        Guid withdrawalId,
        CancellationToken cancellationToken)
    {
        await EnsureCustomerAsync(customerId, cancellationToken);
        return (await dbContext.WithdrawalRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.PublicId == withdrawalId && x.CustomerId == customerId,
                cancellationToken))?.ToResult()
            ?? throw new NotFoundException("The withdrawal request was not found.");
    }

    public async Task<IReadOnlyList<StaffWithdrawalRequestResult>> ListForStaffAsync(
        CancellationToken cancellationToken) =>
        await dbContext.WithdrawalRequests
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => x.ToStaffResult(
                x.Customer.PublicId,
                x.Customer.DisplayName ?? x.Customer.Mobile ?? string.Empty,
                x.Customer.Mobile ?? string.Empty,
                x.BankDetail.PublicId,
                x.Customer.UserType != UserType.Customer))
            .ToListAsync(cancellationToken);

    public async Task<StaffWithdrawalRequestResult> GetForStaffAsync(
        Guid withdrawalId,
        CancellationToken cancellationToken) =>
        (await dbContext.WithdrawalRequests
            .AsNoTracking()
            .Where(x => x.PublicId == withdrawalId)
            .Select(x => x.ToStaffResult(
                x.Customer.PublicId,
                x.Customer.DisplayName ?? x.Customer.Mobile ?? string.Empty,
                x.Customer.Mobile ?? string.Empty,
                x.BankDetail.PublicId,
                x.Customer.UserType != UserType.Customer))
            .SingleOrDefaultAsync(cancellationToken))
        ?? throw new NotFoundException("The withdrawal request was not found.");

    public async Task<WithdrawalBankDetailResult> GetBankDetailForStaffAsync(
        long staffUserId,
        Guid withdrawalId,
        CancellationToken cancellationToken)
    {
        var withdrawal = await dbContext.WithdrawalRequests
            .AsNoTracking()
            .Include(x => x.BankDetail)
            .SingleOrDefaultAsync(x => x.PublicId == withdrawalId, cancellationToken)
            ?? throw new NotFoundException("The withdrawal request was not found.");

        // Employee-origin policy: the full bank destination of an
        // employee-originated withdrawal is visible only to an Owner or
        // System Administrator. Masked list/detail behavior and audited
        // customer-withdrawal access are unchanged.
        var requesterType = await dbContext.Users
            .AsNoTracking()
            .Where(x => x.Id == withdrawal.CustomerId)
            .Select(x => (UserType?)x.UserType)
            .SingleOrDefaultAsync(cancellationToken);
        if (requesterType is null)
        {
            throw new ConflictException(
                "The withdrawal request cannot be viewed: the requesting account is missing or inconsistent. " +
                "Resolve it with staff support first.");
        }

        if (EmployeeOriginGuard.IsEmployeeOriginated(requesterType.Value)
            && !await EmployeeOriginGuard.IsAdministratorTierAsync(dbContext, staffUserId, cancellationToken))
        {
            throw new ForbiddenAppException(
                "Only an Owner or System Administrator may view the bank details of an employee-originated withdrawal.");
        }

        string accountNumber;
        try
        {
            accountNumber = bankDetailProtector.Unprotect(withdrawal.BankDetail.ProtectedAccountNumber);
        }
        catch (CryptographicException exception)
        {
            // Fail closed: an unreadable account number must never surface as
            // a partial or wrong value, and the failure is audited.
            dbContext.AddAuditLog(new AuditLog(
                staffUserId,
                "WithdrawalBankViewFailed",
                nameof(WithdrawalRequest),
                withdrawal.RequestNumber,
                null,
                null,
                null,
                null,
                "Stored bank details could not be decrypted.",
                timeProvider.Now));
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(
                "The stored bank details are currently unavailable.", exception);
        }

        dbContext.AddAuditLog(new AuditLog(
            staffUserId,
            "WithdrawalBankViewed",
            nameof(WithdrawalRequest),
            withdrawal.RequestNumber,
            null,
            null,
            null,
            null,
            null,
            timeProvider.Now));
        await dbContext.SaveChangesAsync(cancellationToken);

        return new WithdrawalBankDetailResult(
            withdrawal.PublicId,
            withdrawal.BankDetail.AccountHolderName,
            withdrawal.BankDetail.BankName,
            accountNumber,
            withdrawal.BankDetail.Ifsc);
    }

    public async Task<StaffWithdrawalRequestResult> CompleteAsync(
        long staffUserId,
        Guid withdrawalId,
        CompleteWithdrawalRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRemarks(request.Remarks);
        ValidateTransferReference(request.TransferReference);
        for (var attempt = 1; attempt <= MaxConcurrencyAttempts; attempt++)
        {
            try
            {
                Guid id = Guid.Empty;
                await ExecuteAtomicAsync(async () =>
                {
                    var withdrawal = await dbContext.WithdrawalRequests
                        .SingleOrDefaultAsync(x => x.PublicId == withdrawalId, cancellationToken)
                        ?? throw new NotFoundException("The withdrawal request was not found.");

                    // Replaying a completion is a no-op: the debit happened
                    // once at submission, so completing twice moves no money.
                    if (withdrawal.Status == WithdrawalRequestStatus.Completed)
                    {
                        id = withdrawal.PublicId;
                        return;
                    }

                    // Employee-origin policy: completing an employee-originated
                    // withdrawal requires an Owner or System Administrator
                    // actor (never the requester); customer withdrawals keep
                    // the existing workflow. The completed replay above stays
                    // idempotent by design.
                    await EmployeeOriginGuard.RequireDecisionAuthorityAsync(
                        dbContext, withdrawal.CustomerId, staffUserId, "completed", cancellationToken);

                    try
                    {
                        withdrawal.Complete(
                            staffUserId,
                            timeProvider.Now,
                            request.Remarks?.Trim(),
                            request.TransferReference);
                    }
                    catch (InvalidOperationException exception)
                    {
                        throw new ConflictException(exception.Message);
                    }
                    catch (ArgumentOutOfRangeException exception)
                    {
                        throw new ValidationAppException(exception.Message, "transferReference");
                    }

                    dbContext.AddAuditLog(new AuditLog(
                        staffUserId,
                        "WithdrawalCompleted",
                        nameof(WithdrawalRequest),
                        withdrawal.RequestNumber,
                        JsonSerializer.Serialize(new { Status = WithdrawalRequestStatus.Pending.ToString() }),
                        JsonSerializer.Serialize(new
                        {
                            Status = WithdrawalRequestStatus.Completed.ToString(),
                            withdrawal.Amount,
                            withdrawal.Currency,
                            withdrawal.TransferReference
                        }),
                        null,
                        null,
                        request.Remarks?.Trim(),
                        timeProvider.Now));
                    notificationEventWriter.Add(new NotificationEventRequest(
                        withdrawal.CustomerId,
                        NotificationEventTypes.WalletUpdated,
                        $"withdrawal:{withdrawal.PublicId:N}:completed",
                        new Dictionary<string, string>
                        {
                            ["message"] =
                                $"Your withdrawal {withdrawal.RequestNumber} for ₹{withdrawal.Amount:0.00} has been paid."
                        },
                        "/wallet"));
                    await dbContext.SaveChangesAsync(cancellationToken);
                    id = withdrawal.PublicId;
                }, cancellationToken);

                return await GetForStaffAsync(id, cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyAttempts)
            {
                dbContext.ChangeTracker.Clear();
            }
            catch (DbUpdateConcurrencyException exception)
            {
                throw new ConflictException(
                    $"The withdrawal was changed by another operation. Retry the request. ({exception.GetType().Name})");
            }
        }

        throw new ConflictException("The withdrawal request could not be completed.");
    }

    public async Task<StaffWithdrawalRequestResult> RejectAsync(
        long staffUserId,
        Guid withdrawalId,
        DecideWithdrawalRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRemarks(request.Remarks);
        for (var attempt = 1; attempt <= MaxConcurrencyAttempts; attempt++)
        {
            try
            {
                Guid id = Guid.Empty;
                await ExecuteAtomicAsync(async () =>
                {
                    var withdrawal = await dbContext.WithdrawalRequests
                        .SingleOrDefaultAsync(x => x.PublicId == withdrawalId, cancellationToken)
                        ?? throw new NotFoundException("The withdrawal request was not found.");

                    // Idempotent replay: an already-reversed rejection returns
                    // the current state without crediting a second time.
                    if (withdrawal.Status == WithdrawalRequestStatus.Rejected && withdrawal.ReversalCredited)
                    {
                        id = withdrawal.PublicId;
                        return;
                    }

                    // Employee-origin policy: see CompleteAsync — the same rule
                    // governs rejection (including its compensating reversal).
                    // The already-reversed replay above stays idempotent.
                    await EmployeeOriginGuard.RequireDecisionAuthorityAsync(
                        dbContext, withdrawal.CustomerId, staffUserId, "rejected", cancellationToken);

                    if (withdrawal.Status == WithdrawalRequestStatus.Pending)
                    {
                        try
                        {
                            withdrawal.Reject(staffUserId, timeProvider.Now, request.Remarks?.Trim());
                        }
                        catch (InvalidOperationException exception)
                        {
                            throw new ConflictException(exception.Message);
                        }
                    }
                    else if (withdrawal.Status != WithdrawalRequestStatus.Rejected)
                    {
                        throw new ConflictException(
                            $"A withdrawal in status '{withdrawal.Status}' cannot transition; terminal states are final.");
                    }

                    var wallet = await dbContext.Wallets
                        .SingleOrDefaultAsync(x => x.CustomerId == withdrawal.CustomerId, cancellationToken)
                        ?? throw new ConflictException("The customer wallet was not found.");

                    var reversalKey = $"withdrawal:{withdrawal.PublicId:N}:reversal";
                    var alreadyCredited = await dbContext.WalletTransactions.AnyAsync(
                        x => x.WalletId == wallet.Id && x.IdempotencyKey == reversalKey,
                        cancellationToken);
                    if (!alreadyCredited)
                    {
                        wallet.Credit(
                            WalletTransactionType.WithdrawalReversal,
                            withdrawal.Amount,
                            reversalKey,
                            $"Withdrawal reversal for {withdrawal.RequestNumber}",
                            timeProvider.Now);
                        withdrawal.MarkReversalCredited();
                    }
                    else if (!withdrawal.ReversalCredited)
                    {
                        withdrawal.MarkReversalCredited();
                    }

                    dbContext.AddAuditLog(new AuditLog(
                        staffUserId,
                        "WithdrawalRejected",
                        nameof(WithdrawalRequest),
                        withdrawal.RequestNumber,
                        JsonSerializer.Serialize(new { Status = WithdrawalRequestStatus.Pending.ToString() }),
                        JsonSerializer.Serialize(new
                        {
                            Status = WithdrawalRequestStatus.Rejected.ToString(),
                            withdrawal.Amount,
                            withdrawal.Currency
                        }),
                        null,
                        null,
                        request.Remarks?.Trim(),
                        timeProvider.Now));
                    notificationEventWriter.Add(new NotificationEventRequest(
                        withdrawal.CustomerId,
                        NotificationEventTypes.WalletUpdated,
                        $"withdrawal:{withdrawal.PublicId:N}:rejected",
                        new Dictionary<string, string>
                        {
                            ["message"] =
                                $"Your withdrawal {withdrawal.RequestNumber} for ₹{withdrawal.Amount:0.00} was rejected and the amount was returned to your wallet."
                        },
                        "/wallet"));
                    await dbContext.SaveChangesAsync(cancellationToken);
                    id = withdrawal.PublicId;
                }, cancellationToken);

                return await GetForStaffAsync(id, cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyAttempts)
            {
                dbContext.ChangeTracker.Clear();
            }
            catch (DbUpdateConcurrencyException exception)
            {
                throw new ConflictException(
                    $"The withdrawal was changed by another operation. Retry the request. ({exception.GetType().Name})");
            }
            catch (DbUpdateException) when (attempt < MaxConcurrencyAttempts)
            {
                // A unique-index race on the reversal credit means another
                // attempt posted it: recover by returning the current state.
                // A transient failure with nothing committed is retried by
                // the loop; the reversal-key check in the body keeps it safe.
                dbContext.ChangeTracker.Clear();
                var current = await dbContext.WithdrawalRequests
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.PublicId == withdrawalId, cancellationToken);
                if (current is not null && current.Status == WithdrawalRequestStatus.Rejected)
                {
                    return await GetForStaffAsync(withdrawalId, cancellationToken);
                }
            }
        }

        throw new ConflictException("The withdrawal request could not be rejected.");
    }

    private async Task<WithdrawalRequest?> FindByIdempotencyKeyAsync(
        long customerId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        await dbContext.WithdrawalRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.CustomerId == customerId && x.IdempotencyKey == idempotencyKey,
                cancellationToken);

    private async Task<Wallet> GetOrCreateWalletAsync(long customerId, CancellationToken cancellationToken)
    {
        var wallet = await dbContext.Wallets.SingleOrDefaultAsync(
            x => x.CustomerId == customerId,
            cancellationToken);
        if (wallet is not null)
        {
            return wallet;
        }

        wallet = new Wallet(customerId, options.Currency);
        dbContext.Wallets.Add(wallet);
        await dbContext.SaveChangesAsync(cancellationToken);
        return wallet;
    }

    private async Task EnsureCustomerAsync(long customerId, CancellationToken cancellationToken)
    {
        if (customerId <= 0 || !await dbContext.Users.AnyAsync(
                x => x.Id == customerId && x.IsActive && (x.UserType == UserType.Customer || x.UserType == UserType.Employee),
                cancellationToken))
        {
            throw new NotFoundException("The customer was not found.");
        }
    }

    private async Task ExecuteAtomicAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            await operation();
            return;
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private static void ValidatePositiveAmount(decimal amount, string field)
    {
        var rounded = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (rounded <= 0 || rounded != amount)
        {
            throw new ValidationAppException("Amount must be positive and have at most two decimal places.", field);
        }
    }

    private static void ValidateIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ValidationAppException("This field is required.", nameof(idempotencyKey));
        }

        if (idempotencyKey.Trim().Length > 100)
        {
            throw new ValidationAppException("This field cannot exceed 100 characters.", nameof(idempotencyKey));
        }
    }

    private static void ValidateRemarks(string? remarks)
    {
        if (remarks is not null && remarks.Trim().Length > 1000)
        {
            throw new ValidationAppException("Remarks cannot exceed 1000 characters.", "remarks");
        }
    }

    private static void ValidateTransferReference(string? transferReference)
    {
        var normalized = transferReference?.Trim() ?? string.Empty;
        if (normalized.Length < 6 || normalized.Length > 100)
        {
            throw new ValidationAppException(
                "A bank transfer reference (UTR) between 6 and 100 characters is required to mark a withdrawal paid.",
                "transferReference");
        }
    }
}
