using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace DoodhDirect.Api.Authorization;

public sealed class AuditingAuthorizationMiddlewareResultHandler(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<AuditingAuthorizationMiddlewareResultHandler> logger)
    : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged || authorizeResult.Forbidden)
        {
            await WriteAuditAsync(context, authorizeResult.Forbidden, CancellationToken.None);

            context.Response.StatusCode = authorizeResult.Forbidden
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            var errorCode = authorizeResult.Forbidden ? "FORBIDDEN" : "UNAUTHORIZED";
            var message = authorizeResult.Forbidden
                ? "You are not authorized to perform this action."
                : "Authentication is required.";
            await context.Response.WriteAsJsonAsync(
                new ApiResponse<object>(
                    false,
                    null,
                    message,
                    [new ApiError(errorCode, null, message)]),
                context.RequestAborted);
            return;
        }

        await next(context);
    }

    private async Task WriteAuditAsync(
        HttpContext context,
        bool forbidden,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
            long? userId = null;
            var userIdValue = context.User.FindFirstValue("user_id");
            if (long.TryParse(
                userIdValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedUserId))
            {
                userId = parsedUserId;
            }

            var endpoint = context.GetEndpoint()?.DisplayName ?? context.Request.Path.Value ?? "unknown";
            dbContext.AuditLogs.Add(new AuditLog(
                userId,
                forbidden ? "AUTHORIZATION_FORBIDDEN" : "AUTHORIZATION_CHALLENGED",
                "Endpoint",
                endpoint.Length <= 100 ? endpoint : endpoint[..100],
                null,
                null,
                context.Connection.RemoteIpAddress?.ToString(),
                context.Request.Headers.UserAgent.ToString(),
                $"{context.Request.Method} {context.Request.Path}",
                clock.UtcNow));
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to persist authorization failure audit for {Method} {Path}.",
                context.Request.Method,
                context.Request.Path);
        }
    }
}
