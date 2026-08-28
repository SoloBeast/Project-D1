using DoodhDirect.Domain.Catalogue;

namespace DoodhDirect.Application.Branches;

/// <summary>
/// Administrative branch record returned by the Branch Management module.
/// <see cref="BranchNumber"/> is allocated server-side from the centralized
/// <c>BRANCH</c> numbering series and is never supplied by the client.
/// </summary>
public sealed record BranchResult(
    Guid PublicId,
    string Code,
    string Name,
    string? AddressLine1,
    string? AddressLine2,
    string? Locality,
    string City,
    string State,
    string? PinCode,
    decimal Latitude,
    decimal Longitude,
    decimal? ServiceRadiusKm,
    bool IsActive,
    string? BranchNumber,
    DateTime? CreatedAt,
    DateTime? UpdatedAt);

/// <summary>
/// Request used to create or update a branch. <see cref="Code"/> is the stable
/// business key referenced by order allocations and scoped numbering series.
/// </summary>
public sealed record UpsertBranchRequest(
    string Code,
    string Name,
    string? AddressLine1,
    string? AddressLine2,
    string? Locality,
    string City,
    string State,
    string? PinCode,
    decimal Latitude,
    decimal Longitude,
    decimal? ServiceRadiusKm);

public interface IBranchService
{
    Task<IReadOnlyList<BranchResult>> ListAsync(CancellationToken cancellationToken);

    Task<BranchResult> GetAsync(Guid branchId, CancellationToken cancellationToken);

    Task<BranchResult> CreateAsync(long actorUserId, UpsertBranchRequest request, CancellationToken cancellationToken);

    Task<BranchResult> UpdateAsync(long actorUserId, Guid branchId, UpsertBranchRequest request, CancellationToken cancellationToken);

    Task<BranchResult> SetActiveAsync(long actorUserId, Guid branchId, bool isActive, CancellationToken cancellationToken);
}

public static class BranchMappings
{
    public static BranchResult ToBranchResult(this Branch branch) =>
        new(
            branch.PublicId,
            branch.Code,
            branch.Name,
            branch.AddressLine1,
            branch.AddressLine2,
            branch.Locality,
            branch.City,
            branch.State,
            branch.PinCode,
            branch.Latitude,
            branch.Longitude,
            branch.ServiceRadiusKm,
            branch.IsActive,
            branch.BranchNumber,
            branch.CreatedAt,
            branch.UpdatedAt);
}
