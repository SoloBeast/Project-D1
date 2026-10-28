using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Notifications;

/// <summary>
/// Firebase Cloud Messaging push gateway over the FCM HTTP v1 API.
/// Sends a visible <c>notification</c> payload (title + body) so the operating
/// system can display a system notification when the app is backgrounded or
/// terminated, plus <c>data</c> fields carrying the existing notification id,
/// event type and validated deep link for tap navigation and inbox
/// reconciliation. No new notification records are created here; the existing
/// event pipeline and delivery state machine own persistence.
/// </summary>
internal sealed class FcmPushGateway(
    IHttpClientFactory httpClientFactory,
    IOptions<FcmPushOptions> options,
    ILogger<FcmPushGateway> logger) : INotificationChannelGateway
{
    public const string HttpClientName = "FcmPush";

    public NotificationChannel Channel => NotificationChannel.Push;
    public string ProviderCode => "FCM";

    private const string MessagingScope = "https://www.googleapis.com/auth/firebase.messaging";
    private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private FcmServiceAccount? _serviceAccount;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public async Task<NotificationProviderResult> SendAsync(
        NotificationProviderMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Channel != NotificationChannel.Push)
        {
            return Fail(
                "FCM_WRONG_CHANNEL",
                $"The FCM gateway cannot send channel '{message.Channel}'.",
                false);
        }

        if (string.IsNullOrWhiteSpace(message.Destination))
        {
            return Fail("FCM_NO_DESTINATION", "The push device has no destination token.", true);
        }

        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.ProjectId) ||
            string.IsNullOrWhiteSpace(config.ServiceAccountFile))
        {
            return Fail(
                "FCM_NOT_CONFIGURED",
                "FCM push is selected but Notifications:Fcm:ProjectId or " +
                "Notifications:Fcm:ServiceAccountFile is not configured.",
                false);
        }

        try
        {
            var account = await LoadServiceAccountAsync(config, cancellationToken).ConfigureAwait(false);
            var accessToken = await GetAccessTokenAsync(config, account, cancellationToken).ConfigureAwait(false);
            return await SendMessageAsync(config, accessToken, message, cancellationToken).ConfigureAwait(false);
        }
        catch (FcmPermanentException exception)
        {
            logger.LogWarning(
                "FCM push delivery {DeliveryId} failed permanently with {FailureCode}.",
                message.DeliveryId,
                exception.Code);
            return Fail(exception.Code, exception.Message, exception.InvalidateDestination);
        }
        catch (FcmRetryableException exception)
        {
            logger.LogWarning(
                "FCM push delivery {DeliveryId} failed transiently with {FailureCode}.",
                message.DeliveryId,
                exception.Code);
            return new NotificationProviderResult(
                NotificationAttemptOutcome.RetryableFailure,
                FailureCode: exception.Code,
                FailureMessage: exception.Message);
        }
    }

    private async Task<FcmServiceAccount> LoadServiceAccountAsync(
        FcmPushOptions config,
        CancellationToken cancellationToken)
    {
        if (_serviceAccount is not null)
        {
            return _serviceAccount;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(config.ServiceAccountFile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FcmPermanentException(
                "FCM_CREDENTIALS_UNREADABLE",
                $"The FCM service-account file '{config.ServiceAccountFile}' could not be read: {Limit(exception.Message, 300)}.");
        }

        FcmServiceAccount account;
        try
        {
            account = FcmServiceAccount.Parse(json);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new FcmPermanentException(
                "FCM_CREDENTIALS_INVALID",
                $"The FCM service-account file '{config.ServiceAccountFile}' is not a valid service-account key: {Limit(exception.Message, 300)}.");
        }

        _serviceAccount = account;
        return account;
    }

    private async Task<string> GetAccessTokenAsync(
        FcmPushOptions config,
        FcmServiceAccount account,
        CancellationToken cancellationToken)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt - TimeSpan.FromMinutes(5))
        {
            return _accessToken;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt - TimeSpan.FromMinutes(5))
            {
                return _accessToken;
            }

            var assertion = CreateJwtAssertion(account);
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, account.TokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion
                })
            };

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                throw new FcmRetryableException(
                    "FCM_AUTH_TRANSPORT_ERROR",
                    $"The Google OAuth token endpoint could not be reached: {Limit(exception.Message, 300)}.");
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new FcmRetryableException(
                    "FCM_AUTH_TIMEOUT",
                    $"The Google OAuth token request timed out: {Limit(exception.Message, 300)}.");
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw MapTokenError(response.StatusCode, body);
                }

                try
                {
                    using var document = JsonDocument.Parse(body);
                    var root = document.RootElement;
                    var token = root.GetProperty("access_token").GetString();
                    var expiresIn = root.TryGetProperty("expires_in", out var expiresElement) &&
                        expiresElement.TryGetInt32(out var seconds)
                        ? seconds
                        : 3600;
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        throw new FcmRetryableException(
                            "FCM_AUTH_BAD_RESPONSE",
                            "The Google OAuth token endpoint returned an empty access token.");
                    }

                    _accessToken = token;
                    _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn));
                    return token;
                }
                catch (JsonException exception)
                {
                    throw new FcmRetryableException(
                        "FCM_AUTH_BAD_RESPONSE",
                        $"The Google OAuth token response could not be parsed: {Limit(exception.Message, 300)}.");
                }
            }
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<NotificationProviderResult> SendMessageAsync(
        FcmPushOptions config,
        string accessToken,
        NotificationProviderMessage message,
        CancellationToken cancellationToken)
    {
        var url = $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(config.ProjectId)}/messages:send";
        var payload = JsonSerializer.Serialize(BuildPayload(message));
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new FcmRetryableException(
                "FCM_TRANSPORT_ERROR",
                $"The FCM send endpoint could not be reached: {Limit(exception.Message, 300)}.");
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FcmRetryableException(
                "FCM_TIMEOUT",
                $"The FCM send request timed out: {Limit(exception.Message, 300)}.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new NotificationProviderResult(
                    NotificationAttemptOutcome.Delivered,
                    ProviderMessageId: TryExtractMessageName(body));
            }

            throw MapSendError(response.StatusCode, body);
        }
    }

    private static Dictionary<string, object> BuildPayload(NotificationProviderMessage message)
    {
        var notification = new Dictionary<string, string> { ["body"] = message.Body };
        if (!string.IsNullOrWhiteSpace(message.Title))
        {
            notification["title"] = message.Title;
        }

        var data = new Dictionary<string, string>(message.Data, StringComparer.Ordinal);
        if (IsSafeDeepLink(message.DeepLink))
        {
            data["deepLink"] = message.DeepLink!;
            data["deep_link"] = message.DeepLink!;
        }
        else
        {
            data.Remove("deepLink");
            data.Remove("deep_link");
        }

        return new Dictionary<string, object>
        {
            ["message"] = new Dictionary<string, object>
            {
                ["token"] = message.Destination,
                ["notification"] = notification,
                ["data"] = data,
                ["android"] = new Dictionary<string, object> { ["priority"] = "HIGH" },
                ["apns"] = new Dictionary<string, object>
                {
                    ["headers"] = new Dictionary<string, string> { ["apns-priority"] = "10" },
                    ["payload"] = new Dictionary<string, object>
                    {
                        ["aps"] = new Dictionary<string, object> { ["sound"] = "default" }
                    }
                }
            }
        };
    }

    /// <summary>
    /// Only app-relative links are forwarded to devices. Absolute URLs are
    /// dropped so a compromised template or payload can never drive the app
    /// to an external destination.
    /// </summary>
    private static bool IsSafeDeepLink(string? deepLink) =>
        !string.IsNullOrWhiteSpace(deepLink) &&
        deepLink.StartsWith("/", StringComparison.Ordinal) &&
        !deepLink.StartsWith("//", StringComparison.Ordinal);

    private static string CreateJwtAssertion(FcmServiceAccount account)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = new Dictionary<string, string>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT"
        };
        if (!string.IsNullOrWhiteSpace(account.PrivateKeyId))
        {
            header["kid"] = account.PrivateKeyId;
        }

        var payload = new Dictionary<string, object>
        {
            ["iss"] = account.ClientEmail,
            ["scope"] = MessagingScope,
            ["aud"] = account.TokenUri,
            ["iat"] = now,
            ["exp"] = now + 3600
        };

        var encodedHeader = Base64UrlEncode(JsonSerializer.Serialize(header));
        var encodedPayload = Base64UrlEncode(JsonSerializer.Serialize(payload));
        var signingInput = $"{encodedHeader}.{encodedPayload}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(account.PrivateKey);
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(string value) =>
        Base64UrlEncode(Encoding.UTF8.GetBytes(value));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Exception MapTokenError(HttpStatusCode status, string body)
    {
        var detail = $"Google OAuth error {(int)status}: {Limit(ExtractGoogleMessage(body), 300)}";
        return (int)status switch
        {
            429 => new FcmRetryableException("FCM_AUTH_RATE_LIMITED", detail),
            >= 500 => new FcmRetryableException("FCM_AUTH_SERVER_ERROR", detail),
            _ => new FcmPermanentException(
                "FCM_AUTHENTICATION_FAILED",
                $"{detail} Verify the service-account key, project id, and system clock. " +
                "No push was sent and retrying with the same credentials will keep failing."),
        };
    }

    private static Exception MapSendError(HttpStatusCode status, string body)
    {
        var googleMessage = ExtractGoogleMessage(body);
        var errorCodes = ExtractGoogleErrorCodes(body);
        var code = (int)status;

        if (code == 429 || errorCodes.Contains("QUOTA_EXCEEDED"))
        {
            return new FcmRetryableException(
                "FCM_RATE_LIMITED",
                Limit($"FCM rate limit exceeded ({googleMessage}). The existing bounded retry policy applies.", 500));
        }

        if (code >= 500 || errorCodes.Contains("UNAVAILABLE") || errorCodes.Contains("INTERNAL"))
        {
            return new FcmRetryableException(
                "FCM_SERVER_ERROR",
                Limit($"FCM server error {code} ({googleMessage}). The existing bounded retry policy applies.", 500));
        }

        if (code == 401)
        {
            return new FcmPermanentException(
                "FCM_UNAUTHORIZED",
                "FCM rejected the request as unauthorized. Verify the service-account key, " +
                    "that the Firebase Cloud Messaging API is enabled, and that the project id matches. " +
                    $"Google reports: {Limit(googleMessage, 300)}");
        }

        if (code == 403 || errorCodes.Contains("SENDER_ID_MISMATCH"))
        {
            return new FcmPermanentException(
                "FCM_SENDER_MISMATCH",
                "FCM reports a sender/project mismatch for this device token. The app's Firebase " +
                    $"project does not match the configured project. Google reports: {Limit(googleMessage, 300)}");
        }

        if (errorCodes.Contains("UNREGISTERED"))
        {
            return new FcmPermanentException(
                "FCM_UNREGISTERED",
                "FCM reports this device token is no longer registered (app uninstalled or token " +
                    "expired). The device destination is invalidated so future pushes stop.",
                InvalidateDestination: true);
        }

        if (errorCodes.Contains("INVALID_ARGUMENT"))
        {
            return new FcmPermanentException(
                "FCM_INVALID_TOKEN",
                $"FCM rejected this device token as invalid: {Limit(googleMessage, 300)} " +
                "The device destination is invalidated so future pushes stop.",
                InvalidateDestination: true);
        }

        return new FcmPermanentException(
            code == 404 ? "FCM_NOT_FOUND" : "FCM_REQUEST_REJECTED",
            Limit($"FCM rejected the request with HTTP {code}: {googleMessage}", 500));
    }

    private static string? TryExtractMessageName(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("name", out var name)
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            // A 2xx without a parsable name still means FCM accepted the message.
            return null;
        }
    }

    private static string ExtractGoogleMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                // FCM send errors nest the message; OAuth token errors are flat.
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var message))
                {
                    return message.GetString() ?? string.Empty;
                }

                if (error.ValueKind == JsonValueKind.String)
                {
                    var description = document.RootElement.TryGetProperty("error_description", out var detail)
                        ? detail.GetString()
                        : null;
                    return string.IsNullOrWhiteSpace(description)
                        ? error.GetString() ?? string.Empty
                        : $"{error.GetString()}: {description}";
                }
            }
        }
        catch (JsonException)
        {
        }

        return body;
    }

    private static HashSet<string> ExtractGoogleErrorCodes(string body)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error) ||
                !error.TryGetProperty("details", out var details) ||
                details.ValueKind != JsonValueKind.Array)
            {
                return codes;
            }

            foreach (var detail in details.EnumerateArray())
            {
                if (detail.TryGetProperty("errorCode", out var errorCode) &&
                    errorCode.GetString() is { } value &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    codes.Add(value.Trim());
                }
            }
        }
        catch (JsonException)
        {
        }

        return codes;
    }

    private static NotificationProviderResult Fail(string code, string message, bool invalidateDestination) =>
        new(NotificationAttemptOutcome.PermanentFailure,
            FailureCode: code,
            FailureMessage: message,
            InvalidateDestination: invalidateDestination);

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private sealed class FcmServiceAccount
    {
        public required string ProjectId { get; init; }
        public required string PrivateKey { get; init; }
        public required string ClientEmail { get; init; }
        public string? PrivateKeyId { get; init; }
        public string TokenUri { get; init; } = DefaultTokenUri;

        public static FcmServiceAccount Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) ||
                !string.Equals(type.GetString(), "service_account", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The JSON is not a service-account key (type != service_account).");
            }

            static string Required(JsonElement element, string name)
            {
                if (!element.TryGetProperty(name, out var property) ||
                    string.IsNullOrWhiteSpace(property.GetString()))
                {
                    throw new InvalidOperationException($"The service-account key is missing '{name}'.");
                }

                return property.GetString()!;
            }

            var tokenUri = root.TryGetProperty("token_uri", out var tokenUriElement)
                ? tokenUriElement.GetString()
                : null;
            return new FcmServiceAccount
            {
                ProjectId = Required(root, "project_id"),
                PrivateKey = Required(root, "private_key"),
                ClientEmail = Required(root, "client_email"),
                PrivateKeyId = root.TryGetProperty("private_key_id", out var kid) ? kid.GetString() : null,
                TokenUri = string.IsNullOrWhiteSpace(tokenUri) ? DefaultTokenUri : tokenUri
            };
        }
    }

    private sealed class FcmRetryableException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private sealed class FcmPermanentException(
        string code,
        string message,
        bool InvalidateDestination = false) : Exception(message)
    {
        public string Code { get; } = code;
        public bool InvalidateDestination { get; } = InvalidateDestination;
    }
}
