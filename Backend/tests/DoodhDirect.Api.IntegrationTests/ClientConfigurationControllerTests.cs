using System.Reflection;
using DoodhDirect.Api.Controllers;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class ClientConfigurationControllerTests
{
    [Fact]
    public async Task Get_ReturnsWebClientKeyInOkEnvelope()
    {
        var service = new CapturingClientConfigurationService
        {
            WebClientKey = "AIza-web-client-key"
        };
        var controller = new ClientConfigurationController(service);

        var response = await controller.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var envelope = Assert.IsType<ApiResponse<ClientConfigurationResult>>(ok.Value);
        Assert.True(envelope.Success);
        Assert.Equal("AIza-web-client-key", envelope.Data!.GoogleMapsWebClientKey);
    }

    [Fact]
    public async Task Get_WhenUnconfigured_ReturnsNullWebClientKey()
    {
        var controller = new ClientConfigurationController(new CapturingClientConfigurationService());

        var response = await controller.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var envelope = Assert.IsType<ApiResponse<ClientConfigurationResult>>(ok.Value);
        Assert.True(envelope.Success);
        Assert.Null(envelope.Data!.GoogleMapsWebClientKey);
    }

    [Fact]
    public void ClientConfigurationEndpoint_IsProtectedByFallbackPolicy_NotAllowAnonymous()
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(
            typeof(ClientConfigurationController).GetMethod(
                nameof(ClientConfigurationController.Get),
                BindingFlags.Instance | BindingFlags.Public));

        // No explicit [AllowAnonymous] and no explicit [Authorize]: the global fallback
        // RequireAuthenticatedUser policy (Program.cs) protects this endpoint for every
        // signed-in user (customer/employee/owner), exactly like AuthController.Me/Logout.
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.Empty(typeof(ClientConfigurationController)
            .GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.Empty(method.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        Assert.Empty(typeof(ClientConfigurationController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true));
    }

    private sealed class CapturingClientConfigurationService : IClientConfigurationService
    {
        public string? WebClientKey { get; set; }

        public Task<string?> GetGoogleMapsWebClientKeyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(WebClientKey);
    }
}
