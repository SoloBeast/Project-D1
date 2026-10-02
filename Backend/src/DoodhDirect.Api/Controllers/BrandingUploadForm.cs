namespace DoodhDirect.Api.Controllers;

/// <summary>Multipart form binding for branding asset uploads.</summary>
public sealed class BrandingUploadForm
{
    public IFormFile? File { get; init; }
}
