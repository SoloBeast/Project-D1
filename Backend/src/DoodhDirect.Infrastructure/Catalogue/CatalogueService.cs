using DoodhDirect.Application.Catalogue;
using DoodhDirect.Application.Common;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Catalogue;

public sealed class CatalogueService(DoodhDirectDbContext dbContext) : ICatalogueService
{
    private static readonly string[] SupportedUnits = ["litre", "kilogram", "gram", "piece"];

    public async Task<IReadOnlyList<ProductResult>> GetActiveProductsAsync(Guid? categoryId, CancellationToken cancellationToken)
    {
        var query = ActiveProductQuery();
        if (categoryId.HasValue)
        {
            query = query.Where(product => product.Category.PublicId == categoryId.Value);
        }

        var products = await query
            .OrderBy(product => product.Category.Name)
            .ThenBy(product => product.Name)
            .ToListAsync(cancellationToken);
        return products.Select(product => product.ToResult(activeAvailabilityOnly: true)).ToArray();
    }

    public async Task<ProductResult> GetActiveProductAsync(Guid productId, CancellationToken cancellationToken)
    {
        var product = await ActiveProductQuery()
            .SingleOrDefaultAsync(item => item.PublicId == productId, cancellationToken)
            ?? throw new NotFoundException("The active product was not found.");
        return product.ToResult(activeAvailabilityOnly: true);
    }

    public async Task<IReadOnlyList<ProductCategoryResult>> GetActiveCategoriesAsync(CancellationToken cancellationToken) =>
        (await dbContext.ProductCategories.AsNoTracking()
            .Where(category => category.IsActive && category.Products.Any(product =>
                product.IsActive && product.ProductBranches.Any(branch => branch.IsAvailable && branch.Branch.IsActive)))
            .OrderBy(category => category.Name)
            .ToListAsync(cancellationToken))
        .Select(category => category.ToResult())
        .ToArray();

    public async Task<IReadOnlyList<ProductResult>> GetProductsForAdministrationAsync(CancellationToken cancellationToken) =>
        (await ProductQuery(asNoTracking: true)
            .OrderBy(product => product.Category.Name)
            .ThenBy(product => product.Name)
            .ToListAsync(cancellationToken))
        .Select(product => product.ToResult())
        .ToArray();

    public async Task<ProductResult> GetProductForAdministrationAsync(Guid productId, CancellationToken cancellationToken) =>
        (await FindProductAsync(productId, cancellationToken)).ToResult();

    public async Task<ProductResult> CreateProductAsync(UpsertProductRequest request, CancellationToken cancellationToken)
    {
        ValidateProduct(request);
        var category = await FindCategoryAsync(request.CategoryId, cancellationToken);
        if (!category.IsActive)
            throw new BusinessRuleException("Products must belong to an active category.");

        var normalizedSku = NormalizeCode(request.Sku);
        if (await dbContext.Products.AnyAsync(product => product.Sku == normalizedSku, cancellationToken))
            throw new ConflictException("The SKU is already in use.");

        var product = new Product(category.Id, normalizedSku, request.Name, request.Description, request.UnitOfMeasure, request.Price);
        dbContext.Products.Add(product);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetProductForAdministrationAsync(product.PublicId, cancellationToken);
    }

    public async Task<ProductResult> UpdateProductAsync(Guid productId, UpsertProductRequest request, CancellationToken cancellationToken)
    {
        ValidateProduct(request);
        var product = await FindProductAsync(productId, cancellationToken);
        var category = await FindCategoryAsync(request.CategoryId, cancellationToken);
        if (!category.IsActive)
            throw new BusinessRuleException("Products must belong to an active category.");

        var normalizedSku = NormalizeCode(request.Sku);
        if (await dbContext.Products.AnyAsync(item => item.Sku == normalizedSku && item.Id != product.Id, cancellationToken))
            throw new ConflictException("The SKU is already in use.");

        product.Update(category.Id, normalizedSku, request.Name, request.Description, request.UnitOfMeasure, request.Price);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetProductForAdministrationAsync(product.PublicId, cancellationToken);
    }

    public async Task<ProductResult> SetProductActiveAsync(Guid productId, bool isActive, CancellationToken cancellationToken)
    {
        var product = await FindProductAsync(productId, cancellationToken);
        if (isActive && !product.Category.IsActive)
            throw new BusinessRuleException("A product cannot be activated while its category is inactive.");

        if (isActive) product.Activate();
        else product.Deactivate();
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetProductForAdministrationAsync(product.PublicId, cancellationToken);
    }

