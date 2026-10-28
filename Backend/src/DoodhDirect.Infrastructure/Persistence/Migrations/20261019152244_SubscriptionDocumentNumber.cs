using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionDocumentNumber : Migration
    {
        /// <summary>
        /// Backfills <c>Subscription.SubscriptionNumber</c> for pre-migration
        /// rows and advances the branch SUB counters past the backfilled
        /// values. Kept as a named constant so the migration chain test can
        /// execute the exact shipped statement against simulated historical
        /// rows. See <see cref="Up"/> for the strategy rationale.
        /// </summary>
        public const string BackfillSql = @"
DECLARE @istNow DATETIME2(3) = DATEADD(MINUTE, 330, SYSUTCDATETIME());
DECLARE @today DATE = CAST(@istNow AS DATE);
DECLARE @startYear INT = CASE WHEN MONTH(@today) >= 4 THEN YEAR(@today) ELSE YEAR(@today) - 1 END;
DECLARE @fy NVARCHAR(5) = RIGHT(N'00' + CAST(@startYear % 100 AS NVARCHAR(4)), 2)
    + N'-' + RIGHT(N'00' + CAST((@startYear + 1) % 100 AS NVARCHAR(4)), 2);

CREATE TABLE #SubBackfill (Id BIGINT PRIMARY KEY, Scope NVARCHAR(100) NOT NULL, Seq BIGINT NOT NULL);

INSERT INTO #SubBackfill (Id, Scope, Seq)
SELECT s.Id, UPPER(LTRIM(RTRIM(s.BranchCodeSnapshot))),
    ROW_NUMBER() OVER (PARTITION BY UPPER(LTRIM(RTRIM(s.BranchCodeSnapshot))) ORDER BY s.Id)
FROM dbo.Subscription s
WHERE s.SubscriptionNumber IS NULL
    AND EXISTS (
        SELECT 1 FROM dbo.NumberSeries n
        WHERE n.IsActive = 1
            AND ((n.Code = N'SUB' AND n.ScopeKey = UPPER(LTRIM(RTRIM(s.BranchCodeSnapshot))))
                OR (n.Code = N'SUB_' + UPPER(LTRIM(RTRIM(s.BranchCodeSnapshot))) AND n.ScopeKey = UPPER(LTRIM(RTRIM(s.BranchCodeSnapshot))))
                OR (n.Code = N'SUB' AND n.ScopeKey = N'')));

;WITH Bases AS (
    SELECT b.Id, b.Scope, b.Seq,
        COALESCE((
            SELECT MAX(n.LastUsedNumber)
            FROM dbo.NumberSeries n
            WHERE (n.Code = N'SUB' AND n.ScopeKey = b.Scope)
                OR (n.Code = N'SUB_' + b.Scope AND n.ScopeKey = b.Scope)
                OR (n.Code = N'SUB' AND n.ScopeKey = N'')
        ), 0) AS Base
    FROM #SubBackfill b
)
UPDATE s
SET s.SubscriptionNumber = N'SUB/' + r.Scope + N'/' + @fy + N'/' +
    CASE WHEN LEN(CAST(r.Base + r.Seq AS NVARCHAR(20))) >= 6
        THEN CAST(r.Base + r.Seq AS NVARCHAR(20))
        ELSE RIGHT(N'000000' + CAST(r.Base + r.Seq AS NVARCHAR(20)), 6) END
FROM dbo.Subscription s
JOIN Bases r ON r.Id = s.Id;

;WITH MaxAlloc AS (
    SELECT b.Scope, MAX(b.Base + b.Seq) AS NewLastUsed
    FROM (
        SELECT f.Scope, f.Seq,
            COALESCE((
                SELECT MAX(n.LastUsedNumber)
                FROM dbo.NumberSeries n
                WHERE (n.Code = N'SUB' AND n.ScopeKey = f.Scope)
                    OR (n.Code = N'SUB_' + f.Scope AND n.ScopeKey = f.Scope)
                    OR (n.Code = N'SUB' AND n.ScopeKey = N'')
            ), 0) AS Base
        FROM #SubBackfill f
    ) b
    GROUP BY b.Scope
)
UPDATE n
SET n.LastUsedNumber = m.NewLastUsed,
    n.LastUsedAtUtc = @istNow
FROM dbo.NumberSeries n
JOIN MaxAlloc m ON ((n.Code = N'SUB' AND n.ScopeKey = m.Scope)
    OR (n.Code = N'SUB_' + m.Scope AND n.ScopeKey = m.Scope)
    OR (n.Code = N'SUB' AND n.ScopeKey = N''))
WHERE n.LastUsedNumber < m.NewLastUsed;

DROP TABLE #SubBackfill;
";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubscriptionNumber",
                schema: "dbo",
                table: "Subscription",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscription_SubscriptionNumber",
                schema: "dbo",
                table: "Subscription",
                column: "SubscriptionNumber",
                unique: true,
                filter: "[SubscriptionNumber] IS NOT NULL");

            // Backfill human-readable document numbers for pre-migration
            // subscriptions, then advance the branch SUB counters past the
            // backfilled values. The format mirrors
            // NumberSeriesService.Format for SUB/{SCOPE}/{FY}/{NUMBER:000000}
            // (scope = branch code snapshot, FY = Indian financial year at
            // migration time, sequence zero-padded to 6 without truncating
            // wider values). Sequences continue from each scope's live
            // counter so fresh allocations can never collide; rows whose
            // scope has no active SUB series keep NULL (creation fails fast
            // until an administrator configures the series) instead of
            // receiving a fabricated number. PublicId/primary keys are
            // untouched. SUB counters use ResetPolicy Never, so no reset
            // handling is required. LastUsedAtUtc stores the IST wall clock,
            // matching NumberSeries.NextNumber domain semantics.
            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Subscription_SubscriptionNumber",
                schema: "dbo",
                table: "Subscription");

            migrationBuilder.DropColumn(
                name: "SubscriptionNumber",
                schema: "dbo",
                table: "Subscription");
        }
    }
}
