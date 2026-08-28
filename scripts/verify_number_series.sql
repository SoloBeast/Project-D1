SET NOCOUNT ON;
PRINT '=== 1. MIGRATION HISTORY (last 6, DESC) ===';
SELECT TOP (6) MigrationId, ProductVersion
FROM dbo.__EFMigrationsHistory
ORDER BY MigrationId DESC;

PRINT '';
PRINT '=== 2. NUMBER SERIES TABLE PRESENCE ===';
SELECT TABLE_SCHEMA, TABLE_NAME
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'NumberSeries';

PRINT '';
PRINT '=== 3. INDEXES ON NumberSeries / Delivery / CustomerProfile / Branch ===';
SELECT
    OBJECT_NAME(i.object_id) AS TableName,
    i.name AS IndexName,
    i.is_unique AS IsUnique,
    i.filter_definition AS FilterDefinition,
    COL_NAME(i.object_id, ic.column_id) AS ColumnName
FROM sys.indexes i
JOIN sys.index_columns ic
    ON i.object_id = ic.object_id AND i.index_id = ic.index_id
WHERE i.object_id IN (OBJECT_ID('dbo.NumberSeries'), OBJECT_ID('dbo.Delivery'), OBJECT_ID('dbo.CustomerProfile'), OBJECT_ID('dbo.Branch'))
  AND i.name IS NOT NULL
ORDER BY TableName, IndexName, ic.key_ordinal;

PRINT '';
PRINT '=== 3b. CONSTRAINTS ON NumberSeries ===';
SELECT CONSTRAINT_NAME, CONSTRAINT_TYPE, TABLE_NAME
FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
WHERE TABLE_NAME = 'NumberSeries'
ORDER BY CONSTRAINT_TYPE, CONSTRAINT_NAME;

PRINT '';
PRINT '=== 4. SEEDED SERIES ROWS ===';
SELECT Code, Template, StartingNumber, LastUsedNumber, IncrementBy, ResetPolicy, IsActive
FROM dbo.NumberSeries
ORDER BY Code;

PRINT '';
PRINT '=== 5. BUSINESS TABLE ROW COUNTS (all dbo tables) ===';
SELECT t.name AS TableName, p.rows AS [RowCount]
FROM sys.tables t
JOIN sys.partitions p ON t.object_id = p.object_id AND p.index_id IN (0, 1)
WHERE t.schema_id = SCHEMA_ID('dbo')
ORDER BY t.name;

PRINT '';
PRINT '=== 6. NEW COLUMNS NULL CHECK (migration must NOT have backfilled) ===';
SELECT
    (SELECT COUNT(*) FROM dbo.CustomerProfile WHERE CustomerNumber IS NOT NULL) AS CustomerNumbersAssigned,
    (SELECT COUNT(*) FROM dbo.Branch WHERE BranchNumber IS NOT NULL) AS BranchNumbersAssigned,
    (SELECT COUNT(*) FROM dbo.Delivery WHERE DeliveryNumber IS NOT NULL) AS DeliveryNumbersAssigned;
