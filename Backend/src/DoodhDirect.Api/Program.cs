using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DoodhDirect.Api.Authorization;
using DoodhDirect.Api.Middleware;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Infrastructure;
using DoodhDirect.Infrastructure.Catalogue;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Notifications;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
LoadLocalDotEnv(builder.Configuration, builder.Environment.ContentRootPath);
const string corsPolicyName = "DoodhDirectWeb";

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "DoodhDirect.Api"));

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException($"Configuration section '{JwtOptions.SectionName}' is required.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());
builder.Services.AddSingleton<IAuthorizationPolicyProvider, DoodhDirectAuthorizationPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, BranchScopeAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuditingAuthorizationMiddlewareResultHandler>();

var configuredCorsOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy(corsPolicyName, policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policy.SetIsOriginAllowed(origin =>
                Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
        }
        else if (configuredCorsOrigins.Length > 0)
        {
            policy.WithOrigins(configuredCorsOrigins);
        }

        policy.AllowAnyMethod();
        policy.AllowAnyHeader();
    });
});

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "DoodhDirect API";
        document.Info.Version = "v1";
        document.Info.Description = "Identity/RBAC, customer, catalogue, ordering, subscriptions, payments, wallet, delivery, dairy, doorstep testing, secure live dairy cameras, and provider-neutral notifications API.";
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??=
            new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);

        SetStringEnumSchema(
            document.Components.Schemas,
            "DeliverySourceType",
            ["OneTimeOrder", "SubscriptionOccurrence"]);
        SetStringEnumSchema(
            document.Components.Schemas,
            "DeliveryStatus",
            [
                "ReadyForAssignment",
                "Assigned",
                "PickedUp",
                "OutForDelivery",
                "Arrived",
                "Delivered",
                "Failed"
            ]);

        document.Components.SecuritySchemes ??=
            new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes["bearerAuth"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Enter a DoodhDirect access token."
        };
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        var endpointMetadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (endpointMetadata.OfType<IAllowAnonymous>().Any())
        {
            operation.Security = [];
            return Task.CompletedTask;
        }

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("bearerAuth", context.Document, null)] = []
        });
        return Task.CompletedTask;
    });
});
builder.Services.AddControllers(options =>
    {
        options.Filters.Add(new ProducesAttribute("application/json"));
    })
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(item => item.Value?.Errors.Count > 0)
            .SelectMany(item => item.Value!.Errors.Select(error =>
                new ApiError("VALIDATION_ERROR", item.Key, error.ErrorMessage)))
            .ToArray();
        return new BadRequestObjectResult(new ApiResponse<object>(
            false,
            null,
            "Request validation failed.",
            errors));
    };
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var cancellationToken = CancellationToken.None;
    await scope.ServiceProvider
        .GetRequiredService<IdentitySeedService>()
        .SeedAsync(cancellationToken);
    if (app.Environment.IsDevelopment())
    {
        await scope.ServiceProvider
            .GetRequiredService<DevelopmentCustomerSeedService>()
            .SeedAsync(cancellationToken);
    }
    await scope.ServiceProvider
        .GetRequiredService<CatalogueSeedService>()
        .SeedAsync(cancellationToken);
    await scope.ServiceProvider
        .GetRequiredService<NotificationTemplateSeedService>()
        .SeedAsync(cancellationToken);
    if (app.Environment.IsDevelopment())
    {
        await scope.ServiceProvider
            .GetRequiredService<DevelopmentDeliveryStaffSeedService>()
            .SeedAsync(cancellationToken);
        await scope.ServiceProvider
            .GetRequiredService<DevelopmentDairyManagerSeedService>()
            .SeedAsync(cancellationToken);
    }
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("CorrelationId", httpContext.TraceIdentifier);
        diagnosticContext.Set("ClientIp", httpContext.Connection.RemoteIpAddress?.ToString());
    };
});
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseCors(corsPolicyName);
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference(options =>
    {
        options.Title = "DoodhDirect API";
        options.Theme = ScalarTheme.Default;
    }).AllowAnonymous();

    app.MapPost(
            "/api/v1/development/notifications/samples",
            async (
                ClaimsPrincipal user,
                IDevelopmentNotificationService notificationService,
                CancellationToken cancellationToken) =>
            {
                var userIdValue = user.FindFirstValue("user_id");
                if (!long.TryParse(
                        userIdValue,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var userId))
                {
                    throw new UnauthorizedAppException();
                }

                var eventIds = await notificationService.CreateSamplesAsync(
                    new NotificationActor(userId),
                    cancellationToken);
                return Results.Ok(ApiResponse<IReadOnlyCollection<Guid>>.Ok(
                    eventIds,
                    "Development notification samples created."));
            })
        .WithTags("Development notifications")
        .Produces<ApiResponse<IReadOnlyCollection<Guid>>>(StatusCodes.Status200OK);
}

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
}).AllowAnonymous();
app.MapControllers();

app.Run();

static void SetStringEnumSchema(
    IDictionary<string, IOpenApiSchema> schemas,
    string schemaName,
    IReadOnlyCollection<string> values)
{
    if (!schemas.ContainsKey(schemaName))
    {
        return;
    }

    schemas[schemaName] = new OpenApiSchema
    {
        Type = JsonSchemaType.String,
        Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList()
    };
}

static void LoadLocalDotEnv(ConfigurationManager configuration, string contentRootPath)
{
    var directory = new DirectoryInfo(contentRootPath);
    while (directory is not null)
    {
        var path = Path.Combine(directory.FullName, ".env");
        if (File.Exists(path))
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var separator = trimmed.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var name = trimmed[..separator].Trim();
                var value = trimmed[(separator + 1)..].Trim().Trim('"', '\'');
                values[name] = value;
            }

            var mapped = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            values.TryGetValue("RAZORPAY_KEY_ID", out var keyId);
            values.TryGetValue("RAZORPAY_KEY_SECRET", out var keySecret);
            if (!string.IsNullOrWhiteSpace(keyId) && !string.IsNullOrWhiteSpace(keySecret))
            {
                mapped["Payments:Provider"] = "Razorpay";
                mapped["Payments:RazorpayKeyId"] = keyId;
                mapped["Payments:RazorpayKeySecret"] = keySecret;
            }
            if (values.TryGetValue("RAZORPAY_WEBHOOK_SECRET", out var webhookSecret) &&
                !string.IsNullOrWhiteSpace(webhookSecret))
            {
                mapped["Payments:RazorpayWebhookSecret"] = webhookSecret;
            }

            configuration.AddInMemoryCollection(mapped);
            return;
        }

        directory = directory.Parent;
    }
}

public partial class Program;
