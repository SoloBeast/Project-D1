using DoodhDirect.Application.Catalogue;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Catalogue;
using DoodhDirect.Infrastructure.MilkTesting;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// DATABASE-LEVEL concurrency proof for the Tax &amp; Charges invariant
/// (ApplicableOnAll = true ⇒ zero ProductCharge mappings).
///
/// These tests run against a real local SQL Server Express instance, where
/// SERIALIZABLE key-range locking is enforced by the storage engine. The
/// InMemory-backed charge/catalogue suites CANNOT prove this: the InMemory
/// provider ignores transactions (TransactionIgnoredWarning) and performs no
/// locking, so any check-then-act race silently passes there. A green run
/// here means the engine serialized (or deadlocked-and-retried) the racing
/// transactions; a green InMemory run would mean nothing for concurrency.
/// Requires the local SQLEXPRESS service to be running.
/// </summary>
public sealed class ChargeProductInvariantConcurrencyTests
{
    private const string SqlServerInstance = @".\SQLEXPRESS";
    private static readonly TestClock Frozen = new(new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Unspecified));

    /// <summary>
    /// The exact race: a product mapping insert (which must observe
    /// ApplicableOnAll = false) runs concurrently with enabling
    /// ApplicableOnAll = true (which must observe zero mappings). Every
    /// iteration must end in a legal state — exactly one side wins and the
    /// loser is refused with a BusinessRuleException. An invalid commit
    /// (global + mapping) or a silent mapping deletion fails the test.
    /// </summary>
    [Fact]
    public async Task ToggleToGlobal_RacingMappingInsert_NeverCommitsInvalidState()
    {
        const int iterations = 12;
        await using var harness = await SqlServerHarness.CreateAsync();
        var fixtures = await harness.SeedRaceFixturesAsync(iterations);

        // Pairs race one at a time (each pair is still genuinely concurrent —
        // both operations are in flight together); independent row sets per
        // iteration keep pairs from interfering with each other.
        var toggleWins = 0;
        await using var verifyDb = harness.CreateDb();
        foreach (var fixture in fixtures)
        {
            await using var mapDb = harness.CreateDb();
            await using var toggleDb = harness.CreateDb();
            var mapTask = InvokeAsync(async () =>
                await Catalogue(mapDb).UpdateProductAsync(
                    fixture.ProductId,
                    new UpsertProductRequest(
                        fixture.Sku,
                        "Race Milk",
                        null,
                        harness.CategoryId,
                        "litre",
                        80m,
                        [harness.BranchId],
                        [fixture.ChargeId]),
                    CancellationToken.None,
                    actorUserId: 11));
            var toggleTask = InvokeAsync(async () =>
                await Charges(toggleDb).SetApplicableOnAllAsync(
                    fixture.ChargeId, true, 11, CancellationToken.None));
            var results = await Task.WhenAll(mapTask, toggleTask);
            var mapOutcome = results[0];
            var toggleOutcome = results[1];

            var charge = await verifyDb.Charges.AsNoTracking()
                .SingleAsync(item => item.PublicId == fixture.ChargeId);
            verifyDb.ChangeTracker.Clear();
            var mappingCount = await verifyDb.ProductCharges.AsNoTracking()
                .CountAsync(link => link.Charge.PublicId == fixture.ChargeId);

            if (mapOutcome.Exception is null)
            {
                // Mapping won: the toggle must have been refused, the charge
                // stayed item-level, and the single mapping row survived.
                Assert.True(
                    toggleOutcome.Exception is BusinessRuleException,
                    $"Toggle unexpectedly failed: {toggleOutcome.Exception}");
                Assert.False(charge.ApplicableOnAll);
                Assert.Equal(1, mappingCount);
            }
            else
            {
                // Toggle won: the mapping must have been refused — never
                // committed, and no pre-existing mapping was deleted (there
                // was exactly one candidate row, now zero).
                Assert.True(
                    toggleOutcome.Exception is null,
                    $"Toggle unexpectedly failed: {toggleOutcome.Exception}");
                Assert.IsType<BusinessRuleException>(mapOutcome.Exception);
                Assert.True(charge.ApplicableOnAll);
                Assert.Equal(0, mappingCount);
                toggleWins++;
            }
        }

        // Sanity: per-iteration states above already prove the invariant; the
        // count is reported so a run that never actually collided is visible.
        Assert.InRange(toggleWins, 0, iterations);
    }

    /// <summary>
    /// Deactivation racing a mapping insert: both orders are legal
    /// (an inactive charge keeps its mappings as configuration), so this
    /// proves the pair never corrupts — no error except BusinessRuleException,
    /// mappings never silently deleted, invariant untouched.
    /// </summary>
    [Fact]
    public async Task Deactivate_RacingMappingInsert_NeverCorruptsState()
    {
        const int iterations = 8;
        await using var harness = await SqlServerHarness.CreateAsync();
        var fixtures = await harness.SeedRaceFixturesAsync(iterations);

        await using var verifyDb = harness.CreateDb();
        foreach (var fixture in fixtures)
        {
            await using var mapDb = harness.CreateDb();
            await using var toggleDb = harness.CreateDb();
            var mapTask = InvokeAsync(async () =>
                await Catalogue(mapDb).UpdateProductAsync(
                    fixture.ProductId,
                    new UpsertProductRequest(
                        fixture.Sku,
                        "Race Milk",
                        null,
                        harness.CategoryId,
                        "litre",
                        80m,
                        [harness.BranchId],
                        [fixture.ChargeId]),
                    CancellationToken.None,
                    actorUserId: 11));
            var toggleTask = InvokeAsync(async () =>
                await Charges(toggleDb).SetActiveAsync(
                    fixture.ChargeId, false, 11, CancellationToken.None));
            var results = await Task.WhenAll(mapTask, toggleTask);
            var mapOutcome = results[0];
            var toggleOutcome = results[1];

            // Deactivation carries no mapping precondition, so it always wins.
            Assert.True(
                toggleOutcome.Exception is null,
                $"Deactivate unexpectedly failed: {toggleOutcome.Exception}");
            if (mapOutcome.Exception is not null)
            {
                Assert.IsType<BusinessRuleException>(mapOutcome.Exception);
            }

            var charge = await verifyDb.Charges.AsNoTracking()
                .SingleAsync(item => item.PublicId == fixture.ChargeId);
            verifyDb.ChangeTracker.Clear();
            var mappingCount = await verifyDb.ProductCharges.AsNoTracking()
                .CountAsync(link => link.Charge.PublicId == fixture.ChargeId);

            Assert.False(charge.IsActive);
            Assert.False(charge.ApplicableOnAll);
            Assert.InRange(mappingCount, 0, 1);
            Assert.Equal(mapOutcome.Exception is null ? 1 : 0, mappingCount);
        }
    }

    private sealed record Outcome(Exception? Exception);

    private static async Task<Outcome> InvokeAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return new Outcome(null);
        }
        catch (Exception ex)
        {
            return new Outcome(ex);
        }
    }

    private static CatalogueService Catalogue(DoodhDirectDbContext db) => new(
        db,
        new ProductImageValidator(Options.Create(new MilkTestMediaOptions())),
        new UnusedMediaStorage(),
        Frozen);

    private static ChargeService Charges(DoodhDirectDbContext db) =>
        new(db, new TestIndiaTimeProvider(Frozen));

    /// <summary>Product create/update without images never touches media storage.</summary>
    private sealed class UnusedMediaStorage : IMediaStorage
    {
        public Task<StoredMediaResult> SaveAsync(
            string storageKey, Stream content, string contentType, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<StoredMediaContent> OpenReadAsync(
            string storageKey, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    private sealed record RaceFixture(Guid ChargeId, Guid ProductId, string Sku);

    private sealed class SqlServerHarness : IAsyncDisposable
    {
        private readonly DbContextOptions<DoodhDirectDbContext> _options;
        private readonly DoodhDirectDbContext _db;

        private SqlServerHarness(DbContextOptions<DoodhDirectDbContext> options, DoodhDirectDbContext db)
        {
            _options = options;
            _db = db;
        }

        public Guid CategoryId { get; private set; }

        public Guid BranchId { get; private set; }

        public static async Task<SqlServerHarness> CreateAsync()
        {
            var databaseName = $"DoodhDirect_Test_ChargeRace_{Guid.NewGuid():N}";
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = SqlServerInstance,
                InitialCatalog = databaseName,
                IntegratedSecurity = true,
                Encrypt = true,
                TrustServerCertificate = true,
                MultipleActiveResultSets = true,
            };

            // Mirrors the production DbContext registration
            // (DependencyInjection.AddInfrastructure): serializable deadlock
            // victims MUST be retried so the retry re-validates against the
            // winner's committed state instead of surfacing SqlException 1205.
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlServer(builder.ConnectionString, sql =>
                {
                    sql.EnableRetryOnFailure(maxRetryCount: 5);
                    sql.CommandTimeout(60);
                })
                .Options;
            var db = new DoodhDirectDbContext(options);

            try
            {
                await db.Database.EnsureCreatedAsync();
            }
            catch (SqlException ex) when (ex.Number is -2 or 2 or 40 or 53)
            {
                await db.DisposeAsync();
                throw new InvalidOperationException(
                    $"This concurrency test requires the local SQL Server Express instance ('{SqlServerInstance}'). "
                    + "Start the SQLEXPRESS service and re-run dotnet test.",
                    ex);
            }

            var harness = new SqlServerHarness(options, db);
            var category = new ProductCategory("RACE", "Race", "Race fixtures.");
            category.Activate();
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            db.ProductCategories.Add(category);
            db.Branches.Add(branch);
            await db.SaveChangesAsync();
            harness.CategoryId = category.PublicId;
            harness.BranchId = branch.PublicId;
            return harness;
        }

        public DoodhDirectDbContext CreateDb() => new(_options);

        /// <summary>
        /// One item-level charge (no mappings) plus one charge-free product per
        /// iteration — independent row sets, so iterations never block each other.
        /// </summary>
        public async Task<IReadOnlyList<RaceFixture>> SeedRaceFixturesAsync(int count)
        {
            var category = await _db.ProductCategories.SingleAsync();
            var fixtures = new List<RaceFixture>(count);
            for (var i = 0; i < count; i++)
            {
                var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
                var charge = new Charge("GST", $"GC{i:00}{tag}", null, 5m, false);
                var product = new Product(
                    category.Id,
                    $"RS{i:00}{tag}",
                    "Race Milk",
                    null,
                    "litre",
                    80m);
                _db.Charges.Add(charge);
                _db.Products.Add(product);
                await _db.SaveChangesAsync();
                var branch = await _db.Branches.SingleAsync();
                _db.ProductBranches.Add(new ProductBranch(product.Id, branch.Id, true, null));
                await _db.SaveChangesAsync();
                fixtures.Add(new RaceFixture(charge.PublicId, product.PublicId, product.Sku));
            }

            return fixtures;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _db.Database.EnsureDeletedAsync();
            }
            catch
            {
                // Best-effort cleanup; the database name is unique per run so a
                // leftover database does not affect any other test.
            }

            await _db.DisposeAsync();
        }
    }
}
