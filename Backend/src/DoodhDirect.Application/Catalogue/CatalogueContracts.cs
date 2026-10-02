using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Domain.Catalogue;

namespace DoodhDirect.Application.Catalogue;

public sealed record ProductCategoryResult(
    Guid PublicId,
    string Code,
    string Name,
    string? Description,
    bool IsActive);

public sealed record BranchAvailabilityResult(
    Guid BranchId,
    string BranchCode,
    string BranchName,
    bool IsAvailable,
    decimal? MaxDailyQuantity);

/// <summary>
/// Safe Tax &amp; Charges master projection for a product's item-level assignment
/// (admin surfaces only — customer catalogue payloads never carry it).
/// </summary>
public sealed record ProductApplicableChargeResult(
    Guid ChargeId,
    string ChargeCode,
    string ChargeType,
    string? Description,
    decimal Percentage,
    bool IsActive);

public sealed record ProductResult(
    Guid PublicId,
    string Sku,
    string Name,
    string? Description,
    ProductCategoryResult Category,
    string UnitOfMeasure,
    decimal Price,
    bool IsActive,
    // Customer-facing image URL (null when no image is configured). Never a
    // storage key or server filesystem path.
    string? ImageUrl,
    IReadOnlyList<BranchAvailabilityResult> BranchAvailability,
    IReadOnlyList<ProductApplicableChargeResult> ApplicableCharges);

public sealed record UpsertProductCategoryRequest(
    string Code,
    string Name,
    string? Description);

public sealed record UpsertProductRequest(
    string Sku,
    string Name,
    string? Description,
    Guid CategoryId,
    string UnitOfMeasure,
    decimal Price,
    IReadOnlyList<Guid> BranchIds,
    // Optional item-level Tax & Charges assignment (charge public ids). Null is
    // treated as "no charges" for backwards compatibility with existing callers.
    IReadOnlyList<Guid>? ApplicableChargeIds = null);

public sealed record SetProductBranchAvailabilityRequest(
    Guid BranchId,
    bool IsAvailable,
    decimal? MaxDailyQuantity);

public sealed record ProductImageActor(long UserId);

/// <summary>Public image metadata for the product image-management endpoints.</summary>
public sealed record ProductImageResult(
    Guid ProductId,
    Guid ImageId,
    string FileName,
    string ContentType,
    long FileSize,
    DateTime UploadedAtUtc);

public interface ICatalogueService
{
    Task<IReadOnlyList<ProductResult>> GetActiveProductsAsync(Guid? categoryId, CancellationToken cancellationToken);
    Task<ProductResult> GetActiveProductAsync(Guid productId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProductCategoryResult>> GetActiveCategoriesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductResult>> GetProductsForAdministrationAsync(CancellationToken cancellationToken);
    Task<ProductResult> GetProductForAdministrationAsync(Guid productId, CancellationToken cancellationToken); Task<ProductResult> CreateProductAsync(
        UpsertProductRequest request,
        CancellationToken cancellationToken,
        // Acting-user id for the ProductCharge assignment audit rows. Optional
        // only so existing direct-call tests compile unchanged — the HTTP layer
        // always resolves it from the authenticated principal.
        long? actorUserId = null);

    Task<ProductResult> UpdateProductAsync(
        Guid productId,
        UpsertProductRequest request,
        CancellationToken cancellationToken,
        long? actorUserId = null);
    Task<ProductResult> SetProductActiveAsync(Guid productId, bool isActive, CancellationToken cancellationToken);
    Task<ProductResult> SetBranchAvailabilityAsync(Guid productId, SetProductBranchAvailabilityRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductCategoryResult>> GetCategoriesForAdministrationAsync(CancellationToken cancellationToken);
    Task<ProductCategoryResult> CreateCategoryAsync(UpsertProductCategoryRequest request, CancellationToken cancellationToken);
    Task<ProductCategoryResult> UpdateCategoryAsync(Guid categoryId, UpsertProductCategoryRequest request, CancellationToken cancellationToken);
    Task<ProductCategoryResult> SetCategoryActiveAsync(Guid categoryId, bool isActive, CancellationToken cancellationToken);

    /// <summary>Adds the product's image, or replaces the current one atomically.
    /// The previous blob is deleted only after the new state is committed.</summary>
    Task<ProductImageResult> UpsertProductImageAsync(
        ProductImageActor actor,
        Guid productId,
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken);

    /// <summary>Removes the current product image. The blob is deleted only
    /// after the database state is committed; the product falls back to the
    /// branded no-image presentation. Returns the removed image metadata.</summary>
    Task<ProductImageResult> RemoveProductImageAsync(
        ProductImageActor actor,
        Guid productId,
        CancellationToken cancellationToken);

    /// <summary>Opens the current product image content for public reads, or
    /// null when the product has no image.</summary>
    Task<StoredMediaContent?> OpenProductImageAsync(
        Guid productId,
        CancellationToken cancellationToken);
}

public static class CatalogueMappings
{
    public static ProductCategoryResult ToResult(this ProductCategory category) =>
        new(category.PublicId, category.Code, category.Name, category.Description, category.IsActive);

    /// <summary>
    /// Projects a product with its customer-facing image URL when a current
    /// image row exists. The URL is a stable public content endpoint — never a
    /// storage key or server path.
    /// </summary>
    /// <summary>Projects a product. <paramref name="includeApplicableCharges"/> is
    /// true for admin surfaces only — customer catalogue payloads never carry the
    /// Tax &amp; Charges assignment configuration.</summary>
    public static ProductResult ToResult(
        this Product product,
        bool activeAvailabilityOnly = false,
        bool includeApplicableCharges = false) => new(
        product.PublicId,
        product.Sku,
        product.Name,
        product.Description,
        product.Category.ToResult(),
        product.UnitOfMeasure,
        product.Price,
        product.IsActive,
        product.ProductImages.Count > 0
            ? $"/api/v1/products/{product.PublicId}/image"
            : null,
        product.ProductBranches
            .Where(item => !activeAvailabilityOnly || item.IsAvailable && item.Branch.IsActive)
            .OrderBy(item => item.Branch.Name)
            .Select(item => new BranchAvailabilityResult(
                item.Branch.PublicId,
                item.Branch.Code,
                item.Branch.Name,
                item.IsAvailable,
                item.MaxDailyQuantity))
            .ToArray(),
        includeApplicableCharges
            ? product.ProductCharges
                .OrderBy(link => link.Charge.ChargeCode)
                .Select(link => new ProductApplicableChargeResult(
                    link.Charge.PublicId,
                    link.Charge.ChargeCode,
                    link.Charge.ChargeType,
                    link.Charge.Description,
                    link.Charge.Percentage,
                    link.Charge.IsActive))
                .ToArray()
            : Array.Empty<ProductApplicableChargeResult>());
}
