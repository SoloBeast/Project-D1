-- Read-only verification of the applied 20260901053009_AddOtpChallengeReqId migration.
-- Safe: SELECT only, no modifications.

PRINT '=== 1. ReqId column on dbo.OtpChallenge ===';
SELECT c.name AS ColumnName,
       t.name AS DataType,
       c.max_length AS MaxLengthBytes,
       c.is_nullable AS IsNullable
FROM sys.columns c
JOIN sys.types t ON c.user_type_id = t.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.OtpChallenge')
  AND c.name = 'ReqId';

PRINT '=== 2. IX_OtpChallenge_ReqId index ===';
SELECT i.name AS IndexName,
       i.type_desc AS IndexType,
       i.is_unique AS IsUnique,
       i.is_primary_key AS IsPrimaryKey
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID('dbo.OtpChallenge')
  AND i.name = 'IX_OtpChallenge_ReqId';

PRINT '=== 3. Migration history entry ===';
SELECT MigrationId, ProductVersion
FROM __EFMigrationsHistory
WHERE MigrationId = '20260901053009_AddOtpChallengeReqId';

PRINT '=== 4. Full migration history (latest 10) ===';
SELECT TOP (10) MigrationId, ProductVersion
FROM __EFMigrationsHistory
ORDER BY MigrationId;

PRINT '=== 5. Unrelated schema check: any indexes on OtpChallenge beyond expected ===';
SELECT i.name AS IndexName, i.type_desc AS IndexType
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID('dbo.OtpChallenge')
ORDER BY i.name;

PRINT '=== 6. Business data untouched: OtpChallenge row count before/after = current count ===';
SELECT COUNT(*) AS OtpChallengeRowCount FROM dbo.OtpChallenge;
