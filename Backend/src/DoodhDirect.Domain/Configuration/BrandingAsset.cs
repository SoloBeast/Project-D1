using DoodhDirect.Domain.Common;
using DoodhDirect.Domain.Identity;

namespace DoodhDirect.Domain.Configuration;

/// <summary>
/// The two globally-managed branding surfaces. There is exactly one active
/// asset of each kind for the whole business — no branch/customer scoping.
/// </summary>
public enum BrandingAssetKind
{
    Logo,
    StartupAnimation
}

/// <summary>
/// The current active branding asset of one kind. At most one row exists per
/// <see cref="AssetKind"/> (enforced by a unique AssetKind index); the row
/// itself is the authoritative reference to the stored blob. Upload history
/// lives in the AuditLog — no history rows are kept here.
/// </summary>
public sealed class BrandingAsset : AuditableEntity
{
    private BrandingAsset() { }

    public BrandingAsset(
        BrandingAssetKind assetKind,
        string storageKey,
        string fileName,
        string contentType,
        long fileSize,
        long uploadedByUserId,
        DateTime uploadedAt)
    {
        if (fileSize <= 0) throw new ArgumentOutOfRangeException(nameof(fileSize));
        if (uploadedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(uploadedByUserId));
        if (uploadedAt.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "The timestamp must be India-local with an unspecified DateTime kind.",
                nameof(uploadedAt));
        }

        AssetKind = assetKind;
        StorageKey = Required(storageKey, nameof(storageKey));
        FileName = Required(fileName, nameof(fileName));
        ContentType = Required(contentType, nameof(contentType));
        FileSize = fileSize;
        UploadedByUserId = uploadedByUserId;
        UploadedAt = uploadedAt;
    }

    public BrandingAssetKind AssetKind { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long FileSize { get; private set; }
    public long UploadedByUserId { get; private set; }
    public DateTime UploadedAt { get; private set; }

    public User UploadedByUser { get; private set; } = null!;

    /// <summary>
    /// Points the current asset at replacement content during a safe replace:
    /// the new blob is already stored and durably committed before the previous
    /// blob is deleted.
    /// </summary>
    public void ReplaceContent(
        string storageKey,
        string fileName,
        string contentType,
        long fileSize,
        long uploadedByUserId,
        DateTime uploadedAt)
    {
        if (fileSize <= 0) throw new ArgumentOutOfRangeException(nameof(fileSize));
        if (uploadedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(uploadedByUserId));
        if (uploadedAt.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "The timestamp must be India-local with an unspecified DateTime kind.",
                nameof(uploadedAt));
        }

        StorageKey = Required(storageKey, nameof(storageKey));
        FileName = Required(fileName, nameof(fileName));
        ContentType = Required(contentType, nameof(contentType));
        FileSize = fileSize;
        UploadedByUserId = uploadedByUserId;
        UploadedAt = uploadedAt;
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();
}
