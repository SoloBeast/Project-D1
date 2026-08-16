using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoodhDirect.Application.Payments;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Payments;

public sealed class MockPaymentGateway(IOptions<PaymentOptions> options) : IPaymentGateway
{
    private readonly PaymentOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, GatewayOrderRequest> _orders = new(StringComparer.Ordinal);

    public string ProviderName => "Mock";
    public string? PublicKeyId => "mock_development_key";
    public bool IsLive => false;

    public Task<GatewayOrderResult> CreateOrderAsync(
        GatewayOrderRequest request,
        CancellationToken cancellationToken)
    {
        var gatewayOrderId = $"order_mock_{request.PaymentId:N}";
        _orders[gatewayOrderId] = request;
        return Task.FromResult(new GatewayOrderResult(
            gatewayOrderId,
            "created",
            request.AmountMinor,
            request.Currency));
    }

    public bool VerifyPaymentSignature(
        string gatewayOrderId,
        string gatewayPaymentId,
        string signature) =>
        gatewayOrderId.StartsWith("order_mock_", StringComparison.Ordinal) &&
        gatewayPaymentId.StartsWith("pay_mock_", StringComparison.Ordinal) &&
        FixedTimeEquals(signature, "mock_verified");

    public Task<GatewayPaymentStatusResult> GetPaymentStatusAsync(
        string gatewayPaymentId,
        CancellationToken cancellationToken)
    {
        var successful = gatewayPaymentId.StartsWith("pay_mock_", StringComparison.Ordinal);
        var orderId = successful
            ? $"order_mock_{gatewayPaymentId[9..]}"
            : "order_mock_invalid";

        _orders.TryGetValue(orderId, out var order);
        successful = successful && order is not null;
        return Task.FromResult(new GatewayPaymentStatusResult(
            gatewayPaymentId,
            orderId,
            successful ? "captured" : "failed",
            order?.AmountMinor ?? 0,
            order?.Currency ?? _options.Currency,
            successful,
            !successful));
    }

    public bool VerifyWebhookSignature(ReadOnlySpan<byte> payload, string signature) =>
        VerifyHmac(payload, signature, _options.MockSigningSecret);

    public GatewayWebhookEvent ParseWebhook(ReadOnlySpan<byte> payload) =>
        ParseWebhookPayload(payload);

