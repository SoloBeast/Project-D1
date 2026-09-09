using DoodhDirect.Domain.Catalogue;

namespace DoodhDirect.Application.Branches;

/// <summary>
/// Administrative branch record returned by the Branch Management module.
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
    bool IsArchived,
    DateTime? ArchivedAt,
    DateTime? CreatedAt,
    DateTime? UpdatedAt);

/// <summary>
/// Outcome of an administrative delete request for a branch. The server — never the client —
/// decides whether the branch was permanently deleted (<see cref="IsDeleted"/>) because nothing
/// references it, or archived (<see cref="IsArchived"/> with the retained <see cref="Branch"/>)
/// because operational or historical records still reference it.
/// </summary>
public sealed record BranchDeleteResult(
    bool IsDeleted,
    bool IsArchived,
    BranchResult? Branch);

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

    /// <summary>
    /// Deletes or archives a branch (admin/owner operation). The server decides which action is
    /// taken: a deactivated branch with no dependent or historical records is permanently deleted,
    /// while a deactivated branch still referenced by operational history is archived instead. An
    /// active branch can neither be deleted nor archived — deactivate it first.
    /// </summary>
    Task<BranchDeleteResult> DeleteAsync(long actorUserId, Guid branchId, CancellationToken cancellationToken);
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
            branch.IsArchived,
            branch.ArchivedAt,
            branch.CreatedAt,
            branch.UpdatedAt);
}
