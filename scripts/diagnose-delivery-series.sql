SET NOCOUNT ON;

PRINT '=== 1. All Delivery rows with a DeliveryNumber (per-number sequence) ===';
SELECT
    d.Id,
    d.PublicId,
    d.DeliveryNumber,
    d.ReferenceNumber,
    d.OrderId,
    d.SubscriptionDeliveryId,
    d.Status,
    d.ScheduledDate,
    d.CreatedAtUtc,
    o.OrderNumber
FROM dbo.Delivery d
LEFT JOIN dbo.[Order] o ON o.Id = d.OrderId
WHERE d.DeliveryNumber IS NOT NULL
ORDER BY TRY_CONVERT(bigint, RIGHT(d.DeliveryNumber, 6)), d.CreatedAtUtc;

PRINT '=== 2. The exact DEL/000001 row ===';
SELECT
    d.Id,
    d.PublicId,
    d.DeliveryNumber,
    d.ReferenceNumber,
    d.OrderId,
    d.Status,
    d.CreatedAtUtc,
    o.OrderNumber,
    o.Status AS OrderStatus
FROM dbo.Delivery d
LEFT JOIN dbo.[Order] o ON o.Id = d.OrderId
WHERE d.DeliveryNumber = 'DEL/000001';

PRINT '=== 3. Highest DeliveryNumber sequence (by numeric suffix) ===';
SELECT
    MAX(TRY_CONVERT(bigint, RIGHT(DeliveryNumber, 6))) AS HighestSequence,
    MAX(DeliveryNumber) AS MaxLexical,
    COUNT(*) AS DeliveryNumberCount
FROM dbo.Delivery
WHERE DeliveryNumber IS NOT NULL;

PRINT '=== 4. Delivery count (total rows) ===';
SELECT COUNT(*) AS TotalDeliveryRows, SUM(CASE WHEN DeliveryNumber IS NULL THEN 1 ELSE 0 END) AS NullDeliveryNumbers
FROM dbo.Delivery;

PRINT '=== 5. NumberSeries rows for DELIVERY (exact Code/ScopeKey/template/counter) ===';
SELECT
    Id,
    PublicId,
    Code,
    ScopeKey,
    [Description],
    Template,
    StartingNumber,
    LastUsedNumber,
    IncrementBy,
    ResetPolicy,
    IsActive,
    LastUsedAtUtc,
    CreatedAtUtc,
    UpdatedAtUtc,
    RowVersion
FROM dbo.NumberSeries
WHERE Code = 'DELIVERY'
ORDER BY ScopeKey;

PRINT '=== 6. ALL NumberSeries rows (overview) ===';
SELECT Code, ScopeKey, Template, StartingNumber, LastUsedNumber, IncrementBy, ResetPolicy, IsActive, LastUsedAtUtc
FROM dbo.NumberSeries
ORDER BY Code, ScopeKey;

PRINT '=== 7. Duplicate DeliveryNumber check (should be none due to unique index) ===';
SELECT DeliveryNumber, COUNT(*) AS Cnt
FROM dbo.Delivery
WHERE DeliveryNumber IS NOT NULL
GROUP BY DeliveryNumber
HAVING COUNT(*) > 1;
