using System.Reflection;
using System.Security.Claims;
using DoodhDirect.Api.Controllers;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class OtpProviderConfigurationControllerTests
{
    [Theory]
    [InlineData(typeof(OtpProviderConfigurationController), nameof(OtpProviderConfigurationController.Get), AuthorizationCodes.SetupOtpProviderRead)]
    [InlineData(typeof(OtpProviderConfigurationController), nameof(OtpProviderConfigurationController.Update), AuthorizationCodes.SetupOtpProviderManage)]
    [InlineData(typeof(OtpProviderConfigurationController), nameof(OtpProviderConfigurationController.Test), AuthorizationCodes.SetupOtpProviderManage)]
    public void OtpProviderRoutes_RequireExpectedPermission(
        Type controllerType,
        string methodName,
        string permission)
    {
        var method = controllerType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);

        var authorize = method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Concat(controllerType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());

        Assert.Contains(authorize, attribute => attribute.Policy == $"permission:{permission}");
        Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
    }

    [Fact]
    public void OtpProviderController_ClassLevelRequiresReadPermission()
    {
        var authorize = Assert.Single(Assert.IsType<AuthorizeAttribute[]>(
            typeof(OtpProviderConfigurationController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)));

        Assert.Equal($"permission:{AuthorizationCodes.SetupOtpProviderRead}", authorize.Policy);
    }

    [Fact]
    public async Task Get_ReturnsConfiguredStateInOkEnvelope()
    {
        var service = new CapturingOtpProviderConfigurationService
        {
            ConfigurationResult = new OtpProviderConfigurationResult(
                "MSG91", true, "widget-1", "Test", true, "Configured")
        };
        var controller = new OtpProviderConfigurationController(service)
        {
            ControllerContext = ContextWithUser("42")
        };

        var response = await controller.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var envelope = Assert.IsType<ApiResponse<OtpProviderConfigurationResult>>(ok.Value);
        Assert.True(envelope.Success);
        Assert.Equal("MSG91", envelope.Data!.Provider);
        Assert.True(envelope.Data.Enabled);
        Assert.Equal("widget-1", envelope.Data.WidgetId);
        Assert.True(envelope.Data.Configured);
    }

    [Fact]
    public async Task Update_ForwardsRequestUserIdIpAndUserAgent()
    {
        var service = new CapturingOtpProviderConfigurationService();
        var controller = new OtpProviderConfigurationController(service)
        {
            ControllerContext = ContextWithUser("42", "203.0.113.5", "update-agent")
        };
        var request = new UpdateOtpProviderConfigurationRequest(true, "widget-1", null, "Test");

        var response = await controller.Update(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        Assert.Same(request, service.LastUpdateRequest);
        Assert.Equal(42, service.LastUpdateUserId);
        Assert.Equal("203.0.113.5", service.LastUpdateIp);
        Assert.Equal("update-agent", service.LastUpdateUserAgent);
    }

    [Fact]
    public async Task Update_WhenUserClaimIsMissing_ThrowsUnauthorized()
    {
        var controller = new OtpProviderConfigurationController(
            new CapturingOtpProviderConfigurationService())
        {
            ControllerContext = ContextWithUser(userId: null)
        };

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            controller.Update(
                new UpdateOtpProviderConfigurationRequest(null, null, null, null),
                CancellationToken.None));
    }

    [Fact]
    public async Task Update_WhenUserClaimIsNotNumeric_ThrowsUnauthorized()
    {
        var controller = new OtpProviderConfigurationController(
            new CapturingOtpProviderConfigurationService())
        {
            ControllerContext = ContextWithUser("not-a-number")
        };

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            controller.Update(
                new UpdateOtpProviderConfigurationRequest(null, null, null, null),
                CancellationToken.None));
    }

    [Fact]
    public async Task Test_ForwardsUserIdIpAndUserAgent()
    {
        var service = new CapturingOtpProviderConfigurationService
        {
            TestResult = new OtpProviderTestResult(true, "Configuration verified. A test OTP was sent successfully.")
        };
        var controller = new OtpProviderConfigurationController(service)
        {
            ControllerContext = ContextWithUser("7", "203.0.113.9", "test-agent")
        };

        var response = await controller.Test(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var envelope = Assert.IsType<ApiResponse<OtpProviderTestResult>>(ok.Value);
        Assert.True(envelope.Success);
        Assert.Equal(7, service.LastTestUserId);
        Assert.Equal("203.0.113.9", service.LastTestIp);
        Assert.Equal("test-agent", service.LastTestUserAgent);
    }

    private static ControllerContext ContextWithUser(
        string? userId,
        string? ip = null,
        string? userAgent = null)
    {
        var context = new DefaultHttpContext
        {
            User = userId is null
                ? new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "Test"))
                : new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("user_id", userId)],
                    authenticationType: "Test"))
        };

        if (ip is not null)
        {
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        }

        if (userAgent is not null)
        {
            context.Request.Headers["User-Agent"] = userAgent;
        }

        return new ControllerContext { HttpContext = context };
    }

    private sealed class CapturingOtpProviderConfigurationService : IOtpProviderConfigurationService
    {
        public OtpProviderConfigurationResult ConfigurationResult { get; set; } =
            new("MSG91", false, null, null, false, "Not Configured");

        public OtpProviderTestResult TestResult { get; set; } = new(true, "ok");

        public UpdateOtpProviderConfigurationRequest? LastUpdateRequest { get; private set; }
        public long? LastUpdateUserId { get; private set; }
        public string? LastUpdateIp { get; private set; }
        public string? LastUpdateUserAgent { get; private set; }
        public long? LastTestUserId { get; private set; }
        public string? LastTestIp { get; private set; }
        public string? LastTestUserAgent { get; private set; }

        public Task<OtpProviderConfigurationResult> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ConfigurationResult);

        public Task<OtpProviderConfigurationResult> UpdateAsync(
            UpdateOtpProviderConfigurationRequest request,
            long actorUserId,
            string? ipAddress,
            string? userAgent,
            CancellationToken cancellationToken)
        {
            LastUpdateRequest = request;
            LastUpdateUserId = actorUserId;
            LastUpdateIp = ipAddress;
            LastUpdateUserAgent = userAgent;
            return Task.FromResult(ConfigurationResult);
        }

        public Task<OtpProviderTestResult> TestAsync(
            long actorUserId,
            string? ipAddress,
            string? userAgent,
            CancellationToken cancellationToken)
        {
            LastTestUserId = actorUserId;
            LastTestIp = ipAddress;
            LastTestUserAgent = userAgent;
            return Task.FromResult(TestResult);
        }
    }
}
