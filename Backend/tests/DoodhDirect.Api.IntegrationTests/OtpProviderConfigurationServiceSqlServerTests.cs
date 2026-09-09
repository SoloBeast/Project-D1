using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Infrastructure.OtpProvider;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Regression tests for OtpProviderConfigurationService against a real local
/// SQL Server Express instance (.\SQLEXPRESS). They exercise the EF Core SQL
/// translation of LoadAsync (Key.StartsWith(...)) which previously produced a
/// "could not be translated" error on SQL Server while silently working on
/// SQLite/in-memory providers. Each test creates a dedicated throwaway
/// database and deletes it on dispose so no production data is touched.
/// Requires the local SQLEXPRESS service to be running.
/// </summary>
public sealed class OtpProviderConfigurationServiceSqlServerTests
{
    private const string SqlServerInstance = @".\SQLEXPRESS";

    [Fact]
    public async Task GetAsync_OnSqlServer_WithZeroRows_ReturnsNotConfigured()
    {
        await using var harness = await SqlServerTestHarness.CreateAsync();
        var service = CreateService(harness.Db);

        var result = await service.GetAsync(CancellationToken.None);

        Assert.Equal(OtpProviderConfiguration.ProviderMsg91, result.Provider);
        Assert.False(result.Enabled);
        Assert.Null(result.WidgetId);
        Assert.Null(result.Environment);
        Assert.False(result.Configured);
        Assert.Equal("Not Configured", result.Status);
    }

    [Fact]
    public async Task GetAsync_OnSqlServer_WithConfiguredRows_ReturnsConfiguredState()
    {
        await using var harness = await SqlServerTestHarness.CreateAsync();
        await SeedConfiguredAsync(harness.Db);
        var service = CreateService(harness.Db);

        var result = await service.GetAsync(CancellationToken.None);

        Assert.Equal(OtpProviderConfiguration.ProviderMsg91, result.Provider);
        Assert.True(result.Enabled);
        Assert.Equal("widget-1", result.WidgetId);
        Assert.Equal(OtpProviderConfiguration.EnvironmentTest, result.Environment);
        Assert.True(result.Configured);
        Assert.Equal("Configured", result.Status);
    }

    [Fact]
    public async Task UpdateAsync_OnSqlServer_PersistsProtectedAuthKeyAndReadsBack()
    {
        await using var harness = await SqlServerTestHarness.CreateAsync();
        var protector = new OtpProviderSecretProtector(new EphemeralDataProtectionProvider());
        var service = CreateService(harness.Db, protector);

        await service.UpdateAsync(
            new UpdateOtpProviderConfigurationRequest(true, "widget-1", "authkey-1", "Test"),
            42,
            "127.0.0.1",
            "sqlserver-test",
            CancellationToken.None);

        var result = await service.GetAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Equal("widget-1", result.WidgetId);
        Assert.Equal(OtpProviderConfiguration.EnvironmentTest, result.Environment);
        Assert.True(result.Configured);

        var storedAuthKey = await harness.Db.SystemConfigurations
            .SingleAsync(x => x.Key == OtpProviderConfigurationKeys.AuthKey);
        Assert.True(storedAuthKey.IsSensitive);
        Assert.Equal("authkey-1", protector.Unprotect(storedAuthKey.Value));
    }

    private static OtpProviderConfigurationService CreateService(
        DoodhDirectDbContext db,
        OtpProviderSecretProtector? protector = null) =>
        new(
            db,
            new TestClock(new DateTime(2026, 9, 1, 11, 30, 0)),
            protector ?? new OtpProviderSecretProtector(new EphemeralDataProtectionProvider()),
            new NoOpMsg91Provider());

    private static async Task SeedConfiguredAsync(DoodhDirectDbContext db)
    {
        var protector = new OtpProviderSecretProtector(new EphemeralDataProtectionProvider());
        db.SystemConfigurations.Add(new SystemConfiguration(
            OtpProviderConfigurationKeys.Provider,
            OtpProviderConfiguration.ProviderMsg91,
            "string",
            "OTP provider (MSG91)."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            OtpProviderConfigurationKeys.Enabled,
            "true",
            "bool",
            "Whether the OTP provider is enabled for sending identity OTPs."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            OtpProviderConfigurationKeys.WidgetId,
            "widget-1",
            "string",
            "MSG91 OTP widget identifier."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            OtpProviderConfigurationKeys.Environment,
            OtpProviderConfiguration.EnvironmentTest,
            "string",
            "MSG91 environment (Test or Production)."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            OtpProviderConfigurationKeys.AuthKey,
            protector.Protect("authkey-1"),
            "sensitive",
            "MSG91 auth key (protected, never exposed).",
            true));
        await db.SaveChangesAsync();
    }

    private sealed class SqlServerTestHarness : IAsyncDisposable
    {
        private SqlServerTestHarness(DoodhDirectDbContext db) => Db = db;

        public DoodhDirectDbContext Db { get; }

        public static async Task<SqlServerTestHarness> CreateAsync()
        {
            var databaseName = $"DoodhDirect_Test_Otp_{Guid.NewGuid():N}";
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = SqlServerInstance,
                InitialCatalog = databaseName,
                IntegratedSecurity = true,
                Encrypt = true,
                TrustServerCertificate = true,
                MultipleActiveResultSets = true,
            };

            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlServer(builder.ConnectionString, sql => sql.CommandTimeout(60))
                .Options;
            var db = new DoodhDirectDbContext(options);

            try
            {
                await db.Database.EnsureCreatedAsync();
            }
            catch (SqlException ex) when (IsServerUnavailable(ex))
            {
                await db.DisposeAsync();
                throw new InvalidOperationException(
                    $"This regression test requires the local SQL Server Express instance ('{SqlServerInstance}'). "
                    + "Start the SQLEXPRESS service and re-run dotnet test.",
                    ex);
            }

            return new SqlServerTestHarness(db);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Db.Database.EnsureDeletedAsync();
            }
            catch
            {
                // Best-effort cleanup; the database name is unique per run so a
                // leftover database does not affect any other test.
            }

            await Db.DisposeAsync();
        }

        private static bool IsServerUnavailable(SqlException ex) =>
            ex.Number is -2 or 2 or 40 or 53;
    }

    private sealed class NoOpMsg91Provider : IMsg91OtpProvider
    {
        public Task<Msg91OtpSendResult> SendAsync(
            Msg91OtpSendRequest request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<Msg91OtpSendResult> RetryAsync(
            Msg91OtpRetryRequest request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<Msg91OtpVerifyResult> VerifyAsync(
            Msg91OtpVerifyRequest request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<Msg91OtpValidationResult> ValidateAccessTokenAsync(
            Msg91OtpAccessTokenRequest request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }
}
