using DoodhDirect.Application.Cameras;
using DoodhDirect.Application.Common;
using DoodhDirect.Domain.Cameras;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Infrastructure.Cameras;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class CameraServiceTests
{
    [Fact]
    public async Task GetPublic_ReturnsOnlyActivePublicCamerasInDisplayOrder()
    {
        await using var fixture = await CameraFixture.CreateAsync();
        var first = await fixture.AddCameraAsync(fixture.MainBranchId, "PUBLIC-2", "Second", true, true, 2);
        var second = await fixture.AddCameraAsync(fixture.MainBranchId, "PUBLIC-1", "First", true, true, 1);
        await fixture.AddCameraAsync(fixture.MainBranchId, "PRIVATE", "Private", false, true, 0);
        await fixture.AddCameraAsync(fixture.MainBranchId, "INACTIVE", "Inactive", true, false, 0);
        fixture.Gateway.UnavailableCameraIds.Add(first.PublicId);

        var result = (await fixture.Service.GetPublicAsync(CancellationToken.None)).ToArray();

        Assert.Equal([second.PublicId, first.PublicId], result.Select(x => x.CameraId));
        Assert.True(result[0].IsAvailable);
        Assert.False(result[1].IsAvailable);
        Assert.All(result, camera =>
        {
            Assert.DoesNotContain("PRIVATE", camera.DisplayName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("INACTIVE", camera.DisplayName, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task GetPublicStream_DoesNotExposePrivateInactiveOrUnknownCameras()
    {
        await using var fixture = await CameraFixture.CreateAsync();
        var privateCamera = await fixture.AddCameraAsync(fixture.MainBranchId, "PRIVATE", "Private", false, true, 0);
        var inactiveCamera = await fixture.AddCameraAsync(fixture.MainBranchId, "INACTIVE", "Inactive", true, false, 0);

        await Assert.ThrowsAsync<NotFoundException>(() => fixture.Service.GetPublicStreamAsync(
            privateCamera.PublicId,
            CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => fixture.Service.GetPublicStreamAsync(
            inactiveCamera.PublicId,
            CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => fixture.Service.GetPublicStreamAsync(
            Guid.NewGuid(),
            CancellationToken.None));
        Assert.Empty(fixture.Gateway.IssuedRequests);
    }

    [Fact]
    public async Task GetPublicStream_WhenGatewayCannotIssue_FailsClosed()
    {
        await using var fixture = await CameraFixture.CreateAsync();
        var camera = await fixture.AddCameraAsync(fixture.MainBranchId, "PUBLIC", "Public", true, true, 0);
        fixture.Gateway.CanIssueStreams = false;

        await Assert.ThrowsAsync<CameraStreamUnavailableException>(() => fixture.Service.GetPublicStreamAsync(
            camera.PublicId,
            CancellationToken.None));

        Assert.Empty(fixture.Gateway.IssuedRequests);
    }

    [Fact]
    public async Task GetManaged_FiltersScopedActorsAndAllowsGlobalActors()
    {
        await using var fixture = await CameraFixture.CreateAsync();
        var main = await fixture.AddCameraAsync(fixture.MainBranchId, "MAIN", "Main", true, true, 0);
        var north = await fixture.AddCameraAsync(fixture.NorthBranchId, "NORTH", "North", true, true, 0);
        var scopedActor = new CameraActor(7, new HashSet<long> { fixture.MainBranchId }, false);
        var globalActor = new CameraActor(8, new HashSet<long>(), true);

        var scoped = await fixture.Service.GetManagedAsync(scopedActor, null, CancellationToken.None);
        var global = await fixture.Service.GetManagedAsync(globalActor, null, CancellationToken.None);

        Assert.Equal(main.PublicId, Assert.Single(scoped).CameraId);
        Assert.Equal(
            new[] { main.PublicId, north.PublicId }.OrderBy(x => x),
            global.Select(x => x.CameraId).OrderBy(x => x));
        await Assert.ThrowsAsync<NotFoundException>(() => fixture.Service.GetManagedAsync(
            scopedActor,
            fixture.NorthBranchId,
            CancellationToken.None));
    }

    [Fact]
    public async Task Update_RequiresAccessToBothSourceAndDestinationBranches()
    {
        await using var fixture = await CameraFixture.CreateAsync();
        var camera = await fixture.AddCameraAsync(fixture.MainBranchId, "YARD", "Yard", true, true, 0);
        var sourceOnly = new CameraActor(7, new HashSet<long> { fixture.MainBranchId }, false);
        var destinationOnly = new CameraActor(8, new HashSet<long> { fixture.NorthBranchId }, false);
        var request = UpdateRequest(fixture.NorthBranchId, "YARD-NORTH");

        await Assert.ThrowsAsync<NotFoundException>(() => fixture.Service.UpdateAsync(
            sourceOnly,
            camera.PublicId,
            request,
            CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => fixture.Service.UpdateAsync(
            destinationOnly,
            camera.PublicId,
            request,
            CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.Cameras.AsNoTracking().SingleAsync(x => x.PublicId == camera.PublicId);
        Assert.Equal(fixture.MainBranchId, persisted.BranchId);
        Assert.Equal("YARD", persisted.InternalIdentifier);
    }

    [Fact]
    public async Task Create_RejectsDuplicateIdentifierAndAddressShapedStreamReference()
    {
        await using var fixture = await CameraFixture.CreateAsync();
        var actor = new CameraActor(7, new HashSet<long> { fixture.MainBranchId }, false);
        await fixture.Service.CreateAsync(
            actor,
            CreateRequest(fixture.MainBranchId, "YARD", "opaque-stream-1"),
            CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => fixture.Service.CreateAsync(
            actor,
            CreateRequest(fixture.MainBranchId, " yard ", "opaque-stream-2"),
            CancellationToken.None));
        var validation = await Assert.ThrowsAsync<ValidationAppException>(() => fixture.Service.CreateAsync(
            actor,
            CreateRequest(fixture.MainBranchId, "SHED", "https://internal.example/live.m3u8"),
            CancellationToken.None));

        Assert.Equal("providerStreamReference", validation.Field);
    }

    [Fact]
    public async Task CreateAndUpdate_AuditMetadataWithoutProviderStreamReference()
    {
        await using var fixture = await CameraFixture.CreateAsync();
        var actor = new CameraActor(17, new HashSet<long> { fixture.MainBranchId }, false);
        var created = await fixture.Service.CreateAsync(
            actor,
            CreateRequest(fixture.MainBranchId, "YARD", "secret-shaped-opaque-create"),
            CancellationToken.None);
        await fixture.Service.UpdateAsync(
            actor,
            created.CameraId,
            UpdateRequest(fixture.MainBranchId, "YARD", "secret-shaped-opaque-update"),
            CancellationToken.None);

        var audits = await fixture.Db.AuditLogs
            .AsNoTracking()
            .Where(x => x.EntityType == "Camera" && x.EntityId == created.CameraId.ToString())
            .OrderBy(x => x.Id)
            .ToArrayAsync();

        Assert.Equal(["CAMERA.CREATE", "CAMERA.UPDATE"], audits.Select(x => x.Action));
        Assert.All(audits, audit =>
        {
            Assert.DoesNotContain("ProviderStreamReference", audit.OldValueJson ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("ProviderStreamReference", audit.NewValueJson ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-shaped-opaque", audit.OldValueJson ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-shaped-opaque", audit.NewValueJson ?? string.Empty, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task DevelopmentGateway_IssuesShortLivedHttpsHlsDescriptorOnlyWhenExplicitlyConfigured()
    {
        var now = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Unspecified);
        var timeProvider = new TestIndiaTimeProvider(new TestClock(now));
        var gateway = new DevelopmentCameraStreamGateway(
            Options.Create(new CameraStreamOptions
            {
                Provider = CameraStreamOptions.DevelopmentMockProvider,
                DevelopmentHlsPlaybackUrl = "https://development.example/live.m3u8",
                DescriptorLifetimeMinutes = 4
            }),
            timeProvider);
        var request = new CameraStreamRequest(
            Guid.NewGuid(),
            CameraStreamProtocol.Hls,
            CameraStreamOptions.DevelopmentMockProvider,
            "opaque-reference");

        var descriptor = await gateway.IssueAsync(request, CancellationToken.None);

        Assert.Equal(CameraStreamProtocol.Hls, descriptor.Protocol);
        Assert.Equal(new Uri("https://development.example/live.m3u8"), descriptor.PlaybackUri);
        Assert.Equal(
            new DateTimeOffset(timeProvider.ToUtc(now.AddMinutes(4)), TimeSpan.Zero),
            descriptor.ExpiresAtUtc);
        Assert.True(descriptor.IsDevelopmentStream);
    }

    [Theory]
    [InlineData(CameraStreamOptions.UnconfiguredProvider, "https://development.example/live.m3u8")]
    [InlineData(CameraStreamOptions.DevelopmentMockProvider, "http://development.example/live.m3u8")]
    [InlineData(CameraStreamOptions.DevelopmentMockProvider, null)]
    public async Task DevelopmentGateway_InvalidOrNonDevelopmentConfiguration_FailsClosed(
        string provider,
        string? playbackUrl)
    {
        var gateway = new DevelopmentCameraStreamGateway(
            Options.Create(new CameraStreamOptions
            {
                Provider = provider,
                DevelopmentHlsPlaybackUrl = playbackUrl
            }),
            new TestClock(DateTime.UtcNow));
        var request = new CameraStreamRequest(
            Guid.NewGuid(),
            CameraStreamProtocol.Hls,
            CameraStreamOptions.DevelopmentMockProvider,
            "opaque-reference");

        Assert.False(gateway.CanIssue(request.Protocol, request.ProviderCode));
        Assert.False(await gateway.IsAvailableAsync(request, CancellationToken.None));
        await Assert.ThrowsAsync<CameraStreamUnavailableException>(() =>
            gateway.IssueAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UnconfiguredGateway_AlwaysFailsClosed()
    {
        var gateway = new UnconfiguredCameraStreamGateway();
        var request = new CameraStreamRequest(
            Guid.NewGuid(),
            CameraStreamProtocol.Hls,
            "ANY",
            "opaque-reference");

        Assert.False(gateway.CanIssue(request.Protocol, request.ProviderCode));
        Assert.False(await gateway.IsAvailableAsync(request, CancellationToken.None));
        await Assert.ThrowsAsync<CameraStreamUnavailableException>(() =>
            gateway.IssueAsync(request, CancellationToken.None));
    }

    private static CreateCameraRequest CreateRequest(
        long branchId,
        string identifier,
        string providerStreamReference) => new(
        branchId,
        identifier,
        "Dairy Yard",
        true,
        1,
        CameraStreamProtocol.Hls,
        "PROVIDER",
        providerStreamReference);

    private static UpdateCameraRequest UpdateRequest(
        long branchId,
        string identifier,
        string providerStreamReference = "opaque-stream-updated") => new(
        branchId,
        identifier,
        "Updated Dairy Yard",
        true,
        true,
        2,
        CameraStreamProtocol.Hls,
        "PROVIDER",
        providerStreamReference);

    private sealed class CameraFixture : IAsyncDisposable
    {
        private CameraFixture(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            CapturingCameraStreamGateway gateway,
            long mainBranchId,
            long northBranchId)
        {
            Connection = connection;
            Db = db;
            Gateway = gateway;
            MainBranchId = mainBranchId;
            NorthBranchId = northBranchId;
            var clock = new TestClock(
                new DateTime(2026, 8, 20, 3, 0, 0, DateTimeKind.Unspecified));
            Service = new CameraService(
                db,
                gateway,
                new TestIndiaTimeProvider(clock));
        }

        public SqliteConnection Connection { get; }
        public DoodhDirectDbContext Db { get; }
        public CapturingCameraStreamGateway Gateway { get; }
        public CameraService Service { get; }
        public long MainBranchId { get; }
        public long NorthBranchId { get; }

        public static async Task<CameraFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var main = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var north = new Branch("NORTH", "North Branch", "Bengaluru", "Karnataka", 13.0358m, 77.5970m);
            db.Branches.AddRange(main, north);
            await db.SaveChangesAsync();
            return new CameraFixture(connection, db, new CapturingCameraStreamGateway(), main.Id, north.Id);
        }

        public async Task<Camera> AddCameraAsync(
            long branchId,
            string identifier,
            string displayName,
            bool isPublic,
            bool isActive,
            int displayOrder)
        {
            var camera = new Camera(branchId, identifier, displayName, isPublic, displayOrder);
            if (!isActive)
            {
                camera.Deactivate();
            }
            Db.Cameras.Add(camera);
            await Db.SaveChangesAsync();
            Db.CameraStreams.Add(new CameraStream(
                camera.Id,
                CameraStreamProtocol.Hls,
                "PROVIDER",
                $"opaque-{identifier.ToLowerInvariant()}"));
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return camera;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class CapturingCameraStreamGateway : ICameraStreamGateway
    {
        public bool CanIssueStreams { get; set; } = true;
        public HashSet<Guid> UnavailableCameraIds { get; } = [];
        public List<CameraStreamRequest> IssuedRequests { get; } = [];

        public bool CanIssue(CameraStreamProtocol protocol, string providerCode) => CanIssueStreams;

        public Task<bool> IsAvailableAsync(
            CameraStreamRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(!UnavailableCameraIds.Contains(request.CameraId));

        public Task<CameraStreamDescriptor> IssueAsync(
            CameraStreamRequest request,
            CancellationToken cancellationToken)
        {
            IssuedRequests.Add(request);
            return Task.FromResult(new CameraStreamDescriptor(
                request.Protocol,
                new Uri("https://streams.example/live.m3u8"),
                DateTimeOffset.UtcNow.AddMinutes(5),
                false));
        }
    }
}
