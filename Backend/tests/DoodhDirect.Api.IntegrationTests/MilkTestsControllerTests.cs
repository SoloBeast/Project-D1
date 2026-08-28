using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using DoodhDirect.Api.Authorization;
using DoodhDirect.Api.Controllers;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.MilkTesting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class MilkTestsControllerTests
{
    private const long MaximumTransportUploadSize = 50L * 1024L * 1024L;

    [Theory]
    [InlineData(typeof(CustomerDeliveryMilkTestsController), "api/v1/deliveries/{deliveryId:guid}/milk-test")]
    [InlineData(typeof(DeliveryStaffMilkTestsController), "api/v1/delivery/{deliveryId:guid}/milk-test")]
    [InlineData(typeof(MilkTestsController), "api/v1/milk-tests")]
    public void Controller_UsesExpectedRoute(Type controllerType, string expectedRoute)
    {
        var route = Assert.Single(controllerType.GetCustomAttributes<RouteAttribute>());

        Assert.Equal(expectedRoute, route.Template);
    }

    [Theory]
    [InlineData(typeof(CustomerDeliveryMilkTestsController), nameof(CustomerDeliveryMilkTestsController.RequestTest), AuthorizationCodes.MilkTestsRequestOwn)]
    [InlineData(typeof(CustomerDeliveryMilkTestsController), nameof(CustomerDeliveryMilkTestsController.Get), AuthorizationCodes.MilkTestsReadOwn)]
    [InlineData(typeof(MilkTestsController), nameof(MilkTestsController.UploadImage), AuthorizationCodes.MilkTestsOperateAssigned)]
    [InlineData(typeof(MilkTestsController), nameof(MilkTestsController.DeleteImage), AuthorizationCodes.MilkTestsOperateAssigned)]
    [InlineData(typeof(MilkTestsController), nameof(MilkTestsController.ReplaceImage), AuthorizationCodes.MilkTestsOperateAssigned)]
    [InlineData(typeof(MilkTestsController), nameof(MilkTestsController.ReplaceImageAsCustomer), AuthorizationCodes.MilkTestsDecideOwn)]
    [InlineData(typeof(MilkTestsController), nameof(MilkTestsController.Complete), AuthorizationCodes.MilkTestsOperateAssigned)]
    [InlineData(typeof(MilkTestsController), nameof(MilkTestsController.Confirm), AuthorizationCodes.MilkTestsDecideOwn)]
    [InlineData(typeof(MilkTestsController), nameof(MilkTestsController.Reject), AuthorizationCodes.MilkTestsDecideOwn)]
    public void Action_RequiresExpectedPermission(
        Type controllerType,
        string methodName,
        string permission)
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(
            controllerType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public));
        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>(inherit: false));

        Assert.Equal($"permission:{permission}", authorize.Policy);
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    [Fact]
    public void OpenImage_RequiresAnyPermission_OfReadOwnOrOperateAssigned()
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(
            typeof(MilkTestsController).GetMethod(
                nameof(MilkTestsController.OpenImage),
                BindingFlags.Instance | BindingFlags.Public));
        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>(inherit: false));

        Assert.Equal(
            AuthorizationPolicyNames.AnyPermission(
                AuthorizationCodes.MilkTestsReadOwn,
                AuthorizationCodes.MilkTestsOperateAssigned),
            authorize.Policy);
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    [Fact]
    public void StaffController_RequiresAssignedOperationPermission()
    {
        var authorize = Assert.Single(
            typeof(DeliveryStaffMilkTestsController)
                .GetCustomAttributes<AuthorizeAttribute>(inherit: false));

        Assert.Equal(
            $"permission:{AuthorizationCodes.MilkTestsOperateAssigned}",
            authorize.Policy);
    }

    [Fact]
    public void UploadImage_UsesMultipartBindingAndTransportLimits()
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(
            typeof(MilkTestsController).GetMethod(
                nameof(MilkTestsController.UploadImage),
                BindingFlags.Instance | BindingFlags.Public));
        var consumes = Assert.Single(method.GetCustomAttributes<ConsumesAttribute>());
        var requestLimit = Assert.Single(method.GetCustomAttributes<RequestSizeLimitAttribute>());
        var formLimit = Assert.Single(method.GetCustomAttributes<RequestFormLimitsAttribute>());
        var formParameter = Assert.Single(method.GetParameters(), parameter =>
            parameter.ParameterType == typeof(MilkTestImageUploadForm));

        Assert.Equal(["multipart/form-data"], consumes.ContentTypes);
        Assert.Equal(
            MaximumTransportUploadSize,
            ((IRequestSizeLimitMetadata)requestLimit).MaxRequestBodySize);
        Assert.Equal(MaximumTransportUploadSize, formLimit.MultipartBodyLengthLimit);
        Assert.NotNull(formParameter.GetCustomAttribute<FromFormAttribute>());
    }

    [Fact]
    public async Task UploadImage_WithoutImage_IsValidationError()
    {
        var service = new RecordingMilkTestService();
        var controller = CreateController(service);

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            controller.UploadImage(
                Guid.NewGuid(),
                new MilkTestImageUploadForm(),
                CancellationToken.None));

        Assert.Equal("image", exception.Field);
        Assert.Equal("An image is required.", exception.Message);
        Assert.False(service.UploadCalled);
    }

    [Fact]
    public async Task OpenImage_ReturnsProtectedRangeEnabledStreamWithMetadata()
    {
        var service = new RecordingMilkTestService
        {
            OpenedMedia = new StoredMediaContent(
                new MemoryStream([1, 2, 3, 4]),
                "image/jpeg",
                4)
        };
        var controller = CreateController(service);
        var milkTestId = Guid.NewGuid();
        var imageId = Guid.NewGuid();

        var result = await controller.OpenImage(
            milkTestId,
            imageId,
            CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.True(file.EnableRangeProcessing);
        Assert.Equal(4, controller.Response.ContentLength);
        Assert.Equal(milkTestId, service.OpenedMilkTestId);
        Assert.Equal(imageId, service.OpenedImageId);
        Assert.Equal(73, service.LastActor?.UserId);
    }

    [Fact]
    public void RequireActor_ParsesDistinctBranchesAndGlobalAccess()
    {
        var controller = CreateActorController(
            new Claim("user_id", "73"),
            new Claim(AuthorizationCodes.BranchClaim, "11"),
            new Claim(AuthorizationCodes.BranchClaim, "12"),
            new Claim(AuthorizationCodes.BranchClaim, "11"),
            new Claim(AuthorizationCodes.BranchClaim, "invalid"),
            new Claim(AuthorizationCodes.PermissionClaim, AuthorizationCodes.GlobalAccess));

        var actor = controller.GetActor();

        Assert.Equal(73, actor.UserId);
        Assert.Equal([11L, 12L], actor.BranchIds.OrderBy(x => x));
        Assert.True(actor.HasGlobalAccess);
    }

    [Fact]
    public void RequireActor_WithoutGlobalPermission_HasBranchOnlyAccess()
    {
        var controller = CreateActorController(
            new Claim("user_id", "73"),
            new Claim(AuthorizationCodes.BranchClaim, "11"));

        var actor = controller.GetActor();

        Assert.Equal([11L], actor.BranchIds);
        Assert.False(actor.HasGlobalAccess);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    public void RequireActor_WithMissingOrInvalidUserId_IsUnauthorized(string? userId)
    {
        var claims = userId is null ? [] : new[] { new Claim("user_id", userId) };
        var controller = CreateActorController(claims);

        Assert.Throws<UnauthorizedAppException>(() => controller.GetActor());
    }

    [Fact]
    public void RequireActor_ParsesInvariantIdentifiers()
    {
        var controller = CreateActorController(
            new Claim("user_id", 73L.ToString(CultureInfo.InvariantCulture)),
            new Claim(
                AuthorizationCodes.BranchClaim,
                11L.ToString(CultureInfo.InvariantCulture)));

        var actor = controller.GetActor();

        Assert.Equal(73, actor.UserId);
        Assert.Equal([11L], actor.BranchIds);
    }

    private static MilkTestsController CreateController(RecordingMilkTestService service)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("user_id", "73"),
                    new Claim(AuthorizationCodes.BranchClaim, "11")
                ],
                authenticationType: "Test"))
        };

        return new MilkTestsController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static TestMilkTestController CreateActorController(params Claim[] claims)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"))
        };

        return new TestMilkTestController
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class TestMilkTestController : MilkTestControllerBase
    {
        public MilkTestActor GetActor() => RequireMilkTestActor();
    }

    private sealed class RecordingMilkTestService : IMilkTestService
    {
        public bool UploadCalled { get; private set; }
        public MilkTestActor? LastActor { get; private set; }
        public Guid? OpenedMilkTestId { get; private set; }
        public Guid? OpenedImageId { get; private set; }
        public StoredMediaContent OpenedMedia { get; init; } = new(
            new MemoryStream([1]),
            "application/octet-stream",
            1);

        public Task<CustomerMilkTestResult> RequestAsync(
            MilkTestActor actor,
            Guid deliveryId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CustomerMilkTestResult?> GetForCustomerAsync(
            MilkTestActor actor,
            Guid deliveryId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StaffMilkTestResult?> GetForStaffAsync(
            MilkTestActor actor,
            Guid deliveryId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MilkTestImageResult> UploadImageAsync(
            MilkTestActor actor,
            Guid milkTestId,
            Stream content,
            string fileName,
            string? declaredContentType,
            CancellationToken cancellationToken)
        {
            UploadCalled = true;
            throw new NotSupportedException();
        }

        public Task<StaffMilkTestResult> DeleteImageAsync(
            MilkTestActor actor,
            Guid milkTestId,
            Guid imageId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MilkTestImageResult> ReplaceImageAsync(
            MilkTestActor actor,
            Guid milkTestId,
            Guid imageId,
            Stream content,
            string fileName,
            string? declaredContentType,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StoredMediaContent> OpenImageAsync(
            MilkTestActor actor,
            Guid milkTestId,
            Guid imageId,
            CancellationToken cancellationToken)
        {
            LastActor = actor;
            OpenedMilkTestId = milkTestId;
            OpenedImageId = imageId;
            return Task.FromResult(OpenedMedia);
        }

        public Task<StaffMilkTestResult> CompleteAsync(
            MilkTestActor actor,
            Guid milkTestId,
            CompleteMilkTestRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CustomerMilkTestResult> ConfirmAsync(
            MilkTestActor actor,
            Guid milkTestId,
            DecideMilkTestRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CustomerMilkTestResult> RejectAsync(
            MilkTestActor actor,
            Guid milkTestId,
            DecideMilkTestRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
