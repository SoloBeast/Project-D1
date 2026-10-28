/* ============================================================================
   Batch 0 — REQUIRED session SET options (must precede every other batch).
   ----------------------------------------------------------------------------
   DML in this script touches tables with filtered indexes (e.g. Payment,
   Delivery, RefundReplacementRequest, NotificationDelivery). SQL Server
   validates these SET options when such statements are COMPILED, i.e. before
   any statement runs — so the options must already be in effect when the
   reset batch is submitted for compilation, which is why they live in this
   leading batch, terminated by its own GO. Running with sqlcmd -I is
   additionally recommended, but this batch (not the client flag) is the
   real protection: client defaults such as sqlcmd's QUOTED_IDENTIFIER OFF
   would otherwise abort the script before any statement executes.
   ============================================================================ */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

/* ============================================================================
   DoodhDirect — Safe SQL Server Test-Data Reset Script
   Target instance : .\SQLEXPRESS
   Target database : DoodhDirect (all migrations through
                     20261021064923_WithdrawalTransferReference applied;
                     20261021074859_CompanyBankDetail,
                     20261021085623_ManualBankRefundSettlement and
                     20261021104319_RefundBankExportBatches may be absent —
                     their references below are OBJECT_ID-guarded so the
                     preview still runs (missing tables count as 0 rows).
                     Re-check __EFMigrationsHistory before each use — the
                     required baseline grows as migrations are added)
   File            : scripts/sql/ResetDoodhDirectTestData.sql
   ============================================================================
   *** DESTRUCTIVE OPERATION — READ THIS FIRST ***
   ----------------------------------------------------------------------------
   This script DELETES all transactional / operational test data created during
   checkout and payment verification, and RESETS counters that belong to the
   deleted documents:

     CLEARED (children deleted before parents, explicit table names, no
     TRUNCATE, constraints stay enabled — full order is documented in the
     execution block):
       RefundBankExportItemReservation, RefundBankExportItem,
       RefundBankExportBatch,
       NotificationAttempt, NotificationDelivery, WalletTransaction,
       WithdrawalRequest, CustomerBankDetail, Refund,
       VacationRefundOccurrence, MilkTestParameter, MilkTestImage,
       RefundReplacementImage, MilkUsage, DeliveryBatchAllocation,
       DeliveryAssignment, DeliveryOtp, DeliveryLocation,
       RefundReplacementRequest, SubscriptionCancelRefundOccurrence,
       MilkTest, Delivery, SubscriptionDelivery,
       SubscriptionSchedule, SubscriptionCharge, VacationRefundRequest,
       Payment, PaymentWebhook, Subscription, OrderCancellation,
       OrderCharge, OrderItem,
       [Order], MilkBatch, MilkProduction, Notification, NotificationEvent.

     RESET (rows preserved, state re-initialised):
       Wallet.Balance            -> 0.00 for EVERY retained wallet (all
                                    currencies; ledger rows are deleted first
                                    so the BalanceBefore/BalanceAfter invariant
                                    restarts cleanly on the next transaction).
       NumberSeries counters     -> LastUsedNumber = StartingNumber -
                                    IncrementBy, LastUsedAtUtc = NULL, for
                                    ORDER (every ScopeKey present: DB0001,
                                    DB0002, MAIN), DELIVERY, REFUNDREPLACEMENT,
                                    VACATIONREFUND, WITHDRAWAL, plus legacy
                                    scope-suffixed rows ORDER_DB0001 @ DB0001,
                                    ORDER_DB0002 @ DB0002,                                    DELIVERY_DB0001 @ DB0001 and DELIVERY_DB0002 @ DB0002, which
                                    the allocator still resolves through its
                                    scope-suffixed fallback (see
                                    NumberSeriesSeedService). REFUNDBANKEXPORT
                                    (manual-bank export batch numbers) is
                                    reset the same way: every batch row is
                                    cleared, so restarting at StartingNumber
                                    cannot collide. Next generated
                                    number = StartingNumber (seeded as 1,
                                    step 1). A code with no row present
                                    (e.g. WITHDRAWAL before the startup seed
                                    creates it) is simply unaffected.
                                    No legacy global ORDER row
                                    (ScopeKey = '') exists in this database.

     WITHDRAWAL / BANK-DETAIL POLICY (sensitive transactional data — CLEARED,
     not preserved): CustomerBankDetail and WithdrawalRequest rows are
     deleted and the WITHDRAWAL counter is reset, even though the owning
     CustomerProfile/User rows are preserved. Rationale: (1) bank details
     are encrypted account credentials, not customer identity — preserved
     customers re-save details when re-testing; (2) withdrawal requests are
     transactional tickets whose WalletTransaction debit/reversal legs are
     deleted by this reset — keeping the requests would leave Pending
     tickets with no ledger legs and snapshots pointing at deleted state;
     (3) the     orphaned references remain. WithdrawalRequest is deleted BEFORE
     CustomerBankDetail (Restrict FK BankDetailId); both are deleted with
     the WalletTransaction group, before any parent they reference.

     COMPANY-BANK POLICY (configuration — PRESERVED, not cleared): the
     single-row CompanyBankDetail holds the company's own payout account
     used as the source of manual bank-transfer refunds. Unlike customer
     bank credentials it is setup/configuration data (same class as
     SystemConfiguration/BrandingAsset), so this script never deletes it.
     It is counted in the preview PRESERVE block and guarded by an
     in-transaction count check. Table added by migration
     20261021074859_CompanyBankDetail; the new references below are
     OBJECT_ID-guarded so the preview still runs on databases where that
     migration has not been applied yet (missing table counts as 0 rows
     and the integrity check is skipped). Once the migration is applied
     everywhere the guards may be simplified to direct references.

     PRESERVED (never touched by this script):
       User, Role, Permission, UserRole, RolePermission, OtpChallenge,
       UserSession, RefreshToken,       CustomerProfile, CustomerAddress, Branch,
       ProductCategory, Product, ProductBranch, ProductCharge, ProductImage,
       Charge, Wallet (account rows), SystemConfiguration, CompanyBankDetail
       (company payout account — preserved, see COMPANY-BANK POLICY),
       BrandingAsset,
       Camera, CameraStream, NotificationTemplate, NotificationPreference,
       UserDevice, EmployeeInvitation, EmployeeInvitationBranch,
       NumberSeries (all definitions kept; the CUSTOMER counter and all
       never-used zero counters stay byte-identical), AuditLog,
       __EFMigrationsHistory.

   The script NEVER drops/recreates tables, schemas, indexes, triggers, stored
   procedures, sequences or migration history, NEVER reseeds IDENTITY columns,
   and NEVER disables constraints (no TRUNCATE, no NOCHECK).

   Deleting database rows does NOT delete files under App_Data (milk-test
   photos, ticket attachments, product/branding media). See the external-
   storage cleanup plan in the script footer and in the accompanying report.

   HOW TO RUN (SSMS / sqlcmd, connected to .\SQLEXPRESS):
     1. BACK UP FIRST (fresh, full backup — step 6 is enforced by the script):
          BACKUP DATABASE [DoodhDirect]
          TO DISK = N'C:\Backup\DoodhDirect_pre-reset_<yyyymmdd-hhmm>.bak'
          WITH INIT, CHECKSUM;
        (No COMPRESSION: SQL Server Express — the documented local target —
        does not support backup compression. Add COMPRESSION only on
        editions that support it.)
     2. Run this script AS-IS (@ExecuteReset = 0), preferably with sqlcmd -I
        (sets QUOTED_IDENTIFIER ON at connect; the script's leading SET batch
        is the primary protection and does not depend on client defaults).
        It prints a PREVIEW ONLY:
        per-table row counts that would be cleared, preserved-table counts,
        current number-series counters, wallet balances, and a sample of
        external StorageKeys that need separate file cleanup. NOTHING IS
        MODIFIED in preview mode.
     3. Review the preview. Only then set @ExecuteReset = 1 AND
        @BackupConfirmed = 1 in the flags block below and run again.
     4. The script validates everything INSIDE the transaction and ROLLBACKS
        on any failure, so the database is never left partially reset.
   ============================================================================ */

SET NOCOUNT ON;

