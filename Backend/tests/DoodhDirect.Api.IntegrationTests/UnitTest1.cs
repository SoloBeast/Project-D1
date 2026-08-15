using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class ApiFoundationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string CorrelationHeader = "X-Correlation-Id";
    private readonly WebApplicationFactory<Program> _factory;

    public ApiFoundationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LivenessEndpoint_IsAnonymousAndHealthy()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/live", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CorrelationMiddleware_GeneratesValidIdentifier()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/live", CancellationToken.None);

        Assert.True(response.Headers.TryGetValues(CorrelationHeader, out var values));
        Assert.True(Guid.TryParse(Assert.Single(values), out _));
    }

    [Fact]
    public async Task CorrelationMiddleware_PreservesValidIncomingIdentifier()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        var correlationId = Guid.NewGuid().ToString("D");
        request.Headers.Add(CorrelationHeader, correlationId);

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(correlationId, Assert.Single(response.Headers.GetValues(CorrelationHeader)));
    }

    [Fact]
    public async Task FallbackAuthorizationPolicy_RequiresAuthenticatedUser()
    {
        var policyProvider = _factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await policyProvider.GetFallbackPolicyAsync();

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, requirement =>
            requirement is DenyAnonymousAuthorizationRequirement);
    }
}