    public Task<GatewayRefundResult> RefundAsync(
        string gatewayPaymentId,
        long amountMinor,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        Task.FromResult(new GatewayRefundResult(
            $"rfnd_mock_{Guid.NewGuid():N}",
            "processed",
            true,
            false,
            null,
            null));

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    internal static bool VerifyHmac(ReadOnlySpan<byte> payload, string signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
        return supplied.Length == expected.Length &&
            CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    internal static GatewayWebhookEvent ParseWebhookPayload(ReadOnlySpan<byte> payload)
    {
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        var eventType = RequiredString(root, "event");
        var eventId = root.TryGetProperty("id", out var idElement)
            ? idElement.GetString()
            : null;
        eventId ??= Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        JsonElement entity = default;
        var hasEntity = TryGetEntity(root, "payment", out entity) ||
            TryGetEntity(root, "refund", out entity) ||
            TryGetEntity(root, "order", out entity);

        return new GatewayWebhookEvent(
            eventId,
            eventType,
            hasEntity && entity.TryGetProperty("order_id", out var orderId) ? orderId.GetString() : null,
            eventType.StartsWith("payment.", StringComparison.Ordinal) && hasEntity
                ? RequiredString(entity, "id")
                : null,
            eventType.StartsWith("refund.", StringComparison.Ordinal) && hasEntity
                ? RequiredString(entity, "id")
                : null,
            hasEntity && entity.TryGetProperty("status", out var status) ? status.GetString() ?? "unknown" : "unknown",
            hasEntity && entity.TryGetProperty("amount", out var amount) ? amount.GetInt64() : null,
            hasEntity && entity.TryGetProperty("currency", out var currency) ? currency.GetString() : null);
    }

    private static bool TryGetEntity(JsonElement root, string name, out JsonElement entity)
    {
        entity = default;
        return root.TryGetProperty("payload", out var payload) &&
            payload.TryGetProperty(name, out var container) &&
            container.TryGetProperty("entity", out entity);
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new InvalidOperationException($"Gateway payload property '{propertyName}' is required.");
}

public sealed class RazorpayPaymentGateway(
    HttpClient httpClient,
    IOptions<PaymentOptions> options) : IPaymentGateway
{
    private readonly PaymentOptions _options = options.Value;

    public string ProviderName => "Razorpay";
    public string? PublicKeyId => _options.RazorpayKeyId;
    public bool IsLive => true;

    public async Task<GatewayOrderResult> CreateOrderAsync(
        GatewayOrderRequest request,
        CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Post, "orders");
        message.Content = JsonContent.Create(new
        {
            amount = request.AmountMinor,
            currency = request.Currency,
            receipt = request.Receipt,
            notes = new Dictionary<string, string>
            {
                ["payment_id"] = request.PaymentId.ToString("D", CultureInfo.InvariantCulture)
            }
        });

        using var response = await httpClient.SendAsync(message, cancellationToken);
        using var document = await ReadSuccessAsync(response, cancellationToken);
        var root = document.RootElement;
        return new GatewayOrderResult(
            RequiredString(root, "id"),
            RequiredString(root, "status"),
            root.GetProperty("amount").GetInt64(),
            RequiredString(root, "currency"));
    }

    public bool VerifyPaymentSignature(
        string gatewayOrderId,
        string gatewayPaymentId,
        string signature)
    {
        var payload = Encoding.UTF8.GetBytes($"{gatewayOrderId}|{gatewayPaymentId}");
        return MockPaymentGateway.VerifyHmac(
            payload,
            signature,
            _options.RazorpayKeySecret!);
    }

    public async Task<GatewayPaymentStatusResult> GetPaymentStatusAsync(
        string gatewayPaymentId,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.SendAsync(
            CreateRequest(HttpMethod.Get, $"payments/{Uri.EscapeDataString(gatewayPaymentId)}"),
            cancellationToken);
        using var document = await ReadSuccessAsync(response, cancellationToken);
        var root = document.RootElement;
        var status = RequiredString(root, "status");
        return new GatewayPaymentStatusResult(
            RequiredString(root, "id"),
            RequiredString(root, "order_id"),
            status,
            root.GetProperty("amount").GetInt64(),
            RequiredString(root, "currency"),
            string.Equals(status, "captured", StringComparison.OrdinalIgnoreCase),
            status is "failed" or "refunded");
    }

    public bool VerifyWebhookSignature(ReadOnlySpan<byte> payload, string signature) =>
        MockPaymentGateway.VerifyHmac(payload, signature, _options.RazorpayWebhookSecret!);

    public GatewayWebhookEvent ParseWebhook(ReadOnlySpan<byte> payload) =>
        MockPaymentGateway.ParseWebhookPayload(payload);

    public async Task<GatewayRefundResult> RefundAsync(
        string gatewayPaymentId,
        long amountMinor,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var message = CreateRequest(
            HttpMethod.Post,
            $"payments/{Uri.EscapeDataString(gatewayPaymentId)}/refund");
        message.Headers.TryAddWithoutValidation("X-Razorpay-Idempotency-Key", idempotencyKey);
        message.Content = JsonContent.Create(new
        {
            amount = amountMinor,
            speed = "normal"
        });

        using var response = await httpClient.SendAsync(message, cancellationToken);
        using var document = await ReadSuccessAsync(response, cancellationToken);
        var root = document.RootElement;
        var status = RequiredString(root, "status");
        return new GatewayRefundResult(
            RequiredString(root, "id"),
            status,
            string.Equals(status, "processed", StringComparison.OrdinalIgnoreCase),
            string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase),
            null,
            null);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_options.RazorpayKeyId}:{_options.RazorpayKeySecret}"));
        var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        return request;
    }

    private static async Task<JsonDocument> ReadSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = Encoding.UTF8.GetString(bytes);
            throw new HttpRequestException(
                $"Razorpay returned HTTP {(int)response.StatusCode}: {detail}",
                null,
                response.StatusCode);
        }

        return JsonDocument.Parse(bytes);
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new InvalidOperationException($"Razorpay response property '{propertyName}' is required.");
}
