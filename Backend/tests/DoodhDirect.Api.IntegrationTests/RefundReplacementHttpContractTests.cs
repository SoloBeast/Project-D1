using System.Net;
using System.Text.Json;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.RefundReplacement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// D1-D slice 6.1.1 — replacement-details HTTP contract verification
/// (test-only; no production changes).
///
/// The service-level slice 6.1 tests prove the <i>values</i>; these tests
/// prove the <i>wire encoding</i> through the real test host: the production
/// MVC JSON options (the exact instance the output formatters use), the live
/// OpenAPI document, the controller route templates, and the authorization
/// guards. A seeded end-to-end HTTP flow is deliberately not built here: it
/// would exercise the real-clock scheduling/cutoff path and the factory's
/// shared database rather than the contract, and its failure modes would be
/// environmental instead of contractual.
/// </summary>
public sealed class RefundReplacementHttpContractTests : IClassFixture<FoundationApiFactory>
{
    private static readonly Guid RequestId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DeliveryId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OccurrenceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateOnly ReplacementDay = new(2026, 8, 20);

    private readonly FoundationApiFactory _factory;

    public RefundReplacementHttpContractTests(FoundationApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The exact <see cref="JsonSerializerOptions"/> the MVC output
    /// formatters use, resolved from the running test host.
    /// </summary>
    private JsonSerializerOptions ProductionJsonOptions() =>
        _factory.Services.GetRequiredService<IOptions<MvcJsonOptions>>().Value.JsonSerializerOptions;

    [Fact]
    public void MvcJson_UsesCamelCasePropertyNames()
    {
        var options = ProductionJsonOptions();

        // Locks the casing contract the mobile clients rely on. Every
        // replacement-field name asserted below depends on this policy.
        Assert.Same(JsonNamingPolicy.CamelCase, options.PropertyNamingPolicy);
    }

    [Fact]
    public void StaffDetail_ApprovedReplacement_SerializesDateOnlyAndBusinessReferences()
    {
        var json = Serialize(DetailResult(
            status: RefundReplacementStatus.Approved,
            choice: CustomerChoice.ReDelivery,
            replacementDate: ReplacementDay,
            replacementDeliveryPublicId: OccurrenceId,
            replacementDeliveryNumber: "DEL-2026-000123"));

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false });
        var root = document.RootElement;
        Assert.Equal("2026-08-20", root.GetProperty("replacementDate").GetString());
        Assert.Equal("DEL-2026-000123", root.GetProperty("replacementDeliveryNumber").GetString());
        Assert.Equal(
            OccurrenceId.ToString(),
            root.GetProperty("replacementDeliveryPublicId").GetString());
        Assert.Equal("Approved", root.GetProperty("status").GetString());
        Assert.Equal("ReDelivery", root.GetProperty("customerChoice").GetString());
        Assert.Equal("DeliveryFailed", root.GetProperty("source").GetString());
        // The original failed delivery keeps its own public reference; the
        // replacement references are separate properties, never mixed in.
        Assert.Equal("DEL-2026-000100", root.GetProperty("deliveryNumber").GetString());
    }

    [Fact]
    public void StaffDetail_UnmaterializedReplacement_ExposesNullNumberNeverAGuid()
    {
        var json = Serialize(DetailResult(
            status: RefundReplacementStatus.Approved,
            choice: CustomerChoice.ReDelivery,
            replacementDate: ReplacementDay,
            replacementDeliveryPublicId: OccurrenceId,
            replacementDeliveryNumber: null));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("2026-08-20", root.GetProperty("replacementDate").GetString());
        // No operational delivery exists yet: the value is an explicit null,
        // never a fabricated number and never an internal identifier.
        Assert.True(root.TryGetProperty("replacementDeliveryNumber", out var number));
        Assert.Equal(JsonValueKind.Null, number.ValueKind);
    }

    [Fact]
    public void StaffDetail_RefundAndAwaitingChoice_CarryNullReplacementFields()
    {
        var refund = Serialize(DetailResult(
            status: RefundReplacementStatus.Approved,
            choice: CustomerChoice.Refund));
        using (var document = JsonDocument.Parse(refund))
        {
            var root = document.RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("replacementDate").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("replacementDeliveryPublicId").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("replacementDeliveryNumber").ValueKind);
        }

        var awaiting = Serialize(DetailResult(
            status: RefundReplacementStatus.AwaitingCustomerChoice,
            choice: null));
        using (var document = JsonDocument.Parse(awaiting))
        {
            var root = document.RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("replacementDate").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("replacementDeliveryPublicId").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("replacementDeliveryNumber").ValueKind);
        }
    }

    [Fact]
    public void StaffList_Item_ExposesPersistedReplacementDate()
    {
        var withDate = Serialize(ListItem(replacementDate: ReplacementDay));
        using (var document = JsonDocument.Parse(withDate))
        {
            Assert.Equal(
                "2026-08-20",
                document.RootElement.GetProperty("replacementDate").GetString());
        }

        var withoutDate = Serialize(ListItem(replacementDate: null));
        using (var document = JsonDocument.Parse(withoutDate))
        {
            Assert.Equal(
                JsonValueKind.Null,
                document.RootElement.GetProperty("replacementDate").ValueKind);
        }
    }

    [Fact]
    public void ProposalResult_SerializesAdvisoryDateOnly()
    {
        var json = Serialize(new ReplacementDateProposalResult(RequestId, new DateOnly(2026, 8, 19)));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(RequestId.ToString(), root.GetProperty("requestId").GetString());
        Assert.Equal("2026-08-19", root.GetProperty("proposedDate").GetString());
    }

    [Fact]
    public void FailedStatus_SerializesAsFailedString()
    {
        var json = Serialize(DetailResult(status: RefundReplacementStatus.Failed));

        using var document = JsonDocument.Parse(json);
        Assert.Equal("Failed", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OpenApi_RefundReplacementRoutesAndSchemas_ExposeReplacementContract()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var paths = document.RootElement.GetProperty("paths");

        // Staff routes (exact templates from RefundReplacementController).
        AssertVerb(paths, "/api/v1/refund-replacements", "get");
        AssertVerb(paths, "/api/v1/refund-replacements/{requestId}", "get");
        AssertVerb(paths, "/api/v1/refund-replacements/{requestId}/approve", "post");
        AssertVerb(paths, "/api/v1/refund-replacements/{requestId}/reject", "post");
        AssertVerb(paths, "/api/v1/refund-replacements/{requestId}/complete", "post");
        AssertVerb(paths, "/api/v1/refund-replacements/{requestId}/replacement-date-proposal", "get");

        // Customer routes (exact templates from CustomerRefundReplacementListController).
        AssertVerb(paths, "/api/v1/customer/refund-replacements", "get");
        AssertVerb(paths, "/api/v1/customer/refund-replacements/{requestId}", "get");
        AssertVerb(paths, "/api/v1/customer/refund-replacements/{requestId}/choose", "post");

        // The approval input accepts the staff-confirmed date (deserialization
        // is case-insensitive, so the schema name is asserted as declared).
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var approveSchema = FindSchema(schemas, "ApproveRefundReplacementRequest");
        Assert.NotNull(approveSchema);
        Assert.True(
            approveSchema!.Value.TryGetProperty("properties", out var approveProperties)
                && approveProperties.TryGetProperty("replacementDate", out _),
            "ApproveRefundReplacementRequest schema must declare replacementDate.");

        // Detail responses expose the persisted date (date-only) and the
        // operational number alongside the occurrence public id.
        var detailSchema = FindSchema(schemas, "RefundReplacementRequestResult");
        Assert.NotNull(detailSchema);
        var detailProperties = detailSchema!.Value.GetProperty("properties");
        var replacementDate = detailProperties.GetProperty("replacementDate");
        Assert.Contains("string", SchemaTypes(replacementDate));
        Assert.Equal("date", SchemaFormat(replacementDate));
        var deliveryNumber = detailProperties.GetProperty("replacementDeliveryNumber");
        Assert.Contains("string", SchemaTypes(deliveryNumber));
        Assert.True(detailProperties.TryGetProperty("replacementDeliveryPublicId", out _));

        // Staff list rows expose the persisted date.
        var listSchema = FindSchema(schemas, "RefundReplacementListItem");
        Assert.NotNull(listSchema);
        var listDate = listSchema!.Value.GetProperty("properties").GetProperty("replacementDate");
        Assert.Contains("string", SchemaTypes(listDate));
        Assert.Equal("date", SchemaFormat(listDate));

        // The status property is present on the detail schema. Note: the
        // document generator declares enum schemas as{"type": "integer"}
        // without enumerating values, while the runtime
        // JsonStringEnumConverter emits strings ("Approved", "Failed", ...)
        // as proven by the serialization tests above. That generator-wide
        // behavior predates slice 6.1 and is out of scope for this
        // test-only slice, so no enum-value assertion is made here.
        Assert.True(detailProperties.TryGetProperty("status", out _));
    }

    [Theory]
    [InlineData("GET", "/api/v1/refund-replacements")]
    [InlineData("GET", "/api/v1/refund-replacements/11111111-1111-1111-1111-111111111111")]
    [InlineData("GET", "/api/v1/refund-replacements/11111111-1111-1111-1111-111111111111/replacement-date-proposal")]
    [InlineData("GET", "/api/v1/customer/refund-replacements")]
    [InlineData("GET", "/api/v1/customer/refund-replacements/11111111-1111-1111-1111-111111111111")]
    public async Task Anonymous_ReplacementEndpoints_RequireAuthentication(string method, string path)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request, CancellationToken.None);

        // Authorization, pagination, lifecycle, and existing behavior are
        // untouched: without credentials these routes still challenge.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
    }

    private string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, ProductionJsonOptions());

    private static void AssertVerb(JsonElement paths, string template, string verb)
    {
        Assert.True(
            paths.TryGetProperty(template, out var pathItem)
                && pathItem.TryGetProperty(verb, out _),
            $"OpenAPI must describe {verb.ToUpperInvariant()} {template}.");
    }

    private static JsonElement? FindSchema(JsonElement schemas, string name)
    {
        foreach (var schema in schemas.EnumerateObject())
        {
            if (string.Equals(schema.Name, name, StringComparison.Ordinal)
                || string.Equals(schema.Name, name, StringComparison.OrdinalIgnoreCase)
                || schema.Name.EndsWith("." + name, StringComparison.Ordinal))
            {
                return schema.Value;
            }
        }

        return null;
    }

    private static string? SchemaFormat(JsonElement schema)
    {
        if (schema.TryGetProperty("format", out var format))
        {
            return format.GetString();
        }

        if (schema.TryGetProperty("anyOf", out var anyOf))
        {
            return anyOf.EnumerateArray().Select(SchemaFormat).FirstOrDefault(x => x is not null);
        }

        return null;
    }

    private static List<string> SchemaTypes(JsonElement schema)
    {
        // OpenAPI 3.1 emits nullable as a type array (e.g. ["string","null"]).
        if (schema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.Array)
            {
                return type.EnumerateArray()
                    .Select(x => x.GetString() ?? string.Empty)
                    .Where(x => x.Length > 0)
                    .ToList();
            }

            if (type.GetString() is string single)
            {
                return [single];
            }
        }

        if (schema.TryGetProperty("anyOf", out var anyOf))
        {
            return anyOf.EnumerateArray()
                .Select(x => x.TryGetProperty("type", out var t) ? t.GetString() : null)
                .Where(x => x is not null)
                .Cast<string>()
                .ToList();
        }

        return [];
    }

    private static RefundReplacementRequestResult DetailResult(
        RefundReplacementStatus status = RefundReplacementStatus.Pending,
        CustomerChoice? choice = null,
        DateOnly? replacementDate = null,
        Guid? replacementDeliveryPublicId = null,
        string? replacementDeliveryNumber = null) => new(
        RequestId,
        "RR-2026-0001",
        101,
        Guid.Parse("44444444-4444-4444-4444-444444444444"),
        "ORD-1001",
        Guid.Parse("55555555-5555-5555-5555-555555555555"),
        new DateTime(2026, 8, 17, 6, 0, 0, DateTimeKind.Unspecified),
        48,
        10m,
        "Delivery vehicle breakdown",
        DeliveryId,
        "DEL-2026-000100",
        55,
        "Asha Rao",
        "9876543210",
        7,
        "BR-07",
        "Bengaluru Dairy",
        null,
        null,
        RefundReplacementType.Refund,
        RefundReplacementSource.DeliveryFailed,
        status,
        choice,
        choice is null ? null : new DateTime(2026, 8, 17, 10, 0, 0, DateTimeKind.Unspecified),
        "Milk was sour",
        null,
        new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Unspecified),
        new DateTime(2026, 8, 19, 9, 0, 0, DateTimeKind.Unspecified),
        9,
        "Support Lead",
        new DateTime(2026, 8, 17, 11, 0, 0, DateTimeKind.Unspecified),
        "Approved after review",
        null,
        null,
        null,
        null,
        false,
        [],
        replacementDeliveryPublicId,
        replacementDate,
        replacementDeliveryNumber);

    private static RefundReplacementListItem ListItem(DateOnly? replacementDate) => new(
        RequestId,
        "RR-2026-0001",
        101,
        Guid.Parse("44444444-4444-4444-4444-444444444444"),
        "ORD-1001",
        Guid.Parse("55555555-5555-5555-5555-555555555555"),
        new DateTime(2026, 8, 17, 6, 0, 0, DateTimeKind.Unspecified),
        48,
        10m,
        "Delivery vehicle breakdown",
        DeliveryId,
        "DEL-2026-000100",
        55,
        "Asha Rao",
        "9876543210",
        7,
        "BR-07",
        "Bengaluru Dairy",
        null,
        null,
        RefundReplacementType.Refund,
        RefundReplacementSource.DeliveryFailed,
        RefundReplacementStatus.Approved,
        CustomerChoice.ReDelivery,
        new DateTime(2026, 8, 17, 10, 0, 0, DateTimeKind.Unspecified),
        "Milk was sour",
        null,
        new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Unspecified),
        new DateTime(2026, 8, 19, 9, 0, 0, DateTimeKind.Unspecified),
        false,
        0,
        replacementDate);
}
