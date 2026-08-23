using System.Net;
using System.Text;
using DoodhDirect.Infrastructure.Customer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class GoogleAddressLocationLookupTests
{
    [Fact]
    public async Task ReverseGeocodeAsync_MapsGoogleAddressComponents()
    {
        Uri? requestedUri = null;
        var lookup = CreateLookup(
            request =>
            {
                requestedUri = request.RequestUri;
                return JsonResponse("""
                    {
                      "status": "OK",
                      "results": [{
                        "formatted_address": "12 MG Road, Indiranagar, Bengaluru, Karnataka 560038, India",
                        "address_components": [
                          { "long_name": "12", "types": ["street_number"] },
                          { "long_name": "MG Road", "types": ["route"] },
                          { "long_name": "Indiranagar", "types": ["sublocality_level_1", "sublocality"] },
                          { "long_name": "Bengaluru", "types": ["locality"] },
                          { "long_name": "Karnataka", "types": ["administrative_area_level_1"] },
                          { "long_name": "560038", "types": ["postal_code"] },
                          { "long_name": "India", "types": ["country"] }
                        ],
                        "geometry": { "location": { "lat": 12.9716123, "lng": 77.6412345 } }
                      }]
                    }
                    """);
            });

        var result = await lookup.ReverseGeocodeAsync(12.97m, 77.64m, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("12 MG Road, Indiranagar, Bengaluru, Karnataka 560038, India", result.AddressLine1);
        Assert.Equal("Indiranagar", result.Locality);
        Assert.Equal("Bengaluru", result.City);
        Assert.Equal("Karnataka", result.State);
        Assert.Equal("560038", result.PinCode);
        Assert.Equal("India", result.Country);
        Assert.Equal(12.9716123m, result.Latitude);
        Assert.Equal(77.6412345m, result.Longitude);
        Assert.Contains("latlng=12.97%2C77.64", requestedUri!.Query);
        Assert.Contains("key=server-key", requestedUri.Query);
    }

    [Theory]
    [InlineData("{\"status\":\"ZERO_RESULTS\",\"results\":[]}")]
    [InlineData("{\"status\":\"REQUEST_DENIED\",\"results\":[]}")]
    [InlineData("not-json")]
    public async Task ReverseGeocodeAsync_ReturnsNullForUnavailableProviderResult(string payload)
    {
        var lookup = CreateLookup(_ => JsonResponse(payload));

        var result = await lookup.ReverseGeocodeAsync(12.97m, 77.64m, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReverseGeocodeAsync_MissingKeyDoesNotSendProviderRequest()
    {
        var requestCount = 0;
        var lookup = CreateLookup(
            _ =>
            {
                requestCount++;
                return JsonResponse("{}");
            },
            apiKey: null);

        var result = await lookup.ReverseGeocodeAsync(12.97m, 77.64m, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task ReverseGeocodeAsync_NonSuccessHttpResponseReturnsNull()
    {
        var lookup = CreateLookup(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await lookup.ReverseGeocodeAsync(12.97m, 77.64m, CancellationToken.None);

        Assert.Null(result);
    }

    private static GoogleAddressLocationLookup CreateLookup(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        string? apiKey = "server-key")
    {
        var client = new HttpClient(new StubHttpMessageHandler(responseFactory));
        var options = Options.Create(new AddressGeocodingOptions { ApiKey = apiKey });
        return new GoogleAddressLocationLookup(
            client,
            options,
            NullLogger<GoogleAddressLocationLookup>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
    }
}
