using System.Text.Json;
using DoodhDirect.Api.Middleware;
using DoodhDirect.Application.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class ExceptionHandlingMiddlewareTests
{
    [Fact]
    public async Task InsufficientWalletBalance_WritesUnprocessableBusinessErrorEnvelope()
    {
        const string expectedMessage =
            "Insufficient wallet balance. Please add ₹460 to your wallet or choose another payment method.";
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InsufficientWalletBalanceException(340m, 800m, 460m, "INR"),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            new TestWebHostEnvironment());
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        var root = document.RootElement;
        var error = root.GetProperty("errors")[0];
        var serializedResponse = root.GetRawText();

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind);
        Assert.Equal(expectedMessage, root.GetProperty("message").GetString());
        Assert.Equal("INSUFFICIENT_WALLET_BALANCE", error.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("field").ValueKind);
        Assert.Equal(expectedMessage, error.GetProperty("message").GetString());
        Assert.DoesNotContain("INTERNAL_ERROR", serializedResponse);
        Assert.DoesNotContain("Internal Server Error", serializedResponse);
        Assert.DoesNotContain("500", serializedResponse);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DoodhDirect.Api.IntegrationTests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Production";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
