SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

WITH Incident(PublicId) AS (
    SELECT CAST(v.PublicId AS uniqueidentifier)
    FROM (VALUES
        ('B818B208-110B-4C8B-BAF5-4A078DDF2CF3'),
        ('654CDB8B-229D-46B6-AA66-691487F7960E'),
        ('CD1A5DCF-9981-44BB-8B35-FD67D2E8A48D'),
        ('90F8B59D-5476-4A13-AEEC-CAB359A03CDB')
    ) v(PublicId)
), Evidence AS (
    SELECT
        p.Id AS PaymentRowId,
        p.PublicId AS PaymentPublicId,
        p.Status AS PaymentStatus,
        p.Method AS PaymentMethod,
        p.Amount,
        p.Currency,
        p.RefundedAmount,
        p.GatewayOrderId,
        p.GatewayPaymentId,
        p.GatewayStatus,
        p.OrderId,
        o.PublicId AS OrderPublicId,
        o.OrderNumber,
        o.Status AS OrderStatus,
        o.PayableAmount,
        COUNT(DISTINCT d.Id) AS DeliveryCount,
        COUNT(DISTINCT otp.Id) AS OtpCount,
        COUNT(DISTINCT r.Id) AS RefundCount,
        COALESCE(SUM(DISTINCT r.Amount), 0) AS RefundAmount,
        (SELECT COUNT(*)
         FROM NotificationEvent ne
         WHERE ne.PayloadJson LIKE '%' + CONVERT(varchar(36), p.PublicId) + '%'
            OR (p.OrderId IS NOT NULL AND ne.PayloadJson LIKE '%' + CONVERT(varchar(36), o.PublicId) + '%')
            OR (p.GatewayPaymentId IS NOT NULL AND ne.PayloadJson LIKE '%' + p.GatewayPaymentId + '%')
            OR (p.GatewayOrderId IS NOT NULL AND ne.PayloadJson LIKE '%' + p.GatewayOrderId + '%')) AS NotificationEventTokenMatches,
        CONVERT(varchar(64), HASHBYTES('SHA2_256', CONCAT(
            CONVERT(varchar(36), p.PublicId), '|', p.Status, '|', p.Method, '|', CONVERT(varchar(40), p.Amount), '|', p.Currency, '|', CONVERT(varchar(40), p.RefundedAmount), '|', COALESCE(p.GatewayOrderId, ''), '|', COALESCE(p.GatewayPaymentId, ''), '|', COALESCE(p.GatewayStatus, ''), '|', COALESCE(CONVERT(varchar(20), p.OrderId), ''), '|', COALESCE(CONVERT(varchar(20), p.SubscriptionId), ''), '|', CONVERT(varchar(33), p.CreatedAtUtc, 126), '|', COALESCE(CONVERT(varchar(33), p.VerifiedAtUtc, 126), ''), '|', COALESCE(CONVERT(varchar(33), p.FailedAtUtc, 126), ''), '|', COALESCE(CONVERT(varchar(36), o.PublicId), ''), '|', COALESCE(o.OrderNumber, ''), '|', COALESCE(o.Status, ''), '|', COALESCE(CONVERT(varchar(40), o.PayableAmount), ''), '|', CONVERT(varchar(20), COUNT(DISTINCT d.Id)), '|', CONVERT(varchar(20), COUNT(DISTINCT otp.Id)), '|', CONVERT(varchar(20), COUNT(DISTINCT r.Id)), '|', CONVERT(varchar(40), COALESCE(SUM(DISTINCT r.Amount), 0))
        )), 2) AS EvidenceHash
    FROM Payment p
    LEFT JOIN [Order] o ON o.Id = p.OrderId
    LEFT JOIN Delivery d ON d.OrderId = o.Id
    LEFT JOIN DeliveryOtp otp ON otp.DeliveryId = d.Id
    LEFT JOIN Refund r ON r.PaymentId = p.Id
    INNER JOIN Incident i ON i.PublicId = p.PublicId
    GROUP BY p.Id, p.PublicId, p.Status, p.Method, p.Amount, p.Currency, p.RefundedAmount, p.GatewayOrderId, p.GatewayPaymentId, p.GatewayStatus, p.OrderId, p.SubscriptionId, p.CreatedAtUtc, p.VerifiedAtUtc, p.FailedAtUtc, o.PublicId, o.OrderNumber, o.Status, o.PayableAmount
)
SELECT 'SNAPSHOT' AS RecordType, PaymentRowId, CONVERT(varchar(36), PaymentPublicId) AS PaymentPublicId, PaymentStatus, PaymentMethod, Amount, Currency, RefundedAmount, GatewayOrderId, GatewayPaymentId, GatewayStatus, CONVERT(varchar(36), OrderPublicId) AS OrderPublicId, OrderNumber, OrderStatus, PayableAmount, DeliveryCount, OtpCount, RefundCount, RefundAmount, NotificationEventTokenMatches, EvidenceHash
FROM Evidence
ORDER BY PaymentPublicId;

SELECT 'WEBHOOK_LINKAGE' AS RecordType, COUNT(*) AS TotalRazorpayWebhookRows, 'PaymentWebhook has no payload/payment foreign key; incident linkage is not deterministic from this table.' AS Note
FROM PaymentWebhook
WHERE Provider = 'Razorpay';

ROLLBACK TRANSACTION;
