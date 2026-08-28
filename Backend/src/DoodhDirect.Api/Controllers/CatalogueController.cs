using System.ComponentModel.DataAnnotations;
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
            request.ToApplicationRequest(), cancellationToken)));

    [HttpPatch("products/{productId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CatalogueManage)]
    [ProducesResponseType(typeof(ApiResponse<ProductResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ProductResult>>> UpdateProduct(
        Guid productId,
        [FromBody] UpsertProductApiRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ProductResult>.Ok(await catalogueService.UpdateProductAsync(
            productId, request.ToApplicationRequest(), cancellationToken)));

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
    decimal Price)
{
    public UpsertProductRequest ToApplicationRequest() =>
        new(Sku, Name, Description, CategoryId, UnitOfMeasure, Price);
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
