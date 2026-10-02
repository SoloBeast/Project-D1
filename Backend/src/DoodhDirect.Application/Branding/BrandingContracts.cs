using DoodhDirect.Application.Common;
using DoodhDirect.Domain.Configuration;

namespace DoodhDirect.Application.Branding;

/// <summary>Authenticated actor for branding mutations, resolved from the
/// user_id claim by the controller.</summary>
public sealed record BrandingActor(long UserId);

public sealed record BrandingAssetResult(
    BrandingAssetKind Kind,
    string FileName,
    string ContentType,
    long FileSize,
    DateTime UpdatedAt,
    string Url);

/// <summary>The globally active branding. Null members mean "not configured";
/// clients keep their bundled fallback for that asset.</summary>
public sealed record BrandingResult(
    BrandingAssetResult? Logo,
    BrandingAssetResult? StartupAnimation);

// StoredMediaResult / StoredMediaContent are reused from
// DoodhDirect.Application.MilkTesting — the same records the shared
// IMediaStorage abstraction already speaks.

/// <summary>Branding media opened for the public endpoints, carrying the
/// cache-revalidation metadata (strong ETag + Last-Modified) derived from the
/// active asset row so replacement invalidates client caches.</summary>
public sealed record BrandingMediaContent(
    Stream Content,
    string ContentType,
    long FileSize,
    string ETag,
    DateTimeOffset LastModifiedUtc) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>Validated branding upload, buffered and signature-checked.</summary>
public sealed record ValidatedBrandingAsset(
    string FileName,
    string ContentType,
    long FileSize,
    Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public interface IBrandingAssetValidator
{
    /// <summary>Stored-asset size cap for logos (PNG/JPEG/WebP) in bytes.</summary>
    long MaximumLogoFileSize { get; }

    /// <summary>Stored-asset size cap for startup animations (GIF) in bytes.</summary>
    long MaximumAnimationFileSize { get; }

    Task<ValidatedBrandingAsset> ValidateLogoAsync(
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken);

    Task<ValidatedBrandingAsset> ValidateStartupAnimationAsync(
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken);
}

public interface IBrandingService
{
    Task<BrandingResult> GetCurrentBrandingAsync(CancellationToken cancellationToken);

    Task<BrandingResult> UpsertLogoAsync(
        BrandingActor actor,
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken);

    Task<BrandingResult> RemoveLogoAsync(BrandingActor actor, CancellationToken cancellationToken);

    Task<BrandingResult> UpsertStartupAnimationAsync(
        BrandingActor actor,
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken);

    Task<BrandingResult> RemoveStartupAnimationAsync(BrandingActor actor, CancellationToken cancellationToken);

    /// <summary>Opens the active logo content with cache-revalidation headers, or
    /// null when no logo is configured (the public endpoint answers 404 so
    /// clients keep their bundled fallback).</summary>
    Task<BrandingMediaContent?> OpenLogoAsync(CancellationToken cancellationToken);

    /// <summary>Opens the active startup animation content, or null when not
    /// configured.</summary>
    Task<BrandingMediaContent?> OpenStartupAnimationAsync(CancellationToken cancellationToken);
}
