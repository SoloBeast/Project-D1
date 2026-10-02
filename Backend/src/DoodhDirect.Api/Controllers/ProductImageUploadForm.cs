namespace DoodhDirect.Api.Controllers;

/// <summary>Multipart form binding for product image uploads.</summary>
public sealed class ProductImageUploadForm
{
    public IFormFile? Image { get; init; }
}