/* ---------- Batch 1: database guard (no variables cross GO) ---------- */
IF DB_NAME() <> N'DoodhDirect'
BEGIN;
    THROW 50000, 'Safety stop: current database is NOT DoodhDirect. Connect to the DoodhDirect database on .\\SQLEXPRESS and run again.', 1;
END;
GO

/* ==================== EXECUTION FLAGS — EDIT HERE ==================== */
DECLARE @ExecuteReset    BIT = 0;  -- 0 = preview only (default, safe). 1 = execute.
DECLARE @BackupConfirmed BIT = 0;  -- MUST be set to 1 after taking the fresh full backup above.
/* ===================================================================== */

-- Re-assert inside the same batch (GO above cleared nothing; this is belt-and-braces).
IF DB_NAME() <> N'DoodhDirect'
BEGIN;
    THROW 50001, 'Safety re-check failed: not in DoodhDirect. Aborting.', 1;
END;

/* Company bank configuration (PRESERVED — never deleted). Resolved via
   dynamic SQL so this batch still compiles on databases where migration
   20261021074859_CompanyBankDetail has not been applied yet (a missing
   table counts as 0 rows). */
DECLARE @companyBankRows INT = 0;
IF OBJECT_ID(N'dbo.[CompanyBankDetail]', N'U') IS NOT NULL
BEGIN;
    DECLARE @companyBankCount TABLE ([Rows] INT);
    INSERT INTO @companyBankCount EXEC sp_executesql N'SELECT COUNT(*) FROM dbo.[CompanyBankDetail];';
    SELECT @companyBankRows = [Rows] FROM @companyBankCount;
END;

/* Stage 3 export batches (CLEARED with the transactional group). Resolved
   via dynamic SQL so this batch still compiles on databases where
   migration 20261021104319_RefundBankExportBatches has not been applied
   yet (a missing table counts as 0 rows). Child-first order mirrors the
   DELETE block: ItemReservation -> Item -> Batch. */
DECLARE @exportBatchRows INT = 0;
DECLARE @exportItemRows INT = 0;
DECLARE @exportLinkRows INT = 0;
IF OBJECT_ID(N'dbo.[RefundBankExportBatch]', N'U') IS NOT NULL
BEGIN;
    DECLARE @exportBatchCount TABLE ([Rows] INT);
    INSERT INTO @exportBatchCount EXEC sp_executesql N'SELECT COUNT(*) FROM dbo.[RefundBankExportBatch];';
    SELECT @exportBatchRows = [Rows] FROM @exportBatchCount;
END;
IF OBJECT_ID(N'dbo.[RefundBankExportItem]', N'U') IS NOT NULL
BEGIN;
    DECLARE @exportItemCount TABLE ([Rows] INT);
    INSERT INTO @exportItemCount EXEC sp_executesql N'SELECT COUNT(*) FROM dbo.[RefundBankExportItem];';
    SELECT @exportItemRows = [Rows] FROM @exportItemCount;
END;
IF OBJECT_ID(N'dbo.[RefundBankExportItemReservation]', N'U') IS NOT NULL
BEGIN;
    DECLARE @exportLinkCount TABLE ([Rows] INT);
    INSERT INTO @exportLinkCount EXEC sp_executesql N'SELECT COUNT(*) FROM dbo.[RefundBankExportItemReservation];';
    SELECT @exportLinkRows = [Rows] FROM @exportLinkCount;
END;

/* ---------- Guard 1: temporal tables / triggers on target tables ----------
   The EF model declares no temporal tables and no triggers. If either appears
   (e.g. added by a later migration), stop instead of guessing. */
IF EXISTS (
    SELECT 1
    FROM sys.tables AS t
    WHERE t.schema_id = SCHEMA_ID(N'dbo')
      AND t.name IN (N'RefundBankExportItemReservation', N'RefundBankExportItem',
                     N'RefundBankExportBatch',
                     N'NotificationAttempt', N'NotificationDelivery', N'WalletTransaction',
                     N'WithdrawalRequest', N'CustomerBankDetail',
                     N'Refund', N'VacationRefundOccurrence', N'MilkTestParameter',
                     N'MilkTestImage', N'RefundReplacementImage', N'MilkUsage',
                     N'DeliveryBatchAllocation', N'DeliveryAssignment', N'DeliveryOtp',
                     N'DeliveryLocation', N'RefundReplacementRequest',
                     N'SubscriptionCancelRefundOccurrence', N'MilkTest',
                     N'Delivery', N'SubscriptionDelivery', N'SubscriptionSchedule',
                     N'SubscriptionCharge', N'VacationRefundRequest', N'Payment',
                     N'PaymentWebhook', N'Subscription', N'OrderCancellation',
                     N'OrderCharge', N'OrderItem',
                     N'Order', N'MilkBatch', N'MilkProduction', N'Notification',
                     N'NotificationEvent', N'Wallet', N'NumberSeries')
      AND t.temporal_type <> 0
)
BEGIN;
    THROW 50002, 'Safety stop: one or more target tables is a temporal/history table. Classify it before resetting.', 1;
END;

IF EXISTS (
    SELECT 1
    FROM sys.triggers AS tr
    INNER JOIN sys.tables AS t ON tr.parent_id = t.object_id
    WHERE t.schema_id = SCHEMA_ID(N'dbo')
      AND t.name IN (N'RefundBankExportItemReservation', N'RefundBankExportItem',
                     N'RefundBankExportBatch',
                     N'NotificationAttempt', N'NotificationDelivery', N'WalletTransaction',
                     N'WithdrawalRequest', N'CustomerBankDetail',
                     N'Refund', N'VacationRefundOccurrence', N'MilkTestParameter',
                     N'MilkTestImage', N'RefundReplacementImage', N'MilkUsage',
                     N'DeliveryBatchAllocation', N'DeliveryAssignment', N'DeliveryOtp',
                     N'DeliveryLocation', N'RefundReplacementRequest',
                     N'SubscriptionCancelRefundOccurrence', N'MilkTest',
                     N'Delivery', N'SubscriptionDelivery', N'SubscriptionSchedule',
                     N'SubscriptionCharge', N'VacationRefundRequest', N'Payment',
                     N'PaymentWebhook', N'Subscription', N'OrderCancellation',
                     N'OrderCharge', N'OrderItem',
                     N'Order', N'MilkBatch', N'MilkProduction', N'Notification',
                     N'NotificationEvent', N'Wallet', N'NumberSeries')
      AND tr.is_disabled = 0
)
BEGIN;
    THROW 50003, 'Safety stop: an enabled trigger exists on a target table. Review trigger behaviour before resetting.', 1;
END;

/* ==================== PREVIEW (always runs, never modifies) ==================== */
PRINT N'================================================================';
PRINT N'DoodhDirect test-data reset — PREVIEW (no data modified)';
PRINT N'Database : ' + DB_NAME();
PRINT N'Execute  : ' + CASE WHEN @ExecuteReset = 1 THEN N'1 (EXECUTE armed)' ELSE N'0 (preview only)' END;
PRINT N'================================================================';

