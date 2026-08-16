using DoodhDirect.Domain.Catalogue;

namespace DoodhDirect.Application.Catalogue;

public sealed record ProductCategoryResult(
    Guid PublicId,
    string Code,
    string Name,
    string? Description,
    bool IsActive);

public sealed record BranchResult(
    Guid PublicId,
    string Code,
    string Name,
    string City,
    string State,
    bool IsActive);

public sealed record BranchAvailabilityResult(
    Guid BranchId,
    string BranchCode,
    string BranchName,
    bool IsAvailable,
    decimal? MaxDailyQuantity);

public sealed record ProductResult(
    Guid PublicId,
    string Sku,
    string Name,
    string? Description,
    ProductCategoryResult Category,
    string UnitOfMeasure,
    decimal Price,
    bool IsActive,
    IReadOnlyList<BranchAvailabilityResult> BranchAvailability);

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
    decimal Price);

public sealed record SetProductBranchAvailabilityRequest(
    Guid BranchId,
    bool IsAvailable,
    decimal? MaxDailyQuantity);

public interface ICatalogueService
{
    Task<IReadOnlyList<ProductResult>> GetActiveProductsAsync(Guid? categoryId, CancellationToken cancellationToken);
    Task<ProductResult> GetActiveProductAsync(Guid productId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProductCategoryResult>> GetActiveCategoriesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductResult>> GetProductsForAdministrationAsync(CancellationToken cancellationToken);
    Task<ProductResult> GetProductForAdministrationAsync(Guid productId, CancellationToken cancellationToken);
    Task<ProductResult> CreateProductAsync(UpsertProductRequest request, CancellationToken cancellationToken);
    Task<ProductResult> UpdateProductAsync(Guid productId, UpsertProductRequest request, CancellationToken cancellationToken);
    Task<ProductResult> SetProductActiveAsync(Guid productId, bool isActive, CancellationToken cancellationToken);
    Task<ProductResult> SetBranchAvailabilityAsync(Guid productId, SetProductBranchAvailabilityRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductCategoryResult>> GetCategoriesForAdministrationAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<BranchResult>> GetBranchesForAdministrationAsync(CancellationToken cancellationToken);
    Task<ProductCategoryResult> CreateCategoryAsync(UpsertProductCategoryRequest request, CancellationToken cancellationToken);
    Task<ProductCategoryResult> UpdateCategoryAsync(Guid categoryId, UpsertProductCategoryRequest request, CancellationToken cancellationToken);
    Task<ProductCategoryResult> SetCategoryActiveAsync(Guid categoryId, bool isActive, CancellationToken cancellationToken);
}

public static class CatalogueMappings
{
    public static ProductCategoryResult ToResult(this ProductCategory category) =>
        new(category.PublicId, category.Code, category.Name, category.Description, category.IsActive);

    public static BranchResult ToResult(this Branch branch) =>
        new(branch.PublicId, branch.Code, branch.Name, branch.City, branch.State, branch.IsActive);

    public static ProductResult ToResult(this Product product, bool activeAvailabilityOnly = false) => new(
        product.PublicId,
        product.Sku,
        product.Name,
        product.Description,
        product.Category.ToResult(),
        product.UnitOfMeasure,
        product.Price,
        product.IsActive,
        product.ProductBranches
            .Where(item => !activeAvailabilityOnly || item.IsAvailable && item.Branch.IsActive)
            .OrderBy(item => item.Branch.Name)
            .Select(item => new BranchAvailabilityResult(
                item.Branch.PublicId,
                item.Branch.Code,
                item.Branch.Name,
                item.IsAvailable,
                item.MaxDailyQuantity))
            .ToArray());
}
