using System.Text.Json;
using DoodhDirect.Api.Controllers;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Regression tests for the admin product upsert HTTP contract.
///
/// The mobile app sends item-level Tax &amp; Charges assignments as
/// <c>applicableChargeIds</c> in the create/update body. That field was once
/// missing from <see cref="UpsertProductApiRequest"/>, so ASP.NET silently
/// discarded it and every product save wiped (or never wrote) the charge
/// mappings — the product list showed no taxes after reopening, and the
/// "Applicable on All" toggle stayed enabled with zero mappings to guard.
/// These tests deserialize the exact payload the app sends (web JSON
/// defaults, same as the API pipeline) and prove the ids survive mapping to
/// the application request.
/// </summary>
public sealed class CatalogueUpsertApiRequestTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    private const string MobilePayload = """
        {
          "sku": "MLK001",
          "name": "Cow Milk",
          "description": "Cow Milk",
          "categoryId": "11111111-1111-1111-1111-111111111111",
          "unitOfMeasure": "litre",
          "price": 60,
          "branchIds": ["22222222-2222-2222-2222-222222222222"],
          "applicableChargeIds": ["33333333-3333-3333-3333-333333333333"]
        }
        """;

    [Fact]
    public void UpsertProduct_BindsApplicableChargeIds_FromMobilePayload()
    {
        var apiRequest = JsonSerializer.Deserialize<UpsertProductApiRequest>(MobilePayload, WebDefaults);

        Assert.NotNull(apiRequest);
        Assert.Equal(
            new[] { Guid.Parse("33333333-3333-3333-3333-333333333333") },
            apiRequest!.ApplicableChargeIds);
    }

    [Fact]
    public void UpsertProduct_ForwardsApplicableChargeIds_ToApplicationRequest()
    {
        var apiRequest = JsonSerializer.Deserialize<UpsertProductApiRequest>(MobilePayload, WebDefaults);

        var applicationRequest = apiRequest!.ToApplicationRequest();

        Assert.Equal(
            new[] { Guid.Parse("33333333-3333-3333-3333-333333333333") },
            applicationRequest.ApplicableChargeIds);
    }

    [Fact]
    public void UpsertProduct_OmittedChargeIds_DefaultToNull()
    {
        const string payloadWithoutCharges = """
            {
              "sku": "MLK001",
              "name": "Cow Milk",
              "description": null,
              "categoryId": "11111111-1111-1111-1111-111111111111",
              "unitOfMeasure": "litre",
              "price": 60,
              "branchIds": ["22222222-2222-2222-2222-222222222222"]
            }
            """;

        var apiRequest = JsonSerializer.Deserialize<UpsertProductApiRequest>(payloadWithoutCharges, WebDefaults);

        Assert.NotNull(apiRequest);
        Assert.Null(apiRequest!.ApplicableChargeIds);
        Assert.Null(apiRequest.ToApplicationRequest().ApplicableChargeIds);
    }
}
