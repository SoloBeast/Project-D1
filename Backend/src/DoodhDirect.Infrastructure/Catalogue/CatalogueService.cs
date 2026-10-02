using System.Data;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Catalogue;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.MilkTesting;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Catalogue;

public sealed class CatalogueService(
    DoodhDirectDbContext dbContext,
    IProductImageValidator productImageValidator,
    IMediaStorage mediaStorage,
    IIndiaTimeProvider timeProvider) : ICatalogueService
{
    private const string ProductAuditEntityType = "Product";
    private const string ProductChargeAssignedAuditAction = "PRODUCT.CHARGE_ASSIGNED";
    private const string ProductChargeUnassignedAuditAction = "PRODUCT.CHARGE_UNASSIGNED";
    private const string ProductImageStoragePrefix = "product-images";
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
        .Select(product => product.ToResult(includeApplicableCharges: true))
        .ToArray();

    public async Task<ProductResult> GetProductForAdministrationAsync(Guid productId, CancellationToken cancellationToken) =>
        (await FindProductAsync(productId, cancellationToken)).ToResult(includeApplicableCharges: true);

    public async Task<ProductResult> CreateProductAsync(
        UpsertProductRequest request,
        CancellationToken cancellationToken,
        long? actorUserId = null)
    {
        ValidateProduct(request);
        var category = await FindCategoryAsync(request.CategoryId, cancellationToken);
        if (!category.IsActive)
            throw new BusinessRuleException("Products must belong to an active category.");

        var normalizedSku = NormalizeCode(request.Sku);
        if (await dbContext.Products.AnyAsync(product => product.Sku == normalizedSku, cancellationToken))
            throw new ConflictException("The SKU is already in use.");

        // The charge-eligibility check and the mapping insert MUST be atomic:
        // a concurrent global-toggle (ApplicableOnAll = true) checks for zero
        // mappings inside its own serializable transaction, so this side must
        // hold the matching serializable scope — otherwise the toggle can slip
        // between this check and this insert and commit an invalid state.
        Product? created = null;
        IReadOnlyList<Charge> assignedCharges = [];
        await ExecuteSerializableAsync(async () =>
        {
            var branches = await ResolveBranchesAsync(request.BranchIds, cancellationToken);
            var charges = await ResolveApplicableChargesAsync(request.ApplicableChargeIds, existingLinks: null, cancellationToken);
            var product = new Product(category.Id, normalizedSku, request.Name, request.Description, request.UnitOfMeasure, request.Price);
            dbContext.Products.Add(product);
            foreach (var branch in branches)
            {
                // Guarded (not blind Add) so a deadlock-retry re-execution of
                // this delegate stays idempotent: single-run behavior is
                // unchanged because the collection starts empty.
                if (product.ProductBranches.All(link => link.BranchId != branch.Id))
                {
                    product.ProductBranches.Add(new ProductBranch(product.Id, branch.Id, isAvailable: true, maxDailyQuantity: null));
                }
            }
            foreach (var charge in charges)
            {
                if (product.ProductCharges.All(link => link.ChargeId != charge.Id))
                {
                    product.ProductCharges.Add(new ProductCharge(product.Id, charge.Id));
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            created = product;
            assignedCharges = charges;
        }, cancellationToken);

        // Assignment audits are written only AFTER the mapping transaction
        // commits (mirroring ChargeService): a rolled-back create writes no
        // audit rows, and a retried delegate never duplicates them.
        AddChargeAssignmentAudits(
            created!,
            assignedCharges: assignedCharges,
            unassignedCharges: [],
            actorUserId,
            timeProvider.Now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetProductForAdministrationAsync(created!.PublicId, cancellationToken);
    }

    public async Task<ProductResult> UpdateProductAsync(
        Guid productId,
        UpsertProductRequest request,
        CancellationToken cancellationToken,
        long? actorUserId = null)
    {
        ValidateProduct(request);
        var product = await FindProductAsync(productId, cancellationToken);
        var category = await FindCategoryAsync(request.CategoryId, cancellationToken);
        if (!category.IsActive)
            throw new BusinessRuleException("Products must belong to an active category.");

        var normalizedSku = NormalizeCode(request.Sku);
        if (await dbContext.Products.AnyAsync(item => item.Sku == normalizedSku && item.Id != product.Id, cancellationToken))
            throw new ConflictException("The SKU is already in use.");

        // Same atomicity requirement as create: the eligibility check and the
        // mapping replacement commit inside one serializable transaction so a
        // concurrent global-toggle cannot interleave between them.
        IReadOnlyList<Charge> assignedCharges = [];
        IReadOnlyList<Charge> unassignedCharges = [];
        await ExecuteSerializableAsync(async () =>
        {
            var branches = await ResolveBranchesAsync(request.BranchIds, cancellationToken);
            var charges = await ResolveApplicableChargesAsync(request.ApplicableChargeIds, product.ProductCharges, cancellationToken);
            product.Update(category.Id, normalizedSku, request.Name, request.Description, request.UnitOfMeasure, request.Price);

            // Capture the linked charges BEFORE replacement mutates the navigation —
            // a removed link's charge details must survive for the unassignment
            // audit even though its ProductCharge row will not exist after the save.
            var previouslyLinkedCharges = product.ProductCharges
                .Select(link => link.Charge)
                .ToList();
            ApplyBranchReplacements(product, branches);
            ApplyChargeReplacements(product, charges);

            // Audit ONLY actual changes: charges kept on the product appear in
            // neither diff list, so re-saving an unchanged mapping set is silent.
            var requestedChargeIds = charges.Select(charge => charge.Id).ToHashSet();
            var previousChargeIds = previouslyLinkedCharges.Select(charge => charge.Id).ToHashSet();
            assignedCharges = charges.Where(charge => !previousChargeIds.Contains(charge.Id)).ToList();
            unassignedCharges = previouslyLinkedCharges.Where(charge => !requestedChargeIds.Contains(charge.Id)).ToList();

            await dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        AddChargeAssignmentAudits(
            product,
            assignedCharges: assignedCharges,
            unassignedCharges: unassignedCharges,
            actorUserId,
            timeProvider.Now);
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
        if (!branch.IsActive || branch.IsArchived)
            throw new BusinessRuleException("Product availability can only be assigned to an active, non-archived branch.");

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

    public async Task<ProductImageResult> UpsertProductImageAsync(
        ProductImageActor actor,
        Guid productId,
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken)
    {
        await using var validated = await productImageValidator.ValidateAsync(
            content,
            fileName,
            declaredContentType,
            cancellationToken);

        var product = await ProductQuery()
            .SingleOrDefaultAsync(item => item.PublicId == productId, cancellationToken)
            ?? throw new NotFoundException("The product was not found.");

        var now = timeProvider.Now;
        var storageKey = $"{ProductImageStoragePrefix}/{now:yyyy/MM}/{product.PublicId:N}/{Guid.NewGuid():N}{ImageExtension(validated.ContentType)}";
        var stored = await mediaStorage.SaveAsync(
            storageKey,
            validated.Content,
            validated.ContentType,
            cancellationToken);

        var current = product.ProductImages.SingleOrDefault();
        var previousStorageKey = current?.StorageKey;
        var previousFileName = current?.FileName;
        var previousContentType = current?.ContentType;
        var previousFileSize = current?.FileSize;
        ProductImage image;
        try
        {
            if (stored.FileSize != validated.FileSize)
            {
                throw new InvalidOperationException("The stored media size does not match the validated image size.");
            }

            if (current is null)
            {
                image = new ProductImage(
                    product.Id,
                    stored.StorageKey,
                    validated.FileName,
                    validated.ContentType,
                    stored.FileSize,
                    actor.UserId,
                    now);
                // Adding through the DbSet is sufficient: EF fixup attaches the
                // row to product.ProductImages when it is saved.
                dbContext.ProductImages.Add(image);
                AddAudit(actor.UserId, "PRODUCT.IMAGE_UPLOAD", product.PublicId, null,
                    new { ImageId = image.PublicId, image.FileName, image.ContentType, image.FileSize }, null, now);
            }
            else
            {
                current.ReplaceContent(
                    stored.StorageKey,
                    validated.FileName,
                    validated.ContentType,
                    stored.FileSize,
                    actor.UserId,
                    now);
                image = current;
                // Old-value snapshots were captured before ReplaceContent
                // mutated the row. Storage keys are never written to audit
                // payloads (consistent with the milk-test image audits).
                AddAudit(actor.UserId, "PRODUCT.IMAGE_REPLACE", product.PublicId,
                    new { FileName = previousFileName, ContentType = previousContentType, FileSize = previousFileSize },
                    new { ImageId = current.PublicId, current.FileName, current.ContentType, current.FileSize },
                    null,
                    now);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Compensating cleanup for PRE-commit failures only: the database
            // state was not committed, so the newly stored blob must not linger
            // and the previous image stays intact.
            await mediaStorage.DeleteIfExistsAsync(stored.StorageKey, CancellationToken.None);
            throw;
        }

        // Post-commit cleanup of the old blob. A failure here may leave an
        // orphaned file temporarily (consistent with the existing media
        // pattern) but must never fail the committed operation or delete the
        // blob the database now references.
        if (previousStorageKey is not null)
        {
            try
            {
                await mediaStorage.DeleteIfExistsAsync(previousStorageKey, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Orphaned old file tolerated; the committed state is correct.
            }
        }

        return ToImageResult(product.PublicId, image);
    }

    public async Task<ProductImageResult> RemoveProductImageAsync(
        ProductImageActor actor,
        Guid productId,
        CancellationToken cancellationToken)
    {
        var product = await ProductQuery()
            .SingleOrDefaultAsync(item => item.PublicId == productId, cancellationToken)
            ?? throw new NotFoundException("The product was not found.");

        var image = product.ProductImages.SingleOrDefault()
            ?? throw new NotFoundException("The product image was not found.");

        var storageKey = image.StorageKey;
        dbContext.ProductImages.Remove(image);
        AddAudit(actor.UserId, "PRODUCT.IMAGE_REMOVE", product.PublicId,
            new { ImageId = image.PublicId, image.FileName, image.ContentType, image.FileSize },
            null,
            null,
            timeProvider.Now);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Blob cleanup happens only after the database commit. A failure here
        // may leave an orphaned file temporarily but never a dangling
        // reference, and the committed removal must not be reported as failed.
        try
        {
            await mediaStorage.DeleteIfExistsAsync(storageKey, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Orphaned file tolerated; the committed state is correct.
        }

        return ToImageResult(productId, image);
    }

    public async Task<StoredMediaContent?> OpenProductImageAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        var product = await ProductQuery(asNoTracking: true)
            .SingleOrDefaultAsync(item => item.PublicId == productId, cancellationToken)
            ?? throw new NotFoundException("The product was not found.");

        var image = product.ProductImages.SingleOrDefault();
        if (image is null)
        {
            return null;
        }

        var stored = await mediaStorage.OpenReadAsync(image.StorageKey, cancellationToken);
        return new StoredMediaContent(stored.Content, image.ContentType, image.FileSize);
    }

    /// <summary>
    /// Runs the charge-check-plus-mapping-mutation unit inside a serializable
    /// transaction (same shape as <c>ChargeService.ExecuteSerializableAsync</c>),
    /// so the global-toggle side — which performs its zero-mapping check under
    /// serializable isolation — serializes against this side instead of racing
    /// it. A deadlock victim rolls back and the SQL Server execution strategy
    /// retries the whole delegate; the mutation loops are idempotent so a
    /// retry never double-inserts.
    /// </summary>
    private async Task ExecuteSerializableAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            await operation();
            return;
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private IQueryable<Product> ActiveProductQuery() => ProductQuery(asNoTracking: true)
        .Where(product => product.IsActive && product.Category.IsActive && product.ProductBranches.Any(branch =>
            branch.IsAvailable && branch.Branch.IsActive));

    private IQueryable<Product> ProductQuery(bool asNoTracking = false)
    {
        var query = dbContext.Products
            .Include(product => product.Category)
            .Include(product => product.ProductImages)
            .Include(product => product.ProductBranches)
            .ThenInclude(branch => branch.Branch)
            .Include(product => product.ProductCharges)
            .ThenInclude(link => link.Charge)
            .AsQueryable();
        return asNoTracking ? query.AsNoTracking() : query;
    }

    private async Task<Product> FindProductAsync(Guid productId, CancellationToken cancellationToken) =>
        await ProductQuery().SingleOrDefaultAsync(product => product.PublicId == productId, cancellationToken)
        ?? throw new NotFoundException("The product was not found.");

    private async Task<ProductCategory> FindCategoryAsync(Guid categoryId, CancellationToken cancellationToken) =>
        await dbContext.ProductCategories.SingleOrDefaultAsync(category => category.PublicId == categoryId, cancellationToken)
        ?? throw new NotFoundException("The product category was not found.");

    private async Task<IReadOnlyList<Branch>> ResolveBranchesAsync(IReadOnlyList<Guid> branchIds, CancellationToken cancellationToken)
    {
        var branchIdsSet = branchIds.Distinct().ToArray();
        var branches = await dbContext.Branches
            .Where(branch => branchIdsSet.Contains(branch.PublicId))
            .ToListAsync(cancellationToken);

        if (branches.Count != branchIdsSet.Length)
            throw new NotFoundException("One or more branches were not found.");

        foreach (var branch in branches)
        {
            if (!branch.IsActive || branch.IsArchived)
                throw new BusinessRuleException("A product can only be assigned to an active, non-archived branch.");
        }

        return branchIdsSet
            .Select(id => branches.Single(branch => branch.PublicId == id))
            .ToArray();
    }

    private void ApplyBranchReplacements(Product product, IReadOnlyList<Branch> branches)
    {
        var requestedBranchIds = branches.Select(branch => branch.Id).ToHashSet();

        // Replace semantics for the assignable (active, non-archived) set: remove links to
        // branches that are no longer selected, while preserving historical links to
        // archived branches (their rows must survive for audit/reporting purposes).
        var removals = product.ProductBranches
            .Where(link => !requestedBranchIds.Contains(link.BranchId) && !link.Branch.IsArchived)
            .ToArray();
        foreach (var removal in removals)
        {
            // Removing through the DbSet marks the join row for deletion. Removing only from
            // the navigation collection would make EF attempt to null the required FK
            // (DeleteBehavior.Restrict), which throws HandleConceptualNulls.
            dbContext.ProductBranches.Remove(removal);
        }

        foreach (var branch in branches)
        {
            if (product.ProductBranches.All(link => link.BranchId != branch.Id))
            {
                product.ProductBranches.Add(new ProductBranch(
                    product.Id,
                    branch.Id,
                    isAvailable: true,
                    maxDailyQuantity: null));
            }
        }
    }

    /// <summary>
    /// Validates the requested Tax &amp; Charges assignment set for a product.
    /// Already-linked charges are always accepted (an inactive charge's mapping is
    /// preserved — configuration survives deactivation), while every NEW link must
    /// reference an active, product-assignable charge (ApplicableOnAll = false).
    /// </summary>
    private async Task<IReadOnlyList<Charge>> ResolveApplicableChargesAsync(
        IReadOnlyList<Guid>? chargeIds,
        ICollection<ProductCharge>? existingLinks,
        CancellationToken cancellationToken)
    {
        if (chargeIds is null || chargeIds.Count == 0)
        {
            return [];
        }

        var requestedIds = chargeIds.Distinct().ToArray();
        var charges = await dbContext.Charges
            .Where(charge => requestedIds.Contains(charge.PublicId))
            .ToListAsync(cancellationToken);
        if (charges.Count != requestedIds.Length)
        {
            throw new NotFoundException("One or more charges were not found.");
        }

        var linkedChargeIds = existingLinks?.Select(link => link.ChargeId).ToHashSet()
            ?? new HashSet<long>();
        foreach (var charge in charges)
        {
            var isNewLink = !linkedChargeIds.Contains(charge.Id);
            if (isNewLink && !charge.IsActive)
            {
                throw new BusinessRuleException(
                    $"Charge '{charge.ChargeCode}' is inactive and cannot be assigned to a product.");
            }

            if (isNewLink && charge.ApplicableOnAll)
            {
                throw new BusinessRuleException(
                    $"This charge is configured as Applicable on All and cannot be assigned to individual products.");
            }
        }

        return charges
            .OrderBy(charge => charge.ChargeType)
            .ThenBy(charge => charge.ChargeCode)
            .ToArray();
    }

    /// <summary>
    /// Replace semantics for the product's charge assignment set: remove mappings
    /// the admin no longer selected, add the newly selected ones. Mappings to
    /// charges that stayed selected survive untouched (even when inactive).
    /// </summary>
    private void ApplyChargeReplacements(Product product, IReadOnlyList<Charge> charges)
    {
        var requestedChargeIds = charges.Select(charge => charge.Id).ToHashSet();

        var removals = product.ProductCharges
            .Where(link => !requestedChargeIds.Contains(link.ChargeId))
            .ToArray();
        foreach (var removal in removals)
        {
            // Removing through the DbSet marks the join row for deletion (same
            // reason as ProductBranch — DeleteBehavior.Restrict on the FK).
            dbContext.ProductCharges.Remove(removal);
        }

        foreach (var charge in charges)
        {
            if (product.ProductCharges.All(link => link.ChargeId != charge.Id))
            {
                product.ProductCharges.Add(new ProductCharge(product.Id, charge.Id));
            }
        }
    }

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
        if (request.BranchIds is null || request.BranchIds.Count == 0)
            throw new ValidationAppException("At least one active branch must be assigned to the product.", nameof(request.BranchIds));
        if (request.BranchIds.Distinct().Count() != request.BranchIds.Count)
            throw new ValidationAppException("Duplicate branches are not allowed.", nameof(request.BranchIds));
    }

    private static void ValidateRequired(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ValidationAppException($"{field} is required.", field);
        if (value.Trim().Length > maxLength)
            throw new ValidationAppException($"{field} cannot exceed {maxLength} characters.", field);
    }

    private static string NormalizeCode(string value) => value.Trim().ToUpperInvariant();

    private static ProductImageResult ToImageResult(Guid productId, ProductImage image) =>
        new(productId, image.PublicId, image.FileName, image.ContentType, image.FileSize, image.UploadedAt);

    private static string ImageExtension(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => throw new ValidationAppException("The validated image type is unsupported.", "image")
    };

    /// <summary>Audit rows for ProductCharge assignment changes, written only
    /// after the mapping transaction commits so a rolled-back product save
    /// never leaves orphan audit entries. Only actual
    /// changes appear: a charge kept on the product is in neither diff list.
    /// Unassignment snapshots capture the charge identity BEFORE the join row
    /// disappears, so the removed relationship stays reconstructible.</summary>
    private void AddChargeAssignmentAudits(
        Product product,
        IReadOnlyList<Charge> assignedCharges,
        IReadOnlyList<Charge> unassignedCharges,
        long? actorUserId,
        DateTime createdAt)
    {
        if (assignedCharges.Count == 0 && unassignedCharges.Count == 0)
        {
            return;
        }

        foreach (var charge in assignedCharges)
        {
            dbContext.AddAuditLog(new AuditLog(
                actorUserId,
                ProductChargeAssignedAuditAction,
                ProductAuditEntityType,
                product.PublicId.ToString(),
                oldValueJson: null,
                newValueJson: ChargeAssignmentSnapshot(product, charge),
                ipAddress: null,
                userAgent: null,
                reason: $"Charge '{charge.ChargeCode}' assigned to product '{product.Sku}'.",
                createdAt));
        }

        foreach (var charge in unassignedCharges)
        {
            dbContext.AddAuditLog(new AuditLog(
                actorUserId,
                ProductChargeUnassignedAuditAction,
                ProductAuditEntityType,
                product.PublicId.ToString(),
                oldValueJson: ChargeAssignmentSnapshot(product, charge),
                newValueJson: null,
                ipAddress: null,
                userAgent: null,
                reason: $"Charge '{charge.ChargeCode}' removed from product '{product.Sku}'.",
                createdAt));
        }
    }

    /// <summary>Stable-identifier snapshot shared by assignment and unassignment
    /// audit rows: public ids for lookups, plus the display/config fields an
    /// administrator needs to understand what changed.</summary>
    private static string ChargeAssignmentSnapshot(Product product, Charge charge) =>
        JsonSerializer.Serialize(new
        {
            ProductId = product.PublicId,
            ProductSku = product.Sku,
            ChargeId = charge.PublicId,
            charge.ChargeCode,
            charge.ChargeType,
            charge.Description,
            charge.Percentage,
            charge.IsActive,
            charge.ApplicableOnAll
        });

    private void AddAudit(
        long userId,
        string action,
        Guid productId,
        object? oldValue,
        object? newValue,
        string? reason,
        DateTime createdAt) =>
        dbContext.AddAuditLog(new AuditLog(
            userId,
            action,
            ProductAuditEntityType,
            productId.ToString(),
            oldValue is null ? null : JsonSerializer.Serialize(oldValue),
            newValue is null ? null : JsonSerializer.Serialize(newValue),
            null,
            null,
            reason,
            createdAt));
}
