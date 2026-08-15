using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Customer;
using DoodhDirect.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

[ApiController]
[Route("api/v1/customers/me")]
[Tags("Customers")]
[Produces("application/json")]
public sealed class CustomerController(
    ICustomerService customerService,
    IAddressLocationLookup addressLocationLookup) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ProfileReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<CustomerProfileResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CustomerProfileResult>>> GetProfile(CancellationToken cancellationToken) =>
        Ok(ApiResponse<CustomerProfileResult>.Ok(await customerService.GetProfileAsync(RequireUserId(), cancellationToken)));

    [HttpPatch]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ProfileUpdateOwn)]
    [ProducesResponseType(typeof(ApiResponse<CustomerProfileResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CustomerProfileResult>>> UpdateProfile(
        [FromBody] UpdateCustomerProfileApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await customerService.UpdateProfileAsync(
            RequireUserId(),
            new UpdateCustomerProfileRequest(request.FirstName, request.LastName, request.DateOfBirth, request.Gender, request.AlternateMobile),
            cancellationToken);
        return Ok(ApiResponse<CustomerProfileResult>.Ok(result));
    }

    [HttpGet("addresses")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ProfileReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<CustomerAddressResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<CustomerAddressResult>>>> GetAddresses(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<CustomerAddressResult>>.Ok(await customerService.GetAddressesAsync(RequireUserId(), cancellationToken)));

    [HttpPost("addresses")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ProfileUpdateOwn)]
    [ProducesResponseType(typeof(ApiResponse<CustomerAddressResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CustomerAddressResult>>> CreateAddress(
        [FromBody] CustomerAddressApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await customerService.CreateAddressAsync(RequireUserId(), request.ToApplicationRequest(), cancellationToken);
        return Ok(ApiResponse<CustomerAddressResult>.Ok(result));
    }

    [HttpGet("addresses/{addressId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ProfileReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<CustomerAddressResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CustomerAddressResult>>> GetAddress(Guid addressId, CancellationToken cancellationToken) =>
        Ok(ApiResponse<CustomerAddressResult>.Ok(await customerService.GetAddressAsync(RequireUserId(), addressId, cancellationToken)));

    [HttpPatch("addresses/{addressId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ProfileUpdateOwn)]
    [ProducesResponseType(typeof(ApiResponse<CustomerAddressResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CustomerAddressResult>>> UpdateAddress(
        Guid addressId,
        [FromBody] CustomerAddressApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await customerService.UpdateAddressAsync(RequireUserId(), addressId, request.ToApplicationRequest(), cancellationToken);
        return Ok(ApiResponse<CustomerAddressResult>.Ok(result));
    }

    [HttpDelete("addresses/{addressId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ProfileUpdateOwn)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<object>>> DeactivateAddress(Guid addressId, CancellationToken cancellationToken)
    {
        await customerService.DeactivateAddressAsync(RequireUserId(), addressId, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "Address deactivated."));
    }

    [HttpGet("address-lookup/reverse")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ProfileUpdateOwn)]
    [ProducesResponseType(typeof(ApiResponse<AddressLookupResult?>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<AddressLookupResult?>>> ReverseGeocode(
        [FromQuery, Range(-90, 90)] decimal latitude,
        [FromQuery, Range(-180, 180)] decimal longitude,
        CancellationToken cancellationToken)
    {
        var result = await addressLocationLookup.ReverseGeocodeAsync(latitude, longitude, cancellationToken);
        return Ok(ApiResponse<AddressLookupResult?>.Ok(result, result is null ? "No address match was available." : null));
    }

    private long RequireUserId()
    {
        var value = User.FindFirstValue("user_id");
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : throw new UnauthorizedAppException();
    }
}

public sealed record UpdateCustomerProfileApiRequest(
    [MaxLength(100)] string? FirstName,
    [MaxLength(100)] string? LastName,
    DateOnly? DateOfBirth,
    [MaxLength(40)] string? Gender,
    [MaxLength(20)] string? AlternateMobile);

public sealed record CustomerAddressApiRequest(
    [Required, MaxLength(80)] string Label,
    [Required, MaxLength(200)] string AddressLine1,
    [MaxLength(200)] string? AddressLine2,
    [Required, MaxLength(120)] string Locality,
    [Required, MaxLength(100)] string City,
    [Required, MaxLength(100)] string State,
    [Required, StringLength(6, MinimumLength = 6)] string PinCode,
    [MaxLength(160)] string? Landmark,
    [MaxLength(500)] string? DeliveryInstructions,
    [Required, MaxLength(160)] string ContactName,
    [Required, MaxLength(20)] string ContactMobile,
    decimal? Latitude,
    decimal? Longitude,
    bool IsDefault)
{
    public UpsertCustomerAddressRequest ToApplicationRequest() => new(
        Label, AddressLine1, AddressLine2, Locality, City, State, PinCode, Landmark,
        DeliveryInstructions, ContactName, ContactMobile, Latitude, Longitude, IsDefault);
}

[ApiController]
[Route("api/v1/admin/customers")]
[Tags("Customer administration")]
[Produces("application/json")]
public sealed class CustomerAdministrationController(ICustomerService customerService) : ControllerBase
{
    [HttpGet("{customerId:guid}/profile")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CustomerProfilesRead)]
    [ProducesResponseType(typeof(ApiResponse<CustomerProfileResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CustomerProfileResult>>> GetProfile(Guid customerId, CancellationToken cancellationToken) =>
        Ok(ApiResponse<CustomerProfileResult>.Ok(await customerService.GetProfileByCustomerIdAsync(customerId, cancellationToken)));

    [HttpPatch("{customerId:guid}/profile")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CustomerProfilesManage)]
    [ProducesResponseType(typeof(ApiResponse<CustomerProfileResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CustomerProfileResult>>> UpdateProfile(
        Guid customerId,
        [FromBody] UpdateCustomerProfileApiRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<CustomerProfileResult>.Ok(await customerService.UpdateProfileByCustomerIdAsync(
            customerId,
            new UpdateCustomerProfileRequest(request.FirstName, request.LastName, request.DateOfBirth, request.Gender, request.AlternateMobile),
            cancellationToken)));

    [HttpGet("{customerId:guid}/addresses")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CustomerProfilesRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<CustomerAddressResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<CustomerAddressResult>>>> GetAddresses(Guid customerId, CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<CustomerAddressResult>>.Ok(await customerService.GetAddressesByCustomerIdAsync(customerId, cancellationToken)));

    [HttpPost("{customerId:guid}/addresses")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CustomerProfilesManage)]
    [ProducesResponseType(typeof(ApiResponse<CustomerAddressResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CustomerAddressResult>>> CreateAddress(
        Guid customerId,
        [FromBody] CustomerAddressApiRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<CustomerAddressResult>.Ok(await customerService.CreateAddressByCustomerIdAsync(
            customerId, request.ToApplicationRequest(), cancellationToken)));

    [HttpGet("{customerId:guid}/addresses/{addressId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CustomerProfilesRead)]
    [ProducesResponseType(typeof(ApiResponse<CustomerAddressResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CustomerAddressResult>>> GetAddress(
        Guid customerId,
        Guid addressId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<CustomerAddressResult>.Ok(await customerService.GetAddressByCustomerIdAsync(
            customerId, addressId, cancellationToken)));

    [HttpPatch("{customerId:guid}/addresses/{addressId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CustomerProfilesManage)]
    [ProducesResponseType(typeof(ApiResponse<CustomerAddressResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CustomerAddressResult>>> UpdateAddress(
        Guid customerId,
        Guid addressId,
        [FromBody] CustomerAddressApiRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<CustomerAddressResult>.Ok(await customerService.UpdateAddressByCustomerIdAsync(
            customerId, addressId, request.ToApplicationRequest(), cancellationToken)));

    [HttpDelete("{customerId:guid}/addresses/{addressId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CustomerProfilesManage)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<object>>> DeactivateAddress(
        Guid customerId,
        Guid addressId,
        CancellationToken cancellationToken)
    {
        await customerService.DeactivateAddressByCustomerIdAsync(customerId, addressId, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "Address deactivated."));
    }
}
