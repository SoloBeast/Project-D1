using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using Microsoft.Extensions.Logging;

namespace DoodhDirect.Infrastructure.OtpProvider;

/// <summary>
/// MSG91 OTP Widget API client (v5), verified against the official docs
/// (2025-08-11). Follows the RazorpayPaymentGateway HTTP pattern: typed
/// HttpClient, authkey header, safe error diagnostics, and controlled provider
/// exceptions. The auth key and OTP codes are never echoed into exception
/// messages or logs.
///
/// Verified flow:
///   widget/sendOtp           body {"widgetId","identifier"}  -> reqId
///   widget/retryOtp          body {"widgetId","reqId"}       -> same reqId
///   widget/verifyOtp         body {"widgetId","reqId","otp"} -> access token (JWT)
///   widget/verifyAccessToken body {"access-token":"<JWT>"}   -> attested identifier
///
/// Live responses (verified 2026-09-03) use a flat success envelope where the
/// root-level "message" string IS the returned value (reqId / JWT / identifier):
///   {"message":"<hex reqId>","type":"success"}
///   {"message":"<JWT access token>","type":"success"}
///   {"message":"<attested identifier>","type":"success"}
/// The nested doc envelope (data.message) is also still supported.
///
/// The application never trusts client-side widget callbacks; every request is
/// made by the backend with the authkey header. widgetId is part of the JSON
/// body (NOT a header) for send/retry/verify. MSG91 can return HTTP 200 even
/// for error envelopes {"message","type":"error","code"} so HTTP 200 alone is
/// never treated as success.
/// </summary>
public sealed class Msg91ApiClient(
    HttpClient httpClient,
    IMsg91ProviderSettingsProvider settingsProvider,
    ILogger<Msg91ApiClient> logger) : IMsg91OtpProvider
{
    private const string JsonMediaType = "application/json";
    private const string RedactedMarker = "[REDACTED]";

    public async Task<Msg91OtpSendResult> SendAsync(
        Msg91OtpSendRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await GetConfiguredSettingsAsync(cancellationToken);
        using var message = CreateRequest(HttpMethod.Post, "widget/sendOtp", settings);
        message.Content = JsonContent.Create(new
        {
            widgetId = settings.WidgetId!,
            identifier = request.Destination
        });

        using var response = await httpClient.SendAsync(message, cancellationToken);
        using var document = await ReadSuccessAsync(response, cancellationToken);
        return new Msg91OtpSendResult(ReadRequiredReqId(document.RootElement, "widget/sendOtp"));
    }

    public async Task<Msg91OtpSendResult> RetryAsync(
        Msg91OtpRetryRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await GetConfiguredSettingsAsync(cancellationToken);
        using var message = CreateRequest(HttpMethod.Post, "widget/retryOtp", settings);
        message.Content = JsonContent.Create(new
        {
            widgetId = settings.WidgetId!,
            reqId = request.ReqId
        });

        using var response = await httpClient.SendAsync(message, cancellationToken);
        using var document = await ReadSuccessAsync(response, cancellationToken);
        var root = document.RootElement;
        return new Msg91OtpSendResult(ReadOptionalReqId(root) ?? request.ReqId);
    }

    public async Task<Msg91OtpVerifyResult> VerifyAsync(
        Msg91OtpVerifyRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await GetConfiguredSettingsAsync(cancellationToken);
        using var message = CreateRequest(HttpMethod.Post, "widget/verifyOtp", settings);
        message.Content = JsonContent.Create(new
        {
            widgetId = settings.WidgetId!,
            reqId = request.ReqId,
            otp = request.Code
        });

        using var response = await httpClient.SendAsync(message, cancellationToken);
        using var document = await ReadSuccessAsync(response, cancellationToken);
        var accessToken = ReadRequiredAccessToken(document.RootElement);
        return new Msg91OtpVerifyResult(accessToken);
    }

    public async Task<Msg91OtpValidationResult> ValidateAccessTokenAsync(
        Msg91OtpAccessTokenRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await GetConfiguredSettingsAsync(cancellationToken);
        using var message = CreateRequest(HttpMethod.Post, "widget/verifyAccessToken", settings);
        // Exact documented field name "access-token" (hyphenated). Dictionary
        // keys are serialized verbatim, unlike anonymous-type member names.
        message.Content = JsonContent.Create(new Dictionary<string, string>
        {
            ["access-token"] = request.AccessToken
        });

        using var response = await httpClient.SendAsync(message, cancellationToken);
        using var document = await ReadSuccessAsync(response, cancellationToken);
        var verifiedIdentifier = ReadAttestedIdentifier(document.RootElement);
        return new Msg91OtpValidationResult(verifiedIdentifier);
    }

    private async Task<Msg91ProviderSettings> GetConfiguredSettingsAsync(
        CancellationToken cancellationToken)
    {
        var settings = await settingsProvider.GetAsync(cancellationToken);
        if (!settings.IsConfigured)
        {
            throw new OtpProviderUnavailableException(
                "The OTP service is not configured. Please contact support.");
        }

        return settings;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string relativePath,
        Msg91ProviderSettings settings)
    {
        var request = new HttpRequestMessage(method, relativePath);
        request.Headers.TryAddWithoutValidation("authkey", settings.AuthKey!);
        return request;
    }

    private async Task<JsonDocument> ReadSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // §10 error classification: 4xx client/credential errors are a provider
            // rejection (422 OTP_PROVIDER_REJECTED); 5xx transport errors are a
            // provider outage (503 OTP_PROVIDER_UNAVAILABLE). A raw HttpRequestException
            // would surface as a 500 INTERNAL_ERROR, which is why the exceptions are
            // classified here.
            LogSanitizedErrorBody(bytes);
            throw CreateProviderException(response.StatusCode, bytes, isErrorEnvelope: false);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw new OtpProviderUnavailableException(
                "The OTP service returned an unreadable response. Please try again in a few moments.");
        }

        if (IsErrorResponse(document.RootElement))
        {
            document.Dispose();
            // MSG91 returns documented error envelopes with HTTP 200
            // (e.g. {"message":"AuthenticationFailure","type":"error","code":401});
            // an error envelope is still a provider rejection → 422.
            LogSanitizedErrorBody(bytes);
            throw CreateProviderException(response.StatusCode, bytes, isErrorEnvelope: true);
        }

        LogSanitizedResponseBody(document.RootElement);
        return document;
    }

    private static bool IsErrorResponse(JsonElement root) =>
        root.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && string.Equals(type.GetString(), "error", StringComparison.OrdinalIgnoreCase);

    private static AppException CreateProviderException(
        HttpStatusCode statusCode,
        byte[] responseBody,
        bool isErrorEnvelope)
    {
        var diagnostic = ReadSafeDiagnosticFromBody(responseBody);
        var statusCodeInt = (int)statusCode;

        if (isErrorEnvelope || statusCodeInt is >= 400 and < 500)
        {
            var rejected = $"The OTP service rejected the request (HTTP {statusCodeInt}).";
            if (diagnostic is not null)
            {
                rejected += $" Provider message: {diagnostic}.";
            }

            return new OtpProviderRejectedException(rejected);
        }

        var unavailable = $"The OTP service is temporarily unavailable (HTTP {statusCodeInt}).";
        if (diagnostic is not null)
        {
            unavailable += $" Provider message: {diagnostic}.";
        }

        return new OtpProviderUnavailableException(unavailable);
    }

    private static string? ReadSafeDiagnosticFromBody(byte[] responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return ReadSafeDiagnostic(document.RootElement);
        }
        catch (JsonException)
        {
            // Keep the provider message generic when the body is not JSON.
            return null;
        }
    }

    private static string? ReadSafeDiagnostic(JsonElement root)
    {
        // MSG91 error envelopes use "message" (e.g. "AuthenticationFailure")
        // while plain HTTP error bodies use "error" (e.g. "Widget Not Found !").
        if (!root.TryGetProperty("message", out var property))
        {
            root.TryGetProperty("error", out property);
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }

    private static string ReadRequiredReqId(JsonElement root, string context) =>
        ReadOptionalReqId(root)
        ?? throw new OtpProviderUnavailableException(
            $"The OTP service response for '{context}' did not include the required 'reqId'. Please try again in a few moments.");

    private static string? ReadOptionalReqId(JsonElement root)
    {
        // Verified sendOtp/retryOtp responses:
        //   {"data":{"type":"success","message":"<hex reqId placeholder>"},"status":"200","description":"Success"}
        // so the reqId is read from data.message first, then the legacy message
        // object envelope, then a root-level reqId for compatibility. The live
        // flat envelope (verified 2026-09-03) returns the reqId as the root
        // "message" string:
        //   {"message":"<hex reqId>","type":"success"}
        var reqId = TryReadDataMessage(root);

        if (reqId is null
            && root.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object)
        {
            reqId = TryReadString(message, "reqId");
        }

        return reqId
            ?? TryReadString(root, "reqId")
            ?? TryReadString(root, "message");
    }

    /// <summary>
    /// Reads the access token (JWT) returned by widget/verifyOtp. The official
    /// sample shows {"data":null,...} for this call, so the token is read from
    /// either data.access_token / data.accessToken or a root-level token, and a
    /// missing token fails closed (no session is ever issued).
    /// </summary>
    private static string ReadRequiredAccessToken(JsonElement root)
    {
        // The official verifyOtp sample shows {"data":null,...} for this call.
        // Read the JWT from data.access_token/data.accessToken first, then a
        // root-level token; a missing token fails closed (no session is issued).
        if (TryReadDataProperty(root, "access_token", out var token)
            || TryReadDataProperty(root, "accessToken", out token))
        {
            return token;
        }

        var rootToken = TryReadString(root, "access_token")
            ?? TryReadString(root, "accessToken")
            // Flat live envelope: the root "message" string IS the JWT.
            ?? TryReadString(root, "message");
        if (rootToken is not null)
        {
            return rootToken;
        }

        throw new OtpProviderUnavailableException(
            "The OTP service response for 'widget/verifyOtp' did not include the required access token.");
    }

    /// <summary>
    /// Reads the provider-attested identifier (mobile/email) returned by
    /// widget/verifyAccessToken. The documented shape nests it in data.message:
    ///   {"data":{"type":"success","message":"919999999999"},"status":"200","description":"Success"}
    /// while the live flat envelope (verified 2026-09-03) returns it as the root
    /// "message" string:
    ///   {"message":"919354816929","type":"success"}
    /// A missing or blank identifier fails closed (the verification is rejected).
    /// </summary>
    private static string? ReadAttestedIdentifier(JsonElement root) =>
        TryReadDataMessage(root) ?? TryReadString(root, "message");

    private static string? TryReadDataMessage(JsonElement root)
    {
        var message = TryReadDataProperty(root, "message", out var value)
            ? value
            : null;
        return message;
    }

    private static bool TryReadDataProperty(JsonElement root, string propertyName, out string value)
    {
        value = null!;
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var result = TryReadString(data, propertyName);
        if (result is null)
        {
            return false;
        }

        value = result;
        return true;
    }

    private static string? TryReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : null;

    private void LogSanitizedResponseBody(JsonElement root)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        try
        {
            var node = JsonNode.Parse(root.GetRawText());
            if (node is null)
            {
                return;
            }

            RedactSensitiveValues(node);
            logger.LogDebug(
                "MSG91 widget response body (sensitive values redacted): {ResponseBody}",
                node.ToJsonString());
        }
        catch (JsonException)
        {
            // Diagnostic logging must never break the OTP flow.
        }
    }

    private void LogSanitizedErrorBody(byte[] responseBody)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var node = JsonNode.Parse(document.RootElement.GetRawText());
            if (node is null)
            {
                return;
            }

            RedactSensitiveValues(node);
            logger.LogDebug(
                "MSG91 widget error response body (sensitive values redacted): {ResponseBody}",
                node.ToJsonString());
        }
        catch (JsonException)
        {
            // The body is not JSON; log the raw text truncated so diagnostics
            // remain possible for unexpected (non-JSON) provider responses.
            var text = System.Text.Encoding.UTF8.GetString(responseBody);
            var safeLength = Math.Min(text.Length, 500);
            logger.LogDebug(
                "MSG91 widget error response body (non-JSON, truncated): {ResponseBody}",
                text[..safeLength]);
        }
    }

    private static void RedactSensitiveValues(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (IsSensitivePropertyName(property.Key))
                {
                    // Redact the entire value so its digits are never exposed.
                    obj[property.Key] = RedactedMarker;
                    continue;
                }

                if (property.Value is not null)
                {
                    RedactSensitiveValues(property.Value);
                }
            }

            return;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    RedactSensitiveValues(item);
                }
            }

            return;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            // Catch standalone OTP codes / full mobile numbers echoed as plain values.
            if (LooksLikeSensitiveNumber(text))
            {
                value.ReplaceWith(RedactedMarker);
            }
        }
    }

    private static bool IsSensitivePropertyName(string name) =>
        name.Equals("authkey", StringComparison.OrdinalIgnoreCase)
        || name.Equals("otp", StringComparison.OrdinalIgnoreCase)
        || name.Equals("otpcode", StringComparison.OrdinalIgnoreCase)
        || name.Equals("mobile", StringComparison.OrdinalIgnoreCase)
        || name.Equals("identifier", StringComparison.OrdinalIgnoreCase)
        || name.Equals("accesstoken", StringComparison.OrdinalIgnoreCase)
        || name.Equals("widgetid", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeSensitiveNumber(string value)
    {
        if (value.Length is < 4 or > 15)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
