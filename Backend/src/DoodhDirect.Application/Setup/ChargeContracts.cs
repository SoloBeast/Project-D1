using DoodhDirect.Domain.Setup;

namespace DoodhDirect.Application.Setup;

/// <summary>
/// A Tax &amp; Charges master record as seen by the admin Setup screen.
/// </summary>
public sealed record ChargeResult(
    Guid PublicId,
    string ChargeType,
    string ChargeCode,
    string? Description,
    decimal Percentage,
    bool IsActive,
    bool ApplicableOnAll,
    bool IsUsed,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CreateChargeRequest(
    string ChargeType,
    string ChargeCode,
    string? Description,
    decimal Percentage,
    bool ApplicableOnAll = true);

public sealed record UpdateChargeRequest(
    string ChargeType,
    string? Description,
    decimal Percentage);

/// <summary>
/// Command for the dedicated applicability toggle. Switching an item charge
/// (<c>false</c>) back to global (<c>true</c>) is refused server-side while any
/// ProductCharge mapping still references the charge.
/// </summary>
public sealed record SetChargeApplicabilityRequest(bool ApplicableOnAll);

/// <summary>
/// Admin CRUD for the Setup → Tax &amp; Charges master. Checkout reads the active
/// charges directly (a plain query inside the order transaction) — the master
/// service is not involved in the pricing path.
/// </summary>
public interface IChargeService
{
    Task<IReadOnlyList<ChargeResult>> ListAsync(CancellationToken cancellationToken);

    Task<ChargeResult> GetAsync(Guid publicId, CancellationToken cancellationToken);

    Task<ChargeResult> CreateAsync(
        CreateChargeRequest request,
        long actorUserId,
        CancellationToken cancellationToken);

    Task<ChargeResult> UpdateAsync(
        Guid publicId,
        UpdateChargeRequest request,
        long actorUserId,
        CancellationToken cancellationToken);

    Task<ChargeResult> SetActiveAsync(
        Guid publicId,
        bool isActive,
        long actorUserId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Toggles the applicability mode. Enabling <see cref="ChargeResult.ApplicableOnAll"/>
    /// is refused while any product assignment exists (server-side invariant).
    /// </summary>
    Task<ChargeResult> SetApplicableOnAllAsync(
        Guid publicId,
        bool applicableOnAll,
        long actorUserId,
        CancellationToken cancellationToken);

    /// <summary>Deletes a charge; refused once any order snapshot references its code.</summary>
    Task DeleteAsync(Guid publicId, long actorUserId, CancellationToken cancellationToken);
}