/* Rows that WOULD be cleared. */
SELECT N'CLEAR' AS [Action], v.[Table], v.[Rows]
FROM (VALUES
    (N'NotificationAttempt',       (SELECT COUNT(*) FROM dbo.[NotificationAttempt])),
    (N'NotificationDelivery',      (SELECT COUNT(*) FROM dbo.[NotificationDelivery])),
    (N'WalletTransaction',         (SELECT COUNT(*) FROM dbo.[WalletTransaction])),
    (N'WithdrawalRequest',         (SELECT COUNT(*) FROM dbo.[WithdrawalRequest])),
    (N'CustomerBankDetail',        (SELECT COUNT(*) FROM dbo.[CustomerBankDetail])),
    (N'Refund',                    (SELECT COUNT(*) FROM dbo.[Refund])),
    (N'VacationRefundOccurrence',  (SELECT COUNT(*) FROM dbo.[VacationRefundOccurrence])),
    (N'MilkTestParameter',         (SELECT COUNT(*) FROM dbo.[MilkTestParameter])),
    (N'MilkTestImage',             (SELECT COUNT(*) FROM dbo.[MilkTestImage])),
    (N'RefundReplacementImage',    (SELECT COUNT(*) FROM dbo.[RefundReplacementImage])),
    (N'MilkUsage',                 (SELECT COUNT(*) FROM dbo.[MilkUsage])),
    (N'DeliveryBatchAllocation',   (SELECT COUNT(*) FROM dbo.[DeliveryBatchAllocation])),
    (N'DeliveryAssignment',        (SELECT COUNT(*) FROM dbo.[DeliveryAssignment])),
    (N'DeliveryOtp',               (SELECT COUNT(*) FROM dbo.[DeliveryOtp])),
    (N'DeliveryLocation',          (SELECT COUNT(*) FROM dbo.[DeliveryLocation])),
    (N'RefundReplacementRequest',  (SELECT COUNT(*) FROM dbo.[RefundReplacementRequest])),
    (N'SubscriptionCancelRefundOccurrence', (SELECT COUNT(*) FROM dbo.[SubscriptionCancelRefundOccurrence])),
    (N'MilkTest',                  (SELECT COUNT(*) FROM dbo.[MilkTest])),
    (N'Delivery',                  (SELECT COUNT(*) FROM dbo.[Delivery])),
    (N'SubscriptionDelivery',      (SELECT COUNT(*) FROM dbo.[SubscriptionDelivery])),
    (N'SubscriptionSchedule',      (SELECT COUNT(*) FROM dbo.[SubscriptionSchedule])),
    (N'SubscriptionCharge',        (SELECT COUNT(*) FROM dbo.[SubscriptionCharge])),
    (N'VacationRefundRequest',     (SELECT COUNT(*) FROM dbo.[VacationRefundRequest])),
    (N'Payment',                   (SELECT COUNT(*) FROM dbo.[Payment])),
    (N'PaymentWebhook',            (SELECT COUNT(*) FROM dbo.[PaymentWebhook])),
    (N'Subscription',              (SELECT COUNT(*) FROM dbo.[Subscription])),
    (N'OrderCancellation',         (SELECT COUNT(*) FROM dbo.[OrderCancellation])),
    (N'OrderCharge',               (SELECT COUNT(*) FROM dbo.[OrderCharge])),
    (N'OrderItem',                 (SELECT COUNT(*) FROM dbo.[OrderItem])),
    (N'Order',                     (SELECT COUNT(*) FROM dbo.[Order])),
    (N'MilkBatch',                 (SELECT COUNT(*) FROM dbo.[MilkBatch])),
    (N'MilkProduction',            (SELECT COUNT(*) FROM dbo.[MilkProduction])),
    (N'Notification',              (SELECT COUNT(*) FROM dbo.[Notification])),
    (N'NotificationEvent',         (SELECT COUNT(*) FROM dbo.[NotificationEvent]))
) AS v([Table], [Rows])
ORDER BY v.[Table];

/* Stage 3 export batches that WOULD be cleared (OBJECT_ID-guarded counts;
   missing tables report 0 rows — see the header baseline note). */
SELECT N'CLEAR' AS [Action], v.[Table], v.[Rows]
FROM (VALUES
    (N'RefundBankExportItemReservation', @exportLinkRows),
    (N'RefundBankExportItem',            @exportItemRows),
    (N'RefundBankExportBatch',           @exportBatchRows)
) AS v([Table], [Rows])
ORDER BY v.[Table];

/* Rows that stay, with balances / counters that WOULD be reset. */
SELECT N'RESET' AS [Action], v.[Table], v.[Rows]
FROM (VALUES
    (N'Wallet (balances -> 0.00)', (SELECT COUNT(*) FROM dbo.[Wallet])),
    (N'Wallet nonzero balances',   (SELECT COUNT(*) FROM dbo.[Wallet] WHERE [Balance] <> CAST(0 AS DECIMAL(18,2)))),
    (N'NumberSeries ORDER rows',    (SELECT COUNT(*) FROM dbo.[NumberSeries] WHERE [Code] = N'ORDER')),
    (N'NumberSeries DELIVERY rows', (SELECT COUNT(*) FROM dbo.[NumberSeries] WHERE [Code] = N'DELIVERY')),
    (N'NumberSeries REFUNDREPLACEMENT rows', (SELECT COUNT(*) FROM dbo.[NumberSeries] WHERE [Code] = N'REFUNDREPLACEMENT')),
    (N'NumberSeries VACATIONREFUND rows',    (SELECT COUNT(*) FROM dbo.[NumberSeries] WHERE [Code] = N'VACATIONREFUND')),
    (N'NumberSeries WITHDRAWAL rows (reset; absent until first startup seed)', (SELECT COUNT(*) FROM dbo.[NumberSeries] WHERE [Code] = N'WITHDRAWAL')),
    (N'NumberSeries REFUNDBANKEXPORT rows (reset; absent until first startup seed)', (SELECT COUNT(*) FROM dbo.[NumberSeries] WHERE [Code] = N'REFUNDBANKEXPORT')),
    (N'NumberSeries ORDER_DB0001 @ DB0001 (reset)',       (SELECT COUNT(*) FROM dbo.[NumberSeries] WHERE [Code] = N'ORDER_DB0001' AND [ScopeKey] = N'DB0001')),
    (N'NumberSeries ORDER_DB0002 @ DB0002 (reset)',       (SELECT COUNT(*) FROM dbo.[NumberSeries] WHERE [Code] = N'ORDER_DB0002' AND [ScopeKey] = N'DB0002')),
    (N'NumberSeries DELIVERY_DB0001 @ DB0001 (reset)',    (SELECT COUNT(*) FROM dbo.[NumberSeries] WHERE [Code] = N'DELIVERY_DB0001' AND [ScopeKey] = N'DB0001')),
    (N'NumberSeries DELIVERY_DB0002 @ DB0002 (reset)',    (SELECT COUNT(*) FROM dbo.[NumberSeries] WHERE [Code] = N'DELIVERY_DB0002' AND [ScopeKey] = N'DB0002')),
    (N'NumberSeries CUSTOMER rows (PRESERVED, not reset)', (SELECT COUNT(*) FROM dbo.[NumberSeries] WHERE [Code] = N'CUSTOMER'))
) AS v([Table], [Rows]);

/* Current counter values for ALL series rows and the expected next number.
   Action = RESET for transactional counters whose documents are cleared
   (ORDER every scope, DELIVERY global, REFUNDREPLACEMENT, VACATIONREFUND,
   WITHDRAWAL global, plus legacy scope-suffixed rows ORDER_DB0001 @ DB0001,
   ORDER_DB0002 @ DB0002, DELIVERY_DB0001 @ DB0001,
   DELIVERY_DB0002 @ DB0002, which the allocator still resolves through its
   scope-suffixed fallback); PRESERVE for CUSTOMER and never-used zero counters. */
SELECT
    [Code], [ScopeKey], [IsActive], [StartingNumber], [IncrementBy],
    [LastUsedNumber], [LastUsedAtUtc],
    CASE WHEN ([Code] IN (N'ORDER', N'DELIVERY', N'REFUNDREPLACEMENT', N'VACATIONREFUND', N'WITHDRAWAL', N'REFUNDBANKEXPORT')
           OR ([Code] = N'ORDER_DB0001' AND [ScopeKey] = N'DB0001')
           OR ([Code] = N'ORDER_DB0002' AND [ScopeKey] = N'DB0002')
           OR ([Code] = N'DELIVERY_DB0001' AND [ScopeKey] = N'DB0001')
           OR ([Code] = N'DELIVERY_DB0002' AND [ScopeKey] = N'DB0002'))
         THEN N'RESET' ELSE N'PRESERVE' END AS [Action],
    CASE WHEN ([Code] IN (N'ORDER', N'DELIVERY', N'REFUNDREPLACEMENT', N'VACATIONREFUND', N'WITHDRAWAL', N'REFUNDBANKEXPORT')
           OR ([Code] = N'ORDER_DB0001' AND [ScopeKey] = N'DB0001')
           OR ([Code] = N'ORDER_DB0002' AND [ScopeKey] = N'DB0002')
           OR ([Code] = N'DELIVERY_DB0001' AND [ScopeKey] = N'DB0001')
           OR ([Code] = N'DELIVERY_DB0002' AND [ScopeKey] = N'DB0002'))
         THEN [StartingNumber] ELSE NULL END AS [ExpectedNextNumberAfterReset]
