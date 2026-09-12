-- ============================================================================
-- apply-uat-series-correction.sql
--
-- GUARDED, DATA-ONLY correction for the DoodhDirect UAT NumberSeries drift.
--
-- SCOPE OF THIS SCRIPT:
--   * PART 1 (DELIVERY_DB0001):  NO-OP confirmation only. The scoped row
--     (Code='DELIVERY_DB0001', ScopeKey='DB0001', LastUsedNumber=5) ALREADY
--     EXISTS. Inserting it again would create a duplicate (Code, ScopeKey)
--     row and corrupt the series table, so this script deliberately does NOT
--     insert anything. It only asserts the expected state is present.
--   * PART 2 (DELIVERY_DB0002):  DATA-ONLY fix. The row Id 42 was created with
--     ScopeKey='DB002' (missing a zero) although the branch code is 'DB0002'.
--     The row has never been used (LastUsedNumber=0, LastUsedAtUtc IS NULL) and
--     nothing references 'DB002'. This script aligns the ScopeKey to 'DB0002'.
--
-- GUARANTEES:
--   * Every precondition is asserted BEFORE any write; failure raises and the
--     batch aborts (run with sqlcmd -b so a non-zero exit is returned).
--   * The write is wrapped in a single transaction and verified with
--     @@ROWCOUNT; an unexpected row count rolls back.
--   * No schema change, no migration, no uniqueness change, no counter reset,
--     no deletion of any Delivery/Order/NumberSeries row.
--
-- USAGE:
--   sqlcmd -S .\SQLEXPRESS -d DoodhDirect -E -C -b -i scripts\apply-uat-series-correction.sql -W -s "|"
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @typoRows int;
DECLARE @correctRows int;
DECLARE @usedRows int;

-- ---------------------------------------------------------------------------
-- GUARD 0 (PART 1 NO-OP): the scoped DELIVERY_DB0001 row must already exist.
-- ---------------------------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM dbo.NumberSeries
    WHERE Code = 'DELIVERY_DB0001' AND ScopeKey = 'DB0001')
BEGIN
    ; THROW 51000, 'PRECONDITION FAILED: DELIVERY_DB0001 (DB0001) is missing. The expected state changed; aborting (no insert performed).', 1;
END

-- Confirm the DB0001 counter is still 5 (documents the intended no-op).
IF NOT EXISTS (
    SELECT 1 FROM dbo.NumberSeries
    WHERE Code = 'DELIVERY_DB0001' AND ScopeKey = 'DB0001' AND LastUsedNumber = 5)
BEGIN
    ; THROW 51001, 'PRECONDITION FAILED: DELIVERY_DB0001 (DB0001) exists but LastUsedNumber is not 5. Manual review required.', 1;
END

-- ---------------------------------------------------------------------------
-- GUARD 1 (PART 2): branch DB0002 must exist and be active.
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM dbo.Branch WHERE Code = 'DB0002')
BEGIN
    ; THROW 51002, 'PRECONDITION FAILED: branch DB0002 not found.', 1;
END

IF NOT EXISTS (SELECT 1 FROM dbo.Branch WHERE Code = 'DB0002' AND IsActive = 1)
BEGIN
    ; THROW 51003, 'PRECONDITION FAILED: branch DB0002 is not active.', 1;
END

-- ---------------------------------------------------------------------------
-- GUARD 2 (PART 2): exactly one typo'd row (DELIVERY_DB0002, DB002).
-- ---------------------------------------------------------------------------
SELECT @typoRows = COUNT(*)
FROM dbo.NumberSeries
WHERE Code = 'DELIVERY_DB0002' AND ScopeKey = 'DB002';

IF @typoRows <> 1
BEGIN
    ; THROW 51004, 'PRECONDITION FAILED: expected exactly one DELIVERY_DB0002 row with ScopeKey DB002.', 1;
END

-- ---------------------------------------------------------------------------
-- GUARD 3 (PART 2): a correctly scoped row must NOT already exist (idempotency).
-- ---------------------------------------------------------------------------
SELECT @correctRows = COUNT(*)
FROM dbo.NumberSeries
WHERE Code = 'DELIVERY_DB0002' AND ScopeKey = 'DB0002';

IF @correctRows > 0
BEGIN
    ; THROW 51005, 'PRECONDITION FAILED: a DELIVERY_DB0002 (DB0002) row already exists; no correction required.', 1;
END

-- ---------------------------------------------------------------------------
-- GUARD 4 (PART 2): the typo'd row must be unused.
-- ---------------------------------------------------------------------------
SELECT @usedRows = COUNT(*)
FROM dbo.NumberSeries
WHERE Code = 'DELIVERY_DB0002' AND ScopeKey = 'DB002'
  AND (LastUsedNumber <> 0 OR LastUsedAtUtc IS NOT NULL);

IF @usedRows > 0
BEGIN
    ; THROW 51006, 'PRECONDITION FAILED: DELIVERY_DB0002 has been used; automatic correction is unsafe.', 1;
END

-- ---------------------------------------------------------------------------
-- GUARD 5 (PART 2): no generated DeliveryNumber depends on the DB002 scope.
-- ---------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM dbo.Delivery WHERE DeliveryNumber LIKE '%DB002%')
BEGIN
    ; THROW 51007, 'PRECONDITION FAILED: a Delivery references DB002; aborting.', 1;
END

-- ---------------------------------------------------------------------------
-- APPLY: single transaction, verified row count.
-- ---------------------------------------------------------------------------
BEGIN TRAN;

UPDATE dbo.NumberSeries
SET ScopeKey = 'DB0002'
WHERE Code = 'DELIVERY_DB0002' AND ScopeKey = 'DB002';

IF @@ROWCOUNT <> 1
BEGIN
    ROLLBACK TRAN;
    ; THROW 51008, 'Correction affected an unexpected number of rows; transaction rolled back.', 1;
END

COMMIT TRAN;

-- ---------------------------------------------------------------------------
-- VERIFY AFTER.
-- ---------------------------------------------------------------------------
PRINT '=== BEFORE/AFTER: all DELIVERY% series rows ===';
SELECT Id, Code, ScopeKey, Template, StartingNumber, LastUsedNumber, IncrementBy,
       ResetPolicy, IsActive, LastUsedAtUtc
FROM dbo.NumberSeries
WHERE Code LIKE 'DELIVERY%'
ORDER BY Id;

PRINT '=== VERIFY: exactly one (DELIVERY_DB0002, DB0002) and zero (…, DB002) ===';
SELECT
    (SELECT COUNT(*) FROM dbo.NumberSeries WHERE Code = 'DELIVERY_DB0002' AND ScopeKey = 'DB0002') AS CorrectScoped,
    (SELECT COUNT(*) FROM dbo.NumberSeries WHERE Code = 'DELIVERY_DB0002' AND ScopeKey = 'DB002')  AS TypoScoped,
    (SELECT COUNT(*) FROM dbo.NumberSeries WHERE Code = 'DELIVERY_DB0001' AND ScopeKey = 'DB0001') AS Db0001Scoped;

PRINT '=== VERIFY: DB0001 deliveries unchanged ===';
SELECT Id, DeliveryNumber, OrderId, BranchId, Status
FROM dbo.Delivery
WHERE DeliveryNumber LIKE 'DLV/DB0001/%'
ORDER BY Id;
