namespace DoodhDirect.Api.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (!Guid.TryParse(correlationId, out _))
        {
            correlationId = Guid.NewGuid().ToString("D");
        }

        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        context.Items[HeaderName] = correlationId;
        await next(context);
    }
}
