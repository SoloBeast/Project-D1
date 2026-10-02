using System.Security.Claims;
using System.Text.Json;
using DoodhDirect.Api.Controllers;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Branding;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Branding;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class BrandingServiceTests
{
    [Fact]
    public async Task CurrentBranding_Unconfigured_ReturnsNulls()
    {
        await using var harness = await BrandingHarness.CreateAsync();

        var result = await harness.Service.GetCurrentBrandingAsync(CancellationToken.None);

        Assert.Null(result.Logo);
        Assert.Null(result.StartupAnimation);
    }

    [Fact]
    public async Task UploadLogo_ValidPng_PersistsActiveReferenceAndReturnsPublicMetadata()
    {
        await using var harness = await BrandingHarness.CreateAsync();

        var result = await UploadLogoAsync(harness, "milk.png", "image/png");

        var logo = Assert.IsType<BrandingAssetResult>(result.Logo);
        Assert.Equal(BrandingAssetKind.Logo, logo.Kind);
        Assert.Equal("milk.png", logo.FileName);
        Assert.Equal("image/png", logo.ContentType);
        Assert.Equal(PngBytes.LongLength, logo.FileSize);
        Assert.Equal("/api/v1/branding/logo", logo.Url);
        var savedKey = Assert.Single(harness.Storage.SavedKeys);
        Assert.StartsWith("branding/", savedKey);
        Assert.Contains("/logo/", savedKey);
        Assert.EndsWith(".png", savedKey);
        var row = await harness.Db.BrandingAssets.SingleAsync();
        Assert.Equal(BrandingAssetKind.Logo, row.AssetKind);
        Assert.Equal(harness.Admin.Id, row.UploadedByUserId);
        Assert.Equal(PngBytes.LongLength, row.FileSize);
    }

    [Theory]
    [InlineData("png", "image/png", ".png")]
    [InlineData("jpeg", "image/jpeg", ".jpg")]
    [InlineData("webp", "image/webp", ".webp")]
    public async Task UploadLogo_AcceptsEverySupportedFormat(string format, string declaredType, string expectedExtension)
    {
        await using var harness = await BrandingHarness.CreateAsync();
        var bytes = format switch
        {
            "png" => PngBytes,
            "jpeg" => JpegBytes,
            "webp" => WebpBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

        var result = await UploadLogoAsync(harness, $"logo{expectedExtension}", declaredType, bytes);

        Assert.Equal(declaredType, result.Logo!.ContentType);
        Assert.EndsWith(expectedExtension, Assert.Single(harness.Storage.SavedKeys));
    }

    [Fact]
    public async Task UploadLogo_WithNonImageMagicBytes_IsRejected()
    {
        await using var harness = await BrandingHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() => UploadLogoAsync(
            harness, "payload.png", "image/png", ExeBytes));

        Assert.Empty(harness.Storage.SavedKeys);
        Assert.Empty(await harness.Db.BrandingAssets.ToListAsync());
    }

    [Fact]
    public async Task UploadLogo_WithDeclaredMimeMismatch_IsRejected()
    {
        await using var harness = await BrandingHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() => UploadLogoAsync(
            harness, "milk.png", "image/gif", PngBytes));

        Assert.Empty(harness.Storage.SavedKeys);
    }

    [Fact]
    public async Task UploadLogo_OversizedFile_IsRejected()
    {
        await using var harness = await BrandingHarness.CreateAsync(new BrandingMediaOptions
        {
            MaximumLogoFileSizeMegabytes = 1
        });

        await Assert.ThrowsAsync<ValidationAppException>(() => UploadLogoAsync(
            harness, "huge.png", "image/png", OversizedBytes(PngBytes)));

        Assert.Empty(harness.Storage.SavedKeys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UploadLogo_WithBlankFileName_IsRejected(string fileName)
    {
        await using var harness = await BrandingHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() => UploadLogoAsync(
            harness, fileName, "image/png", PngBytes));
    }

    [Fact]
    public async Task UploadLogo_WithOverLongFileName_IsRejected()
    {
        await using var harness = await BrandingHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() => UploadLogoAsync(
            harness, new string('a', 256) + ".png", "image/png", PngBytes));
    }

    [Fact]
    public async Task UploadLogo_SanitizesFileNameForPathSafety()
    {
        await using var harness = await BrandingHarness.CreateAsync();

        var result = await UploadLogoAsync(harness, @"..\..\evil.png", "image/png");

        Assert.Equal("evil.png", result.Logo!.FileName);
    }

    [Fact]
    public async Task ReplaceLogo_CommitsNewAssetThenDeletesOldBlob()
    {
        await using var harness = await BrandingHarness.CreateAsync();
        await UploadLogoAsync(harness, "first.png", "image/png");
        var firstKey = Assert.Single(harness.Storage.SavedKeys);

        var result = await UploadLogoAsync(harness, "second.jpg", "image/jpeg", JpegBytes);

        Assert.Equal("image/jpeg", result.Logo!.ContentType);
        Assert.Equal(1, await harness.Db.BrandingAssets.CountAsync());
        Assert.Contains(firstKey, harness.Storage.DeletedKeys);
        var opened = await harness.Service.OpenLogoAsync(CancellationToken.None);
        Assert.NotNull(opened);
        Assert.Equal("image/jpeg", opened.ContentType);
    }

    [Fact]
    public async Task ReplaceLogo_WhenStorageSaveFails_KeepsExistingLogo()
    {
        await using var harness = await BrandingHarness.CreateAsync();
        await UploadLogoAsync(harness, "first.png", "image/png");
        var firstKey = Assert.Single(harness.Storage.SavedKeys);
        harness.Storage.FailNextSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => UploadLogoAsync(
            harness, "second.jpg", "image/jpeg", JpegBytes));

        var current = await harness.Service.GetCurrentBrandingAsync(CancellationToken.None);
        Assert.Equal("first.png", current.Logo!.FileName);
        var row = await harness.Db.BrandingAssets.SingleAsync();
        Assert.Equal("image/png", row.ContentType);
        Assert.DoesNotContain(firstKey, harness.Storage.DeletedKeys);
        // Only the original blob remains stored; the failed upload never lingers.
        Assert.Single(harness.Storage.SavedKeys);
    }

    [Fact]
    public async Task ReplaceLogo_WhenStoredSizeVerificationFails_CleansNewBlobAndKeepsExisting()
    {
        await using var harness = await BrandingHarness.CreateAsync();
        await UploadLogoAsync(harness, "first.png", "image/png");
        harness.Storage.ReturnMismatchedSize = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => UploadLogoAsync(
            harness, "second.jpg", "image/jpeg", JpegBytes));

        var current = await harness.Service.GetCurrentBrandingAsync(CancellationToken.None);
        Assert.Equal("first.png", current.Logo!.FileName);
        // The new blob was stored and then compensated away pre-commit.
        Assert.Equal(2, harness.Storage.SavedKeys.Count);
        Assert.Contains(harness.Storage.SavedKeys[1], harness.Storage.DeletedKeys);
    }

    [Fact]
    public async Task RemoveLogo_DeletesRowAndBlobAndReturnsUnconfiguredState()
    {
        await using var harness = await BrandingHarness.CreateAsync();
        await UploadLogoAsync(harness, "milk.png", "image/png");
        var key = Assert.Single(harness.Storage.SavedKeys);

        var result = await harness.Service.RemoveLogoAsync(harness.AdminActor, CancellationToken.None);

        Assert.Null(result.Logo);
        Assert.Empty(await harness.Db.BrandingAssets.ToListAsync());
        Assert.Contains(key, harness.Storage.DeletedKeys);
        Assert.Null(await harness.Service.OpenLogoAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RemoveLogo_WhenNotConfigured_IsNotFound()
    {
        await using var harness = await BrandingHarness.CreateAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.RemoveLogoAsync(harness.AdminActor, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveLogo_WhenBlobCleanupFails_StillSucceedsWithOrphanTolerated()
    {
        await using var harness = await BrandingHarness.CreateAsync();
        await UploadLogoAsync(harness, "milk.png", "image/png");
        var key = Assert.Single(harness.Storage.SavedKeys);
        harness.Storage.FailNextDelete = true;

        var result = await harness.Service.RemoveLogoAsync(harness.AdminActor, CancellationToken.None);

        Assert.Null(result.Logo);
        Assert.Empty(await harness.Db.BrandingAssets.ToListAsync());
        Assert.Contains(key, harness.Storage.DeletedKeys);
    }

    [Theory]
    [InlineData("gif87a")]
    [InlineData("gif89a")]
    public async Task UploadStartupAnimation_AcceptsGif87aAndGif89a(string variant)
    {
        await using var harness = await BrandingHarness.CreateAsync();
        var bytes = variant == "gif87a" ? Gif87aBytes : Gif89aBytes;

        var result = await UploadAnimationAsync(harness, "intro.gif", "image/gif", bytes);

        var animation = Assert.IsType<BrandingAssetResult>(result.StartupAnimation);
        Assert.Equal(BrandingAssetKind.StartupAnimation, animation.Kind);
        Assert.Equal("image/gif", animation.ContentType);
        Assert.Equal("/api/v1/branding/startup-animation", animation.Url);
        var savedKey = Assert.Single(harness.Storage.SavedKeys);
        Assert.StartsWith("branding/", savedKey);
        Assert.Contains("/startup-animation/", savedKey);
        Assert.EndsWith(".gif", savedKey);
    }

    [Fact]
    public async Task UploadStartupAnimation_WithNonGifContent_IsRejected()
    {
        await using var harness = await BrandingHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() => UploadAnimationAsync(
            harness, "intro.gif", "image/gif", PngBytes));

        Assert.Empty(harness.Storage.SavedKeys);
    }

    [Fact]
    public async Task UploadStartupAnimation_WithDeclaredMimeMismatch_IsRejected()
    {
        await using var harness = await BrandingHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() => UploadAnimationAsync(
            harness, "intro.gif", "image/png", Gif89aBytes));

        Assert.Empty(harness.Storage.SavedKeys);
    }

    [Fact]
    public async Task UploadStartupAnimation_OversizedFile_IsRejected()
    {
        await using var harness = await BrandingHarness.CreateAsync(new BrandingMediaOptions
        {
            MaximumAnimationFileSizeMegabytes = 1
        });

        await Assert.ThrowsAsync<ValidationAppException>(() => UploadAnimationAsync(
            harness, "intro.gif", "image/gif", OversizedBytes(Gif89aBytes)));

        Assert.Empty(harness.Storage.SavedKeys);
    }

    [Fact]
    public async Task ReplaceAnimation_CommitsNewAssetThenDeletesOldBlob()
    {
        await using var harness = await BrandingHarness.CreateAsync();
        await UploadAnimationAsync(harness, "first.gif", "image/gif");
        var firstKey = Assert.Single(harness.Storage.SavedKeys);

        var result = await UploadAnimationAsync(harness, "second.gif", "image/gif");

        Assert.Equal("second.gif", result.StartupAnimation!.FileName);
        Assert.Equal(1, await harness.Db.BrandingAssets.CountAsync());
        Assert.Contains(firstKey, harness.Storage.DeletedKeys);
    }

    [Fact]
    public async Task ReplaceAnimation_WhenStorageSaveFails_KeepsExistingAnimation()
    {
        await using var harness = await BrandingHarness.CreateAsync();
        await UploadAnimationAsync(harness, "first.gif", "image/gif");
        var firstKey = Assert.Single(harness.Storage.SavedKeys);
        harness.Storage.FailNextSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => UploadAnimationAsync(
            harness, "second.gif", "image/gif"));

        var current = await harness.Service.GetCurrentBrandingAsync(CancellationToken.None);
        Assert.Equal("first.gif", current.StartupAnimation!.FileName);
        Assert.DoesNotContain(firstKey, harness.Storage.DeletedKeys);
        Assert.Single(harness.Storage.SavedKeys);
    }

    [Fact]
    public async Task RemoveAnimation_DeletesRowAndBlob()
    {
        await using var harness = await BrandingHarness.CreateAsync();
        await UploadAnimationAsync(harness, "intro.gif", "image/gif");
        var key = Assert.Single(harness.Storage.SavedKeys);

        var result = await harness.Service.RemoveStartupAnimationAsync(harness.AdminActor, CancellationToken.None);

        Assert.Null(result.StartupAnimation);
        Assert.Empty(await harness.Db.BrandingAssets.ToListAsync());
        Assert.Contains(key, harness.Storage.DeletedKeys);
    }

    [Fact]
    public async Task LogoAndAnimation_CoexistAsSeparateSingletonRows()
    {
        await using var harness = await BrandingHarness.CreateAsync();
        await UploadLogoAsync(harness, "milk.png", "image/png");
        await UploadAnimationAsync(harness, "intro.gif", "image/gif");

        var result = await harness.Service.GetCurrentBrandingAsync(CancellationToken.None);

        Assert.Equal(2, await harness.Db.BrandingAssets.CountAsync());
        Assert.NotNull(result.Logo);
        Assert.NotNull(result.StartupAnimation);
        var openedLogo = await harness.Service.OpenLogoAsync(CancellationToken.None);
        var openedAnimation = await harness.Service.OpenStartupAnimationAsync(CancellationToken.None);
        Assert.Equal("image/png", openedLogo.ContentType);
        Assert.Equal("image/gif", openedAnimation.ContentType);
    }

    [Fact]
    public async Task OpenLogo_CarriesCacheRevalidationMetadata()
    {
        await using var harness = await BrandingHarness.CreateAsync();
        await UploadLogoAsync(harness, "milk.png", "image/png");

        var opened = await harness.Service.OpenLogoAsync(CancellationToken.None);

        Assert.StartsWith("\"", opened.ETag);
        Assert.EndsWith($"-{PngBytes.LongLength}\"", opened.ETag);
        Assert.Equal(PngBytes.LongLength, opened.FileSize);
        Assert.Equal(TimeSpan.FromHours(5.5), opened.LastModifiedUtc.Offset);
    }

    [Fact]
    public async Task BrandingOperations_AreAuditedWithExactActions()
    {
        await using var harness = await BrandingHarness.CreateAsync();

        await UploadLogoAsync(harness, "one.png", "image/png");
        await UploadLogoAsync(harness, "two.jpg", "image/jpeg", JpegBytes);
        await harness.Service.RemoveLogoAsync(harness.AdminActor, CancellationToken.None);
        await UploadAnimationAsync(harness, "one.gif", "image/gif");
        await UploadAnimationAsync(harness, "two.gif", "image/gif");
        await harness.Service.RemoveStartupAnimationAsync(harness.AdminActor, CancellationToken.None);

        var actions = await harness.Db.AuditLogs
            .Where(log => log.EntityType == "Branding")
            .Select(log => log.Action)
            .ToListAsync();
        Assert.Contains("BRANDING.LOGO_UPLOAD", actions);
        Assert.Contains("BRANDING.LOGO_REPLACE", actions);
        Assert.Contains("BRANDING.LOGO_REMOVE", actions);
        Assert.Contains("BRANDING.ANIMATION_UPLOAD", actions);
        Assert.Contains("BRANDING.ANIMATION_REPLACE", actions);
        Assert.Contains("BRANDING.ANIMATION_REMOVE", actions);
    }

    [Fact]
    public async Task BrandingAudits_NeverContainStorageKeysOrPaths()
    {
        await using var harness = await BrandingHarness.CreateAsync();

        await UploadLogoAsync(harness, "one.png", "image/png");
        await UploadLogoAsync(harness, "two.jpg", "image/jpeg", JpegBytes);
        await harness.Service.RemoveLogoAsync(harness.AdminActor, CancellationToken.None);

        var audits = await harness.Db.AuditLogs
            .Where(log => log.EntityType == "Branding")
            .ToListAsync();
        Assert.NotEmpty(audits);
        foreach (var audit in audits)
        {
            foreach (var payload in new[] { audit.OldValueJson, audit.NewValueJson })
            {
                if (payload is null)
                {
                    continue;
                }

                // Storage keys always begin with the "branding/" prefix; safe
                // metadata (FileName/ContentType/FileSize) never does.
                Assert.DoesNotContain("branding/", payload);
                Assert.DoesNotContain("StorageKey", payload);
                Assert.DoesNotContain("Path", payload);
            }
        }
    }

    [Fact]
    public async Task BrandingResult_NeverExposesStorageInternalsOrUploaderIdentity()
    {
        await using var harness = await BrandingHarness.CreateAsync();
        await UploadLogoAsync(harness, "milk.png", "image/png");
        await UploadAnimationAsync(harness, "intro.gif", "image/gif");

        var result = await harness.Service.GetCurrentBrandingAsync(CancellationToken.None);
        var json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain("StorageKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Storage", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UploadedBy", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UserId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Audit", json, StringComparison.OrdinalIgnoreCase);
        // Storage keys embed the "branding/<kind>/" folder path; the public
        // URLs (…/api/v1/branding/logo) never do.
        Assert.DoesNotContain("branding/logo/", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("branding/startup-animation/", json, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private static Task<BrandingResult> UploadLogoAsync(
        BrandingHarness harness,
        string fileName,
        string declaredType,
        byte[]? bytes = null) =>
        harness.Service.UpsertLogoAsync(
            harness.AdminActor,
            new MemoryStream(bytes ?? PngBytes, writable: false),
            fileName,
            declaredType,
            CancellationToken.None);

    private static Task<BrandingResult> UploadAnimationAsync(
        BrandingHarness harness,
        string fileName,
        string declaredType,
        byte[]? bytes = null) =>
        harness.Service.UpsertStartupAnimationAsync(
            harness.AdminActor,
            new MemoryStream(bytes ?? Gif89aBytes, writable: false),
            fileName,
            declaredType,
            CancellationToken.None);

    private static byte[] OversizedBytes(byte[] prefix)
    {
        var bytes = new byte[1024 * 1024 + 1];
        prefix.AsSpan().CopyTo(bytes);
        return bytes;
    }

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01];
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x01];
    private static readonly byte[] WebpBytes =
    [
        0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00,
        0x57, 0x45, 0x42, 0x50, 0x01
    ];
    private static readonly byte[] Gif87aBytes = [0x47, 0x49, 0x46, 0x38, 0x37, 0x61, 0x01];
    private static readonly byte[] Gif89aBytes = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01];
    private static readonly byte[] ExeBytes = [0x4D, 0x5A, 0x90, 0x00];

    /// <summary>In-memory media storage recording saved/deleted keys so the
    /// replace/remove ordering and failure windows can be observed.</summary>
    private sealed class CapturingBrandingMediaStorage : IMediaStorage
    {
        private readonly Dictionary<string, (byte[] Bytes, string ContentType)> _blobs = new();

        public List<string> SavedKeys { get; } = [];
        public List<string> DeletedKeys { get; } = [];

        public bool ReturnMismatchedSize { get; set; }

        public bool FailNextSave { get; set; }

        public bool FailNextDelete { get; set; }

        public Task<StoredMediaResult> SaveAsync(
            string storageKey,
            Stream content,
            string contentType,
            CancellationToken cancellationToken)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new InvalidOperationException("simulated storage outage");
            }

            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            var bytes = buffer.ToArray();
            _blobs[storageKey] = (bytes, contentType);
            SavedKeys.Add(storageKey);
            return Task.FromResult(new StoredMediaResult(
                storageKey,
                contentType,
                ReturnMismatchedSize ? bytes.LongLength + 1 : bytes.LongLength));
        }

        public Task<StoredMediaContent> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
        {
            if (!_blobs.TryGetValue(storageKey, out var blob))
            {
                throw new NotFoundException("The branding asset content was not found.");
            }

            Stream content = new MemoryStream(blob.Bytes, writable: false);
            return Task.FromResult(new StoredMediaContent(content, blob.ContentType, blob.Bytes.LongLength));
        }

        public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken)
        {
            if (FailNextDelete)
            {
                FailNextDelete = false;
                DeletedKeys.Add(storageKey);
                throw new InvalidOperationException("simulated blob cleanup outage");
            }

            DeletedKeys.Add(storageKey);
            _blobs.Remove(storageKey);
            return Task.CompletedTask;
        }
    }

    private sealed class BrandingHarness(
        DoodhDirectDbContext db,
        User admin,
        CapturingBrandingMediaStorage storage,
        BrandingService service) : IAsyncDisposable
    {
        public DoodhDirectDbContext Db { get; } = db;
        public User Admin { get; } = admin;
        public CapturingBrandingMediaStorage Storage { get; } = storage;
        public BrandingService Service { get; } = service;

        public BrandingActor AdminActor => new(Admin.Id);

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public static async Task<BrandingHarness> CreateAsync(
            BrandingMediaOptions? options = null)
        {
            var db = CreateDb();
            var admin = new User(UserType.SystemAdministrator);
            db.Users.Add(admin);
            await db.SaveChangesAsync();
            var storage = new CapturingBrandingMediaStorage();
            var effectiveOptions = options ?? new BrandingMediaOptions();
            return new BrandingHarness(
                db,
                admin,
                storage,
                new BrandingService(
                    db,
                    new BrandingAssetValidator(Options.Create(effectiveOptions)),
                    storage,
                    new TestClock(DateTime.SpecifyKind(new DateTime(2026, 9, 28, 10, 0, 0), DateTimeKind.Unspecified))));
        }

        private static DoodhDirectDbContext CreateDb()
        {
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseInMemoryDatabase($"branding-tests-{Guid.NewGuid():N}")
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new DoodhDirectDbContext(options);
        }
    }
}