FROM dbo.[NumberSeries]
ORDER BY [Code], [ScopeKey];

/* Preserved master / identity counts (must be identical after the reset). */
SELECT N'PRESERVE' AS [Action], v.[Table], v.[Rows]
FROM (VALUES
    (N'User',                   (SELECT COUNT(*) FROM dbo.[User])),
    (N'Role',                   (SELECT COUNT(*) FROM dbo.[Role])),
    (N'Permission',             (SELECT COUNT(*) FROM dbo.[Permission])),
    (N'UserRole',               (SELECT COUNT(*) FROM dbo.[UserRole])),
    (N'RolePermission',         (SELECT COUNT(*) FROM dbo.[RolePermission])),
    (N'OtpChallenge',           (SELECT COUNT(*) FROM dbo.[OtpChallenge])),
    (N'UserSession',            (SELECT COUNT(*) FROM dbo.[UserSession])),
    (N'RefreshToken',           (SELECT COUNT(*) FROM dbo.[RefreshToken])),
    (N'CustomerProfile',        (SELECT COUNT(*) FROM dbo.[CustomerProfile])),
    (N'CustomerAddress',        (SELECT COUNT(*) FROM dbo.[CustomerAddress])),
    (N'Branch',                 (SELECT COUNT(*) FROM dbo.[Branch])),
    (N'ProductCategory',        (SELECT COUNT(*) FROM dbo.[ProductCategory])),
    (N'Product',                (SELECT COUNT(*) FROM dbo.[Product])),
    (N'ProductBranch',          (SELECT COUNT(*) FROM dbo.[ProductBranch])),
    (N'ProductCharge',          (SELECT COUNT(*) FROM dbo.[ProductCharge])),
    (N'ProductImage',           (SELECT COUNT(*) FROM dbo.[ProductImage])),
    (N'Charge',                 (SELECT COUNT(*) FROM dbo.[Charge])),
    (N'CompanyBankDetail (PRESERVED)', @companyBankRows),
    (N'SystemConfiguration',    (SELECT COUNT(*) FROM dbo.[SystemConfiguration])),
    (N'BrandingAsset',          (SELECT COUNT(*) FROM dbo.[BrandingAsset])),
    (N'Camera',                 (SELECT COUNT(*) FROM dbo.[Camera])),
    (N'CameraStream',           (SELECT COUNT(*) FROM dbo.[CameraStream])),
    (N'NotificationTemplate',   (SELECT COUNT(*) FROM dbo.[NotificationTemplate])),
    (N'NotificationPreference', (SELECT COUNT(*) FROM dbo.[NotificationPreference])),
    (N'UserDevice',             (SELECT COUNT(*) FROM dbo.[UserDevice])),
    (N'EmployeeInvitation',     (SELECT COUNT(*) FROM dbo.[EmployeeInvitation])),
    (N'EmployeeInvitationBranch',(SELECT COUNT(*) FROM dbo.[EmployeeInvitationBranch])),
    (N'NumberSeries (all definitions)', (SELECT COUNT(*) FROM dbo.[NumberSeries])),
    (N'AuditLog (preserved)',   (SELECT COUNT(*) FROM dbo.[AuditLog]))
) AS v([Table], [Rows])
ORDER BY v.[Table];

/* External-file evidence sample: the SQL DELETEs below do NOT remove these
   files. Use the StorageKeys to scope the separate filesystem cleanup. */
PRINT N'--- External StorageKey sample: MilkTestImage (local media root App_Data/MilkTestMedia) ---';
SELECT TOP (25) [StorageKey], [FileName], [ContentType], [FileSize]
FROM dbo.[MilkTestImage]
ORDER BY [UploadedAtUtc] DESC;

PRINT N'--- External StorageKey sample: RefundReplacementImage (IMediaStorage local files) ---';
SELECT TOP (25) [StorageKey], [FileName], [ContentType], [FileSize]
FROM dbo.[RefundReplacementImage]
ORDER BY [UploadedAtUtc] DESC;

PRINT N'--- Distinct PaymentGroupId values on test payments (split-payment groups being cleared) ---';
SELECT DISTINCT [PaymentGroupId]
FROM dbo.[Payment]
WHERE [PaymentGroupId] IS NOT NULL;

/* ---------- Preview-only exit ---------- */
IF @ExecuteReset = 0
BEGIN;
    PRINT N'No data was modified (@ExecuteReset = 0). Take a fresh full backup, review the preview above, then set @ExecuteReset = 1 AND @BackupConfirmed = 1 to execute.';
    RETURN;
END;

/* ---------- Guard 2: explicit backup opt-in ---------- */
IF @BackupConfirmed = 0
BEGIN;
    THROW 50004, 'Safety stop: @BackupConfirmed = 0. Take a fresh FULL backup of DoodhDirect immediately before executing, then set @BackupConfirmed = 1.', 1;
END;

IF NOT EXISTS (
    SELECT 1
    FROM msdb.dbo.backupset
    WHERE database_name = N'DoodhDirect'
      AND [type] = N'D'  -- full database backup
      AND backup_finish_date >= DATEADD(HOUR, -4, SYSUTCDATETIME())
)
BEGIN;
    THROW 50005, 'Safety stop: no FULL backup of DoodhDirect finished in the last 4 hours (msdb.dbo.backupset). Take a fresh BACKUP DATABASE ... WITH INIT, CHECKSUM and retry (no COMPRESSION on Express).', 1;
END;

/* ==================== EXECUTION (single explicit transaction) ==================== */
PRINT N'Starting reset inside an explicit transaction...';

