using DoodhDirect.Application.Common;
using DoodhDirect.Infrastructure.MilkTesting;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class MilkTestMediaTests
{
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x01];
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01];
    private static readonly byte[] WebpBytes =
        [0x52, 0x49, 0x46, 0x46, 0x04, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x01];

    [Theory]
    [MemberData(nameof(ValidImages))]
    public async Task Validator_AcceptsSupportedSignatures(
        byte[] bytes,
        string declaredContentType,
        string expectedContentType)
    {
        var validator = CreateValidator();
        await using var input = new MemoryStream(bytes);

        var result = await validator.ValidateAsync(
            input,
            "sample.bin",
            declaredContentType,
            CancellationToken.None);

        Assert.Equal("sample.bin", result.FileName);
        Assert.Equal(expectedContentType, result.ContentType);
        Assert.Equal(bytes.LongLength, result.FileSize);
        await using var validatedContent = result.Content;
        Assert.Equal(bytes, await ReadAllBytesAsync(validatedContent));
    }

    public static TheoryData<byte[], string, string> ValidImages => new()
    {
        { JpegBytes, "image/jpeg", "image/jpeg" },
        { PngBytes, "IMAGE/PNG", "image/png" },
        { WebpBytes, " image/webp ", "image/webp" }
    };

    [Fact]
    public async Task Validator_SanitizesClientFileName()
    {
        var validator = CreateValidator();
        await using var input = new MemoryStream(JpegBytes);

        var result = await validator.ValidateAsync(
            input,
            "../untrusted/photo.jpg",
            "image/jpeg",
            CancellationToken.None);

        await using var validatedContent = result.Content;
        Assert.Equal("photo.jpg", result.FileName);
    }

    [Fact]
    public async Task Validator_RejectsEmptyContent()
    {
        var validator = CreateValidator();
        await using var input = new MemoryStream();

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            validator.ValidateAsync(
                input,
                "empty.jpg",
                "image/jpeg",
                CancellationToken.None));

        Assert.Equal("image", exception.Field);
        Assert.Contains("empty", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validator_RejectsUnsupportedSignature()
    {
        var validator = CreateValidator();
        await using var input = new MemoryStream("not an image"u8.ToArray());

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            validator.ValidateAsync(
                input,
                "fake.jpg",
                "image/jpeg",
                CancellationToken.None));

        Assert.Equal("image", exception.Field);
        Assert.Contains("JPEG, PNG, or WebP", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validator_RejectsDeclaredContentTypeMismatch()
    {
        var validator = CreateValidator();
        await using var input = new MemoryStream(PngBytes);

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            validator.ValidateAsync(
                input,
                "photo.png",
                "image/jpeg",
                CancellationToken.None));

        Assert.Equal("image", exception.Field);
        Assert.Contains("does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validator_RejectsContentOverConfiguredLimit()
    {
        var validator = CreateValidator(maximumFileSizeMegabytes: 1);
        await using var input = new MemoryStream(new byte[(1024 * 1024) + 1]);

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            validator.ValidateAsync(
                input,
                "large.jpg",
                "image/jpeg",
                CancellationToken.None));

        Assert.Equal("image", exception.Field);
        Assert.Contains("maximum size of 1 MB", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalStorage_SavesOpensAndDeletesNestedContent()
    {
        await using var root = new TemporaryDirectory();
        var storage = CreateStorage(root.Path);
        const string storageKey = "2026/08/branch/test/photo.jpg";
        await using var input = new MemoryStream(JpegBytes);

        var saved = await storage.SaveAsync(
            storageKey,
            input,
            "image/jpeg",
            CancellationToken.None);

        Assert.Equal(storageKey, saved.StorageKey);
        Assert.Equal("image/jpeg", saved.ContentType);
        Assert.Equal(JpegBytes.LongLength, saved.FileSize);
        Assert.True(File.Exists(System.IO.Path.Combine(
            root.Path,
            "2026",
            "08",
            "branch",
            "test",
            "photo.jpg")));

        var opened = await storage.OpenReadAsync(storageKey, CancellationToken.None);
        await using (opened.Content)
        {
            Assert.Equal(JpegBytes.LongLength, opened.FileSize);
            Assert.Equal(JpegBytes, await ReadAllBytesAsync(opened.Content));
        }

        await storage.DeleteIfExistsAsync(storageKey, CancellationToken.None);
        await storage.DeleteIfExistsAsync(storageKey, CancellationToken.None);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            storage.OpenReadAsync(storageKey, CancellationToken.None));
    }

    [Theory]
    [InlineData("../outside.jpg")]
    [InlineData("nested/../../outside.jpg")]
    public async Task LocalStorage_RejectsTraversal(string storageKey)
    {
        await using var root = new TemporaryDirectory();
        var storage = CreateStorage(root.Path);
        await using var input = new MemoryStream(JpegBytes);

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            storage.SaveAsync(
                storageKey,
                input,
                "image/jpeg",
                CancellationToken.None));
    }

    [Fact]
    public async Task LocalStorage_RejectsRootedPath()
    {
        await using var root = new TemporaryDirectory();
        var storage = CreateStorage(root.Path);
        var rootedKey = System.IO.Path.Combine(
            System.IO.Path.GetPathRoot(root.Path)!,
            "outside.jpg");
        await using var input = new MemoryStream(JpegBytes);

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            storage.SaveAsync(
                rootedKey,
                input,
                "image/jpeg",
                CancellationToken.None));
    }

    [Fact]
    public async Task LocalStorage_RemovesTemporaryFileWhenDestinationAlreadyExists()
    {
        await using var root = new TemporaryDirectory();
        var storage = CreateStorage(root.Path);
        const string storageKey = "duplicate/photo.jpg";

        await using (var first = new MemoryStream(JpegBytes))
        {
            await storage.SaveAsync(
                storageKey,
                first,
                "image/jpeg",
                CancellationToken.None);
        }

        await using var duplicate = new MemoryStream(PngBytes);
        await Assert.ThrowsAnyAsync<IOException>(() =>
            storage.SaveAsync(
                storageKey,
                duplicate,
                "image/png",
                CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(
            System.IO.Path.Combine(root.Path, "duplicate"),
            "*.tmp"));
        Assert.Equal(JpegBytes, await File.ReadAllBytesAsync(
            System.IO.Path.Combine(root.Path, "duplicate", "photo.jpg")));
    }

    private static MilkTestImageValidator CreateValidator(
        int maximumFileSizeMegabytes = 1) => new(Options.Create(
        new MilkTestMediaOptions
        {
            Provider = "Local",
            LocalRootPath = "unused",
            MaximumFileSizeMegabytes = maximumFileSizeMegabytes
        }));

    private static LocalMediaStorage CreateStorage(string rootPath) => new(Options.Create(
        new MilkTestMediaOptions
        {
            Provider = "Local",
            LocalRootPath = rootPath,
            MaximumFileSizeMegabytes = 1
        }));

    private static async Task<byte[]> ReadAllBytesAsync(Stream content)
    {
        using var output = new MemoryStream();
        await content.CopyToAsync(output);
        return output.ToArray();
    }

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"DoodhDirect-MilkTest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }
}
