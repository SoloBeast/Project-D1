SET NOCOUNT ON;

PRINT '=== 1a. Does an EXACT scoped (DELIVERY, DB0001) row exist? ===';
SELECT Id, Code, ScopeKey, Template, StartingNumber, LastUsedNumber, IncrementBy, ResetPolicy, IsActive, LastUsedAtUtc, CreatedAtUtc
FROM dbo.NumberSeries
WHERE Code = 'DELIVERY' AND ScopeKey = 'DB0001';

PRINT '=== 1b. Does a scope-SUFFIXED (DELIVERY_DB0001, DB0001) row exist? ===';
SELECT Id, Code, ScopeKey, Template, StartingNumber, LastUsedNumber, IncrementBy, ResetPolicy, IsActive, LastUsedAtUtc, CreatedAtUtc
FROM dbo.NumberSeries
WHERE Code = 'DELIVERY_DB0001';

PRINT '=== 1c. All DELIVERY% rows ===';
SELECT Id, Code, ScopeKey, Template, StartingNumber, LastUsedNumber, IncrementBy, ResetPolicy, IsActive, LastUsedAtUtc, CreatedAtUtc
FROM dbo.NumberSeries
WHERE Code LIKE 'DELIVERY%'
ORDER BY Code;

PRINT '=== 1d. Deliveries for branch DB0001 (BranchId 6) 000001..000005 ===';
SELECT d.Id, d.DeliveryNumber, d.ReferenceNumber, d.OrderId, d.BranchId, d.Status, d.CreatedAtUtc
FROM dbo.Delivery d
WHERE d.BranchId = 6
ORDER BY d.Id;

PRINT '=== 1e. Branch DB0001 validity ===';
SELECT Id, Code, Name, IsActive FROM dbo.Branch WHERE Code = 'DB0001';

PRINT '=== 2a. Branch codes (DB0002 vs DB002) ===';
SELECT Id, Code, Name, IsActive FROM dbo.Branch WHERE Code IN ('DB0002','DB002') OR Name LIKE '%Village%';

PRINT '=== 2b. DELIVERY_DB0002 row exact ===';
SELECT Id, Code, ScopeKey, Template, StartingNumber, LastUsedNumber, IncrementBy, ResetPolicy, IsActive, LastUsedAtUtc, CreatedAtUtc
FROM dbo.NumberSeries
WHERE Code = 'DELIVERY_DB0002';

PRINT '=== 2c. Any exact (DELIVERY, DB0002) row? ===';
SELECT Id, Code, ScopeKey, Template, StartingNumber, LastUsedNumber, IsActive
FROM dbo.NumberSeries
WHERE Code = 'DELIVERY' AND ScopeKey = 'DB0002';

PRINT '=== 2d. Deliveries for DB0002 branch ===';
SELECT d.Id, d.DeliveryNumber, d.ReferenceNumber, d.OrderId, d.BranchId, d.Status, d.CreatedAtUtc
FROM dbo.Delivery d
WHERE d.BranchId = (SELECT Id FROM dbo.Branch WHERE Code = 'DB0002');

PRINT '=== 2e. Orders for DB0002 ===';
SELECT Id, OrderNumber, BranchId, BranchCodeSnapshot, Status, CreatedAtUtc
FROM dbo.[Order]
WHERE BranchCodeSnapshot = 'DB0002' OR BranchId = (SELECT Id FROM dbo.Branch WHERE Code = 'DB0002');

PRINT '=== 2f. Any DeliveryNumber containing DB002 (would indicate dependency on the typo row) ===';
SELECT Id, DeliveryNumber, ReferenceNumber, BranchId
FROM dbo.Delivery
WHERE DeliveryNumber LIKE '%DB002%';

PRINT '=== 2g. NumberSeries rows whose number format could reference DB002 ===';
SELECT Id, Code, ScopeKey, Template, LastUsedNumber FROM dbo.NumberSeries WHERE ScopeKey = 'DB002';

PRINT '=== 3. Orders without deliveries (pending/confirmed) that could be affected ===';
SELECT o.Id, o.OrderNumber, o.BranchId, o.BranchCodeSnapshot, o.Status, o.CreatedAtUtc
FROM dbo.[Order] o
WHERE o.BranchCodeSnapshot = 'DB0001'
  AND NOT EXISTS (SELECT 1 FROM dbo.Delivery d WHERE d.OrderId = o.Id)
ORDER BY o.Id;