    public async Task<ProductResult> SetBranchAvailabilityAsync(Guid productId, SetProductBranchAvailabilityRequest request, CancellationToken cancellationToken)
    {
        if (request.MaxDailyQuantity is <= 0)
            throw new ValidationAppException("Maximum daily quantity must be greater than zero.", nameof(request.MaxDailyQuantity));
        if (request.MaxDailyQuantity.HasValue && decimal.Round(request.MaxDailyQuantity.Value, 3) != request.MaxDailyQuantity.Value)
            throw new ValidationAppException("Maximum daily quantity supports up to three decimal places.", nameof(request.MaxDailyQuantity));

        var product = await FindProductAsync(productId, cancellationToken);
        var branch = await dbContext.Branches.SingleOrDefaultAsync(item => item.PublicId == request.BranchId, cancellationToken)
            ?? throw new NotFoundException("The branch was not found.");
        if (!branch.IsActive)
            throw new BusinessRuleException("Product availability can only be assigned to an active branch.");

        var assignment = product.ProductBranches.SingleOrDefault(item => item.BranchId == branch.Id);
        if (assignment is null)
        {
            assignment = new ProductBranch(product.Id, branch.Id, request.IsAvailable, request.MaxDailyQuantity);
            dbContext.ProductBranches.Add(assignment);
        }
        else
        {
            assignment.Update(request.IsAvailable, request.MaxDailyQuantity);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetProductForAdministrationAsync(product.PublicId, cancellationToken);
    }

    public async Task<IReadOnlyList<ProductCategoryResult>> GetCategoriesForAdministrationAsync(CancellationToken cancellationToken) =>
        (await dbContext.ProductCategories.AsNoTracking()
            .OrderBy(category => category.Name)
            .ToListAsync(cancellationToken))
        .Select(category => category.ToResult())
        .ToArray();

    public async Task<IReadOnlyList<BranchResult>> GetBranchesForAdministrationAsync(CancellationToken cancellationToken) =>
        (await dbContext.Branches.AsNoTracking()
            .Where(branch => branch.IsActive)
            .OrderBy(branch => branch.Name)
            .ToListAsync(cancellationToken))
        .Select(branch => new BranchResult(branch.PublicId, branch.Code, branch.Name, branch.City, branch.State, branch.IsActive))
        .ToArray();

    public async Task<ProductCategoryResult> CreateCategoryAsync(UpsertProductCategoryRequest request, CancellationToken cancellationToken)
    {
        ValidateCategory(request);
        var normalizedCode = NormalizeCode(request.Code);
        if (await dbContext.ProductCategories.AnyAsync(category => category.Code == normalizedCode, cancellationToken))
            throw new ConflictException("The category code is already in use.");

        var category = new ProductCategory(normalizedCode, request.Name, request.Description);
        dbContext.ProductCategories.Add(category);
        await dbContext.SaveChangesAsync(cancellationToken);
        return category.ToResult();
    }

    public async Task<ProductCategoryResult> UpdateCategoryAsync(Guid categoryId, UpsertProductCategoryRequest request, CancellationToken cancellationToken)
    {
        ValidateCategory(request);
        var category = await FindCategoryAsync(categoryId, cancellationToken);
        var normalizedCode = NormalizeCode(request.Code);
        if (await dbContext.ProductCategories.AnyAsync(item => item.Code == normalizedCode && item.Id != category.Id, cancellationToken))
            throw new ConflictException("The category code is already in use.");

        category.Update(normalizedCode, request.Name, request.Description);
        await dbContext.SaveChangesAsync(cancellationToken);
        return category.ToResult();
    }

    public async Task<ProductCategoryResult> SetCategoryActiveAsync(Guid categoryId, bool isActive, CancellationToken cancellationToken)
    {
        var category = await FindCategoryAsync(categoryId, cancellationToken);
        if (isActive) category.Activate();
        else category.Deactivate();
        await dbContext.SaveChangesAsync(cancellationToken);
        return category.ToResult();
    }

    private IQueryable<Product> ActiveProductQuery() => ProductQuery(asNoTracking: true)
        .Where(product => product.IsActive && product.Category.IsActive && product.ProductBranches.Any(branch =>
            branch.IsAvailable && branch.Branch.IsActive));

    private IQueryable<Product> ProductQuery(bool asNoTracking = false)
    {
        var query = dbContext.Products
            .Include(product => product.Category)
            .Include(product => product.ProductBranches)
            .ThenInclude(branch => branch.Branch)
            .AsQueryable();
        return asNoTracking ? query.AsNoTracking() : query;
    }

    private async Task<Product> FindProductAsync(Guid productId, CancellationToken cancellationToken) =>
        await ProductQuery().SingleOrDefaultAsync(product => product.PublicId == productId, cancellationToken)
        ?? throw new NotFoundException("The product was not found.");

    private async Task<ProductCategory> FindCategoryAsync(Guid categoryId, CancellationToken cancellationToken) =>
        await dbContext.ProductCategories.SingleOrDefaultAsync(category => category.PublicId == categoryId, cancellationToken)
        ?? throw new NotFoundException("The product category was not found.");

    private static void ValidateCategory(UpsertProductCategoryRequest request)
    {
        ValidateRequired(request.Code, nameof(request.Code), 50);
        ValidateRequired(request.Name, nameof(request.Name), 160);
        if (request.Description?.Length > 500)
            throw new ValidationAppException("Description cannot exceed 500 characters.", nameof(request.Description));
    }

    private static void ValidateProduct(UpsertProductRequest request)
    {
        ValidateRequired(request.Sku, nameof(request.Sku), 50);
        ValidateRequired(request.Name, nameof(request.Name), 200);
        ValidateRequired(request.UnitOfMeasure, nameof(request.UnitOfMeasure), 20);
        if (!SupportedUnits.Contains(request.UnitOfMeasure.Trim().ToLowerInvariant(), StringComparer.Ordinal))
            throw new ValidationAppException("Unit of measure is not supported.", nameof(request.UnitOfMeasure));
        if (request.Description?.Length > 2000)
            throw new ValidationAppException("Description cannot exceed 2000 characters.", nameof(request.Description));
        if (request.Price <= 0 || decimal.Round(request.Price, 2) != request.Price)
            throw new ValidationAppException("Price must be positive and support no more than two decimal places.", nameof(request.Price));
    }

    private static void ValidateRequired(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ValidationAppException($"{field} is required.", field);
        if (value.Trim().Length > maxLength)
            throw new ValidationAppException($"{field} cannot exceed {maxLength} characters.", field);
    }

    private static string NormalizeCode(string value) => value.Trim().ToUpperInvariant();
}