BEGIN TRY;
    BEGIN TRANSACTION;

    /* Capture pre-reset master counts for the in-transaction integrity check. */
    DECLARE @preUser INT = (SELECT COUNT(*) FROM dbo.[User]);
    DECLARE @preBranch INT = (SELECT COUNT(*) FROM dbo.[Branch]);
    DECLARE @preProduct INT = (SELECT COUNT(*) FROM dbo.[Product]);
    DECLARE @preCustomerProfile INT = (SELECT COUNT(*) FROM dbo.[CustomerProfile]);
    DECLARE @preCustomerAddress INT = (SELECT COUNT(*) FROM dbo.[CustomerAddress]);
    DECLARE @preCharge INT = (SELECT COUNT(*) FROM dbo.[Charge]);
    /* Company bank configuration is preserved; resolved via dynamic SQL
       so the batch compiles where migration 20261021074859 is not yet
       applied (missing table counts as 0 rows). */
    DECLARE @preCompanyBank INT = 0;
    IF OBJECT_ID(N'dbo.[CompanyBankDetail]', N'U') IS NOT NULL
    BEGIN;
        DECLARE @preCompanyBankCount TABLE ([Rows] INT);
        INSERT INTO @preCompanyBankCount EXEC sp_executesql N'SELECT COUNT(*) FROM dbo.[CompanyBankDetail];';
        SELECT @preCompanyBank = [Rows] FROM @preCompanyBankCount;
    END;
    DECLARE @preNumberSeries INT = (SELECT COUNT(*) FROM dbo.[NumberSeries]);
    DECLARE @preWallet INT = (SELECT COUNT(*) FROM dbo.[Wallet]);

    /* CUSTOMER series snapshot: CustomerProfile rows (and their CUST/ numbers)
       are preserved, so the CUSTOMER counter must emerge byte-identical. */
    DECLARE @preCustomer TABLE (
        [ScopeKey] NVARCHAR(50) NOT NULL,
        [StartingNumber] BIGINT NOT NULL,
        [LastUsedNumber] BIGINT NOT NULL,
        [LastUsedAtUtc] DATETIME2 NULL
    );
    INSERT INTO @preCustomer ([ScopeKey], [StartingNumber], [LastUsedNumber], [LastUsedAtUtc])
    SELECT [ScopeKey], [StartingNumber], [LastUsedNumber], [LastUsedAtUtc]
    FROM dbo.[NumberSeries]
    WHERE [Code] = N'CUSTOMER';

    /* ------------------------------------------------------------------
       DELETION ORDER (child -> parent, every FK verified in
       DoodhDirectDbContext.OnModelCreating; all relationships use
       DeleteBehavior.Restrict except the CASCADES noted, which are still
       deleted explicitly so nothing depends on cascade):
         NotificationAttempt -> NotificationDelivery (both reference
           Notification; Attempt references Delivery).
         WalletTransaction (references Wallet, Payment, [Order],
           Subscription — must precede all four).
         WithdrawalRequest -> CustomerBankDetail (Restrict): request rows
           first, then the bank-detail rows (both reference only retained
           User rows otherwise).
         SubscriptionCancelRefundOccurrence -> SubscriptionDelivery +
           Subscription (Restrict): before either parent.
         OrderCancellation -> [Order] (Cascade, deleted explicitly).
         RefundBankExportItemReservation -> RefundBankExportItem + Refund
           (both Restrict): links first, before either parent.
         RefundBankExportItem -> RefundBankExportBatch + RefundReplacementRequest
           (both Restrict): items before their batch and their ticket.
         RefundBankExportBatch (parent kept until its items are gone).
         Refund -> Payment (Refund.PaymentId, Restrict).
         VacationRefundOccurrence (references its request with Cascade and
           SubscriptionDelivery with Restrict — precedes both).
         MilkTestParameter / MilkTestImage -> MilkTest (Restrict).
         RefundReplacementImage -> RefundReplacementRequest (Restrict).
         MilkUsage (references Batch, Delivery, [Order],
           SubscriptionDelivery, OrderItem, DeliveryBatchAllocation).
         DeliveryBatchAllocation -> Delivery + MilkBatch.
         DeliveryAssignment / DeliveryOtp -> Delivery.
         DeliveryLocation -> Delivery (Cascade, deleted explicitly).
         Cycle-break UPDATES (documented below), then
         RefundReplacementRequest (references Delivery, MilkTest,
           [Order], SubscriptionDelivery — precedes all four),
         MilkTest -> Delivery,
         Delivery -> [Order] + SubscriptionDelivery,
         SubscriptionDelivery -> Subscription (+ nulled RRR/self links),
         SubscriptionSchedule / SubscriptionCharge (Charge rows Cascade
           from Subscription) -> Subscription,
         VacationRefundRequest -> Subscription,
         Payment -> [Order] + Subscription,
         PaymentWebhook (standalone — no FK to payments; cleared with payments),
         Subscription -> Customer/Profile/Branch/Address (parents kept),
         OrderCharge / OrderItem (both Cascade from [Order]),
         [Order] (reserved word, always bracketed),
         MilkBatch -> MilkProduction,
         MilkProduction -> Branch (parent kept),
         Notification -> NotificationEvent,
         NotificationEvent -> [User] (parent kept).
       ------------------------------------------------------------------ */

    /* ---- Stage 3 export batches (OBJECT_ID-guarded dynamic SQL: the batch
       still compiles where migration 20261021104319 has not been applied).
       Child-first: Reservation -> Item -> Batch (all Restrict). ---- */
    IF OBJECT_ID(N'dbo.[RefundBankExportItemReservation]', N'U') IS NOT NULL
        EXEC sp_executesql N'DELETE FROM dbo.[RefundBankExportItemReservation];';
    IF OBJECT_ID(N'dbo.[RefundBankExportItem]', N'U') IS NOT NULL
        EXEC sp_executesql N'DELETE FROM dbo.[RefundBankExportItem];';
    IF OBJECT_ID(N'dbo.[RefundBankExportBatch]', N'U') IS NOT NULL
        EXEC sp_executesql N'DELETE FROM dbo.[RefundBankExportBatch];';

    DELETE FROM dbo.[NotificationAttempt];
    DELETE FROM dbo.[NotificationDelivery];
    DELETE FROM dbo.[WalletTransaction];
    /* WithdrawalRequest before CustomerBankDetail (Restrict FK BankDetailId).
       Both are cleared with the wallet group: the ledger legs above include
       every WithdrawalDebit / WithdrawalReversal, so no request may survive
       without its debit. TransferReference travels with its request row. */
    DELETE FROM dbo.[WithdrawalRequest];
    DELETE FROM dbo.[CustomerBankDetail];
    DELETE FROM dbo.[Refund];
    DELETE FROM dbo.[VacationRefundOccurrence];
    DELETE FROM dbo.[MilkTestParameter];
    DELETE FROM dbo.[MilkTestImage];
    DELETE FROM dbo.[RefundReplacementImage];
    DELETE FROM dbo.[MilkUsage];
    DELETE FROM dbo.[DeliveryBatchAllocation];
    DELETE FROM dbo.[DeliveryAssignment];
    DELETE FROM dbo.[DeliveryOtp];
    DELETE FROM dbo.[DeliveryLocation];

    /* ---- Cycle / self-reference handling (documented, narrow, nullable-only):
       1. SubscriptionDelivery.ReplacesSubscriptionDeliveryId is a self-FK
          (Restrict): replacement occurrences point at the occurrence they
          replace. Null it so replacements no longer block parent deletes.
       2. SubscriptionDelivery.RefundReplacementRequestId <-> 
          RefundReplacementRequest.SubscriptionDeliveryId form a cross-table
          cycle (both Restrict, both nullable). Null the SubscriptionDelivery
          side; the request side is removed by the DELETE below.
       3. RefundReplacementRequest.SupersedesRequestId is a self-FK
          (Restrict): legacy-remediation Refund tickets point at the
          superseded ticket. Null it so superseding rows delete first
          without ordering tricks. All three columns are nullable and no
          master row is referenced by them (targets are transactional rows
          deleted in this same transaction). ---- */
    UPDATE dbo.[SubscriptionDelivery]
    SET [ReplacesSubscriptionDeliveryId] = NULL,
        [RefundReplacementRequestId] = NULL
    WHERE [ReplacesSubscriptionDeliveryId] IS NOT NULL
       OR [RefundReplacementRequestId] IS NOT NULL;

    UPDATE dbo.[RefundReplacementRequest]
    SET [SupersedesRequestId] = NULL
    WHERE [SupersedesRequestId] IS NOT NULL;

    DELETE FROM dbo.[RefundReplacementRequest];
    /* SubscriptionCancelRefundOccurrence references SubscriptionDelivery
       and Subscription with Restrict: remove it before either parent. */
    DELETE FROM dbo.[SubscriptionCancelRefundOccurrence];
    DELETE FROM dbo.[MilkTest];
    DELETE FROM dbo.[Delivery];
    DELETE FROM dbo.[SubscriptionDelivery];
    DELETE FROM dbo.[SubscriptionSchedule];
    DELETE FROM dbo.[SubscriptionCharge];
    DELETE FROM dbo.[VacationRefundRequest];
    DELETE FROM dbo.[Payment];
    DELETE FROM dbo.[PaymentWebhook];
    DELETE FROM dbo.[Subscription];
    /* OrderCancellation is a one-to-one child of [Order] (Cascade). Deleted
       explicitly per this script's no-cascade-dependence convention. */
    DELETE FROM dbo.[OrderCancellation];
    DELETE FROM dbo.[OrderCharge];
    DELETE FROM dbo.[OrderItem];
    DELETE FROM dbo.[Order];
    DELETE FROM dbo.[MilkBatch];
    DELETE FROM dbo.[MilkProduction];
    DELETE FROM dbo.[Notification];
    DELETE FROM dbo.[NotificationEvent];

    /* ---- Reset retained wallet balances to exactly zero.
       Authoritative column is Wallet.Balance (decimal 18,2, CK >= 0).
       Ledger rows are already gone, so the next Credit/Debit starts from a
       clean zero with BalanceBefore = 0. ---- */
    UPDATE dbo.[Wallet]
    SET [Balance] = CAST(0 AS DECIMAL(18,2));

    /* ---- Reset transactional number-series counters (definitions kept).
       App semantics (NumberSeries.NextNumber): next = StartingNumber when
       LastUsedNumber < StartingNumber, else LastUsedNumber + IncrementBy.
       Setting LastUsedNumber = StartingNumber - IncrementBy therefore makes
       the next issued number exactly StartingNumber (seeded 1/step 1, so 1).
       ORDER is scoped per branch (ScopeKey = branch code, FinancialYear
       reset, template ORD/<SCOPE>/{FY}/{NUMBER:000000}); every ORDER scope
       row present (DB0001, DB0002, MAIN) is reset. No legacy global ORDER
       row (ScopeKey = '') exists in this database, so there is nothing
       further to reset for ORDER.
       DELIVERY / REFUNDREPLACEMENT / VACATIONREFUND / WITHDRAWAL are global
       (ScopeKey ''). WITHDRAWAL is seeded idempotently at startup
       (EnsureSeriesAsync: missing rows are added, live counters never
       touched), so an absent WITHDRAWAL row is simply unaffected.
       Legacy scope-suffixed rows ORDER_DB0001 @ DB0001, ORDER_DB0002 @
       DB0002, DELIVERY_DB0001 @ DB0001 and DELIVERY_DB0002 @ DB0002 are
       included: the allocator still
       resolves them through its scope-suffixed fallback (verified live
       DLV/DB0001 and DLV/DB0002 delivery numbers and historical ORD
       allocations), and every document they numbered is cleared by this
       reset, so restarting them at StartingNumber cannot collide.
       CUSTOMER is deliberately EXCLUDED: customer profiles are preserved and
       their CUST/ numbers must never be re-issued. ---- */
    UPDATE dbo.[NumberSeries]
    SET [LastUsedNumber] = [StartingNumber] - [IncrementBy],
        [LastUsedAtUtc] = NULL
    WHERE [Code] IN (N'ORDER', N'DELIVERY', N'REFUNDREPLACEMENT', N'VACATIONREFUND', N'WITHDRAWAL', N'REFUNDBANKEXPORT')
       OR ([Code] = N'ORDER_DB0001' AND [ScopeKey] = N'DB0001')
       OR ([Code] = N'ORDER_DB0002' AND [ScopeKey] = N'DB0002')
       OR ([Code] = N'DELIVERY_DB0001' AND [ScopeKey] = N'DB0001')
       OR ([Code] = N'DELIVERY_DB0002' AND [ScopeKey] = N'DB0002');

    /* ================= IN-TRANSACTION VALIDATION (before COMMIT) ================= */
    DECLARE @failures INT = 0;

    /* Stage 3 export tables (OBJECT_ID-guarded; missing tables count as empty). */
    DECLARE @exportBatchRemaining INT = 0;
    DECLARE @exportItemRemaining INT = 0;
    DECLARE @exportLinkRemaining INT = 0;
    IF OBJECT_ID(N'dbo.[RefundBankExportBatch]', N'U') IS NOT NULL
    BEGIN;
        DECLARE @exportBatchCheck TABLE ([Rows] INT);
        INSERT INTO @exportBatchCheck EXEC sp_executesql N'SELECT COUNT(*) FROM dbo.[RefundBankExportBatch];';
        SELECT @exportBatchRemaining = [Rows] FROM @exportBatchCheck;
    END;
    IF OBJECT_ID(N'dbo.[RefundBankExportItem]', N'U') IS NOT NULL
    BEGIN;
        DECLARE @exportItemCheck TABLE ([Rows] INT);
        INSERT INTO @exportItemCheck EXEC sp_executesql N'SELECT COUNT(*) FROM dbo.[RefundBankExportItem];';
        SELECT @exportItemRemaining = [Rows] FROM @exportItemCheck;
    END;
    IF OBJECT_ID(N'dbo.[RefundBankExportItemReservation]', N'U') IS NOT NULL
    BEGIN;
        DECLARE @exportLinkCheck TABLE ([Rows] INT);
        INSERT INTO @exportLinkCheck EXEC sp_executesql N'SELECT COUNT(*) FROM dbo.[RefundBankExportItemReservation];';
        SELECT @exportLinkRemaining = [Rows] FROM @exportLinkCheck;
    END;
    IF @exportLinkRemaining > 0  BEGIN; PRINT N'VALIDATION FAIL: RefundBankExportItemReservation not empty.';  SET @failures += 1; END;
    IF @exportItemRemaining > 0  BEGIN; PRINT N'VALIDATION FAIL: RefundBankExportItem not empty.';              SET @failures += 1; END;
    IF @exportBatchRemaining > 0 BEGIN; PRINT N'VALIDATION FAIL: RefundBankExportBatch not empty.';             SET @failures += 1; END;

    IF EXISTS (SELECT 1 FROM dbo.[NotificationAttempt])       BEGIN; PRINT N'VALIDATION FAIL: NotificationAttempt not empty.';       SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[NotificationDelivery])      BEGIN; PRINT N'VALIDATION FAIL: NotificationDelivery not empty.';      SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[WalletTransaction])         BEGIN; PRINT N'VALIDATION FAIL: WalletTransaction not empty.';         SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[WithdrawalRequest])         BEGIN; PRINT N'VALIDATION FAIL: WithdrawalRequest not empty.';         SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[CustomerBankDetail])        BEGIN; PRINT N'VALIDATION FAIL: CustomerBankDetail not empty.';        SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[Refund])                    BEGIN; PRINT N'VALIDATION FAIL: Refund not empty.';                    SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[VacationRefundOccurrence])  BEGIN; PRINT N'VALIDATION FAIL: VacationRefundOccurrence not empty.';  SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[MilkTestParameter])         BEGIN; PRINT N'VALIDATION FAIL: MilkTestParameter not empty.';         SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[MilkTestImage])             BEGIN; PRINT N'VALIDATION FAIL: MilkTestImage not empty.';             SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[RefundReplacementImage])    BEGIN; PRINT N'VALIDATION FAIL: RefundReplacementImage not empty.';    SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[MilkUsage])                 BEGIN; PRINT N'VALIDATION FAIL: MilkUsage not empty.';                 SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[DeliveryBatchAllocation])   BEGIN; PRINT N'VALIDATION FAIL: DeliveryBatchAllocation not empty.';   SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[DeliveryAssignment])        BEGIN; PRINT N'VALIDATION FAIL: DeliveryAssignment not empty.';        SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[DeliveryOtp])               BEGIN; PRINT N'VALIDATION FAIL: DeliveryOtp not empty.';               SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[DeliveryLocation])          BEGIN; PRINT N'VALIDATION FAIL: DeliveryLocation not empty.';          SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[RefundReplacementRequest])  BEGIN; PRINT N'VALIDATION FAIL: RefundReplacementRequest not empty.';  SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[SubscriptionCancelRefundOccurrence]) BEGIN; PRINT N'VALIDATION FAIL: SubscriptionCancelRefundOccurrence not empty.'; SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[MilkTest])                  BEGIN; PRINT N'VALIDATION FAIL: MilkTest not empty.';                  SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[Delivery])                  BEGIN; PRINT N'VALIDATION FAIL: Delivery not empty.';                  SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[SubscriptionDelivery])      BEGIN; PRINT N'VALIDATION FAIL: SubscriptionDelivery not empty.';      SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[SubscriptionSchedule])      BEGIN; PRINT N'VALIDATION FAIL: SubscriptionSchedule not empty.';      SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[SubscriptionCharge])        BEGIN; PRINT N'VALIDATION FAIL: SubscriptionCharge not empty.';        SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[VacationRefundRequest])     BEGIN; PRINT N'VALIDATION FAIL: VacationRefundRequest not empty.';     SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[Payment])                   BEGIN; PRINT N'VALIDATION FAIL: Payment not empty.';                   SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[PaymentWebhook])            BEGIN; PRINT N'VALIDATION FAIL: PaymentWebhook not empty.';            SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[Subscription])              BEGIN; PRINT N'VALIDATION FAIL: Subscription not empty.';              SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[OrderCancellation])         BEGIN; PRINT N'VALIDATION FAIL: OrderCancellation not empty.';         SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[OrderCharge])               BEGIN; PRINT N'VALIDATION FAIL: OrderCharge not empty.';               SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[OrderItem])                 BEGIN; PRINT N'VALIDATION FAIL: OrderItem not empty.';                 SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[Order])                     BEGIN; PRINT N'VALIDATION FAIL: [Order] not empty.';                     SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[MilkBatch])                 BEGIN; PRINT N'VALIDATION FAIL: MilkBatch not empty.';                 SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[MilkProduction])            BEGIN; PRINT N'VALIDATION FAIL: MilkProduction not empty.';            SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[Notification])              BEGIN; PRINT N'VALIDATION FAIL: Notification not empty.';              SET @failures += 1; END;
    IF EXISTS (SELECT 1 FROM dbo.[NotificationEvent])         BEGIN; PRINT N'VALIDATION FAIL: NotificationEvent not empty.';         SET @failures += 1; END;

    /* Wallet invariant: every retained account exactly zero AND ledger empty. */
    IF EXISTS (SELECT 1 FROM dbo.[Wallet] WHERE [Balance] <> CAST(0 AS DECIMAL(18,2)))
    BEGIN; PRINT N'VALIDATION FAIL: nonzero Wallet.Balance remains.'; SET @failures += 1; END;

    /* Series invariant: every reset-table code sits exactly on StartingNumber - IncrementBy. */
    IF EXISTS (
        SELECT 1 FROM dbo.[NumberSeries]
        WHERE ([Code] IN (N'ORDER', N'DELIVERY', N'REFUNDREPLACEMENT', N'VACATIONREFUND', N'WITHDRAWAL', N'REFUNDBANKEXPORT')
            OR ([Code] = N'ORDER_DB0001' AND [ScopeKey] = N'DB0001')
            OR ([Code] = N'ORDER_DB0002' AND [ScopeKey] = N'DB0002')
            OR ([Code] = N'DELIVERY_DB0001' AND [ScopeKey] = N'DB0001')
            OR ([Code] = N'DELIVERY_DB0002' AND [ScopeKey] = N'DB0002'))
          AND ([LastUsedNumber] <> [StartingNumber] - [IncrementBy] OR [LastUsedAtUtc] IS NOT NULL)
    )
    BEGIN; PRINT N'VALIDATION FAIL: transactional NumberSeries counters not reset.'; SET @failures += 1; END;

    /* Master-data integrity: preserved counts must be unchanged. */
    IF (SELECT COUNT(*) FROM dbo.[User]) <> @preUser
    BEGIN; PRINT N'VALIDATION FAIL: User count changed.'; SET @failures += 1; END;
    IF (SELECT COUNT(*) FROM dbo.[Branch]) <> @preBranch
    BEGIN; PRINT N'VALIDATION FAIL: Branch count changed.'; SET @failures += 1; END;
    IF (SELECT COUNT(*) FROM dbo.[Product]) <> @preProduct
    BEGIN; PRINT N'VALIDATION FAIL: Product count changed.'; SET @failures += 1; END;
    IF (SELECT COUNT(*) FROM dbo.[CustomerProfile]) <> @preCustomerProfile
    BEGIN; PRINT N'VALIDATION FAIL: CustomerProfile count changed.'; SET @failures += 1; END;
    IF (SELECT COUNT(*) FROM dbo.[CustomerAddress]) <> @preCustomerAddress
    BEGIN; PRINT N'VALIDATION FAIL: CustomerAddress count changed.'; SET @failures += 1; END;
    IF (SELECT COUNT(*) FROM dbo.[Charge]) <> @preCharge
    BEGIN; PRINT N'VALIDATION FAIL: Charge count changed.'; SET @failures += 1; END;
    IF OBJECT_ID(N'dbo.[CompanyBankDetail]', N'U') IS NOT NULL
    BEGIN;
        DECLARE @postCompanyBankCount TABLE ([Rows] INT);
        INSERT INTO @postCompanyBankCount EXEC sp_executesql N'SELECT COUNT(*) FROM dbo.[CompanyBankDetail];';
        IF (SELECT [Rows] FROM @postCompanyBankCount) <> @preCompanyBank
        BEGIN; PRINT N'VALIDATION FAIL: CompanyBankDetail count changed (company bank configuration must be kept).'; SET @failures += 1; END;
    END;
    IF (SELECT COUNT(*) FROM dbo.[NumberSeries]) <> @preNumberSeries
    BEGIN; PRINT N'VALIDATION FAIL: NumberSeries row count changed (definitions must be kept).'; SET @failures += 1; END;
    IF EXISTS (
        SELECT [ScopeKey], [StartingNumber], [LastUsedNumber], [LastUsedAtUtc]
        FROM dbo.[NumberSeries]
        WHERE [Code] = N'CUSTOMER'
        EXCEPT
        SELECT [ScopeKey], [StartingNumber], [LastUsedNumber], [LastUsedAtUtc]
        FROM @preCustomer
    ) OR EXISTS (
        SELECT [ScopeKey], [StartingNumber], [LastUsedNumber], [LastUsedAtUtc]
        FROM @preCustomer
        EXCEPT
        SELECT [ScopeKey], [StartingNumber], [LastUsedNumber], [LastUsedAtUtc]
        FROM dbo.[NumberSeries]
        WHERE [Code] = N'CUSTOMER'
    )
    BEGIN; PRINT N'VALIDATION FAIL: CUSTOMER series counter changed (must be preserved).'; SET @failures += 1; END;
    IF (SELECT COUNT(*) FROM dbo.[Wallet]) <> @preWallet
    BEGIN; PRINT N'VALIDATION FAIL: Wallet account count changed (accounts must be kept).'; SET @failures += 1; END;

    IF @failures > 0
    BEGIN;
        THROW 50006, 'Post-reset validation failed. Rolling back — database left unchanged.', 1;
    END;

    COMMIT TRANSACTION;
    PRINT N'Reset committed successfully.';