public sealed class BrandingControllerTests
{
    [Fact]
    public async Task PublicGet_ReturnsBrandingInOkEnvelope()
    {
        var service = new CapturingBrandingService
        {
            Result = new BrandingResult(
                new BrandingAssetResult(
                    BrandingAssetKind.Logo, "milk.png", "image/png", 9,
                    new DateTime(2026, 9, 28, 10, 0, 0), "/api/v1/branding/logo"),
                null)
        };
        var controller = new BrandingController(service);

        var response = await controller.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var envelope = Assert.IsType<ApiResponse<BrandingResult>>(ok.Value);
        Assert.True(envelope.Success);
        Assert.Equal("milk.png", envelope.Data!.Logo!.FileName);
        Assert.Equal("/api/v1/branding/logo", envelope.Data.Logo.Url);
        Assert.Null(envelope.Data.StartupAnimation);
    }

    [Fact]
    public async Task PublicLogo_WhenNotConfigured_ReturnsNotFound()
    {
        var controller = new BrandingController(new CapturingBrandingService());

        var response = await controller.GetLogo(CancellationToken.None);

        Assert.IsType<NotFoundResult>(response);
    }

    [Fact]
    public async Task PublicAnimation_WhenNotConfigured_ReturnsNotFound()
    {
        var controller = new BrandingController(new CapturingBrandingService());

        var response = await controller.GetStartupAnimation(CancellationToken.None);

        Assert.IsType<NotFoundResult>(response);
    }

