using DoodhDirect.Application.Common;
using DoodhDirect.Application.Dairy;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Dairy;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Dairy;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class DairyServiceTests
{
    [Fact]
    public async Task RecordProduction_CreatesExactlyOneMatchingBatch()
    {
        await using var harness = await DairyHarness.CreateAsync();

        var result = await harness.RecordProductionAsync(24.125m);

        var production = await harness.Db.MilkProductions.AsNoTracking().SingleAsync();
        var batch = await harness.Db.MilkBatches.AsNoTracking().SingleAsync();
        Assert.Equal(production.Id, batch.ProductionId);
        Assert.Equal(production.PublicId, result.PublicId);
        Assert.Equal(batch.PublicId, result.Batch.PublicId);
        Assert.Equal(24.125m, batch.QuantityProduced);
        Assert.Equal(24.125m, result.Batch.AvailableQuantity);
        Assert.Equal("L", batch.Unit);
        Assert.Equal(MilkBatchStatus.Available, batch.Status);
    }

    [Theory]
    [InlineData("quantity", 0, 5, "L")]
    [InlineData("quantity", -1, 5, "L")]
    [InlineData("precision", 1.2345, 5, "L")]
    [InlineData("buffalo", 10, 0, "L")]
    [InlineData("unit", 10, 5, "KG")]
    public async Task RecordProduction_RejectsInvalidValues(
        string scenario,
        decimal quantity,
        int buffaloCount,
        string unit)
    {
        await using var harness = await DairyHarness.CreateAsync();
        var request = new RecordMilkProductionRequest(
            harness.Clock.UtcNow,
            "Morning",
            buffaloCount,
            quantity,
            unit,
            null);

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.RecordProductionAsync(
                harness.ManagerActor,
                harness.Branch.Id,
                request,
                CancellationToken.None));

        Assert.NotNull(exception.Field);
        Assert.Empty(await harness.Db.MilkProductions.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.MilkBatches.AsNoTracking().ToListAsync());
        Assert.False(string.IsNullOrWhiteSpace(scenario));
    }

    [Fact]
    public async Task RecordProduction_RejectsNonUtcAndFutureTimestamps()
    {
        await using var harness = await DairyHarness.CreateAsync();
        var local = DateTime.SpecifyKind(harness.Clock.UtcNow, DateTimeKind.Local);
        var future = harness.Clock.UtcNow.AddMinutes(6);

        await Assert.ThrowsAsync<ValidationAppException>(() => harness.RecordProductionAsync(10m, local));
        await Assert.ThrowsAsync<ValidationAppException>(() => harness.RecordProductionAsync(10m, future));

        Assert.Empty(await harness.Db.MilkProductions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task BranchScope_DeniesAnotherBranchAndAllowsGlobalActor()
    {
        await using var harness = await DairyHarness.CreateAsync();
        var otherBranchActor = new DairyActor(harness.ManagerUserId, [harness.OtherBranch.Id]);

        await Assert.ThrowsAsync<ForbiddenAppException>(() => harness.Service.GetAvailabilityAsync(
            otherBranchActor,
            harness.Branch.Id,
            CancellationToken.None));

        var availability = await harness.Service.GetAvailabilityAsync(
            harness.GlobalActor,
            harness.Branch.Id,
            CancellationToken.None);
        Assert.Equal(harness.Branch.Id, availability.BranchId);
    }

    [Fact]
    public async Task ProductionToBatchUniqueness_IsEnforcedByDatabase()
    {
        await using var harness = await DairyHarness.CreateAsync();
        await harness.RecordProductionAsync(12m);
        var production = await harness.Db.MilkProductions.AsNoTracking().SingleAsync();
        harness.Db.MilkBatches.Add(new MilkBatch(
            harness.Branch.Id,
            production.Id,
            "MB-DUPLICATE",
            production.ProductionAtUtc,
            production.QuantityProduced,
            "L"));

        await Assert.ThrowsAsync<DbUpdateException>(() => harness.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Usage_DerivesAvailabilityAndExactRemainderExhaustsBatch()
    {
        await using var harness = await DairyHarness.CreateAsync();
        var production = await harness.RecordProductionAsync(20m);

        await harness.RecordUsageAsync(production.Batch.PublicId, 7.250m, "Delivery dispatch");
        var interim = await harness.Service.GetAvailabilityAsync(
            harness.ManagerActor,
            harness.Branch.Id,
            CancellationToken.None);
        Assert.Equal(20m, interim.QuantityProduced);
        Assert.Equal(7.250m, interim.QuantityUsed);
        Assert.Equal(12.750m, interim.AvailableQuantity);
        Assert.Equal(1, interim.AvailableBatchCount);

        await harness.RecordUsageAsync(production.Batch.PublicId, 12.750m, "Final dispatch");
        harness.Db.ChangeTracker.Clear();
        var batch = await harness.Db.MilkBatches.AsNoTracking().SingleAsync();
        var final = await harness.Service.GetAvailabilityAsync(
            harness.ManagerActor,
            harness.Branch.Id,
            CancellationToken.None);
        Assert.Equal(MilkBatchStatus.Exhausted, batch.Status);
        Assert.Equal(0m, final.AvailableQuantity);
        Assert.Equal(0, final.AvailableBatchCount);
        Assert.Equal(2, await harness.Db.MilkUsages.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Usage_RejectsOverdrawWithoutPersistingLedgerRow()
    {
        await using var harness = await DairyHarness.CreateAsync();
        var production = await harness.RecordProductionAsync(8m);
        await harness.RecordUsageAsync(production.Batch.PublicId, 3m, "Kitchen");

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.RecordUsageAsync(production.Batch.PublicId, 6m, "Overdraw"));

        Assert.Contains("5", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, await harness.Db.MilkUsages.AsNoTracking().CountAsync());
        var availability = await harness.Service.GetAvailabilityAsync(
            harness.ManagerActor,
            harness.Branch.Id,
            CancellationToken.None);
        Assert.Equal(5m, availability.AvailableQuantity);
    }

    [Fact]
    public async Task Usage_RejectsTimestampBeforeProduction()
    {
        await using var harness = await DairyHarness.CreateAsync();
        var productionAt = harness.Clock.UtcNow.AddHours(-1);
        var production = await harness.RecordProductionAsync(8m, productionAt);

        await Assert.ThrowsAsync<ValidationAppException>(() => harness.RecordUsageAsync(
            production.Batch.PublicId,
            1m,
            "Invalid time",
            productionAt.AddSeconds(-1)));

        Assert.Empty(await harness.Db.MilkUsages.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task HistoriesRemainBranchScoped()
    {
        await using var harness = await DairyHarness.CreateAsync();
        var main = await harness.RecordProductionAsync(10m);
        await harness.RecordUsageAsync(main.Batch.PublicId, 2m, "Main dispatch");
        var other = await harness.Service.RecordProductionAsync(
            harness.GlobalActor,
            harness.OtherBranch.Id,
            harness.ProductionRequest(6m),
            CancellationToken.None);
        await harness.Service.RecordUsageAsync(
            harness.GlobalActor,
            other.Batch.PublicId,
            harness.UsageRequest(1m, "Other dispatch"),
            CancellationToken.None);

        var production = await harness.Service.GetProductionHistoryAsync(
            harness.ManagerActor,
            harness.Branch.Id,
            null,
            null,
            CancellationToken.None);
        var usage = await harness.Service.GetUsageHistoryAsync(
            harness.ManagerActor,
            harness.Branch.Id,
            null,
            null,
            CancellationToken.None);

        Assert.Single(production);
        Assert.Equal(main.PublicId, production[0].PublicId);
        Assert.Single(usage);
        Assert.Equal("Main dispatch", usage[0].Purpose);
    }

    private sealed class DairyHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private DairyHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            TestClock clock,
            DairyService service,
            User manager,
            Branch branch,
            Branch otherBranch)
        {
            this.connection = connection;
            Db = db;
            Clock = clock;
            Service = service;
            Manager = manager;
            Branch = branch;
            OtherBranch = otherBranch;
        }

        public DoodhDirectDbContext Db { get; }
        public TestClock Clock { get; }
        public DairyService Service { get; }
        public User Manager { get; }
        public Branch Branch { get; }
        public Branch OtherBranch { get; }
        public long ManagerUserId => Manager.Id;
        public DairyActor ManagerActor => new(Manager.Id, [Branch.Id]);
        public DairyActor GlobalActor => new(Manager.Id, [], HasGlobalAccess: true);

        public RecordMilkProductionRequest ProductionRequest(
            decimal quantity,
            DateTime? productionAtUtc = null) => new(
                productionAtUtc ?? Clock.UtcNow.AddHours(-1),
                "Morning",
                12,
                quantity,
                "L",
                "Fresh production");

        public RecordMilkUsageRequest UsageRequest(
            decimal quantity,
            string purpose,
            DateTime? usedAtUtc = null) => new(
                usedAtUtc ?? Clock.UtcNow,
                quantity,
                purpose,
                null);

        public Task<MilkProductionResult> RecordProductionAsync(
            decimal quantity,
            DateTime? productionAtUtc = null) => Service.RecordProductionAsync(
                ManagerActor,
                Branch.Id,
                ProductionRequest(quantity, productionAtUtc),
                CancellationToken.None);

        public Task<MilkUsageResult> RecordUsageAsync(
            Guid batchId,
            decimal quantity,
            string purpose,
            DateTime? usedAtUtc = null) => Service.RecordUsageAsync(
                ManagerActor,
                batchId,
                UsageRequest(quantity, purpose, usedAtUtc),
                CancellationToken.None);

        public static async Task<DairyHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var branch = new Branch(
                "MAIN",
                "Main Branch",
                "Pune",
                "Maharashtra",
                18.5204m,
                73.8567m);
            var otherBranch = new Branch(
                "NORTH",
                "North Branch",
                "Pune",
                "Maharashtra",
                18.6000m,
                73.9000m);
            var manager = new User(UserType.Employee);
            manager.SetProfile("Dairy Manager");
            manager.SetContact("9000000099", "dairy.manager@example.test");
            db.AddRange(manager, branch, otherBranch);
            await db.SaveChangesAsync();
            var clock = new TestClock(new DateTime(2026, 8, 17, 10, 0, 0, DateTimeKind.Utc));
            return new DairyHarness(
                connection,
                db,
                clock,
                new DairyService(db, clock),
                manager,
                branch,
                otherBranch);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
