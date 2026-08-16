using System.Data;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Wallets;

public sealed class WalletService(
    DoodhDirectDbContext dbContext,
    IClock clock,
    IOptions<PaymentOptions> paymentOptions) : IWalletService
{
    private const int MaxConcurrencyAttempts = 3;
    private readonly PaymentOptions options = paymentOptions.Value;

    public async Task<WalletResult> GetAsync(long customerId, CancellationToken cancellationToken)
    {
        await EnsureCustomerAsync(customerId, cancellationToken);
        var wallet = await GetOrCreateWalletAsync(customerId, cancellationToken);
        return wallet.ToResult();
    }

    public async Task<IReadOnlyList<WalletTransactionResult>> GetTransactionsAsync(
        long customerId,
        CancellationToken cancellationToken)
    {
        await EnsureCustomerAsync(customerId, cancellationToken);
        var walletId = await dbContext.Wallets
            .Where(x => x.CustomerId == customerId)
            .Select(x => (long?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (walletId is null)
        {
            return [];
        }

        return await dbContext.WalletTransactions
            .AsNoTracking()
            .Include(x => x.Payment)
            .Include(x => x.Order)
            .Where(x => x.WalletId == walletId.Value)
            .OrderByDescending(x => x.OccurredAtUtc)
            .ThenByDescending(x => x.Id)
            .Select(x => x.ToResult())
            .ToListAsync(cancellationToken);
    }

    public Task<WalletTransactionResult> TopUpAsync(
        long customerId,
        WalletTopUpRequest request,
        CancellationToken cancellationToken)
    {
        ValidatePositiveAmount(request.Amount, nameof(request.Amount));
        ValidateIdempotencyKey(request.IdempotencyKey);

        return MutateAsync(
            customerId,
            request.IdempotencyKey,
            wallet => wallet.Credit(
                WalletTransactionType.TopUp,
                request.Amount,
                request.IdempotencyKey,
                "Wallet top-up",
                clock.UtcNow),
            null,
            cancellationToken);
    }

    public async Task<WalletTransactionResult> AdjustAsync(
        long administratorUserId,
        Guid customerId,
        WalletAdjustmentRequest request,
        CancellationToken cancellationToken)
    {
        if (administratorUserId <= 0)
        {
            throw new ValidationAppException("The administrator identity is invalid.");
        }

        ValidateSignedAmount(request.Amount, nameof(request.Amount));
        ValidateRequired(request.Reason, nameof(request.Reason), 500);
        ValidateIdempotencyKey(request.IdempotencyKey);

        var resolvedCustomerId = await ResolveCustomerIdAsync(customerId, cancellationToken);
        return await MutateAsync(
            resolvedCustomerId,
            request.IdempotencyKey,
            wallet => wallet.Adjust(
                request.Amount,
                administratorUserId,
                request.IdempotencyKey,
                request.Reason,
                clock.UtcNow),
            (wallet, transaction) =>
            {
                dbContext.AuditLogs.Add(new AuditLog(
                    administratorUserId,
                    "WalletAdjusted",
                    nameof(Wallet),
                    wallet.PublicId.ToString(),
                    JsonSerializer.Serialize(new { Balance = transaction.BalanceBefore, wallet.Currency }),
                    JsonSerializer.Serialize(new { Balance = transaction.BalanceAfter, wallet.Currency, transaction.Amount }),
                    request.IpAddress,
                    request.UserAgent,
                    request.Reason.Trim(),
                    clock.UtcNow));
            },
            cancellationToken);
    }

    public async Task<WalletTransactionResult> DebitOrderAsync(
        long customerId,
        long orderId,
        long paymentId,
        decimal amount,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidatePositiveAmount(amount, nameof(amount));
        ValidateIdempotencyKey(idempotencyKey);
        await EnsurePaymentReferenceAsync(customerId, orderId, paymentId, cancellationToken);

        try
        {
            return await MutateAsync(
                customerId,
                idempotencyKey,
                wallet => wallet.DebitOrder(
                    amount,
                    orderId,
                    paymentId,
                    idempotencyKey,
                    "One-time order payment",
                    clock.UtcNow),
                null,
                cancellationToken);
        }
        catch (WalletBalanceInsufficientException exception)
        {
            throw new InsufficientWalletBalanceException(
                exception.AvailableBalance,
                exception.RequiredAmount,
                exception.Shortfall,
                exception.Currency);
        }
    }

    public async Task<WalletTransactionResult> CreditRefundAsync(
        long customerId,
        long orderId,
        long paymentId,
        decimal amount,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidatePositiveAmount(amount, nameof(amount));
        ValidateIdempotencyKey(idempotencyKey);
        await EnsurePaymentReferenceAsync(customerId, orderId, paymentId, cancellationToken);

        return await MutateAsync(
            customerId,
            idempotencyKey,
            wallet => wallet.Credit(
                WalletTransactionType.RefundCredit,
                amount,
                idempotencyKey,
                "Payment refund credit",
                clock.UtcNow,
                paymentId,
                orderId),
            null,
            cancellationToken);
    }

    private async Task<WalletTransactionResult> MutateAsync(
        long customerId,
        string idempotencyKey,
        Func<Wallet, WalletTransaction> mutation,
        Action<Wallet, WalletTransaction>? afterMutation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxConcurrencyAttempts; attempt++)
        {
            try
            {
                Guid transactionId = Guid.Empty;
                await ExecuteAtomicAsync(async () =>
                {
                    await EnsureCustomerAsync(customerId, cancellationToken);
                    var wallet = await GetOrCreateWalletAsync(customerId, cancellationToken);
                    var existingId = await dbContext.WalletTransactions
                        .Where(x => x.WalletId == wallet.Id && x.IdempotencyKey == idempotencyKey.Trim())
                        .Select(x => (Guid?)x.PublicId)
                        .SingleOrDefaultAsync(cancellationToken);

                    if (existingId is not null)
                    {
                        transactionId = existingId.Value;
                        return;
                    }

                    var transaction = mutation(wallet);
                    afterMutation?.Invoke(wallet, transaction);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    transactionId = transaction.PublicId;
                }, cancellationToken);

                return await GetTransactionAsync(customerId, transactionId, cancellationToken);
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
        }

        throw new ConflictException("The wallet operation could not be completed.");
    }

    private async Task<WalletTransactionResult> GetTransactionAsync(
        long customerId,
        Guid transactionId,
        CancellationToken cancellationToken) =>
        (await dbContext.WalletTransactions
            .AsNoTracking()
            .Include(x => x.Payment)
            .Include(x => x.Order)
            .SingleOrDefaultAsync(
                x => x.PublicId == transactionId && x.Wallet.CustomerId == customerId,
                cancellationToken))?.ToResult()
        ?? throw new NotFoundException("The wallet transaction was not found.");

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
                x => x.Id == customerId && x.IsActive && x.UserType == UserType.Customer,
                cancellationToken))
        {
            throw new NotFoundException("The customer was not found.");
        }
    }

    private async Task<long> ResolveCustomerIdAsync(Guid customerId, CancellationToken cancellationToken) =>
        await dbContext.Users
            .Where(x => x.PublicId == customerId && x.IsActive && x.UserType == UserType.Customer)
            .Select(x => (long?)x.Id)
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new NotFoundException("The customer was not found.");

    private async Task EnsurePaymentReferenceAsync(
        long customerId,
        long orderId,
        long paymentId,
        CancellationToken cancellationToken)
    {
        var validReference = await dbContext.Payments.AnyAsync(
            x => x.Id == paymentId &&
                x.OrderId == orderId &&
                x.CustomerId == customerId &&
                x.Order.CustomerId == customerId,
            cancellationToken);
        if (!validReference)
        {
            throw new NotFoundException("The payment reference was not found for this customer and order.");
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

    private static void ValidateSignedAmount(decimal amount, string field)
    {
        var rounded = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (rounded == 0 || rounded != amount)
        {
            throw new ValidationAppException("Amount must be non-zero and have at most two decimal places.", field);
        }
    }

    private static void ValidateIdempotencyKey(string idempotencyKey) =>
        ValidateRequired(idempotencyKey, nameof(idempotencyKey), 100);

    private static void ValidateRequired(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ValidationAppException("This field is required.", field);
        }

        if (value.Trim().Length > maxLength)
        {
            throw new ValidationAppException($"This field cannot exceed {maxLength} characters.", field);
        }
    }
}