    [Fact]
    public async Task PublicLogo_ServesBytesWithCacheRevalidationHeaders()
    {
        await using var stream = new MemoryStream([0x89, 0x50, 0x4E, 0x47]);
        var service = new CapturingBrandingService
        {
            Media = new BrandingMediaContent(
                stream, "image/png", 4, "\"abc123\"", new DateTimeOffset(2026, 9, 28, 4, 30, 0, TimeSpan.Zero))
        };
        var controller = new BrandingController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var response = await controller.GetLogo(CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(response);
        Assert.Equal("image/png", file.ContentType);
        Assert.True(file.EnableRangeProcessing);
        Assert.Equal("\"abc123\"", controller.Response.Headers.ETag.ToString());
        Assert.Equal("Mon, 28 Sep 2026 04:30:00 GMT", controller.Response.Headers.LastModified.ToString());
        Assert.Equal(4, controller.Response.ContentLength);
    }

    [Fact]
    public async Task AdminUpsertLogo_MissingFile_IsValidationFailure()
    {
        var controller = new BrandingAdministrationController(new CapturingBrandingService())
        {
            ControllerContext = ControllerContextWithUser("42")
        };

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            controller.UpsertLogo(new BrandingUploadForm { File = null }, CancellationToken.None));
    }

    [Fact]
    public async Task AdminUpsertLogo_ForwardsActorAndFileMetadata()
    {
        var service = new CapturingBrandingService
        {
            Result = new BrandingResult(null, null)
        };
        var controller = new BrandingAdministrationController(service)
        {
            ControllerContext = ControllerContextWithUser("42")
        };
        using var content = new MemoryStream([0x89, 0x50, 0x4E, 0x47]);
        var form = new BrandingUploadForm
        {
            File = new FormFile(content, 0, content.Length, "File", "milk.png")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/png"
            }
        };

