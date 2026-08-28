using System.Data.Common;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Setup;

/// <summary>
/// Seeds the initial numbering series for the Setup → Number Series module.
/// Each series is created only if it does not already exist, so re-running the
/// seed never resets or mutates a live counter. No per-type generation logic is
/// hardcoded here — the entities are plain rows that the centralized
/// <see cref="NumberSeriesService"/> consumes.
/// </summary>
public sealed class NumberSeriesSeedService(DoodhDirectDbContext dbContext)
{
    public const string CustomerCode = "CUSTOMER";
    public const string OrderCode = "ORDER";
    public const string BranchCode = "BRANCH";
    public const string DeliveryCode = "DELIVERY";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        // The SetupNumberSeries migration is intentionally not applied to
        // Development databases yet, so the NumberSeries table may not exist at
        // startup. When the table is absent, seeding is a no-op — it runs
        // automatically once the migration has been applied.
        if (!await CanSeedAsync(cancellationToken))
        {
            return;
        }

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();

        await executionStrategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);

            await EnsureSeriesAsync(
                CustomerCode,
                "Customer account numbers",
                "CUST/{NUMBER:0000}",
                cancellationToken);

            await EnsureScopedOrderSeriesForAllBranchesAsync(cancellationToken);

            await UpgradeLegacyScopedOrderSeriesAsync(cancellationToken);

            await EnsureSeriesAsync(
                BranchCode,
                "Branch codes",
                "BR/{NUMBER:000}",
                cancellationToken);

            await EnsureSeriesAsync(
                DeliveryCode,
                "Delivery run numbers",
                "DEL/{NUMBER:000000}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        });
    }

    private async Task<bool> CanSeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Provider-agnostic probe: InMemory/SQLite stores created with
            // EnsureCreated always expose the table, while a relational provider
            // that has not received the migration raises a DbException when the
            // table is missing (e.g. SqlException "Invalid object name").
            await dbContext.NumberSeries.Take(1).AnyAsync(cancellationToken);
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ensures the scoped ORDER series for <paramref name="branchCode"/> exists.
    /// Public so branch-creating services (e.g. the catalogue seed that creates the
    /// MAIN branch) can guarantee a scoped series at creation time regardless of
    /// global seed ordering. Idempotent — a live counter is never reset or mutated.
    /// Order numbers are scoped per branch and reset each Indian financial year, so
    /// the template renders both the branch scope and the FY (e.g.
    /// ORD/MAIN/26-27/000001) and the counter restarts on 1 April.
    /// </summary>
    public async Task EnsureScopedOrderSeriesAsync(
        string branchCode,
        CancellationToken cancellationToken)
    {
        var scopeKey = NormalizeScope(branchCode);
        if (scopeKey.Length == 0)
        {
            return;
        }

        await EnsureSeriesAsync(
            OrderCode,
            $"One-time and subscription order numbers for branch {scopeKey}",
            $"ORD/{scopeKey}/{{FY}}/{{NUMBER:000000}}",
            cancellationToken,
            scopeKey,
            NumberSeriesResetPolicy.FinancialYear);
    }

    /// <summary>
    /// Seeds one ORDER series per active branch, scoped by the branch code, and
    /// deactivates the legacy global ORDER series. Scoped series render the branch
    /// code via the <c>{SCOPE}</c> token so numbers stay unique across branches.
    /// </summary>
    private async Task EnsureScopedOrderSeriesForAllBranchesAsync(
        CancellationToken cancellationToken)
    {
        var activeBranchCodes = await dbContext.Branches
            .Where(branch => branch.IsActive)
            .Select(branch => branch.Code)
            .OrderBy(code => code)
            .ToListAsync(cancellationToken);

        foreach (var branchCode in activeBranchCodes)
        {
            await EnsureScopedOrderSeriesAsync(branchCode, cancellationToken);
        }

        // The legacy global ORDER series no longer receives new allocations because
        // every business save now passes a branch scope. Deactivating it prevents
        // accidental use while preserving the existing row and its counter history.
        var legacyOrder = await dbContext.NumberSeries
            .SingleOrDefaultAsync(
                item => item.Code == OrderCode && item.ScopeKey == string.Empty,
                cancellationToken);

        if (legacyOrder is not null && legacyOrder.IsActive)
        {
            legacyOrder.Deactivate(
                updatedByUserId: null,
                indiaLocalNow: new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));
        }
    }

    private async Task EnsureSeriesAsync(
        string code,
        string description,
        string template,
        CancellationToken cancellationToken,
        string? scopeKey = null,
        NumberSeriesResetPolicy resetPolicy = NumberSeriesResetPolicy.Never)
    {
        var normalizedScope = NormalizeScope(scopeKey);

        var exists = await dbContext.NumberSeries
            .AnyAsync(
                item => item.Code == code && item.ScopeKey == normalizedScope,
                cancellationToken);
        if (exists)
        {
            return;
        }

        dbContext.NumberSeries.Add(new NumberSeries(
            code,
            description,
            template,
            startingNumber: 1,
            incrementBy: 1,
            resetPolicy,
            normalizedScope));

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Upgrades ORDER rows created before the financial-year numbering existed. Scoped
    /// rows still carrying a legacy template (either the tokenized
    /// <c>ORD/{{SCOPE}}/{{NUMBER:000000}}</c> or the concrete per-scope form such as
    /// <c>ORD/BLR/{{NUMBER:000000}}</c>) and which have never issued a number are
    /// reconfigured in place to <c>ORD/<scope>/{{FY}}/{{NUMBER:000000}}</c> with a
    /// financial-year reset. Rows that already issued numbers are left untouched —
    /// reconfiguring them could re-issue existing numbers — and rows already on the
    /// target template are skipped, so the upgrade is idempotent and never mutates a
    /// live counter.
    /// </summary>
    private async Task UpgradeLegacyScopedOrderSeriesAsync(CancellationToken cancellationToken)
    {
        // Legacy rows are narrowed in SQL (translatable predicates only); the template
        // shape check runs in memory because StringComparison overloads are not
        // translatable across providers.
        var candidates = await dbContext.NumberSeries
            .Where(item =>
                item.Code == OrderCode
                && item.ScopeKey != string.Empty
                && item.LastUsedNumber < item.StartingNumber)
            .ToListAsync(cancellationToken);

        var legacyRows = candidates
            .Where(item =>
                !item.Template.Contains("{FY}", StringComparison.OrdinalIgnoreCase)
                && item.Template.Contains("{NUMBER:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var series in legacyRows)
        {
            var targetTemplate = $"ORD/{series.ScopeKey}/{{FY}}/{{NUMBER:000000}}";
            series.Configure(
                series.Description,
                targetTemplate,
                series.StartingNumber,
                series.IncrementBy,
                NumberSeriesResetPolicy.FinancialYear,
                // Fixed India-local stamp keeps seeding free of a wall-clock dependency;
                // mirrors the legacy-series deactivation above.
                new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
                updatedByUserId: null);
        }

        if (legacyRows.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static string NormalizeScope(string? scopeKey) =>
        string.IsNullOrWhiteSpace(scopeKey) ? string.Empty : scopeKey.Trim().ToUpperInvariant();
}
