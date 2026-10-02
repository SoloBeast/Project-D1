using System.ComponentModel.DataAnnotations;

namespace DoodhDirect.Infrastructure.Branding;

/// <summary>
/// Storage and validation limits for globally-managed branding assets.
/// Follows the MilkTestMediaOptions conventions; the values are stored-asset
/// caps (the HTTP transport cap is enforced separately on the controller via
/// RequestSizeLimit, mirroring the Catalogue image endpoints).
/// </summary>
public sealed class BrandingMediaOptions
{
    public const string SectionName = "BrandingMedia";

    [Required]
    public string Provider { get; init; } = "Local";

    /// <summary>Maximum stored logo size in megabytes (PNG/JPEG/WebP).</summary>
    [Range(1, 50)]
    public int MaximumLogoFileSizeMegabytes { get; init; } = 10;

    /// <summary>Maximum stored startup-animation size in megabytes (GIF).</summary>
    [Range(1, 50)]
    public int MaximumAnimationFileSizeMegabytes { get; init; } = 5;
}
