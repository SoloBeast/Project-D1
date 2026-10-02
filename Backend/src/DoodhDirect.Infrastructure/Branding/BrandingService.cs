using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Branding;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Branding;

/// <summary>
/// Globally-managed business branding (one active logo, one active startup
/// animation). Follows the product-image lifecycle exactly: validate, store
/// the new blob first, swap the authoritative DB row, commit, then delete the
/// old blob post-commit. Pre-commit failures clean the new blob and leave the
/// previous branding intact; post-commit blob cleanup failures only ever
/// orphan a file and never leave a dangling DB reference.
/// </summary>
public sealed class BrandingService(
    DoodhDirectDbContext dbContext,
    IBrandingAssetValidator brandingAssetValidator,
    IMediaStorage mediaStorage,
    IIndiaTimeProvider timeProvider) : IBrandingService
{
    private const string AuditEntityType = "Branding";
    public const string LogoUrlPath = "/api/v1/branding/logo";
    public const string StartupAnimationUrlPath = "/api/v1/branding/startup-animation";

    public async Task<BrandingResult> GetCurrentBrandingAsync(CancellationToken cancellationToken)
    {
        var assets = await dbContext.BrandingAssets
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return new BrandingResult(
            ToResult(assets.FirstOrDefault(asset => asset.AssetKind == BrandingAssetKind.Logo), LogoUrlPath),
            ToResult(assets.FirstOrDefault(asset => asset.AssetKind == BrandingAssetKind.StartupAnimation), StartupAnimationUrlPath));
    }

    public Task<BrandingResult> UpsertLogoAsync(
        BrandingActor actor,
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken) =>
        UpsertAsync(
            BrandingAssetKind.Logo,
            actor,
            content,
            fileName,
            declaredContentType,
            cancellationToken);

    public Task<BrandingResult> UpsertStartupAnimationAsync(
        BrandingActor actor,
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken) =>
        UpsertAsync(
            BrandingAssetKind.StartupAnimation,
            actor,
            content,
            fileName,
            declaredContentType,
            cancellationToken);

    public async Task<BrandingResult> RemoveLogoAsync(BrandingActor actor, CancellationToken cancellationToken) =>
        await RemoveAsync(BrandingAssetKind.Logo, actor, cancellationToken);

    public async Task<BrandingResult> RemoveStartupAnimationAsync(BrandingActor actor, CancellationToken cancellationToken) =>
        await RemoveAsync(BrandingAssetKind.StartupAnimation, actor, cancellationToken);

    public async Task<BrandingMediaContent?> OpenLogoAsync(CancellationToken cancellationToken) =>
        await OpenAsync(BrandingAssetKind.Logo, cancellationToken);

    public async Task<BrandingMediaContent?> OpenStartupAnimationAsync(CancellationToken cancellationToken) =>
        await OpenAsync(BrandingAssetKind.StartupAnimation, cancellationToken);

    private async Task<BrandingResult> UpsertAsync(
        BrandingAssetKind kind,
        BrandingActor actor,
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken)
    {
        await using var validated = kind == BrandingAssetKind.Logo
            ? await brandingAssetValidator.ValidateLogoAsync(content, fileName, declaredContentType, cancellationToken)
            : await brandingAssetValidator.ValidateStartupAnimationAsync(content, fileName, declaredContentType, cancellationToken);

        var now = timeProvider.Now;
        var current = await dbContext.BrandingAssets
            .SingleOrDefaultAsync(asset => asset.AssetKind == kind, cancellationToken);
        var storageKey = $"branding/{now:yyyy/MM}/{StorageFolder(kind)}/{Guid.NewGuid():N}{ExtensionFor(validated.ContentType)}";
        var stored = await mediaStorage.SaveAsync(storageKey, validated.Content, validated.ContentType, cancellationToken);

        var previousStorageKey = current?.StorageKey;
        var previousFileName = current?.FileName;
        var previousContentType = current?.ContentType;
        var previousFileSize = current?.FileSize;

        try
        {
            if (stored.FileSize != validated.FileSize)
            {
                throw new InvalidOperationException("The stored media size does not match the validated asset size.");
            }

            if (current is null)
            {
                var asset = new BrandingAsset(
                    kind,
                    stored.StorageKey,
                    validated.FileName,
                    validated.ContentType,
                    stored.FileSize,
                    actor.UserId,
                    now);
                dbContext.BrandingAssets.Add(asset);
                AddAudit(
                    actor.UserId,
                    UploadAction(kind),
                    kind,
                    null,
                    new { AssetId = asset.PublicId, asset.FileName, asset.ContentType, asset.FileSize },
                    now);
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
                // Old-value snapshots were captured before ReplaceContent
                // mutated the row. Storage keys are never written to audit
                // payloads (consistent with the product-image audits).
                AddAudit(
                    actor.UserId,
                    ReplaceAction(kind),
                    kind,
                    new { FileName = previousFileName, ContentType = previousContentType, FileSize = previousFileSize },
                    new { AssetId = current.PublicId, current.FileName, current.ContentType, current.FileSize },
                    now);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Compensating cleanup for PRE-commit failures only: the database
            // state was not committed, so the newly stored blob must not linger
            // and the previous branding stays intact.
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

        return await GetCurrentBrandingAsync(cancellationToken);
    }

    private async Task<BrandingResult> RemoveAsync(
        BrandingAssetKind kind,
        BrandingActor actor,
        CancellationToken cancellationToken)
    {
        var current = await dbContext.BrandingAssets
            .SingleOrDefaultAsync(asset => asset.AssetKind == kind, cancellationToken)
            ?? throw new NotFoundException($"The {Label(kind)} is not configured.");

        var storageKey = current.StorageKey;
        dbContext.BrandingAssets.Remove(current);
        AddAudit(
            actor.UserId,
            RemoveAction(kind),
            kind,
            new { AssetId = current.PublicId, current.FileName, current.ContentType, current.FileSize },
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

        return await GetCurrentBrandingAsync(cancellationToken);
    }

    private async Task<BrandingMediaContent?> OpenAsync(
        BrandingAssetKind kind,
        CancellationToken cancellationToken)
    {
        var asset = await dbContext.BrandingAssets
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.AssetKind == kind, cancellationToken);
        if (asset is null)
        {
            return null;
        }

        var stored = await mediaStorage.OpenReadAsync(asset.StorageKey, cancellationToken);
        // Strong validator derived from the immutable asset identity + size, plus
        // the upload timestamp for Last-Modified. Replacement writes a new row
        // value set, so both change and caches revalidate.
        var etag = $"\"{asset.PublicId:N}-{asset.FileSize}\"";
        var lastModified = new DateTimeOffset(asset.UploadedAt, TimeSpan.FromHours(5.5));
        return new BrandingMediaContent(stored.Content, asset.ContentType, asset.FileSize, etag, lastModified);
    }

    private static BrandingAssetResult? ToResult(BrandingAsset? asset, string urlPath) => asset is null
        ? null
        : new BrandingAssetResult(
            asset.AssetKind,
            asset.FileName,
            asset.ContentType,
            asset.FileSize,
            asset.UploadedAt,
            urlPath);

    private static string StorageFolder(BrandingAssetKind kind) => kind switch
    {
        BrandingAssetKind.Logo => "logo",
        BrandingAssetKind.StartupAnimation => "startup-animation",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string Label(BrandingAssetKind kind) => kind switch
    {
        BrandingAssetKind.Logo => "logo",
        BrandingAssetKind.StartupAnimation => "startup animation",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string UploadAction(BrandingAssetKind kind) => kind switch
    {
        BrandingAssetKind.Logo => "BRANDING.LOGO_UPLOAD",
        BrandingAssetKind.StartupAnimation => "BRANDING.ANIMATION_UPLOAD",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string ReplaceAction(BrandingAssetKind kind) => kind switch
    {
        BrandingAssetKind.Logo => "BRANDING.LOGO_REPLACE",
        BrandingAssetKind.StartupAnimation => "BRANDING.ANIMATION_REPLACE",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string RemoveAction(BrandingAssetKind kind) => kind switch
    {
        BrandingAssetKind.Logo => "BRANDING.LOGO_REMOVE",
        BrandingAssetKind.StartupAnimation => "BRANDING.ANIMATION_REMOVE",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string ExtensionFor(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => throw new ValidationAppException("The validated asset type is unsupported.", "file")
    };

    private void AddAudit(
        long userId,
        string action,
        BrandingAssetKind kind,
        object? oldValue,
        object? newValue,
        DateTime createdAt) =>
        dbContext.AddAuditLog(new AuditLog(
            userId,
            action,
            AuditEntityType,
            kind.ToString(),
            oldValue is null ? null : JsonSerializer.Serialize(oldValue),
            newValue is null ? null : JsonSerializer.Serialize(newValue),
            null,
            null,
            null,
            createdAt));
}