        await controller.UpsertLogo(form, CancellationToken.None);

        Assert.Equal(42, service.LastActor!.UserId);
        Assert.Equal("milk.png", service.LastFileName);
        Assert.Equal("image/png", service.LastDeclaredContentType);
    }

    [Fact]
    public async Task AdminMutations_WithMissingUserClaim_ThrowUnauthorized()
    {
        var controller = new BrandingAdministrationController(new CapturingBrandingService())
        {
            ControllerContext = ControllerContextWithUser(null)
        };

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            controller.RemoveLogo(CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            controller.RemoveStartupAnimation(CancellationToken.None));
    }

    [Theory]
    [InlineData(nameof(BrandingAdministrationController.Get), AuthorizationCodes.SetupBrandingRead)]
    [InlineData(nameof(BrandingAdministrationController.UpsertLogo), AuthorizationCodes.SetupBrandingManage)]
    [InlineData(nameof(BrandingAdministrationController.RemoveLogo), AuthorizationCodes.SetupBrandingManage)]
    [InlineData(nameof(BrandingAdministrationController.UpsertStartupAnimation), AuthorizationCodes.SetupBrandingManage)]
    [InlineData(nameof(BrandingAdministrationController.RemoveStartupAnimation), AuthorizationCodes.SetupBrandingManage)]
    public void AdminRoutes_RequireExpectedPermission(string methodName, string permission)
    {
        var method = typeof(BrandingAdministrationController).GetMethod(methodName);

        var authorize = method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Concat(typeof(BrandingAdministrationController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());

        Assert.Contains(authorize, attribute => attribute.Policy == $"permission:{permission}");
        Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
    }

    [Theory]
    [InlineData(nameof(BrandingController.Get))]
    [InlineData(nameof(BrandingController.GetLogo))]
    [InlineData(nameof(BrandingController.GetStartupAnimation))]
    public void PublicRoutes_AreExplicitlyAnonymous(string methodName)
    {
        var method = typeof(BrandingController).GetMethod(methodName);

        Assert.NotEmpty(method!.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
    }

    [Fact]
    public void PublicController_HasNoPermissionPolicy()
    {
        var authorize = typeof(BrandingController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true);

        Assert.Empty(authorize);
    }

    private static ControllerContext ControllerContextWithUser(string? userId)
    {
        var claims = new List<Claim>();
        if (userId is not null)
        {
            claims.Add(new Claim("user_id", userId));
        }

        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
            }
        };
    }

    private sealed class CapturingBrandingService : IBrandingService
    {
        public BrandingResult Result { get; init; } = new(null, null);

        public BrandingMediaContent? Media { get; init; }

        public BrandingActor? LastActor { get; private set; }

        public string? LastFileName { get; private set; }

        public string? LastDeclaredContentType { get; private set; }

        public Task<BrandingResult> GetCurrentBrandingAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result);

        public Task<BrandingResult> UpsertLogoAsync(
            BrandingActor actor, Stream content, string fileName, string? declaredContentType,
            CancellationToken cancellationToken)
        {
            LastActor = actor;
            LastFileName = fileName;
            LastDeclaredContentType = declaredContentType;
            return Task.FromResult(Result);
        }

        public Task<BrandingResult> RemoveLogoAsync(BrandingActor actor, CancellationToken cancellationToken)
        {
            LastActor = actor;
            return Task.FromResult(Result);
        }

        public Task<BrandingResult> UpsertStartupAnimationAsync(
            BrandingActor actor, Stream content, string fileName, string? declaredContentType,
            CancellationToken cancellationToken)
        {
            LastActor = actor;
            LastFileName = fileName;
            LastDeclaredContentType = declaredContentType;
            return Task.FromResult(Result);
        }

        public Task<BrandingResult> RemoveStartupAnimationAsync(BrandingActor actor, CancellationToken cancellationToken)
        {
            LastActor = actor;
            return Task.FromResult(Result);
        }

        public Task<BrandingMediaContent?> OpenLogoAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Media);

        public Task<BrandingMediaContent?> OpenStartupAnimationAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Media);
    }
}

