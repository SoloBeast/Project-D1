using DoodhDirect.Application.Branding;
using DoodhDirect.Application.Common;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Branding;

/// <summary>
/// Branding asset validation with the same guarantees as the product-image
/// validator: filename sanitization (never trusted for paths), buffered reads
/// under a hard size cap, content-type detection from magic bytes, and a
/// declared-MIME cross-check. Logos accept PNG/JPEG/WebP; the startup
/// animation accepts GIF only (GIF87a/GIF89a). No server-side GIF frame-count
/// or duration validation is attempted — the current infrastructure cannot do
/// that without an additional dependency.
/// </summary>
public sealed class BrandingAssetValidator : IBrandingAssetValidator
{
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] RiffSignature = "RIFF"u8.ToArray();
    private static readonly byte[] WebpSignature = "WEBP"u8.ToArray();
    private static readonly byte[] Gif87aSignature = "GIF87a"u8.ToArray();
    private static readonly byte[] Gif89aSignature = "GIF89a"u8.ToArray();

    private readonly long _maximumLogoFileSize;
    private readonly long _maximumAnimationFileSize;

    public BrandingAssetValidator(IOptions<BrandingMediaOptions> options)
    {
        _maximumLogoFileSize = checked(options.Value.MaximumLogoFileSizeMegabytes * 1024L * 1024L);
        _maximumAnimationFileSize = checked(options.Value.MaximumAnimationFileSizeMegabytes * 1024L * 1024L);
    }

    public long MaximumLogoFileSize => _maximumLogoFileSize;

    public long MaximumAnimationFileSize => _maximumAnimationFileSize;

    public Task<ValidatedBrandingAsset> ValidateLogoAsync(
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken) =>
        ValidateAsync(
            content,
            fileName,
            declaredContentType,
            _maximumLogoFileSize,
            DetectImageContentType,
            "Only valid PNG, JPEG, or WebP images are allowed.",
            "The declared image type does not match the file content.",
            cancellationToken);

    public Task<ValidatedBrandingAsset> ValidateStartupAnimationAsync(
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(declaredContentType) &&
            !string.Equals(declaredContentType.Trim(), "image/gif", StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationAppException(
                "The startup animation must be declared as image/gif.",
                "file");
        }

        return ValidateAsync(
            content,
            fileName,
            declaredContentType,
            _maximumAnimationFileSize,
            DetectAnimationContentType,
            "Only valid GIF animations are allowed.",
            "The declared image type does not match the file content.",
            cancellationToken);
    }

    private static async Task<ValidatedBrandingAsset> ValidateAsync(
        Stream content,
        string fileName,
        string? declaredContentType,
        long maximumFileSize,
        Func<byte[], int, string?> detectContentType,
        string unsupportedMessage,
        string mismatchMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var safeFileName = Path.GetFileName(fileName?.Trim());
        if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName.Length > 255)
        {
            throw new ValidationAppException("A valid file name is required.", "file");
        }

        var buffered = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        try
        {
            while (true)
            {
                var read = await content.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maximumFileSize)
                {
                    throw new ValidationAppException(
                        $"The file exceeds the maximum size of {maximumFileSize / (1024 * 1024)} MB.",
                        "file");
                }

                await buffered.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (total == 0)
            {
                throw new ValidationAppException("The file is empty.", "file");
            }

            var detectedContentType = detectContentType(buffered.GetBuffer(), checked((int)total));
            if (detectedContentType is null)
            {
                throw new ValidationAppException(unsupportedMessage, "file");
            }

            if (!string.IsNullOrWhiteSpace(declaredContentType) &&
                !string.Equals(declaredContentType.Trim(), detectedContentType, StringComparison.OrdinalIgnoreCase))
            {
                throw new ValidationAppException(mismatchMessage, "file");
            }

            buffered.Position = 0;
            return new ValidatedBrandingAsset(safeFileName, detectedContentType, total, buffered);
        }
        catch
        {
            await buffered.DisposeAsync();
            throw;
        }
    }

    private static string? DetectImageContentType(byte[] bytes, int length)
    {
        if (StartsWith(bytes, length, PngSignature))
        {
            return "image/png";
        }
        if (StartsWith(bytes, length, JpegSignature))
        {
            return "image/jpeg";
        }
        if (StartsWith(bytes, length, RiffSignature) &&
            length >= 12 &&
            bytes.AsSpan(8, 4).SequenceEqual(WebpSignature))
        {
            return "image/webp";
        }
        return null;
    }

    private static string? DetectAnimationContentType(byte[] bytes, int length)
    {
        if (StartsWith(bytes, length, Gif87aSignature))
        {
            return "image/gif";
        }
        if (StartsWith(bytes, length, Gif89aSignature))
        {
            return "image/gif";
        }
        return null;
    }

    private static bool StartsWith(byte[] bytes, int length, byte[] signature) =>
        length >= signature.Length && bytes.AsSpan(0, signature.Length).SequenceEqual(signature);
}