END TRY
BEGIN CATCH;
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @msg NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @sev INT = ERROR_SEVERITY();
    DECLARE @st  INT = ERROR_STATE();
    RAISERROR(N'Reset FAILED and was rolled back: %s', @sev, @st, @msg);
END CATCH;

/* ==================== POST-RESET SUMMARY (reads committed state) ==================== */
PRINT N'================================================================';
PRINT N'POST-RESET SUMMARY';
PRINT N'================================================================';
/* Stage 3 export tables (OBJECT_ID-guarded; missing tables report 0). */
DECLARE @postExportBatch INT = 0;
DECLARE @postExportItem INT = 0;
DECLARE @postExportLink INT = 0;
IF OBJECT_ID(N'dbo.[RefundBankExportBatch]', N'U') IS NOT NULL
BEGIN;
    DECLARE @postExportBatchCount TABLE ([Rows] INT);
    INSERT INTO @postExportBatchCount EXEC sp_executesql N'SELECT COUNT(*) FROM dbo.[RefundBankExportBatch];';
    SELECT @postExportBatch = [Rows] FROM @postExportBatchCount;
END;
IF OBJECT_ID(N'dbo.[RefundBankExportItem]', N'U') IS NOT NULL
BEGIN;
    DECLARE @postExportItemCount TABLE ([Rows] INT);
    INSERT INTO @postExportItemCount EXEC sp_executesql N'SELECT COUNT(*) FROM dbo.[RefundBankExportItem];';
    SELECT @postExportItem = [Rows] FROM @postExportItemCount;
