using System.Net;
using System.Text;
using System.Text.Json;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Infrastructure.OtpProvider;
using Microsoft.Extensions.Logging.Abstractions;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class Msg91ApiClientTests
{
    // ---- widget/sendOtp ---------------------------------------------------

    [Fact]
    public async Task SendAsync_ParsesReqIdFromDataMessage_AndSendsVerifiedBody()
    {
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse(
                """{"data":{"type":"success","message":"req-hex-123"},"status":"200","description":"Success"}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var result = await client.SendAsync(
            new Msg91OtpSendRequest("919876543210", "Registration"),
            CancellationToken.None);

        Assert.Equal("req-hex-123", result.ReqId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.msg91.com/api/v5/widget/sendOtp", request.RequestUri!.ToString());
        Assert.Equal("authkey-1", request.Headers.GetValues("authkey").Single());
        Assert.False(request.Headers.Contains("widgetId"), "widgetId must be in the body, never a header.");

        using var body = ParseBody(Assert.Single(handler.RequestBodies));
        Assert.Equal("widget-1", body.RootElement.GetProperty("widgetId").GetString());
        Assert.Equal("919876543210", body.RootElement.GetProperty("identifier").GetString());
        Assert.False(body.RootElement.TryGetProperty("otp", out _), "Send must not carry an OTP.");
        Assert.False(body.RootElement.TryGetProperty("mobile", out _), "Send uses 'identifier', not 'mobile'.");
    }

    [Fact]
    public async Task SendAsync_ParsesReqIdFromLegacyMessageEnvelope()
    {
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{"type":"success","message":{"reqId":"req-nested","msgId":"m-1"}}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var result = await client.SendAsync(
            new Msg91OtpSendRequest("919876543210", "Registration"),
            CancellationToken.None);

        Assert.Equal("req-nested", result.ReqId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.msg91.com/api/v5/widget/sendOtp", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task SendAsync_ParsesReqIdFromRootFallback()
    {
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{"type":"success","reqId":"req-root"}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var result = await client.SendAsync(
            new Msg91OtpSendRequest("919876543210", "Registration"),
            CancellationToken.None);

        Assert.Equal("req-root", result.ReqId);
    }

    [Fact]
    public async Task SendAsync_ParsesReqIdFromFlatLiveEnvelope()
    {
        // Live-probed MSG91 sendOtp success (2026-09-03) returns a flat envelope
        // where the root "message" string IS the reqId:
        //   {"message":"36696366344e343735393130","type":"success"}
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{"message":"req-hex-flat","type":"success"}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var result = await client.SendAsync(
            new Msg91OtpSendRequest("919876543210", "Registration"),
            CancellationToken.None);

        Assert.Equal("req-hex-flat", result.ReqId);
    }

    [Fact]
    public async Task SendAsync_WhenReqIdMissing_ThrowsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{"type":"success"}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var exception = await Assert.ThrowsAsync<OtpProviderUnavailableException>(() =>
            client.SendAsync(
                new Msg91OtpSendRequest("919876543210", "Registration"),
                CancellationToken.None));

        Assert.Contains("reqId", exception.Message);
    }

    [Fact]
    public async Task SendAsync_OnSuccessEnvelopeWithoutReqId_ThrowsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{"type":"success","message":{"msgId":"m-1"}}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var exception = await Assert.ThrowsAsync<OtpProviderUnavailableException>(() =>
            client.SendAsync(
                new Msg91OtpSendRequest("919876543210", "Registration"),
                CancellationToken.None));

        Assert.Contains("reqId", exception.Message);
    }

    [Fact]
    public async Task SendAsync_OnMalformedJsonBody_ThrowsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>Not JSON</body></html>")
            });
        var client = CreateClient(handler, ConfiguredSettings());

        var exception = await Assert.ThrowsAsync<OtpProviderUnavailableException>(() =>
            client.SendAsync(
                new Msg91OtpSendRequest("919876543210", "Registration"),
                CancellationToken.None));

        Assert.Contains("unreadable response", exception.Message);
    }

    // ---- widget/retryOtp --------------------------------------------------

    [Fact]
    public async Task RetryAsync_ReusesSameReqId_AndSendsWidgetIdReqIdBody()
    {
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse(
                """{"data":{"type":"success","message":"req-hex-123"},"status":"200","description":"Success"}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var result = await client.RetryAsync(
            new Msg91OtpRetryRequest("req-hex-123"),
            CancellationToken.None);

        Assert.Equal("req-hex-123", result.ReqId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.msg91.com/api/v5/widget/retryOtp", request.RequestUri!.ToString());
        Assert.Equal("authkey-1", request.Headers.GetValues("authkey").Single());
        Assert.False(request.Headers.Contains("widgetId"));

        using var body = ParseBody(Assert.Single(handler.RequestBodies));
        Assert.Equal("widget-1", body.RootElement.GetProperty("widgetId").GetString());
        Assert.Equal("req-hex-123", body.RootElement.GetProperty("reqId").GetString());
        Assert.False(body.RootElement.TryGetProperty("mobile", out _), "Retry must reuse the reqId, no mobile.");
        Assert.False(body.RootElement.TryGetProperty("otp", out _));
    }

    [Fact]
    public async Task RetryAsync_FallsBackToRequestReqIdWhenResponseOmitsIt()
    {
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{"type":"success"}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var result = await client.RetryAsync(
            new Msg91OtpRetryRequest("req-existing"),
            CancellationToken.None);

        Assert.Equal("req-existing", result.ReqId);
    }

    // ---- widget/verifyOtp -------------------------------------------------

    [Fact]
    public async Task VerifyAsync_SendsWidgetIdReqIdOtp_AndParsesAccessTokenFromData()
    {
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse(
                """{"data":{"access_token":"at-1"},"status":"200","description":"Success"}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var result = await client.VerifyAsync(
            new Msg91OtpVerifyRequest("req-123", "919876543210", "123456", "Registration"),
            CancellationToken.None);

        Assert.Equal("at-1", result.AccessToken);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.msg91.com/api/v5/widget/verifyOtp", request.RequestUri!.ToString());
        Assert.Equal("authkey-1", request.Headers.GetValues("authkey").Single());
        Assert.False(request.Headers.Contains("widgetId"));

        using var body = ParseBody(Assert.Single(handler.RequestBodies));
        Assert.Equal("widget-1", body.RootElement.GetProperty("widgetId").GetString());
        Assert.Equal("req-123", body.RootElement.GetProperty("reqId").GetString());
        Assert.Equal("123456", body.RootElement.GetProperty("otp").GetString());
    }

    [Fact]
    public async Task VerifyAsync_ParsesAccessTokenFromCamelCaseDataProperty()
    {
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{"data":{"accessToken":"at-camel"}}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var result = await client.VerifyAsync(
            new Msg91OtpVerifyRequest("req-123", "919876543210", "123456", "Registration"),
            CancellationToken.None);

        Assert.Equal("at-camel", result.AccessToken);
    }

    [Fact]
    public async Task VerifyAsync_ParsesAccessTokenFromRoot()
    {
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{"type":"success","access_token":"at-root"}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var result = await client.VerifyAsync(
            new Msg91OtpVerifyRequest("req-123", "919876543210", "123456", "Registration"),
            CancellationToken.None);

        Assert.Equal("at-root", result.AccessToken);
    }

    [Fact]
    public async Task VerifyAsync_ParsesAccessTokenFromFlatLiveEnvelope()
    {
        // Live-probed MSG91 responses (2026-09-03) use a flat success envelope
        // where the root "message" string carries the returned value:
        //   {"message":"<JWT>","type":"success"}
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{"message":"at-flat","type":"success"}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var result = await client.VerifyAsync(
            new Msg91OtpVerifyRequest("req-123", "919876543210", "123456", "Registration"),
            CancellationToken.None);

        Assert.Equal("at-flat", result.AccessToken);
    }

    [Fact]
    public async Task VerifyAsync_WhenAccessTokenMissing_ThrowsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{"type":"success"}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var exception = await Assert.ThrowsAsync<OtpProviderUnavailableException>(() =>
            client.VerifyAsync(
                new Msg91OtpVerifyRequest("req-123", "919876543210", "123456", "Registration"),
                CancellationToken.None));

        Assert.Contains("access token", exception.Message);
    }

    // ---- widget/verifyAccessToken ------------------------------------------

    [Fact]
    public async Task ValidateAccessTokenAsync_SendsHyphenatedAccessToken_ReadsAttestedIdentifierFromDataMessage()
    {
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse(
                """{"data":{"type":"success","message":"919999999999"},"status":"200","description":"Success"}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var result = await client.ValidateAccessTokenAsync(
            new Msg91OtpAccessTokenRequest("at-1", "919876543210", "Registration"),
            CancellationToken.None);

        Assert.Equal("919999999999", result.VerifiedIdentifier);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.msg91.com/api/v5/widget/verifyAccessToken", request.RequestUri!.ToString());
        Assert.Equal("authkey-1", request.Headers.GetValues("authkey").Single());
        Assert.False(request.Headers.Contains("widgetId"));

        using var body = ParseBody(Assert.Single(handler.RequestBodies));
        Assert.Equal("at-1", body.RootElement.GetProperty("access-token").GetString());
        Assert.False(body.RootElement.TryGetProperty("widgetId", out _), "verifyAccessToken body carries only the access token.");
        Assert.False(body.RootElement.TryGetProperty("reqId", out _));
        Assert.False(body.RootElement.TryGetProperty("mobile", out _));
    }

    [Fact]
    public async Task ValidateAccessTokenAsync_WhenDataMessageMissing_ReturnsNullIdentifier_ToFailClosed()
    {
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{"type":"success"}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var result = await client.ValidateAccessTokenAsync(
            new Msg91OtpAccessTokenRequest("at-1", "919876543210", "Registration"),
            CancellationToken.None);

        Assert.Null(result.VerifiedIdentifier);
    }

    [Fact]
    public async Task ValidateAccessTokenAsync_ReadsAttestedIdentifierFromFlatLiveEnvelope()
    {
        // Live-probed MSG91 verifyAccessToken shape (2026-09-03) is a flat
        // envelope where the root "message" string is the attested identifier:
        //   {"message":"919354816929","type":"success"}
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{"message":"919354816929","type":"success"}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var result = await client.ValidateAccessTokenAsync(
            new Msg91OtpAccessTokenRequest("at-1", "919876543210", "Registration"),
            CancellationToken.None);

        Assert.Equal("919354816929", result.VerifiedIdentifier);
    }

    // ---- configuration / transport / provider error handling ---------------

    [Fact]
    public async Task SendAsync_WhenNotConfigured_ThrowsProviderUnavailableWithoutCallingHttp()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP should not be invoked when unconfigured."));
        var client = CreateClient(handler, new Msg91ProviderSettings(null, null, null, false));

        var exception = await Assert.ThrowsAsync<OtpProviderUnavailableException>(() =>
            client.SendAsync(
                new Msg91OtpSendRequest("919876543210", "Registration"),
                CancellationToken.None));

        Assert.Contains("not configured", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendAsync_OnHttpError_ClassifiesAsProviderRejectionWithSanitizedTruncatedDiagnostic()
    {
        // §10: a 4xx response is a provider rejection → 422 OTP_PROVIDER_REJECTED.
        var longMessage = string.Join(' ', Enumerable.Repeat("token-secret-value", 40));
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse($$"""{"type":"error","message":"{{longMessage}}"}""", HttpStatusCode.BadRequest));
        var client = CreateClient(handler, ConfiguredSettings());

        var exception = await Assert.ThrowsAsync<OtpProviderRejectedException>(() =>
            client.SendAsync(
                new Msg91OtpSendRequest("919876543210", "Registration"),
                CancellationToken.None));

        Assert.Contains("HTTP 400", exception.Message);
        Assert.Contains("Provider message:", exception.Message);
        // "The OTP service rejected the request (HTTP 400)." (48) + " Provider message: " (19) + diagnostic (<=240) + "." (1)
        Assert.True(exception.Message.Length <= 310);
    }

    [Fact]
    public async Task SendAsync_OnPlainErrorBody_ReadsErrorPropertyAsDiagnostic()
    {
        // Live-probed MSG91 HTTP 400 body: {"error":"Widget Not Found !"} — plain
        // HTTP error bodies use "error", not the envelope "message" property.
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{"error":"Widget Not Found !"}""", HttpStatusCode.BadRequest));
        var client = CreateClient(handler, ConfiguredSettings());

        var exception = await Assert.ThrowsAsync<OtpProviderRejectedException>(() =>
            client.SendAsync(
                new Msg91OtpSendRequest("919876543210", "Registration"),
                CancellationToken.None));

        Assert.Contains("HTTP 400", exception.Message);
        Assert.Contains("Widget Not Found !", exception.Message);
    }

    [Fact]
    public async Task SendAsync_OnErrorTypeResponse_ClassifiesAsRejectedEvenWithOkStatus()
    {
        // §10: HTTP 200 with a provider-level error envelope is NOT success; it is a
        // provider rejection → 422 OTP_PROVIDER_REJECTED.
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{"type":"error","message":"invalid authkey"}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var exception = await Assert.ThrowsAsync<OtpProviderRejectedException>(() =>
            client.SendAsync(
                new Msg91OtpSendRequest("919876543210", "Registration"),
                CancellationToken.None));

        Assert.Contains("HTTP 200", exception.Message);
        Assert.Contains("invalid authkey", exception.Message);
    }

    [Fact]
    public async Task SendAsync_OnDocumentedErrorEnvelope_ClassifiesAsRejectedEvenWithHttp200()
    {
        // Live-probed MSG91 error envelope returned with HTTP 200:
        //   {"message":"AuthenticationFailure","type":"error","code":401}
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{"message":"AuthenticationFailure","type":"error","code":401}"""));
        var client = CreateClient(handler, ConfiguredSettings());

        var exception = await Assert.ThrowsAsync<OtpProviderRejectedException>(() =>
            client.SendAsync(
                new Msg91OtpSendRequest("919876543210", "Registration"),
                CancellationToken.None));

        Assert.Contains("HTTP 200", exception.Message);
        Assert.Contains("AuthenticationFailure", exception.Message);
    }

    [Fact]
    public async Task SendAsync_OnNonJson5xx_ClassifiesAsProviderUnavailableGeneric()
    {
        // §10: a 5xx transport error (non-JSON body) → 503 OTP_PROVIDER_UNAVAILABLE
        // with a generic message (no diagnostic is available from the HTML body).
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("<html><body>Bad Gateway</body></html>")
            });
        var client = CreateClient(handler, ConfiguredSettings());

        var exception = await Assert.ThrowsAsync<OtpProviderUnavailableException>(() =>
            client.SendAsync(
                new Msg91OtpSendRequest("919876543210", "Registration"),
                CancellationToken.None));

        Assert.Contains("HTTP 502", exception.Message);
        Assert.Equal("The OTP service is temporarily unavailable (HTTP 502).", exception.Message);
    }

    [Fact]
    public async Task ValidateAccessTokenAsync_WhenNotConfigured_ThrowsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP should not be invoked when unconfigured."));
        var client = CreateClient(handler, new Msg91ProviderSettings(null, null, null, false));

        var exception = await Assert.ThrowsAsync<OtpProviderUnavailableException>(() =>
            client.ValidateAccessTokenAsync(
                new Msg91OtpAccessTokenRequest("at-1", "919876543210", "Registration"),
                CancellationToken.None));

        Assert.Contains("not configured", exception.Message);
        Assert.Empty(handler.Requests);
    }

    private static Msg91ProviderSettings ConfiguredSettings() =>
        new("widget-1", "authkey-1", "Test", true);

    private static Msg91ApiClient CreateClient(
        StubHttpMessageHandler handler,
        Msg91ProviderSettings settings) =>
        new(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.msg91.com/api/v5/")
            },
            new StubSettingsProvider(settings),
            NullLogger<Msg91ApiClient>.Instance);

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static JsonDocument ParseBody(string json) => JsonDocument.Parse(json);

    private sealed class StubSettingsProvider(Msg91ProviderSettings settings)
        : IMsg91ProviderSettingsProvider
    {
        public Task<Msg91ProviderSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(settings);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return responseFactory(request);
        }
    }
}
