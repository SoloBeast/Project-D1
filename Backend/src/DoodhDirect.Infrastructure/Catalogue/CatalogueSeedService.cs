using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Catalogue;

public sealed class CatalogueSeedService(DoodhDirectDbContext dbContext)
{
    private const string MilkCategoryCode = "MILK";
    private const string MainBranchCode = "MAIN";
    private const string FreshBuffaloMilkSku = "FRESH-BUFFALO-MILK";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();

        await executionStrategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);

            var category = await dbContext.ProductCategories
                .SingleOrDefaultAsync(item => item.Code == MilkCategoryCode, cancellationToken);
            if (category is null)
            {
                category = new ProductCategory(
                    MilkCategoryCode,
                    "Milk",
                    "Fresh dairy milk products.");
                dbContext.ProductCategories.Add(category);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            var branch = await dbContext.Branches
                .SingleOrDefaultAsync(item => item.Code == MainBranchCode, cancellationToken);
            if (branch is null)
            {
                branch = new Branch(
                    MainBranchCode,
                    "Main Branch",
                    "Bengaluru",
                    "Karnataka",
                    12.9716m,
                    77.5946m);
                dbContext.Branches.Add(branch);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            var product = await dbContext.Products
                .SingleOrDefaultAsync(item => item.Sku == FreshBuffaloMilkSku, cancellationToken);
            if (product is null)
            {
                product = new Product(
                    category.Id,
                    FreshBuffaloMilkSku,
                    "Fresh Buffalo Milk",
                    "Fresh buffalo milk sold by the litre.",
                    "litre",
                    80.00m);
                dbContext.Products.Add(product);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            var availability = await dbContext.ProductBranches
                .SingleOrDefaultAsync(
                    item => item.ProductId == product.Id && item.BranchId == branch.Id,
                    cancellationToken);
            if (availability is null)
            {
                dbContext.ProductBranches.Add(new ProductBranch(
                    product.Id,
                    branch.Id,
                    isAvailable: true,
                    maxDailyQuantity: null));
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        });
    }
}