END;
IF OBJECT_ID(N'dbo.[RefundBankExportItemReservation]', N'U') IS NOT NULL
BEGIN;
    DECLARE @postExportLinkCount TABLE ([Rows] INT);
    INSERT INTO @postExportLinkCount EXEC sp_executesql N'SELECT COUNT(*) FROM dbo.[RefundBankExportItemReservation];';
    SELECT @postExportLink = [Rows] FROM @postExportLinkCount;
END;
SELECT
    (SELECT COUNT(*) FROM dbo.[Wallet] WHERE [Balance] <> CAST(0 AS DECIMAL(18,2))) AS [NonzeroWallets],
    (SELECT COUNT(*) FROM dbo.[WalletTransaction]) AS [WalletLedgerRows],
    (SELECT COUNT(*) FROM dbo.[WithdrawalRequest]) AS [WithdrawalRequests],
    (SELECT COUNT(*) FROM dbo.[CustomerBankDetail]) AS [CustomerBankDetails],
    (@companyBankRows) AS [CompanyBankDetailsPreserved],
    (SELECT COUNT(*) FROM dbo.[Payment]) AS [Payments],
    (SELECT COUNT(*) FROM dbo.[Order]) AS [Orders],
    (SELECT COUNT(*) FROM dbo.[OrderCancellation]) AS [OrderCancellations],
    (SELECT COUNT(*) FROM dbo.[Subscription]) AS [Subscriptions],
    (SELECT COUNT(*) FROM dbo.[SubscriptionCancelRefundOccurrence]) AS [SubscriptionCancelRefundOccurrences],
    (SELECT COUNT(*) FROM dbo.[Delivery]) AS [Deliveries],
    (SELECT COUNT(*) FROM dbo.[RefundReplacementRequest]) AS [RefundReplacementRequests],
    (@postExportBatch) AS [RefundBankExportBatches],
    (@postExportItem) AS [RefundBankExportItems],
    (@postExportLink) AS [RefundBankExportItemReservations],
    (SELECT COUNT(*) FROM dbo.[VacationRefundRequest]) AS [VacationRefundRequests],
    (SELECT COUNT(*) FROM dbo.[MilkProduction]) AS [MilkProductions],
    (SELECT COUNT(*) FROM dbo.[MilkBatch]) AS [MilkBatches],
    (SELECT COUNT(*) FROM dbo.[NotificationEvent]) AS [NotificationEvents];

