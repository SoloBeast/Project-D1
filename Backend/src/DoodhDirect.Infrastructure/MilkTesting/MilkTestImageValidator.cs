using DoodhDirect.Application.Common;
using DoodhDirect.Application.MilkTesting;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.MilkTesting;

public sealed class MilkTestImageValidator(IOptions<MilkTestMediaOptions> options) : IMilkTestImageValidator
{
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] RiffSignature = "RIFF"u8.ToArray();
    private static readonly byte[] WebpSignature = "WEBP"u8.ToArray();

    public long MaximumFileSize { get; } = checked(options.Value.MaximumFileSizeMegabytes * 1024L * 1024L);

    public async Task<ValidatedMilkTestImage> ValidateAsync(
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var safeFileName = Path.GetFileName(fileName?.Trim());
        if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName.Length > 255)
        {
            throw new ValidationAppException("A valid image file name is required.", "image");
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
                if (total > MaximumFileSize)
                {
                    throw new ValidationAppException(
                        $"The image exceeds the maximum size of {MaximumFileSize / (1024 * 1024)} MB.",
                        "image");
                }
                await buffered.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (total == 0)
            {
                throw new ValidationAppException("The image file is empty.", "image");
            }

            var detectedContentType = DetectContentType(buffered.GetBuffer(), checked((int)total));
            if (detectedContentType is null)
            {
                throw new ValidationAppException("Only valid JPEG, PNG, or WebP images are allowed.", "image");
            }

            if (!string.IsNullOrWhiteSpace(declaredContentType) &&
                !string.Equals(declaredContentType.Trim(), detectedContentType, StringComparison.OrdinalIgnoreCase))
            {
                throw new ValidationAppException("The declared image type does not match the file content.", "image");
            }

            buffered.Position = 0;
            return new ValidatedMilkTestImage(safeFileName, detectedContentType, total, buffered);
        }
        catch
        {
            await buffered.DisposeAsync();
            throw;
        }
    }

    private static string? DetectContentType(byte[] bytes, int length)
    {
        if (StartsWith(bytes, length, JpegSignature))
        {
            return "image/jpeg";
        }
        if (StartsWith(bytes, length, PngSignature))
        {
            return "image/png";
        }
        if (StartsWith(bytes, length, RiffSignature) &&
            length >= 12 &&
            bytes.AsSpan(8, 4).SequenceEqual(WebpSignature))
        {
            return "image/webp";
        }
        return null;
    }

    private static bool StartsWith(byte[] bytes, int length, byte[] signature) =>
        length >= signature.Length && bytes.AsSpan(0, signature.Length).SequenceEqual(signature);
}
