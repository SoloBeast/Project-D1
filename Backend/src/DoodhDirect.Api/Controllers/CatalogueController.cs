using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Catalogue;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

[ApiController]
[Route("api/v1")]
[Produces("application/json")]
public sealed class CatalogueController(ICatalogueService catalogueService) : ControllerBase
{
    [HttpGet("products")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ProductResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ProductResult>>>> GetProducts(
        [FromQuery] Guid? categoryId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<ProductResult>>.Ok(
            await catalogueService.GetActiveProductsAsync(categoryId, cancellationToken)));

    [HttpGet("products/{productId:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<ProductResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProductResult>>> GetProduct(
        Guid productId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ProductResult>.Ok(
            await catalogueService.GetActiveProductAsync(productId, cancellationToken)));

    /// <summary>
    /// Public product image content. Serves the configured image bytes when one
    /// exists; 404 otherwise so clients keep their branded fallback. The stored
    /// storage key/path is never exposed.
    /// </summary>
    [HttpGet("products/{productId:guid}/image")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProductImage(
        Guid productId,
        CancellationToken cancellationToken)
    {
        var media = await catalogueService.OpenProductImageAsync(productId, cancellationToken);
        if (media is null)
        {
            return NotFound();
        }

        Response.ContentLength = media.FileSize;
        return File(media.Content, media.ContentType, enableRangeProcessing: true);
    }

    [HttpGet("product-categories")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ProductCategoryResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ProductCategoryResult>>>> GetCategories(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<ProductCategoryResult>>.Ok(
            await catalogueService.GetActiveCategoriesAsync(cancellationToken)));
}

[ApiController]
[Route("api/v1/admin")]
[Tags("Catalogue administration")]
[Produces("application/json")]
public sealed class CatalogueAdministrationController(ICatalogueService catalogueService) : ControllerBase
{
    private const long MaximumProductImageTransportSize = 50L * 1024L * 1024L;

    [HttpGet("products")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CatalogueRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ProductResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ProductResult>>>> GetProducts(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<ProductResult>>.Ok(
            await catalogueService.GetProductsForAdministrationAsync(cancellationToken)));

    [HttpGet("products/{productId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CatalogueRead)]
    [ProducesResponseType(typeof(ApiResponse<ProductResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProductResult>>> GetProduct(
        Guid productId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ProductResult>.Ok(
            await catalogueService.GetProductForAdministrationAsync(productId, cancellationToken)));

    [HttpPost("products")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CatalogueManage)]
    [ProducesResponseType(typeof(ApiResponse<ProductResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProductResult>>> CreateProduct(
        [FromBody] UpsertProductApiRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ProductResult>.Ok(await catalogueService.CreateProductAsync(
            request.ToApplicationRequest(), cancellationToken, RequireCatalogueActor())));

    [HttpPatch("products/{productId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CatalogueManage)]
    [ProducesResponseType(typeof(ApiResponse<ProductResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProductResult>>> UpdateProduct(
        Guid productId,
        [FromBody] UpsertProductApiRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ProductResult>.Ok(await catalogueService.UpdateProductAsync(
            productId, request.ToApplicationRequest(), cancellationToken, RequireCatalogueActor())));

    /// <summary>Adds the product image, or replaces the current one atomically.
    /// Requires the existing product-management permission; customers can never
    /// write product images.</summary>
    [HttpPut("products/{productId:guid}/image")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CatalogueManage)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaximumProductImageTransportSize)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaximumProductImageTransportSize)]
    [ProducesResponseType(typeof(ApiResponse<ProductImageResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProductImageResult>>> UpsertProductImage(
        Guid productId,
        [FromForm] ProductImageUploadForm request,
        CancellationToken cancellationToken)
    {
        if (request.Image is null)
        {
            throw new ValidationAppException("An image is required.", "image");
        }

        await using var content = request.Image.OpenReadStream();
        var result = await catalogueService.UpsertProductImageAsync(
            RequireProductImageActor(),
            productId,
            content,
            request.Image.FileName,
            request.Image.ContentType,
            cancellationToken);
        return Ok(ApiResponse<ProductImageResult>.Ok(result));
    }

    /// <summary>Removes the current product image. The product returns to the
    /// branded no-image presentation.</summary>
    [HttpDelete("products/{productId:guid}/image")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CatalogueManage)]
    [ProducesResponseType(typeof(ApiResponse<ProductImageResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProductImageResult>>> RemoveProductImage(
        Guid productId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ProductImageResult>.Ok(await catalogueService.RemoveProductImageAsync(
            RequireProductImageActor(), productId, cancellationToken)));

    private ProductImageActor RequireProductImageActor()
    {
        var userIdValue = User.FindFirstValue("user_id");
        if (!long.TryParse(userIdValue, NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
        {
            throw new UnauthorizedAppException();
        }

        return new ProductImageActor(userId);
    }

    /// <summary>Acting-user id attributed to product configuration audits
    /// (charge assignment/removal). Resolved exactly like the image actor.</summary>
    private long RequireCatalogueActor()
    {
        var userIdValue = User.FindFirstValue("user_id");
        if (!long.TryParse(userIdValue, NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
        {
            throw new UnauthorizedAppException();
        }

        return userId;
    }

    [HttpPost("products/{productId:guid}/activate")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CatalogueManage)]
    [ProducesResponseType(typeof(ApiResponse<ProductResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProductResult>>> ActivateProduct(
        Guid productId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ProductResult>.Ok(await catalogueService.SetProductActiveAsync(
            productId, true, cancellationToken)));

    [HttpPost("products/{productId:guid}/deactivate")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CatalogueManage)]
    [ProducesResponseType(typeof(ApiResponse<ProductResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProductResult>>> DeactivateProduct(
        Guid productId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ProductResult>.Ok(await catalogueService.SetProductActiveAsync(
            productId, false, cancellationToken)));

    [HttpPut("products/{productId:guid}/branches")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CatalogueManage)]
    [ProducesResponseType(typeof(ApiResponse<ProductResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProductResult>>> SetBranchAvailability(
        Guid productId,
        [FromBody] SetProductBranchAvailabilityApiRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ProductResult>.Ok(await catalogueService.SetBranchAvailabilityAsync(
            productId, new SetProductBranchAvailabilityRequest(
                request.BranchId, request.IsAvailable, request.MaxDailyQuantity), cancellationToken)));

    [HttpGet("product-categories")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CatalogueRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ProductCategoryResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ProductCategoryResult>>>> GetCategories(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<ProductCategoryResult>>.Ok(
            await catalogueService.GetCategoriesForAdministrationAsync(cancellationToken)));

    [HttpPost("product-categories")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CatalogueManage)]
    [ProducesResponseType(typeof(ApiResponse<ProductCategoryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProductCategoryResult>>> CreateCategory(
        [FromBody] UpsertProductCategoryApiRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ProductCategoryResult>.Ok(await catalogueService.CreateCategoryAsync(
            request.ToApplicationRequest(), cancellationToken)));

    [HttpPatch("product-categories/{categoryId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CatalogueManage)]
    [ProducesResponseType(typeof(ApiResponse<ProductCategoryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProductCategoryResult>>> UpdateCategory(
        Guid categoryId,
        [FromBody] UpsertProductCategoryApiRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ProductCategoryResult>.Ok(await catalogueService.UpdateCategoryAsync(
            categoryId, request.ToApplicationRequest(), cancellationToken)));

    [HttpPost("product-categories/{categoryId:guid}/activate")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CatalogueManage)]
    [ProducesResponseType(typeof(ApiResponse<ProductCategoryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProductCategoryResult>>> ActivateCategory(
        Guid categoryId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ProductCategoryResult>.Ok(await catalogueService.SetCategoryActiveAsync(
            categoryId, true, cancellationToken)));

    [HttpPost("product-categories/{categoryId:guid}/deactivate")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CatalogueManage)]
    [ProducesResponseType(typeof(ApiResponse<ProductCategoryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProductCategoryResult>>> DeactivateCategory(
        Guid categoryId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ProductCategoryResult>.Ok(await catalogueService.SetCategoryActiveAsync(
            categoryId, false, cancellationToken)));
}

public sealed record UpsertProductApiRequest(
    [Required, MaxLength(80)] string Sku,
    [Required, MaxLength(160)] string Name,
    [MaxLength(1000)] string? Description,
    Guid CategoryId,
    [Required, MaxLength(20)] string UnitOfMeasure,
    decimal Price,
    [Required, MinLength(1)] IReadOnlyList<Guid> BranchIds,
    // Item-level Tax & Charges assignment (charge public ids). MUST be bound
    // here: the admin clients already send `applicableChargeIds`, and without
    // this property the JSON binder silently dropped the field — the product
    // saved with no charge mappings at all. Null means "not supplied" (the
    // service then treats it as no charges, matching the application default).
    IReadOnlyList<Guid>? ApplicableChargeIds = null)
{
    public UpsertProductRequest ToApplicationRequest() =>
        new(Sku, Name, Description, CategoryId, UnitOfMeasure, Price, BranchIds, ApplicableChargeIds);
}

public sealed record UpsertProductCategoryApiRequest(
    [Required, MaxLength(40)] string Code,
    [Required, MaxLength(120)] string Name,
    [MaxLength(500)] string? Description)
{
    public UpsertProductCategoryRequest ToApplicationRequest() =>
        new(Code, Name, Description);
}

public sealed record SetProductBranchAvailabilityApiRequest(
    Guid BranchId,
    bool IsAvailable,
    decimal? MaxDailyQuantity);