SELECT [Code], [ScopeKey], [StartingNumber], [LastUsedNumber],
       CASE WHEN ([Code] IN (N'ORDER', N'DELIVERY', N'REFUNDREPLACEMENT', N'VACATIONREFUND', N'WITHDRAWAL', N'REFUNDBANKEXPORT')
           OR ([Code] = N'ORDER_DB0001' AND [ScopeKey] = N'DB0001')
           OR ([Code] = N'ORDER_DB0002' AND [ScopeKey] = N'DB0002')
           OR ([Code] = N'DELIVERY_DB0001' AND [ScopeKey] = N'DB0001')
           OR ([Code] = N'DELIVERY_DB0002' AND [ScopeKey] = N'DB0002'))
         THEN N'RESET' ELSE N'PRESERVE' END AS [Action],
       CASE WHEN ([Code] IN (N'ORDER', N'DELIVERY', N'REFUNDREPLACEMENT', N'VACATIONREFUND', N'WITHDRAWAL', N'REFUNDBANKEXPORT')
           OR ([Code] = N'ORDER_DB0001' AND [ScopeKey] = N'DB0001')
           OR ([Code] = N'ORDER_DB0002' AND [ScopeKey] = N'DB0002')
           OR ([Code] = N'DELIVERY_DB0001' AND [ScopeKey] = N'DB0001')
           OR ([Code] = N'DELIVERY_DB0002' AND [ScopeKey] = N'DB0002'))
         THEN [StartingNumber] ELSE NULL END AS [NextNumberToBeIssued]
FROM dbo.[NumberSeries]
ORDER BY [Code], [ScopeKey];

/* ============================================================================
   FUTURE MAINTENANCE — assessing new tables and migrations
   ----------------------------------------------------------------------------
   When a migration adds a table (or a code that needs a counter reset),
   classify it explicitly and wire it into EVERY section below. Never add a
   broad dynamic mechanism (e.g. looping over sys.tables): each table needs
   an explicit retention decision recorded here.

   Assessment checklist for a new table T:
     1. Transactional test data (orders, payments, ledger, tickets,
        requests, occurrences, images, webhooks, notifications)?
        -> CLEAR: add T to (a) the header CLEARED inventory, (b) both Guard-1
        name lists (temporal + triggers), (c) the preview CLEAR counts,
        (d) the DELETE block in child-before-parent (Restrict) order — verify
        the FK graph in DoodhDirectDbContext.OnModelCreating and with
        sys.foreign_keys — (e) the in-transaction IF EXISTS validation,
        (f) the post-reset summary. If T is referenced BY a cleared table
        with Restrict, delete T AFTER its dependents; if T references a
        preserved parent, delete T BEFORE anything that would orphan it.
     2. Master / identity / configuration / audit data (users, roles,
        branches, catalogue, charges, templates, preferences, devices,
        invitations, system config, audit log, migration history, customer
        profiles/addresses/numbers)?
        -> PRESERVE: add T to (a) the header PRESERVED inventory, (b) the
        preview PRESERVE counts, (c) the in-transaction master-count
        integrity checks. Never include in a DELETE.
     3. Sensitive credentials linked to preserved owners (bank details,
        secrets, tokens)?
        -> Follow the CustomerBankDetail precedent: CLEAR the credential
        rows with their transactional group (owners stay), so no orphaned
        secrets linger. Document the policy in the header.
     4. New NumberSeries code (or a new scope of an existing code)?
        -> If its documents are cleared: add the code to (a) the preview
        RESET counts, (b) the series Action CASE (preview + post-reset),
        (c) the UPDATE ... WHERE, (d) the series invariant validation.
        If its owners are preserved (e.g. CUSTOMER): do NOT add; extend the
        CUSTOMER-style byte-identical check instead.
     5. Re-run the preview (section 4 of the header) and confirm the new
        table appears under the intended Action with the expected count.

   Current inventory (mirrors the code — keep in sync):
     CLEARED (37): RefundBankExportItemReservation, RefundBankExportItem,
       RefundBankExportBatch (OBJECT_ID-guarded until migration
       20261021104319_RefundBankExportBatches is applied everywhere),
       NotificationAttempt, NotificationDelivery,
       WalletTransaction, WithdrawalRequest, CustomerBankDetail, Refund,
       VacationRefundOccurrence, MilkTestParameter, MilkTestImage,
       RefundReplacementImage, MilkUsage, DeliveryBatchAllocation,
       DeliveryAssignment, DeliveryOtp, DeliveryLocation,
       RefundReplacementRequest, SubscriptionCancelRefundOccurrence,
       MilkTest, Delivery, SubscriptionDelivery, SubscriptionSchedule,
       SubscriptionCharge, VacationRefundRequest, Payment, PaymentWebhook,
       Subscription, OrderCancellation, OrderCharge, OrderItem, [Order],
       MilkBatch, MilkProduction, Notification, NotificationEvent.
     RESET (state re-initialised, rows kept): Wallet.Balance -> 0.00;
       NumberSeries codes ORDER (all scopes), DELIVERY, REFUNDREPLACEMENT,
       VACATIONREFUND, WITHDRAWAL, REFUNDBANKEXPORT, ORDER_DB0001 @ DB0001,
       ORDER_DB0002 @ DB0002, DELIVERY_DB0001 @ DB0001,
       DELIVERY_DB0002 @ DB0002.
     PRESERVED: User (+ roles/permissions/sessions), CustomerProfile,
       CustomerAddress (+ CUSTOMER number history, byte-identical),
       Branch, catalogue + Charge master data, SystemConfiguration,
       CompanyBankDetail (company payout account; OBJECT_ID-guarded until
       migration 20261021074859_CompanyBankDetail is applied everywhere),
       BrandingAsset, Camera/CameraStream, NotificationTemplate/Preference,
       UserDevice, EmployeeInvitation(+Branch), NumberSeries definitions,
       AuditLog, __EFMigrationsHistory, plus external App_Data files.
   ============================================================================ */

/* ============================================================================
   EXTERNAL STORAGE CLEANUP PLAN (separate from SQL — SQL never deletes files)
   ----------------------------------------------------------------------------
   Media provider is LocalMediaStorage (IMediaStorage). Milk-test images live
   under the app's MilkTestMedia:LocalRootPath = "App_Data/MilkTestMedia"
   (relative to the API working directory), with keys shaped
   yyyy/MM/{BranchId}/{MilkTestPublicId}/{guid}.ext. Refund/replacement
   evidence images use the same IMediaStorage abstraction (StorageKey in
   dbo.RefundReplacementImage). Branding assets and ProductImage rows are
   PRESERVED master data — their files must NOT be touched.

   Narrowly scoped file cleanup AFTER the SQL reset succeeds:
     1. Before running the SQL reset, export the exact keys to delete:
          SELECT [StorageKey] FROM dbo.[MilkTestImage];
          SELECT [StorageKey] FROM dbo.[RefundReplacementImage];
        save both result sets (they are the allow-list; delete nothing else).
     2. After the SQL reset commits, for each exported StorageKey delete the
        single file at <media-root>/<StorageKey> if it exists. Do NOT delete
        whole yyyy/MM folders — sibling months may hold retained
        ProductImage/BrandingAsset files.
     3. Verify: re-run the two SELECTs (expect 0 rows) and confirm no
        orphaned files remain for exactly those keys.
   ============================================================================ */
