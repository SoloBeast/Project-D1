using System.Reflection;
using System.Security.Claims;
using DoodhDirect.Api.Controllers;
using DoodhDirect.Application.Cameras;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Cameras;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class CamerasControllerTests
{
    [Fact]
    public void PublicController_ExposesOnlyExpectedRoutesAndPolicy()
    {
        var controllerType = typeof(PublicCamerasController);
        Assert.Equal(
            "api/v1/cameras/public",
            Assert.Single(controllerType.GetCustomAttributes<RouteAttribute>()).Template);
        Assert.Equal(
            $"permission:{AuthorizationCodes.CamerasViewPublic}",
            Assert.Single(controllerType.GetCustomAttributes<AuthorizeAttribute>()).Policy);

        AssertRoutes(controllerType, new Dictionary<string, (string Method, string? Template)>
        {
            [nameof(PublicCamerasController.Get)] = ("GET", null),
            [nameof(PublicCamerasController.GetStream)] = ("GET", "{cameraId:guid}/stream")
        });
    }

    [Fact]
    public void AdminController_ExposesOnlyExpectedRoutes()
    {
        var controllerType = typeof(AdminCamerasController);
        Assert.Equal(
            "api/v1/admin/cameras",
            Assert.Single(controllerType.GetCustomAttributes<RouteAttribute>()).Template);

        AssertRoutes(controllerType, new Dictionary<string, (string Method, string? Template)>
        {
            [nameof(AdminCamerasController.Get)] = ("GET", null),
            [nameof(AdminCamerasController.Create)] = ("POST", null),
            [nameof(AdminCamerasController.Update)] = ("PATCH", "{cameraId:guid}")
        });
    }

    [Theory]
    [InlineData(nameof(AdminCamerasController.Get), AuthorizationCodes.CamerasRead)]
    [InlineData(nameof(AdminCamerasController.Create), AuthorizationCodes.CamerasManage)]
    [InlineData(nameof(AdminCamerasController.Update), AuthorizationCodes.CamerasManage)]
    public void AdminAction_RequiresExpectedPermissionAndIsNotAnonymous(
        string methodName,
        string permission)
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(
            typeof(AdminCamerasController).GetMethod(methodName));

        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal($"permission:{permission}", authorize.Policy);
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>());
    }

    [Fact]
    public async Task PublicActions_ForwardRequestsAndReturnSuccessEnvelopes()
    {
        var service = new CapturingCameraService();
        var controller = new PublicCamerasController(service);
        var cameraId = Guid.NewGuid();

        Assert.Same(
            service.PublicCameras,
            AssertSuccess(await controller.Get(CancellationToken.None), StatusCodes.Status200OK));
        Assert.True(service.GetPublicCalled);

        Assert.Same(
            service.PublicStream,
            AssertSuccess(
                await controller.GetStream(cameraId, CancellationToken.None),
                StatusCodes.Status200OK));
        Assert.Equal(cameraId, service.PublicStreamCameraId);
    }

    [Fact]
    public async Task AdminActions_TranslateClaimsForwardRequestsAndReturnSuccessEnvelopes()
    {
        var service = new CapturingCameraService();
        var controller = CreateAdminController(
            service,
            new Claim("user_id", "73"),
            new Claim(AuthorizationCodes.BranchClaim, "11"),
            new Claim(AuthorizationCodes.BranchClaim, "11"),
            new Claim(AuthorizationCodes.BranchClaim, "invalid"),
            new Claim(AuthorizationCodes.BranchClaim, "29"),
            new Claim(AuthorizationCodes.PermissionClaim, AuthorizationCodes.GlobalAccess));
        var cameraId = Guid.NewGuid();
        var createRequest = new CreateCameraRequest(
            11,
            "YARD",
            "Dairy Yard",
            true,
            1,
            CameraStreamProtocol.Hls,
            "PROVIDER",
            "opaque-create");
        var updateRequest = new UpdateCameraRequest(
            29,
            "YARD-NORTH",
            "North Dairy Yard",
            true,
            true,
            2,
            CameraStreamProtocol.Hls,
            "PROVIDER",
            "opaque-update");

        Assert.Same(
            service.ManagedCameras,
            AssertSuccess(await controller.Get(11, CancellationToken.None), StatusCodes.Status200OK));
        AssertActor(service.GetManagedCall!.Value.Actor, 73, true, 11, 29);
        Assert.Equal(11, service.GetManagedCall.Value.BranchId);

        Assert.Same(
            service.ManagedCamera,
            AssertSuccess(
                await controller.Create(createRequest, CancellationToken.None),
                StatusCodes.Status201Created,
                "Camera metadata created."));
        AssertActor(service.CreateCall!.Value.Actor, 73, true, 11, 29);
        Assert.Same(createRequest, service.CreateCall.Value.Request);

        Assert.Same(
            service.ManagedCamera,
            AssertSuccess(
                await controller.Update(cameraId, updateRequest, CancellationToken.None),
                StatusCodes.Status200OK,
                "Camera metadata updated."));
        AssertActor(service.UpdateCall!.Value.Actor, 73, true, 11, 29);
        Assert.Equal(cameraId, service.UpdateCall.Value.CameraId);
        Assert.Same(updateRequest, service.UpdateCall.Value.Request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    public async Task AdminGet_WithMissingOrInvalidUserIdClaim_IsUnauthorized(string? userId)
    {
        var service = new CapturingCameraService();
        var claims = userId is null ? [] : new[] { new Claim("user_id", userId) };
        var controller = CreateAdminController(service, claims);

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            controller.Get(null, CancellationToken.None));

        Assert.Null(service.GetManagedCall);
    }

    private static void AssertRoutes(
        Type controllerType,
        IReadOnlyDictionary<string, (string Method, string? Template)> expected)
    {
        var actions = controllerType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<HttpMethodAttribute>() is not null)
            .ToArray();

        Assert.Equal(expected.Count, actions.Length);
        foreach (var action in actions)
        {
            var attribute = Assert.IsAssignableFrom<HttpMethodAttribute>(
                action.GetCustomAttribute<HttpMethodAttribute>());
            var contract = expected[action.Name];
            Assert.Equal(contract.Method, Assert.Single(attribute.HttpMethods));
            Assert.Equal(contract.Template, attribute.Template);
        }
    }

    private static T AssertSuccess<T>(
        ActionResult<ApiResponse<T>> response,
        int expectedStatusCode,
        string? expectedMessage = null)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(response.Result);
        Assert.Equal(expectedStatusCode, objectResult.StatusCode);
        var envelope = Assert.IsType<ApiResponse<T>>(objectResult.Value);
        Assert.True(envelope.Success);
        Assert.Equal(expectedMessage, envelope.Message);
        Assert.Empty(envelope.Errors);
        return Assert.IsAssignableFrom<T>(envelope.Data);
    }

    private static void AssertActor(
        CameraActor actor,
        long userId,
        bool hasGlobalAccess,
        params long[] branchIds)
    {
        Assert.Equal(userId, actor.UserId);
        Assert.Equal(hasGlobalAccess, actor.HasGlobalAccess);
        Assert.Equal(branchIds.OrderBy(x => x), actor.BranchIds.OrderBy(x => x));
    }

    private static AdminCamerasController CreateAdminController(
        ICameraService service,
        params Claim[] claims)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"))
        };
        return new AdminCamerasController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class CapturingCameraService : ICameraService
    {
        private static readonly DateTime Timestamp = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

        public IReadOnlyCollection<PublicCameraResult> PublicCameras { get; } =
            [new PublicCameraResult(Guid.NewGuid(), "Dairy Yard", 1, true)];

        public PublicCameraStreamResult PublicStream { get; } = new(
            Guid.NewGuid(),
            "Dairy Yard",
            new CameraStreamDescriptor(
                CameraStreamProtocol.Hls,
                new Uri("https://streams.example/live.m3u8"),
                new DateTimeOffset(Timestamp.AddMinutes(5), TimeSpan.Zero),
                false));

        public ManagedCameraResult ManagedCamera { get; } = new(
            Guid.NewGuid(),
            11,
            "Main Branch",
            "YARD",
            "Dairy Yard",
            true,
            true,
            1,
            CameraStreamProtocol.Hls,
            "PROVIDER",
            "opaque-reference",
            Timestamp,
            Timestamp);

        public IReadOnlyCollection<ManagedCameraResult> ManagedCameras { get; }
        public bool GetPublicCalled { get; private set; }
        public Guid? PublicStreamCameraId { get; private set; }
        public (CameraActor Actor, long? BranchId)? GetManagedCall { get; private set; }
        public (CameraActor Actor, CreateCameraRequest Request)? CreateCall { get; private set; }
        public (CameraActor Actor, Guid CameraId, UpdateCameraRequest Request)? UpdateCall { get; private set; }

        public CapturingCameraService()
        {
            ManagedCameras = [ManagedCamera];
        }

        public Task<IReadOnlyCollection<PublicCameraResult>> GetPublicAsync(
            CancellationToken cancellationToken)
        {
            GetPublicCalled = true;
            return Task.FromResult(PublicCameras);
        }

        public Task<PublicCameraStreamResult> GetPublicStreamAsync(
            Guid cameraId,
            CancellationToken cancellationToken)
        {
            PublicStreamCameraId = cameraId;
            return Task.FromResult(PublicStream);
        }

        public Task<IReadOnlyCollection<ManagedCameraResult>> GetManagedAsync(
            CameraActor actor,
            long? branchId,
            CancellationToken cancellationToken)
        {
            GetManagedCall = (actor, branchId);
            return Task.FromResult(ManagedCameras);
        }

        public Task<ManagedCameraResult> CreateAsync(
            CameraActor actor,
            CreateCameraRequest request,
            CancellationToken cancellationToken)
        {
            CreateCall = (actor, request);
            return Task.FromResult(ManagedCamera);
        }

        public Task<ManagedCameraResult> UpdateAsync(
            CameraActor actor,
            Guid cameraId,
            UpdateCameraRequest request,
            CancellationToken cancellationToken)
        {
            UpdateCall = (actor, cameraId, request);
            return Task.FromResult(ManagedCamera);
        }
    }
}
