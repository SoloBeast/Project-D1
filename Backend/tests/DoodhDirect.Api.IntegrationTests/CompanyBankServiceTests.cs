using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DoodhDirect.Api.Controllers;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Setup;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class CompanyBankServiceTests
{
    private const string Holder = "DoodhDirect Foods Pvt Ltd";
    private const string Bank = "State Bank of India";
    private const string Account = "123456789012";
    private const string Ifsc = "SBIN0001234";
    private const long ActorId = 7;

    [Fact]
    public async Task Get_ReturnsNullWhenNeverConfigured()
    {
        await using var harness = await CompanyBankHarness.CreateAsync();
        Assert.Null(await harness.Service.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Update_CreatesMaskedRecordAndNeverExposesFullNumber()
    {
        await using var harness = await CompanyBankHarness.CreateAsync();

        var result = await harness.Service.UpdateAsync(
            new UpsertCompanyBankDetailRequest(Holder, Bank, Account, Ifsc),
            ActorId, "127.0.0.1", "tests",
            CancellationToken.None);

        Assert.Equal(Holder, result.AccountHolderName);
        Assert.Equal($"···· 9012", result.MaskedAccountNumber);
        Assert.Equal("9012", result.AccountLast4);
        Assert.True(result.IsConfigured);

        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(Account, json);

        var stored = await harness.Db.CompanyBankDetails.AsNoTracking().SingleAsync();
        Assert.NotEqual(Account, stored.ProtectedAccountNumber);
        Assert.Equal("9012", stored.AccountLast4);
    }

    [Fact]
    public async Task Update_WithoutAccountNumber_KeepsStoredNumber()
    {
        await using var harness = await CompanyBankHarness.CreateAsync();
        await harness.Service.UpdateAsync(
            new UpsertCompanyBankDetailRequest(Holder, Bank, Account, Ifsc),
            ActorId, null, null, CancellationToken.None);

        var updated = await harness.Service.UpdateAsync(
            new UpsertCompanyBankDetailRequest("Renamed Holder", "New Bank", null, "HDFC0001234"),
            ActorId, null, null, CancellationToken.None);

        Assert.Equal("Renamed Holder", updated.AccountHolderName);
        Assert.Equal($"···· 9012", updated.MaskedAccountNumber);
        Assert.Equal(1, await harness.Db.CompanyBankDetails.CountAsync());

        var full = await harness.Service.GetDecryptedForExportAsync(ActorId, null, null, CancellationToken.None);
        Assert.Equal(Account, full.AccountNumber);
        Assert.Equal("HDFC0001234", full.Ifsc);
    }

    [Theory]
    [InlineData("", Bank, Account, Ifsc, "accountHolderName")]
    [InlineData(Holder, "", Account, Ifsc, "bankName")]
    [InlineData(Holder, Bank, "12345", Ifsc, "accountNumber")]
    [InlineData(Holder, Bank, Account, "BADIFSC", "ifsc")]
    public async Task Update_RejectsInvalidDetails(
        string holder, string bank, string account, string ifsc, string field)
    {
        await using var harness = await CompanyBankHarness.CreateAsync();

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.UpdateAsync(
                new UpsertCompanyBankDetailRequest(holder, bank, account, ifsc),
                ActorId, null, null, CancellationToken.None));

        Assert.Equal(field, exception.Field);
        Assert.Empty(await harness.Db.CompanyBankDetails.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Protector_PurposeIsolation_CustomerPurposeCannotDecryptCompanyNumber()
    {
        await using var harness = await CompanyBankHarness.CreateAsync();
        await harness.Service.UpdateAsync(
            new UpsertCompanyBankDetailRequest(Holder, Bank, Account, Ifsc),
            ActorId, null, null, CancellationToken.None);

        var stored = await harness.Db.CompanyBankDetails.AsNoTracking().SingleAsync();
        var customerProtector = new Infrastructure.Customer.BankDetailProtector(
            harness.Services.GetRequiredService<IDataProtectionProvider>());

        Assert.Throws<CryptographicException>(() => customerProtector.Unprotect(stored.ProtectedAccountNumber));
    }

    [Fact]
    public async Task Update_AuditsMaskedBeforeAndAfterWithoutPlaintext()
    {
        await using var harness = await CompanyBankHarness.CreateAsync();
        await harness.Service.UpdateAsync(
            new UpsertCompanyBankDetailRequest(Holder, Bank, Account, Ifsc),
            ActorId, "10.0.0.9", "audit-test",
            CancellationToken.None);
        await harness.Service.UpdateAsync(
            new UpsertCompanyBankDetailRequest(Holder, Bank, "987654321098", Ifsc),
            ActorId, "10.0.0.9", "audit-test",
            CancellationToken.None);

        var audits = await harness.Db.AuditLogs
            .AsNoTracking()
            .Where(x => x.Action == CompanyBankService.ActionUpdated)
            .OrderBy(x => x.Id)
            .ToListAsync();

        Assert.Equal(2, audits.Count);
        Assert.All(audits, audit =>
        {
            var payload = (audit.OldValueJson ?? string.Empty) + (audit.NewValueJson ?? string.Empty);
            Assert.DoesNotContain(Account, payload);
            Assert.DoesNotContain("987654321098", payload);
            Assert.Equal("10.0.0.9", audit.IPAddress);
        });
        Assert.Contains("9012", audits[0].NewValueJson);
    }

    [Fact]
    public async Task GetDecryptedForExport_ThrowsWhenUnconfiguredAndAuditsView()
    {
        await using var harness = await CompanyBankHarness.CreateAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.GetDecryptedForExportAsync(ActorId, null, null, CancellationToken.None));

        await harness.Service.UpdateAsync(
            new UpsertCompanyBankDetailRequest(Holder, Bank, Account, Ifsc),
            ActorId, null, null, CancellationToken.None);
        var full = await harness.Service.GetDecryptedForExportAsync(ActorId, "10.0.0.2", "export", CancellationToken.None);

        Assert.Equal(Account, full.AccountNumber);
        Assert.Single(await harness.Db.AuditLogs
            .AsNoTracking()
            .Where(x => x.Action == CompanyBankService.ActionViewedForExport)
            .ToListAsync());
    }

    [Theory]
    [InlineData(nameof(CompanyBankController.Get), AuthorizationCodes.SetupCompanyBankRead)]
    [InlineData(nameof(CompanyBankController.Update), AuthorizationCodes.SetupCompanyBankManage)]
    public void CompanyBankRoute_RequiresExpectedPermission(string methodName, string permission)
    {
        var method = typeof(CompanyBankController).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);

        AuthorizeAttribute? authorize;
        if (methodName == nameof(CompanyBankController.Update))
        {
            authorize = method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>()
                .SingleOrDefault(a => a.Policy == $"permission:{permission}");
        }
        else
        {
            authorize = typeof(CompanyBankController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>()
                .SingleOrDefault(a => a.Policy == $"permission:{permission}");
        }

        Assert.NotNull(authorize);
        Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
        Assert.Empty(typeof(CompanyBankController).GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
    }

    private sealed class CompanyBankHarness : IAsyncDisposable
    {
        private CompanyBankHarness(
            SqliteConnection connection,
            ServiceProvider services,
            DoodhDirectDbContext db,
            CompanyBankService service)
        {
            Connection = connection;
            Services = services;
            Db = db;
            Service = service;
        }

        public SqliteConnection Connection { get; }
        public ServiceProvider Services { get; }
        public DoodhDirectDbContext Db { get; }
        public CompanyBankService Service { get; }

        public static async Task<CompanyBankHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection()
                .AddDataProtection()
                .SetApplicationName("DoodhDirect-CompanyBank-Tests")
                .Services
                .BuildServiceProvider();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var clock = new TestClock(new DateTime(2026, 8, 16, 7, 30, 0, DateTimeKind.Unspecified));
            var timeProvider = new TestIndiaTimeProvider(clock);
            var service = new CompanyBankService(
                db,
                timeProvider,
                new CompanyBankDetailProtector(services.GetRequiredService<IDataProtectionProvider>()));
            return new CompanyBankHarness(connection, services, db, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
            await Services.DisposeAsync();
        }
    }
}