public sealed class BrandingSeedTests
{
    [Fact]
    public async Task BrandingPermissions_AreSeededOnlyForOwnerAndSystemAdmin()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
        var service = new IdentitySeedService(db);

        await service.SeedAsync(CancellationToken.None);

        var assignments = await db.RolePermissions
            .Select(assignment => new { RoleCode = assignment.Role.Code, PermissionCode = assignment.Permission.Code })
            .ToListAsync();

        var systemAdmin = assignments
            .Where(assignment => assignment.RoleCode == AuthorizationCodes.SystemAdmin)
            .Select(assignment => assignment.PermissionCode)
            .ToHashSet();
        Assert.Contains(AuthorizationCodes.SetupBrandingRead, systemAdmin);
        Assert.Contains(AuthorizationCodes.SetupBrandingManage, systemAdmin);

        var owner = assignments
            .Where(assignment => assignment.RoleCode == AuthorizationCodes.Owner)
            .Select(assignment => assignment.PermissionCode)
            .ToList();
        Assert.Equal(
            AuthorizationCodes.Permissions.Keys.OrderBy(code => code),
            owner.OrderBy(code => code));

        foreach (var restrictedRole in new[]
        {
            AuthorizationCodes.Customer,
            AuthorizationCodes.DairyManager,
            AuthorizationCodes.Accountant,
            AuthorizationCodes.DeliveryManager,
            AuthorizationCodes.CustomerSupport,
            AuthorizationCodes.DeliveryStaff
        })
        {
            var roleCodes = assignments
                .Where(assignment => assignment.RoleCode == restrictedRole)
                .Select(assignment => assignment.PermissionCode);
            Assert.DoesNotContain(AuthorizationCodes.SetupBrandingRead, roleCodes);
            Assert.DoesNotContain(AuthorizationCodes.SetupBrandingManage, roleCodes);
        }
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DoodhDirectDbContext>(options => options            .UseInMemoryDatabase($"branding-seed-tests-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        return services.BuildServiceProvider();
    }
}

public sealed class BrandingUniqueKindTests
{
    [Fact]
    public async Task UniqueAssetKindIndex_EnforcesOneActiveAssetPerKind()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new DoodhDirectDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new User(UserType.SystemAdministrator));
            await db.SaveChangesAsync();
            var userId = await db.Users.Select(user => user.Id).SingleAsync();

            db.BrandingAssets.Add(new BrandingAsset(
                BrandingAssetKind.Logo, "branding/logo/one.png", "one.png", "image/png", 9, userId,
                new DateTime(2026, 9, 28, 10, 0, 0)));
            await db.SaveChangesAsync();

            // A second active logo violates the singleton-per-kind invariant.
            db.BrandingAssets.Add(new BrandingAsset(
                BrandingAssetKind.Logo, "branding/logo/two.png", "two.png", "image/png", 9, userId,
                new DateTime(2026, 9, 28, 10, 5, 0)));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

            // The other kind remains insertable.
            db.Entry(db.BrandingAssets.Local.Single(asset => asset.AssetKind == BrandingAssetKind.Logo && asset.FileName == "two.png"))
                .State = EntityState.Detached;
            db.BrandingAssets.Add(new BrandingAsset(
                BrandingAssetKind.StartupAnimation, "branding/startup-animation/one.gif", "one.gif", "image/gif", 9, userId,
                new DateTime(2026, 9, 28, 10, 10, 0)));
            await db.SaveChangesAsync();
            Assert.Equal(2, await db.BrandingAssets.CountAsync());
        }
    }
}
