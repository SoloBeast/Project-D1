using DoodhDirect.Domain.Setup;

namespace DoodhDirect.Application.Setup;

/// <summary>
/// A numbering series visible to Setup → Number Series. <see cref="Code"/> is the stable
/// lookup key used by business services to allocate the next number, and
/// <see cref="ScopeKey"/> (empty = global) scopes an independent counter per scope.
/// </summary>
public sealed record NumberSeriesResult(
    string Code,
    string Description,
    string Template,
    long StartingNumber,
    long LastUsedNumber,
    int IncrementBy,
    NumberSeriesResetPolicy ResetPolicy,
    bool IsActive,
    string? NextNumber,
    DateTime? LastUsedAt,
    long? CreatedByUserId,
    long? UpdatedByUserId,
    string? ScopeKey = null);

public sealed record CreateNumberSeriesRequest(
    string Code,
    string Description,
    string Template,
    long StartingNumber,
    int IncrementBy,
    NumberSeriesResetPolicy ResetPolicy,
    string? ScopeKey = null);

public sealed record UpdateNumberSeriesRequest(
    string Description,
    string Template,
    long StartingNumber,
    int IncrementBy,
    NumberSeriesResetPolicy ResetPolicy);

/// <summary>
/// A template preview computed WITHOUT consuming or advancing the live sequence.
/// <see cref="ScopeKey"/> is included for scoped series (empty = global).
/// </summary>
public sealed record NumberSeriesPreviewResult(
    string Code,
    string Template,
    long NextNumber,
    string FormattedNumber,
    string? ScopeKey = null);

public sealed record NumberSeriesPreviewRequest(
    string Code,
    string Template,
    long? NextNumber = null,
    string? Scope = null);

/// <summary>
/// Centralized numbering service. Owns template parsing/formatting, reset detection,
/// allocation, and concurrency. Business callers request the next number for a series
/// code inside their own serializable transaction so that a rolled-back business save
/// also rolls back the counter.
/// </summary>
public interface INumberSeriesService
{
    /// <summary>
    /// Allocates and returns the next number for <paramref name="seriesCode"/> in the scope
    /// identified by <paramref name="scopeKey"/> (empty/null = the global series).
    /// This advances the counter and MUST be called inside the caller's business
    /// transaction so the allocation commits only when the business save commits.
    /// </summary>
    Task<string> GetNextNumberAsync(
        string seriesCode,
        long? actorUserId,
        CancellationToken cancellationToken,
        string? scopeKey = null);

    /// <summary>
    /// Returns the number that WOULD be allocated next WITHOUT consuming it.
    /// </summary>
    Task<NumberSeriesPreviewResult> PreviewNextNumberAsync(
        string seriesCode,
        CancellationToken cancellationToken,
        string? scopeKey = null);

    /// <summary>
    /// Renders a template for the supplied counter value without touching any series row.
    /// Used by the Setup UI to preview a candidate template. <paramref name="scopeKey"/>
    /// supplies the value rendered by the {SCOPE} token.
    /// </summary>
    NumberSeriesPreviewResult PreviewTemplate(
        string code,
        string template,
        long nextNumber,
        string? scopeKey = null);

    Task<IReadOnlyList<NumberSeriesResult>> ListAsync(CancellationToken cancellationToken);

    Task<NumberSeriesResult> GetAsync(
        string code,
        CancellationToken cancellationToken,
        string? scopeKey = null);

    Task<NumberSeriesResult> CreateAsync(
        CreateNumberSeriesRequest request,
        long actorUserId,
        CancellationToken cancellationToken);

    Task<NumberSeriesResult> UpdateAsync(
        string code,
        UpdateNumberSeriesRequest request,
        long actorUserId,
        CancellationToken cancellationToken,
        string? scopeKey = null);

    Task<NumberSeriesResult> SetActiveAsync(
        string code,
        bool isActive,
        long actorUserId,
        CancellationToken cancellationToken,
        string? scopeKey = null);
}
