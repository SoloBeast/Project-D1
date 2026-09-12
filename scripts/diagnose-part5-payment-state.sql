/* ============================================================================
   PART 5 - READ-ONLY pre-retry verification.
   Confirms whether the affected payment(s) are still pending/recoverable
   before any retry of POST /api/v1/payments/verify, per the task guardrails.
   NO writes. NO schema changes.
   Run: sqlcmd -S .\SQLEXPRESS -d DoodhDirect -E -C -b -i scripts\diagnose-part5-payment-state.sql -W -s "|"
   ============================================================================ */
SET NOCOUNT ON;

PRINT '=== [1] Orders 76 & 77 (affected pending orders) ===';
SELECT
    o.Id                AS OrderId,
    o.OrderNumber,
    o.Status            AS OrderStatus,
    o.BranchId,
    o.BranchCodeSnapshot,
    o.PayableAmount,
    o.CreatedAtUtc
FROM dbo.[Order] o
WHERE o.Id IN (76, 77)
ORDER BY o.Id;

PRINT '=== [2] Deliveries already linked to orders 76 & 77 ===';
SELECT
    d.Id             AS DeliveryId,
    d.OrderId,
    d.DeliveryNumber,
    d.Status         AS DeliveryStatus,
    d.ScheduledDate,
    d.CreatedAtUtc
FROM dbo.Delivery d
WHERE d.OrderId IN (76, 77)
ORDER BY d.Id;

PRINT '=== [3] Payments for orders 76 & 77 (status/method/expiry) ===';
SELECT
    p.Id                AS PaymentId,
    p.OrderId,
    p.Method,                       -- 0=Razorpay,1=Wallet,2=Development
    p.Status,                       -- 0=Initiated,1=Pending,2=Success,3=Failed,4=Cancelled,5=Expired
    p.Amount,
    p.Currency,
    p.GatewayOrderId,
    p.GatewayPaymentId,
    p.GatewayStatus,
    p.ExpiresAtUtc,
    p.VerifiedAtUtc,
    p.FailedAtUtc,
    p.FailureCode,
    p.IdempotencyKey,
    p.CreatedAtUtc
FROM dbo.Payment p
WHERE p.OrderId IN (76, 77)
ORDER BY p.Id;

PRINT '=== [4] All Pending/Expired payments (recoverable window) ===';
-- NOTE: Payment.Status / Method are stored as nvarchar (enum names), not ints.
SELECT
    p.Id AS PaymentId,
    p.OrderId,
    p.Status,
    p.Method,
    p.ExpiresAtUtc,
    p.GatewayPaymentId,
    p.GatewayStatus,
    p.CreatedAtUtc
FROM dbo.Payment p
WHERE p.Status IN ('Pending', 'Expired')
ORDER BY p.Id;

PRINT '=== [4b] Most recent 20 payments joined to their order ===';
SELECT TOP (20)
    p.Id            AS PaymentId,
    p.OrderId,
    o.OrderNumber,
    p.Method,
    p.Status,
    p.Amount,
    p.GatewayOrderId,
    p.GatewayPaymentId,
    p.GatewayStatus,
    p.ExpiresAtUtc,
    p.VerifiedAtUtc,
    p.FailedAtUtc,
    p.CreatedAtUtc
FROM dbo.Payment p
LEFT JOIN dbo.[Order] o ON o.Id = p.OrderId
ORDER BY p.Id DESC;

PRINT '=== [5] Existing deliveries for branch DB0001 (expect 000001-000005) ===';
SELECT
    d.Id             AS DeliveryId,
    d.DeliveryNumber,
    d.Status         AS DeliveryStatus,
    d.OrderId,
    d.BranchId,
    d.CreatedAtUtc
FROM dbo.Delivery d
JOIN dbo.Branch b ON b.Id = d.BranchId
WHERE b.Code = 'DB0001'
ORDER BY d.DeliveryNumber;

PRINT '=== [6] Delivery/Order number series rows (scoped + unscoped) ===';
SELECT
    ns.Id,
    ns.Code,
    ns.ScopeKey,
    ns.Template,
    ns.StartingNumber,
    ns.IncrementBy,
    ns.ResetPolicy,
    ns.IsActive,
    ns.LastUsedNumber
FROM dbo.NumberSeries ns
WHERE ns.Code LIKE 'DELIVERY%' OR ns.Code LIKE 'ORDER%'
ORDER BY ns.Code, ns.ScopeKey;

PRINT '=== [7] Highest DeliveryNumber overall (sanity) ===';
SELECT TOP (5) DeliveryNumber
FROM dbo.Delivery
ORDER BY DeliveryNumber DESC;
