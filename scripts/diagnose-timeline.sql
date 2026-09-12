SET NOCOUNT ON;

PRINT '=== A. Order for the failing verification (000006) ===';
SELECT
    o.Id,
    o.PublicId,
    o.OrderNumber,
    o.Status,
    o.BranchId,
    o.BranchCodeSnapshot,
    o.CustomerId,
    o.PayableAmount,
    o.CreatedAtUtc
FROM dbo.[Order] o
WHERE o.OrderNumber LIKE '%000006'
ORDER BY o.Id;

PRINT '=== B. Delivery rows for that order (should be none before verify) ===';
SELECT d.Id, d.PublicId, d.DeliveryNumber, d.ReferenceNumber, d.OrderId, d.Status, d.CreatedAtUtc
FROM dbo.Delivery d
WHERE d.OrderId IN (SELECT Id FROM dbo.[Order] WHERE OrderNumber LIKE '%000006');

PRINT '=== C. Branch codes ===';
SELECT Id, Code, Name, IsActive FROM dbo.Branch ORDER BY Id;

PRINT '=== D. DELIVERY-related series: creation/update timeline ===';
SELECT
    Id, Code, ScopeKey, Template, StartingNumber, LastUsedNumber, IncrementBy,
    ResetPolicy, IsActive, LastUsedAtUtc, CreatedAtUtc, UpdatedAtUtc
FROM dbo.NumberSeries
WHERE Code LIKE 'DELIVERY%'
ORDER BY CreatedAtUtc;

PRINT '=== E. ORDER-related series: creation/update timeline ===';
SELECT
    Id, Code, ScopeKey, Template, StartingNumber, LastUsedNumber, IncrementBy,
    ResetPolicy, IsActive, LastUsedAtUtc, CreatedAtUtc, UpdatedAtUtc
FROM dbo.NumberSeries
WHERE Code LIKE 'ORDER%'
ORDER BY CreatedAtUtc;

PRINT '=== F. Payments for order 000006 ===';
SELECT p.Id, p.PublicId, p.Status, p.Method, p.Amount, p.GatewayOrderId, p.GatewayPaymentId, p.OrderId, p.CreatedAtUtc, p.VerifiedAtUtc
FROM dbo.Payment p
WHERE p.OrderId IN (SELECT Id FROM dbo.[Order] WHERE OrderNumber LIKE '%000006')
ORDER BY p.Id;

PRINT '=== G. NumberSeries rows by CreatedAtUtc (latest seed activity) ===';
SELECT Id, Code, ScopeKey, LastUsedNumber, LastUsedAtUtc, CreatedAtUtc
FROM dbo.NumberSeries
ORDER BY CreatedAtUtc DESC;
