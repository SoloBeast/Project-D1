using System.Security.Cryptography;
using System.Text.Json;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Customer;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Notifications;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Customer;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using DoodhDirect.Infrastructure.Wallets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class WithdrawalServiceTests
{
    private const string Holder = "Asha Sharma";
    private const string Bank = "State Bank of India";
    private const string Account = "123456789012";
    private const string Ifsc = "SBIN0001234";

    [Fact]
    public async Task Submit_WithoutBankDetails_RejectsWithExactMessageAndNoSideEffects()
    {
        await using var harness = await WithdrawalHarness.CreateAsync();
        await harness.SeedWalletAsync(1000m, "topup-1");

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Withdrawals.SubmitAsync(
                harness.Customer.Id,
                new CreateWithdrawalRequest(100m, "wd-missing-bank"),
                CancellationToken.None));

        Assert.Equal(
            "Please check the bank details on profile section before withdraw request.",
            exception.Message);
        Assert.Empty(await harness.Db.WithdrawalRequests.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.WalletTransactions
            .AsNoTracking()
            .Where(x => x.Type == WalletTransactionType.WithdrawalDebit)
            .ToListAsync());
        Assert.Empty(await harness.Db.NotificationEvents.AsNoTracking().ToListAsync());
        Assert.Equal(1000m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task Submit_WithCompleteBankDetails_CreatesPendingRequestAndDebitsOnce()
    {
        await using var harness = await WithdrawalHarness.CreateAsync();
        await harness.SeedWalletAsync(1000m, "topup-1");
        await harness.SeedBankDetailAsync(harness.Customer.Id);

        var result = await harness.Withdrawals.SubmitAsync(
            harness.Customer.Id,
            new CreateWithdrawalRequest(250m, "wd-1"),
            CancellationToken.None);

        Assert.Equal(WithdrawalRequestStatus.Pending, result.Status);
        Assert.StartsWith("WD/", result.RequestNumber);
        Assert.Equal(250m, result.Amount);
        Assert.Equal("INR", result.Currency);
        Assert.Equal($"{Bank} ···· 9012", result.MaskedDestination);
        Assert.Null(result.DecidedAt);

        var stored = await harness.Db.WithdrawalRequests.AsNoTracking().SingleAsync();
        Assert.Equal(result.PublicId, stored.PublicId);
        Assert.Equal(harness.Customer.Id, stored.CustomerId);
        Assert.Equal(250m, stored.Amount);
        Assert.Equal("wd-1", stored.IdempotencyKey);
        Assert.Equal(Holder, stored.AccountHolderNameSnapshot);
        Assert.Equal(Bank, stored.BankNameSnapshot);
        Assert.Equal(Ifsc, stored.IfscSnapshot);
        Assert.Equal("9012", stored.AccountLast4Snapshot);

        Assert.Equal(750m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        var debit = await harness.Db.WalletTransactions
            .AsNoTracking()
            .SingleAsync(x => x.Type == WalletTransactionType.WithdrawalDebit);
        Assert.Equal(1000m, debit.BalanceBefore);
        Assert.Equal(-250m, debit.Amount);
        Assert.Equal(750m, debit.BalanceAfter);
        Assert.Equal($"withdrawal:{result.PublicId:N}:debit", debit.IdempotencyKey);

        Assert.Empty(await harness.Db.Refunds.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.Payments.AsNoTracking().ToListAsync());

        var audit = Assert.Single(await harness.Db.AuditLogs
            .AsNoTracking()
            .Where(x => x.Action == "WithdrawalSubmitted")
            .ToListAsync());
        Assert.DoesNotContain(Account, audit.NewValueJson ?? string.Empty);

        var notification = Assert.Single(await harness.Db.NotificationEvents
            .AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.WalletUpdated)
            .ToListAsync());
        Assert.Equal($"withdrawal:{result.PublicId:N}:submitted", notification.EventKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    [InlineData(10.125)]
    public async Task Submit_WithInvalidAmount_RejectsWithoutSideEffects(decimal amount)
    {
        await using var harness = await WithdrawalHarness.CreateAsync();
        await harness.SeedWalletAsync(1000m, "topup-1");
        await harness.SeedBankDetailAsync(harness.Customer.Id);

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Withdrawals.SubmitAsync(
                harness.Customer.Id,
                new CreateWithdrawalRequest(amount, $"wd-invalid-{amount}"),
                CancellationToken.None));

        Assert.Equal("amount", exception.Field);
        Assert.Empty(await harness.Db.WithdrawalRequests.AsNoTracking().ToListAsync());
        Assert.Equal(1000m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task Submit_OverBalance_RejectsWithoutSideEffects()
    {
        await using var harness = await WithdrawalHarness.CreateAsync();
        await harness.SeedWalletAsync(1000m, "topup-1");
        await harness.SeedBankDetailAsync(harness.Customer.Id);

        var exception = await Assert.ThrowsAsync<InsufficientWalletBalanceException>(() =>
            harness.Withdrawals.SubmitAsync(
                harness.Customer.Id,
                new CreateWithdrawalRequest(1500m, "wd-over"),
                CancellationToken.None));

        Assert.Equal(1000m, exception.AvailableBalance);
        Assert.Equal(1500m, exception.RequiredAmount);
        Assert.Empty(await harness.Db.WithdrawalRequests.AsNoTracking().ToListAsync());
        Assert.Equal(1000m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task Submit_RevalidatesStaleBalanceOnSecondRequest()
    {
        await using var harness = await WithdrawalHarness.CreateAsync();
        await harness.SeedWalletAsync(500m, "topup-1");
        await harness.SeedBankDetailAsync(harness.Customer.Id);

        await harness.Withdrawals.SubmitAsync(
            harness.Customer.Id,
            new CreateWithdrawalRequest(300m, "wd-first"),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InsufficientWalletBalanceException>(() =>
            harness.Withdrawals.SubmitAsync(
                harness.Customer.Id,
                new CreateWithdrawalRequest(300m, "wd-second"),
                CancellationToken.None));

        Assert.Equal(200m, exception.AvailableBalance);
        Assert.Single(await harness.Db.WithdrawalRequests.AsNoTracking().ToListAsync());
        Assert.Equal(200m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task Submit_WithoutNumberSeries_FailsWithoutFinancialChange()
    {
        await using var harness = await WithdrawalHarness.CreateAsync(seedWithdrawalSeries: false);
        await harness.SeedWalletAsync(1000m, "topup-1");
        await harness.SeedBankDetailAsync(harness.Customer.Id);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Withdrawals.SubmitAsync(
                harness.Customer.Id,
                new CreateWithdrawalRequest(100m, "wd-no-series"),
                CancellationToken.None));

        Assert.Empty(await harness.Db.WithdrawalRequests.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.WalletTransactions
            .AsNoTracking()
            .Where(x => x.Type == WalletTransactionType.WithdrawalDebit)
            .ToListAsync());
        Assert.Equal(1000m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task Submit_ForInactiveOrUnknownCustomer_ProducesNoDebitOrTicket()
    {
        await using var harness = await WithdrawalHarness.CreateAsync();
        harness.InactiveCustomer.Deactivate();
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Withdrawals.SubmitAsync(
                harness.InactiveCustomer.Id,
                new CreateWithdrawalRequest(100m, "wd-inactive"),
                CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Withdrawals.SubmitAsync(
                999999,
                new CreateWithdrawalRequest(100m, "wd-unknown"),
                CancellationToken.None));

        Assert.Empty(await harness.Db.WithdrawalRequests.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Submit_RetryWithSameIdempotencyKey_ReturnsSameRequestWithoutDuplicates()
    {
        await using var harness = await WithdrawalHarness.CreateAsync();
        await harness.SeedWalletAsync(1000m, "topup-1");
        await harness.SeedBankDetailAsync(harness.Customer.Id);

        var first = await harness.Withdrawals.SubmitAsync(
            harness.Customer.Id,
            new CreateWithdrawalRequest(250m, "wd-retry"),
            CancellationToken.None);
        var second = await harness.Withdrawals.SubmitAsync(
            harness.Customer.Id,
            new CreateWithdrawalRequest(250m, "wd-retry"),
            CancellationToken.None);

        Assert.Equal(first.PublicId, second.PublicId);
        Assert.Single(await harness.Db.WithdrawalRequests.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.WalletTransactions
            .AsNoTracking()
            .Where(x => x.Type == WalletTransactionType.WithdrawalDebit)
            .ToListAsync());
        Assert.Equal(750m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task CustomerCannotReadAnotherCustomersWithdrawal()
    {
        await using var harness = await WithdrawalHarness.CreateAsync();
        await harness.SeedWalletAsync(1000m, "topup-1");
        await harness.SeedBankDetailAsync(harness.Customer.Id);
        var created = await harness.Withdrawals.SubmitAsync(
            harness.Customer.Id,
            new CreateWithdrawalRequest(100m, "wd-private"),
            CancellationToken.None);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Withdrawals.GetForCustomerAsync(
                harness.OtherCustomer.Id, created.PublicId, CancellationToken.None));
        Assert.Empty(await harness.Withdrawals.ListForCustomerAsync(
            harness.OtherCustomer.Id, CancellationToken.None));
    }

    [Fact]
    public async Task CustomerResultsNeverExposeFullAccountNumber()
    {
        await using var harness = await WithdrawalHarness.CreateAsync();
        await harness.SeedWalletAsync(1000m, "topup-1");
        await harness.SeedBankDetailAsync(harness.Customer.Id);
        var created = await harness.Withdrawals.SubmitAsync(
            harness.Customer.Id,
            new CreateWithdrawalRequest(100m, "wd-masked"),
            CancellationToken.None);

        Assert.Equal($"{Bank} ···· 9012", created.MaskedDestination);
        var listJson = JsonSerializer.Serialize(
            await harness.Withdrawals.ListForCustomerAsync(harness.Customer.Id, CancellationToken.None));
        Assert.DoesNotContain(Account, listJson);

        var staff = await harness.Withdrawals.GetForStaffAsync(created.PublicId, CancellationToken.None);
        var staffJson = JsonSerializer.Serialize(staff);
        Assert.DoesNotContain(Account, staffJson);
        Assert.Equal($"{Bank} ···· 9012", staff.MaskedDestination);
    }

    [Fact]
    public async Task StaffBankDetailView_ReturnsFullAccountNumber()
    {
        await using var harness = await WithdrawalHarness.CreateAsync();
        await harness.SeedWalletAsync(1000m, "topup-1");
        await harness.SeedBankDetailAsync(harness.Customer.Id);
        var created = await harness.Withdrawals.SubmitAsync(
            harness.Customer.Id,
            new CreateWithdrawalRequest(100m, "wd-staff-view"),
            CancellationToken.None);

        var detail = await harness.Withdrawals.GetBankDetailForStaffAsync(
            harness.Staff.Id, created.PublicId, CancellationToken.None);

        Assert.Equal(created.PublicId, detail.WithdrawalPublicId);
        Assert.Equal(Holder, detail.AccountHolderName);
        Assert.Equal(Bank, detail.BankName);
        Assert.Equal(Account, detail.AccountNumber);
        Assert.Equal(Ifsc, detail.Ifsc);

        Assert.Single(await harness.Db.AuditLogs
            .AsNoTracking()
            .Where(x => x.Action == "WithdrawalBankViewed")
            .ToListAsync());
    }

    [Fact]
    public async Task StaffBankDetailView_FailsClosedWhenKeysAreUnavailable()
    {
        await using var harness = await WithdrawalHarness.CreateAsync();
        await harness.SeedWalletAsync(1000m, "topup-1");
        await harness.SeedBankDetailAsync(harness.Customer.Id);
        var created = await harness.Withdrawals.SubmitAsync(
            harness.Customer.Id,
            new CreateWithdrawalRequest(100m, "wd-unreadable"),
            CancellationToken.None);

        var foreignService = new WithdrawalService(
            harness.Db,
            harness.TimeProvider,
            Options.Create(new PaymentOptions
            {
                Provider = "Mock",
                Currency = "INR",
                PaymentExpiryMinutes = 15,
                MockSigningSecret = "test-signing-secret"
            }),
            new NumberSeriesService(harness.Db, harness.TimeProvider),
            new BankDetailProtector(new EphemeralDataProtectionProvider()),
            new TestNotificationEventWriter(harness.Db, harness.TimeProvider));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            foreignService.GetBankDetailForStaffAsync(
                harness.Staff.Id, created.PublicId, CancellationToken.None));

        Assert.DoesNotContain(Account, exception.Message);
    }

    [Fact]
    public async Task Reject_RestoresFundsExactlyOnceAndIsIdempotent()
    {
        await using var harness = await WithdrawalHarness.CreateAsync();
        await harness.SeedWalletAsync(1000m, "topup-1");
        await harness.SeedBankDetailAsync(harness.Customer.Id);
        var created = await harness.Withdrawals.SubmitAsync(
            harness.Customer.Id,
            new CreateWithdrawalRequest(250m, "wd-reject"),
            CancellationToken.None);

        var rejected = await harness.Withdrawals.RejectAsync(
            harness.Staff.Id, created.PublicId, new DecideWithdrawalRequest("Bank account closed."),
            CancellationToken.None);

        Assert.Equal(WithdrawalRequestStatus.Rejected, rejected.Status);
        Assert.Equal("Bank account closed.", rejected.DecisionRemarks);
        Assert.Equal(1000m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);

        var reversal = await harness.Db.WalletTransactions
            .AsNoTracking()
            .SingleAsync(x => x.Type == WalletTransactionType.WithdrawalReversal);
        Assert.Equal(750m, reversal.BalanceBefore);
        Assert.Equal(250m, reversal.Amount);
        Assert.Equal(1000m, reversal.BalanceAfter);

        var replayed = await harness.Withdrawals.RejectAsync(
            harness.Staff.Id, created.PublicId, new DecideWithdrawalRequest("Bank account closed."),
            CancellationToken.None);
        Assert.Equal(WithdrawalRequestStatus.Rejected, replayed.Status);
        Assert.Equal(1000m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Equal(1, await harness.Db.WalletTransactions
            .CountAsync(x => x.Type == WalletTransactionType.WithdrawalReversal));

        await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Withdrawals.CompleteAsync(
                harness.Staff.Id,
                created.PublicId,
                new CompleteWithdrawalRequest("NEFT123456", null),
                CancellationToken.None));
    }

    [Fact]
    public async Task Complete_MarksPaidWithoutSecondDebitAndIsIdempotent()
    {
        await using var harness = await WithdrawalHarness.CreateAsync();
        await harness.SeedWalletAsync(1000m, "topup-1");
        await harness.SeedBankDetailAsync(harness.Customer.Id);
        var created = await harness.Withdrawals.SubmitAsync(
            harness.Customer.Id,
            new CreateWithdrawalRequest(250m, "wd-complete"),
            CancellationToken.None);

        var completed = await harness.Withdrawals.CompleteAsync(
            harness.Staff.Id,
            created.PublicId,
            new CompleteWithdrawalRequest("NEFT123456", "Paid via NEFT."),
            CancellationToken.None);

        Assert.Equal(WithdrawalRequestStatus.Completed, completed.Status);
        Assert.Equal("NEFT123456", completed.TransferReference);
        Assert.Equal("Paid via NEFT.", completed.DecisionRemarks);
        Assert.NotNull(completed.DecidedAt);
        Assert.Equal(
            "NEFT123456",
            (await harness.Db.WithdrawalRequests.AsNoTracking().SingleAsync()).TransferReference);
        Assert.Equal(750m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Single(await harness.Db.WalletTransactions
            .AsNoTracking()
            .Where(x => x.Type == WalletTransactionType.WithdrawalDebit)
            .ToListAsync());
        Assert.Empty(await harness.Db.WalletTransactions
            .AsNoTracking()
            .Where(x => x.Type == WalletTransactionType.WithdrawalReversal)
            .ToListAsync());

        var replayed = await harness.Withdrawals.CompleteAsync(
            harness.Staff.Id,
            created.PublicId,
            new CompleteWithdrawalRequest("NEFT123456", "Paid via NEFT."),
            CancellationToken.None);
        Assert.Equal(WithdrawalRequestStatus.Completed, replayed.Status);
        Assert.Equal(750m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);

        await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Withdrawals.RejectAsync(
                harness.Staff.Id, created.PublicId, new DecideWithdrawalRequest(null),
                CancellationToken.None));
    }

    [Fact]
    public async Task Complete_WithoutTransferReference_RejectsWithoutStateChange()
    {
        await using var harness = await WithdrawalHarness.CreateAsync();
        await harness.SeedWalletAsync(1000m, "topup-1");
        await harness.SeedBankDetailAsync(harness.Customer.Id);
        var created = await harness.Withdrawals.SubmitAsync(
            harness.Customer.Id,
            new CreateWithdrawalRequest(250m, "wd-no-utr"),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Withdrawals.CompleteAsync(
                harness.Staff.Id,
                created.PublicId,
                new CompleteWithdrawalRequest("  ", null),
                CancellationToken.None));

        Assert.Equal("transferReference", exception.Field);
        var stored = await harness.Db.WithdrawalRequests.AsNoTracking().SingleAsync();
        Assert.Equal(WithdrawalRequestStatus.Pending, stored.Status);
        Assert.Null(stored.TransferReference);
        Assert.Equal(750m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task ConcurrentWithdrawals_CannotOverdrawWallet()
    {
        const string sqlServerInstance = @".\SQLEXPRESS";
        var databaseName = $"DoodhDirect_Test_WithdrawalRace_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = sqlServerInstance,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            MultipleActiveResultSets = true,
        };
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseSqlServer(builder.ConnectionString, sql =>
            {
                sql.EnableRetryOnFailure(maxRetryCount: 5);
                sql.CommandTimeout(60);
            })
            .Options;

        await using var seedDb = new DoodhDirectDbContext(options);
        try
        {
            await seedDb.Database.EnsureCreatedAsync();
        }
        catch (SqlException ex) when (ex.Number is -2 or 2 or 40 or 53)
        {
            await seedDb.DisposeAsync();
            throw new InvalidOperationException(
                $"This concurrency test requires the local SQL Server Express instance ('{sqlServerInstance}'). " +
                "Start the SQLEXPRESS service and re-run dotnet test.",
                ex);
        }

        var clock = new TestClock(new DateTime(2026, 8, 16, 7, 30, 0, DateTimeKind.Unspecified));
        var paymentOptions = Options.Create(new PaymentOptions
        {
            Provider = "Mock",
            Currency = "INR",
            PaymentExpiryMinutes = 15,
            MockSigningSecret = "test-signing-secret"
        });
        var protector = new BankDetailProtector(new EphemeralDataProtectionProvider());

        var customer = new User(UserType.Customer);
        customer.SetProfile("Race Customer");
        seedDb.Users.Add(customer);
        seedDb.NumberSeries.Add(new NumberSeries(
            "WITHDRAWAL", "Withdrawal Number", "WD/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never));
        await seedDb.SaveChangesAsync();

        var seedTime = new TestIndiaTimeProvider(clock);
        var seedCustomers = new CustomerService(
            seedDb, new NumberSeriesService(seedDb, seedTime), seedTime, protector);
        var wallet = new Wallet(customer.Id, "INR");
        seedDb.Wallets.Add(wallet);
        wallet.Credit(WalletTransactionType.TopUp, 1000m, "race-topup", "Wallet top-up", seedTime.Now);
        await seedDb.SaveChangesAsync();
        await seedCustomers.SaveBankDetailAsync(
            customer.Id,
            new UpsertCustomerBankDetailRequest(Holder, Bank, Account, Ifsc),
            CancellationToken.None);
        await seedDb.DisposeAsync();

        var first = AttemptSubmitAsync(options, protector, clock, paymentOptions, customer.Id, 600m, "race-1");
        var second = AttemptSubmitAsync(options, protector, clock, paymentOptions, customer.Id, 600m, "race-2");
        var outcomes = await Task.WhenAll(first, second);

        Assert.Equal(1, outcomes.Count(x => x is null));
        var failure = Assert.Single(outcomes.Where(x => x is not null));
        Assert.True(
            failure is InsufficientWalletBalanceException || failure is ConflictException,
            $"Unexpected failure: {failure!.GetType().Name}: {failure.Message}");

        await using var verifyDb = new DoodhDirectDbContext(options);
        try
        {
            Assert.Equal(400m, (await verifyDb.Wallets.AsNoTracking().SingleAsync()).Balance);
            Assert.Equal(-600m, await verifyDb.WalletTransactions
                .Where(x => x.Type == WalletTransactionType.WithdrawalDebit)
                .SumAsync(x => x.Amount));
            Assert.Single(await verifyDb.WithdrawalRequests.AsNoTracking().ToListAsync());
        }
        finally
        {
            try
            {
                await verifyDb.Database.EnsureDeletedAsync();
            }
            catch
            {
                // Best-effort cleanup; the database name is unique per run.
            }

            await verifyDb.DisposeAsync();
        }
    }

    private static async Task<Exception?> AttemptSubmitAsync(
        DbContextOptions<DoodhDirectDbContext> options,
        BankDetailProtector protector,
        TestClock clock,
        IOptions<PaymentOptions> paymentOptions,
        long customerId,
        decimal amount,
        string idempotencyKey)
    {
        await using var db = new DoodhDirectDbContext(options);
        var time = new TestIndiaTimeProvider(clock);
        var service = new WithdrawalService(
            db, time, paymentOptions, new NumberSeriesService(db, time), protector,
            new TestNotificationEventWriter(db, clock));
        try
        {
            await service.SubmitAsync(
                customerId, new CreateWithdrawalRequest(amount, idempotencyKey), CancellationToken.None);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class WithdrawalHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private WithdrawalHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            User customer,
            User otherCustomer,
            User inactiveCustomer,
            User staff,
            TestIndiaTimeProvider timeProvider,
            CustomerService customers,
            WithdrawalService withdrawals)
        {
            this.connection = connection;
            Db = db;
            Customer = customer;
            OtherCustomer = otherCustomer;
            InactiveCustomer = inactiveCustomer;
            Staff = staff;
            TimeProvider = timeProvider;
            Customers = customers;
            Withdrawals = withdrawals;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public User OtherCustomer { get; }
        public User InactiveCustomer { get; }
        public User Staff { get; }
        public TestIndiaTimeProvider TimeProvider { get; }
        public CustomerService Customers { get; }
        public WithdrawalService Withdrawals { get; }

        public static async Task<WithdrawalHarness> CreateAsync(bool seedWithdrawalSeries = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");

            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            customer.SetContact("+919876543210", null);
            var otherCustomer = new User(UserType.Customer);
            otherCustomer.SetProfile("Other Customer");
            var inactiveCustomer = new User(UserType.Customer);
            inactiveCustomer.SetProfile("Inactive Customer");
            var staff = new User(UserType.SystemAdministrator);
            staff.SetProfile("Accountant");
            db.Users.AddRange(customer, otherCustomer, inactiveCustomer, staff);
            await db.SaveChangesAsync();

            if (seedWithdrawalSeries)
            {
                db.NumberSeries.Add(new NumberSeries(
                    "WITHDRAWAL", "Withdrawal Number", "WD/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never));
                await db.SaveChangesAsync();
            }

            var clock = new TestClock(new DateTime(2026, 8, 16, 7, 30, 0, DateTimeKind.Unspecified));
            var timeProvider = new TestIndiaTimeProvider(clock);
            var paymentOptions = Options.Create(new PaymentOptions
            {
                Provider = "Mock",
                Currency = "INR",
                PaymentExpiryMinutes = 15,
                MockSigningSecret = "test-signing-secret"
            });
            var writer = new TestNotificationEventWriter(db, clock);
            var numberSeries = new NumberSeriesService(db, timeProvider);
            var protector = new BankDetailProtector(new EphemeralDataProtectionProvider());
            return new WithdrawalHarness(
                connection,
                db,
                customer,
                otherCustomer,
                inactiveCustomer,
                staff,
                timeProvider,
                new CustomerService(db, numberSeries, timeProvider, protector),
                new WithdrawalService(db, timeProvider, paymentOptions, numberSeries, protector, writer));
        }

        public async Task SeedWalletAsync(decimal amount, string idempotencyKey)
        {
            var wallet = await Db.Wallets.SingleOrDefaultAsync(x => x.CustomerId == Customer.Id);
            if (wallet is null)
            {
                wallet = new Wallet(Customer.Id, "INR");
                Db.Wallets.Add(wallet);
            }

            wallet.Credit(WalletTransactionType.TopUp, amount, idempotencyKey, "Wallet top-up", TimeProvider.Now);
            await Db.SaveChangesAsync();
        }

        public async Task SeedBankDetailAsync(long userId) =>
            await Customers.SaveBankDetailAsync(
                userId,
                new UpsertCustomerBankDetailRequest(Holder, Bank, Account, Ifsc),
                CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
