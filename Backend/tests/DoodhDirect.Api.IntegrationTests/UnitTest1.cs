using System.Net;
using System.Text.Json;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class ApiFoundationTests : IClassFixture<FoundationApiFactory>
{
    private const string CorrelationHeader = "X-Correlation-Id";
    private const string AllowedDevelopmentOrigin = "http://localhost:54187";
    private readonly FoundationApiFactory _factory;

    public ApiFoundationTests(FoundationApiFactory factory)
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
    public async Task CorsPreflight_AllowsDevelopmentLocalhostOriginWithDynamicPort()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        request.Headers.Add("Origin", AllowedDevelopmentOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            AllowedDevelopmentOrigin,
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains(
            "POST",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Methods")));
        var allowedHeaders = Assert.Single(
            response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("authorization", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-type", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task CorsPreflight_RejectsNonLocalDevelopmentOrigin()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        request.Headers.Add("Origin", "https://untrusted.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
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

    [Fact]
    public async Task CatalogueEndpoints_AllowPublicReadsAndProtectAdministration()
    {
        using var client = _factory.CreateClient();

        using var publicResponse = await client.GetAsync(
            "/api/v1/products",
            CancellationToken.None);
        using var adminResponse = await client.GetAsync(
            "/api/v1/admin/products",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, adminResponse.StatusCode);
    }

    [Fact]
    public async Task OpenApiDocument_DescribesBearerSecurityAndAnonymousAuthOperations()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", CancellationToken.None);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = document.RootElement;
        var bearer = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("bearerAuth");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        Assert.Equal("JWT", bearer.GetProperty("bearerFormat").GetString());

        var paths = root.GetProperty("paths");
        var register = paths.GetProperty("/api/v1/auth/register").GetProperty("post");
        Assert.Equal(0, register.GetProperty("security").GetArrayLength());

        var me = paths.GetProperty("/api/v1/auth/me").GetProperty("get");
        var requirement = me.GetProperty("security")[0];
        Assert.True(requirement.TryGetProperty("bearerAuth", out _));

        var products = paths.GetProperty("/api/v1/products").GetProperty("get");
        Assert.Equal(0, products.GetProperty("security").GetArrayLength());

        var adminProducts = paths.GetProperty("/api/v1/admin/products").GetProperty("get");
        var adminRequirement = adminProducts.GetProperty("security")[0];
        Assert.True(adminRequirement.TryGetProperty("bearerAuth", out _));
    }
}

public sealed class FoundationApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection;

    public FoundationApiFactory()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseSqlite(connection)
            .Options;
        using var db = new DoodhDirectDbContext(options);
        db.Database.EnsureCreated();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<DoodhDirectDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<DoodhDirectDbContext>>();
            services.RemoveAll<DoodhDirectDbContext>();
            services.AddDbContext<DoodhDirectDbContext>(options => options.UseSqlite(connection));
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await connection.DisposeAsync();
    }
}
