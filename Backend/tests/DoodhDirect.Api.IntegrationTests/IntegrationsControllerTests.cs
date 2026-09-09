using System.Reflection;
using System.Security.Claims;
using DoodhDirect.Api.Controllers;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class IntegrationsControllerTests
{
    [Theory]
    [InlineData(typeof(IntegrationsController), nameof(IntegrationsController.Get), AuthorizationCodes.SetupIntegrationsRead)]
    [InlineData(typeof(IntegrationsController), nameof(IntegrationsController.Update), AuthorizationCodes.SetupIntegrationsManage)]
    [InlineData(typeof(IntegrationsController), nameof(IntegrationsController.Test), AuthorizationCodes.SetupIntegrationsManage)]
    public void IntegrationRoutes_RequireExpectedPermission(
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
    public void IntegrationsController_ClassLevelRequiresReadPermission()
    {
        var authorize = Assert.Single(Assert.IsType<AuthorizeAttribute[]>(
            typeof(IntegrationsController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)));

        Assert.Equal($"permission:{AuthorizationCodes.SetupIntegrationsRead}", authorize.Policy);
    }

    [Fact]
    public async Task Get_ReturnsConfiguredStateInOkEnvelope()
    {
        var service = new CapturingIntegrationConfigurationService
        {
            ConfigurationResult = ConfiguredResult()
        };
        var controller = new IntegrationsController(service)
        {
            ControllerContext = ContextWithUser("42")
        };

        var response = await controller.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var envelope = Assert.IsType<ApiResponse<IntegrationConfigurationResult>>(ok.Value);
        Assert.True(envelope.Success);
        Assert.Equal("no-reply@doodhdirect.in", envelope.Data!.EmailFromAddress);
        Assert.Equal("smtp.example.com", envelope.Data.EmailHost);
        Assert.Equal(587, envelope.Data.EmailPort);
        Assert.True(envelope.Data.EmailPasswordConfigured);
        Assert.True(envelope.Data.EmailIsConfigured);
        Assert.Equal("https://app.example.com", envelope.Data.InviteUrlBase);
        Assert.Equal("rzp_test_key", envelope.Data.RazorpayKeyId);
        Assert.True(envelope.Data.RazorpayKeySecretConfigured);
        Assert.True(envelope.Data.RazorpayIsConfigured);
        Assert.True(envelope.Data.GoogleMapsApiKeyConfigured);
        Assert.True(envelope.Data.GoogleMapsIsConfigured);
    }

    [Fact]
    public async Task Update_ForwardsRequestUserIdIpAndUserAgent()
    {
        var service = new CapturingIntegrationConfigurationService();
        var controller = new IntegrationsController(service)
        {
            ControllerContext = ContextWithUser("42", "203.0.113.5", "update-agent")
        };
        var request = new UpdateIntegrationConfigurationRequest(
            EmailFromAddress: "no-reply@doodhdirect.in",
            EmailHost: "smtp.example.com");

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
        var controller = new IntegrationsController(new CapturingIntegrationConfigurationService())
        {
            ControllerContext = ContextWithUser(userId: null)
        };

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            controller.Update(
                new UpdateIntegrationConfigurationRequest(EmailHost: "smtp.example.com"),
                CancellationToken.None));
    }

    [Fact]
    public async Task Update_WhenUserClaimIsNotNumeric_ThrowsUnauthorized()
    {
        var controller = new IntegrationsController(new CapturingIntegrationConfigurationService())
        {
            ControllerContext = ContextWithUser("not-a-number")
        };

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            controller.Update(
                new UpdateIntegrationConfigurationRequest(EmailHost: "smtp.example.com"),
                CancellationToken.None));
    }

    [Fact]
    public async Task Test_ForwardsUserIdIpAndUserAgent()
    {
        var service = new CapturingIntegrationConfigurationService
        {
            TestResult = new IntegrationTestResult(
                true,
                "Configuration verified. A test email was sent to no-reply@doodhdirect.in.")
        };
        var controller = new IntegrationsController(service)
        {
            ControllerContext = ContextWithUser("7", "203.0.113.9", "test-agent")
        };

        var response = await controller.Test(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var envelope = Assert.IsType<ApiResponse<IntegrationTestResult>>(ok.Value);
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

    private static IntegrationConfigurationResult ConfiguredResult() => new(
        EmailFromAddress: "no-reply@doodhdirect.in",
        EmailFromName: "DoodhDirect",
        EmailHost: "smtp.example.com",
        EmailPort: 587,
        EmailUserName: "smtp-user",
        EmailUseSsl: true,
        EmailPasswordConfigured: true,
        EmailIsConfigured: true,
        InviteUrlBase: "https://app.example.com",
        RazorpayKeyId: "rzp_test_key",
        RazorpayKeySecretConfigured: true,
        RazorpayWebhookSecretConfigured: true,
        RazorpayIsConfigured: true,
        GoogleMapsBaseUrl: "https://maps.googleapis.com/maps/api/geocode/json",
        GoogleMapsApiKeyConfigured: true,
        GoogleMapsIsConfigured: true,
        GoogleMapsWebClientKey: "maps-web-client-key");

    private sealed class CapturingIntegrationConfigurationService : IIntegrationConfigurationService
    {
        public IntegrationConfigurationResult ConfigurationResult { get; set; } = ConfiguredResult();

        public IntegrationTestResult TestResult { get; set; } = new(
            true,
            "Configuration verified. A test email was sent to no-reply@doodhdirect.in.");

        public UpdateIntegrationConfigurationRequest? LastUpdateRequest { get; private set; }
        public long? LastUpdateUserId { get; private set; }
        public string? LastUpdateIp { get; private set; }
        public string? LastUpdateUserAgent { get; private set; }
        public long? LastTestUserId { get; private set; }
        public string? LastTestIp { get; private set; }
        public string? LastTestUserAgent { get; private set; }

        public Task<IntegrationConfigurationResult> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ConfigurationResult);

        public Task<IntegrationConfigurationResult> UpdateAsync(
            UpdateIntegrationConfigurationRequest request,
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

        public Task<IntegrationTestResult> TestAsync(
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
