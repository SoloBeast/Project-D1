-- ============================================================================
-- fix-product-branch-availability.sql
-- ----------------------------------------------------------------------------
-- Repair catalogue visibility after the 2026-09-03 UAT reset.
--
-- Symptom:  GET /api/v1/products and GET /api/v1/product-categories return
--           HTTP 200 with an empty "data" array, so the Customer shop screen
--           shows "No products available".
--
-- Root cause: the reset deactivated the original branches (HR/DB/001 = id 4,
-- HR/BR/002 = id 5) and created new active branches (DB0001 = id 6,
-- DB0002 = id 7). The ProductBranch availability rows still point only at the
-- INACTIVE branches. CatalogueService.ActiveProductQuery() requires
--   product.IsActive && category.IsActive &&
--   ProductBranches.Any(b => b.IsAvailable && b.Branch.IsActive)
-- hence all products are (correctly) hidden.
--
-- Fix: for every active product in an active category, ensure an availability
-- row exists on EVERY active branch. This is idempotent (INSERT ... WHERE NOT
-- EXISTS) and makes products visible to every branch.
-- ============================================================================
BEGIN TRANSACTION;

INSERT INTO dbo.ProductBranch (ProductId, BranchId, IsAvailable, MaxDailyQuantity)
SELECT p.Id, b.Id, 1, NULL
FROM dbo.Product p
JOIN dbo.ProductCategory c ON c.Id = p.CategoryId
JOIN dbo.Branch b ON b.IsActive = 1
WHERE p.IsActive = 1
  AND c.IsActive = 1
  AND NOT EXISTS (
      SELECT 1
      FROM dbo.ProductBranch pb
      WHERE pb.ProductId = p.Id AND pb.BranchId = b.Id
  );

COMMIT;
GO

-- ============================================================================
-- Verification: every active product must have at least one ACTIVE + AVAILABLE
-- branch row. Re-run this block any time to confirm catalogue visibility.
-- ============================================================================
SET NOCOUNT ON;
SELECT
    p.Id AS ProductId,
    p.Sku,
    p.Name,
    p.IsActive AS ProductIsActive,
    c.IsActive AS CategoryIsActive,
    COUNT(pb.Id) AS AvailabilityRows,
    SUM(CASE WHEN b.IsActive = 1 AND pb.IsAvailable = 1 THEN 1 ELSE 0 END) AS ActiveAvailableRows,
    STRING_AGG(b.Code + '(' + CASE WHEN b.IsActive = 1 THEN 'active' ELSE 'inactive' END + ')', ', ') AS Branches
FROM dbo.Product p
JOIN dbo.ProductCategory c ON c.Id = p.CategoryId
LEFT JOIN dbo.ProductBranch pb ON pb.ProductId = p.Id
LEFT JOIN dbo.Branch b ON b.Id = pb.BranchId
GROUP BY p.Id, p.Sku, p.Name, p.IsActive, c.IsActive
ORDER BY p.Sku;
GO
