using System.Text.Json;
using DoodhDirect.Application.Common;

namespace DoodhDirect.Api.Middleware;

public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            await WriteErrorAsync(context, exception);
        }
    }

    private async Task WriteErrorAsync(HttpContext context, Exception exception)
    {
        var (statusCode, errors, message) = exception switch
        {
            AppException appException =>
                (appException.StatusCode,
                    new[] { new ApiError(appException.Code, appException.Field, appException.Message) },
                    appException.Message),
            _ =>
                (StatusCodes.Status500InternalServerError,
                    new[] { new ApiError("INTERNAL_ERROR", null, "An unexpected error occurred.") },
                    "An unexpected error occurred.")
        };

        if (statusCode >= 500)
        {
            logger.LogError(exception, "Unhandled exception. CorrelationId: {CorrelationId}", context.TraceIdentifier);
        }
        else
        {
            logger.LogWarning(exception, "Request failed with status {StatusCode}. CorrelationId: {CorrelationId}", statusCode, context.TraceIdentifier);
        }

        var response = new ApiResponse<object>(false, null, message, errors);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }
}
