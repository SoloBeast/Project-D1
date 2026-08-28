using System.Data;
using System.Text.RegularExpressions;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Customer;
using DoodhDirect.Application.Setup;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Customer;

public sealed class CustomerService(
    DoodhDirectDbContext dbContext,
    INumberSeriesService numberSeriesService,
    IIndiaTimeProvider timeProvider) : ICustomerService
{
    private static readonly Regex PinCodePattern = new("^[1-9][0-9]{5}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MobilePattern = new("^(?:\\+?91)?[6-9][0-9]{9}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly IReadOnlySet<string> SupportedGenders =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Male",
            "Female",
            "Other",
        };

    public async Task<CustomerProfileResult> GetProfileAsync(long userId, CancellationToken cancellationToken)
    {
        var profile = await GetOrCreateProfileAsync(userId, cancellationToken);
        return profile.ToResult();
    }

    public async Task<CustomerProfileResult> UpdateProfileAsync(long userId, UpdateCustomerProfileRequest request, CancellationToken cancellationToken)
    {
        ValidateProfile(request);
        var profile = await GetOrCreateProfileAsync(userId, cancellationToken);
        profile.Update(request.FirstName, request.LastName, request.DateOfBirth, request.Gender, request.AlternateMobile);
        await dbContext.SaveChangesAsync(cancellationToken);
        return profile.ToResult();
    }

    public async Task<IReadOnlyList<CustomerAddressResult>> GetAddressesAsync(long userId, CancellationToken cancellationToken) =>
        await dbContext.CustomerAddresses
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.IsActive)
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.Label)
            .Select(x => x.ToResult())
            .ToListAsync(cancellationToken);

    public async Task<CustomerAddressResult> GetAddressAsync(long userId, Guid publicId, CancellationToken cancellationToken) =>
        (await FindAddressAsync(userId, publicId, cancellationToken)).ToResult();

    public async Task<CustomerAddressResult> CreateAddressAsync(long userId, UpsertCustomerAddressRequest request, CancellationToken cancellationToken)
    {
        ValidateAddress(request);
        await EnsureUserExistsAsync(userId, cancellationToken);
        CustomerAddress? createdAddress = null;
        await ExecuteInTransactionAsync(async () =>
        {
            createdAddress = new CustomerAddress(
                userId,
                request.Label.Trim(),
                request.AddressLine1.Trim(),
                request.Locality.Trim(),
                request.City.Trim(),
                request.State.Trim(),
                request.PinCode.Trim(),
                request.ContactName.Trim(),
                request.ContactMobile.Trim(),
                request.Latitude!.Value,
                request.Longitude!.Value);
            if (request.IsDefault)
            {
                await ClearOtherDefaultsAsync(userId, 0, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            createdAddress.Update(
                request.Label, request.AddressLine1, request.AddressLine2, request.Locality, request.City,
                request.State, request.PinCode, request.Landmark, request.DeliveryInstructions,
                request.ContactName, request.ContactMobile, request.IsDefault, request.Latitude.Value, request.Longitude.Value);
            dbContext.CustomerAddresses.Add(createdAddress);
            await dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
        return createdAddress!.ToResult();
    }

    public async Task<CustomerAddressResult> UpdateAddressAsync(long userId, Guid publicId, UpsertCustomerAddressRequest request, CancellationToken cancellationToken)
    {
        ValidateAddress(request);
        CustomerAddress? updatedAddress = null;
        await ExecuteInTransactionAsync(async () =>
        {
            updatedAddress = await FindAddressAsync(userId, publicId, cancellationToken);
            if (request.IsDefault && !updatedAddress.IsDefault)
            {
                await ClearOtherDefaultsAsync(userId, updatedAddress.Id, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            updatedAddress.Update(
                request.Label, request.AddressLine1, request.AddressLine2, request.Locality, request.City,
                request.State, request.PinCode, request.Landmark, request.DeliveryInstructions,
                request.ContactName, request.ContactMobile, request.IsDefault, request.Latitude!.Value, request.Longitude!.Value);
            await dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
        return updatedAddress!.ToResult();
    }

    public async Task DeactivateAddressAsync(long userId, Guid publicId, CancellationToken cancellationToken)
    {
        await ExecuteInTransactionAsync(async () =>
        {
            var address = await FindAddressAsync(userId, publicId, cancellationToken);
            address.Deactivate();
            await dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<CustomerProfileResult> GetProfileByCustomerIdAsync(Guid customerId, CancellationToken cancellationToken) =>
        (await GetProfileAsync(await ResolveCustomerIdAsync(customerId, cancellationToken), cancellationToken));

    public async Task<CustomerProfileResult> UpdateProfileByCustomerIdAsync(Guid customerId, UpdateCustomerProfileRequest request, CancellationToken cancellationToken) =>
        await UpdateProfileAsync(await ResolveCustomerIdAsync(customerId, cancellationToken), request, cancellationToken);

    public async Task<IReadOnlyList<CustomerAddressResult>> GetAddressesByCustomerIdAsync(Guid customerId, CancellationToken cancellationToken) =>
        await GetAddressesAsync(await ResolveCustomerIdAsync(customerId, cancellationToken), cancellationToken);

    public async Task<CustomerAddressResult> GetAddressByCustomerIdAsync(Guid customerId, Guid addressPublicId, CancellationToken cancellationToken) =>
        await GetAddressAsync(await ResolveCustomerIdAsync(customerId, cancellationToken), addressPublicId, cancellationToken);

    public async Task<CustomerAddressResult> CreateAddressByCustomerIdAsync(Guid customerId, UpsertCustomerAddressRequest request, CancellationToken cancellationToken) =>
        await CreateAddressAsync(await ResolveCustomerIdAsync(customerId, cancellationToken), request, cancellationToken);

    public async Task<CustomerAddressResult> UpdateAddressByCustomerIdAsync(Guid customerId, Guid addressPublicId, UpsertCustomerAddressRequest request, CancellationToken cancellationToken) =>
        await UpdateAddressAsync(await ResolveCustomerIdAsync(customerId, cancellationToken), addressPublicId, request, cancellationToken);

    public async Task DeactivateAddressByCustomerIdAsync(Guid customerId, Guid addressPublicId, CancellationToken cancellationToken) =>
        await DeactivateAddressAsync(await ResolveCustomerIdAsync(customerId, cancellationToken), addressPublicId, cancellationToken);

    private async Task<long> ResolveCustomerIdAsync(Guid customerId, CancellationToken cancellationToken) =>
        await dbContext.Users
            .Where(x => x.PublicId == customerId && x.IsActive && x.UserType == Domain.Identity.UserType.Customer)
            .Select(x => (long?)x.Id)
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new NotFoundException("The customer was not found.");

    private async Task<CustomerProfile> GetOrCreateProfileAsync(long userId, CancellationToken cancellationToken)
    {
        var profile = await dbContext.CustomerProfiles.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (profile is not null)
        {
            return profile;
        }

        await EnsureUserExistsAsync(userId, cancellationToken);
        CustomerProfile? createdProfile = null;
        await ExecuteInTransactionAsync(async () =>
        {
            createdProfile = new CustomerProfile(userId);
            createdProfile.AssignCustomerNumber(
                await numberSeriesService.GetNextNumberAsync("CUSTOMER", userId, cancellationToken));
            dbContext.CustomerProfiles.Add(createdProfile);
            await dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
        return createdProfile!;
    }

    private async Task<CustomerAddress> FindAddressAsync(long userId, Guid publicId, CancellationToken cancellationToken) =>
        await dbContext.CustomerAddresses.SingleOrDefaultAsync(
            x => x.UserId == userId && x.PublicId == publicId && x.IsActive,
            cancellationToken)
        ?? throw new NotFoundException("The address was not found.");

    private async Task EnsureUserExistsAsync(long userId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Users.AnyAsync(x => x.Id == userId && x.IsActive, cancellationToken))
        {
            throw new NotFoundException("The customer was not found.");
        }
    }

    private async Task ClearOtherDefaultsAsync(long userId, long addressId, CancellationToken cancellationToken)
    {
        var defaults = await dbContext.CustomerAddresses
            .Where(x => x.UserId == userId && x.Id != addressId && x.IsActive && x.IsDefault)
            .ToListAsync(cancellationToken);
        foreach (var address in defaults)
        {
            address.ClearDefault();
        }
    }

    private async Task ExecuteInTransactionAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private void ValidateProfile(UpdateCustomerProfileRequest request)
    {
        if (request.DateOfBirth > timeProvider.Today)
        {
            throw new ValidationAppException("Date of birth cannot be in the future.", nameof(request.DateOfBirth));
        }

        ValidateGender(request.Gender);
        ValidateMobile(request.AlternateMobile, nameof(request.AlternateMobile));
    }

    private static void ValidateAddress(UpsertCustomerAddressRequest request)
    {
        ValidateRequired(request.Label, nameof(request.Label), 80);
        ValidateRequired(request.AddressLine1, nameof(request.AddressLine1), 200);
        ValidateRequired(request.Locality, nameof(request.Locality), 120);
        ValidateRequired(request.City, nameof(request.City), 100);
        ValidateRequired(request.State, nameof(request.State), 100);
        ValidateRequired(request.PinCode, nameof(request.PinCode), 6);
        ValidateRequired(request.ContactName, nameof(request.ContactName), 160);
        ValidateRequired(request.ContactMobile, nameof(request.ContactMobile), 20);

        if (!PinCodePattern.IsMatch(request.PinCode.Trim()))
        {
            throw new ValidationAppException("PIN code must be a valid six-digit Indian PIN code.", nameof(request.PinCode));
        }

        ValidateMobile(request.ContactMobile, nameof(request.ContactMobile));
        if (!request.Latitude.HasValue || request.Latitude is < -90 or > 90)
        {
            throw new ValidationAppException("Latitude must be between -90 and 90.", nameof(request.Latitude));
        }

        if (!request.Longitude.HasValue || request.Longitude is < -180 or > 180)
        {
            throw new ValidationAppException("Longitude must be between -180 and 180.", nameof(request.Longitude));
        }
    }

    private static void ValidateRequired(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ValidationAppException("This field is required.", field);
        }

        if (value.Trim().Length > maxLength)
        {
            throw new ValidationAppException($"This field cannot exceed {maxLength} characters.", field);
        }
    }

    private static void ValidateGender(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            !SupportedGenders.Contains(value.Trim()))
        {
            throw new ValidationAppException(
                "Gender must be Male, Female, or Other.",
                nameof(UpdateCustomerProfileRequest.Gender));
        }
    }

    private static void ValidateMobile(string? value, string field)
    {
        if (!string.IsNullOrWhiteSpace(value) && !MobilePattern.IsMatch(value.Trim()))
        {
            throw new ValidationAppException(
                "Mobile number must be a valid Indian mobile number.",
                field);
        }
    }
}
