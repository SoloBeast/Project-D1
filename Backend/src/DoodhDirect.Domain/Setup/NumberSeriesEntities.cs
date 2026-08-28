using DoodhDirect.Domain.Common;

namespace DoodhDirect.Domain.Setup;

/// <summary>
/// Governs how the counter restarts. Resets are driven by this policy's period
/// transition (tracked via <see cref="NumberSeries.LastUsedAt"/>), never by token
/// display alone.
/// </summary>
public enum NumberSeriesResetPolicy
{
    Never,
    Daily,
    Monthly,
    CalendarYear,
    FinancialYear
}

/// <summary>
/// A single configurable numbering series used by the Setup → Number Series module.
/// The internal <see cref="Entity.Id"/> remains the primary key; the generated
/// business number is produced by the centralized template engine in the
/// infrastructure NumberSeriesService.
/// </summary>
/// <remarks>
/// <see cref="ScopeKey"/> scopes a series so the same <see cref="Code"/> can carry one
/// independent counter per scope (e.g. one ORDER series per branch). An empty
/// <see cref="ScopeKey"/> (string.Empty) denotes the legacy global/unscoped series.
/// </remarks>
public sealed class NumberSeries : AuditableEntity
{
    private NumberSeries() { }

    public NumberSeries(
        string code,
        string description,
        string template,
        long startingNumber,
        int incrementBy,
        NumberSeriesResetPolicy resetPolicy,
        string? scopeKey = null)
    {
        if (incrementBy < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(incrementBy), "Increment must be at least 1.");
        }

        Code = code.Trim().ToUpperInvariant();
        ScopeKey = NormalizeScope(scopeKey);
        Description = description.Trim();
        Template = template.Trim();
        StartingNumber = startingNumber;
        LastUsedNumber = startingNumber - 1;
        IncrementBy = incrementBy;
        ResetPolicy = resetPolicy;
        IsActive = true;
    }

    public string Code { get; private set; } = string.Empty;
    public string ScopeKey { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string Template { get; private set; } = string.Empty;
    public long StartingNumber { get; private set; }
    public long LastUsedNumber { get; private set; }
    public int IncrementBy { get; private set; }
    public NumberSeriesResetPolicy ResetPolicy { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime? LastUsedAt { get; private set; }
    public long? CreatedByUserId { get; private set; }
    public long? UpdatedByUserId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// Records an allocation. If a period change is detected (per <paramref name="resetPolicy"/>),
    /// the counter is re-seeded to <see cref="StartingNumber"/>; otherwise it advances by
    /// <see cref="IncrementBy"/> from <see cref="LastUsedNumber"/>.
    /// </summary>
    public long NextNumber(
        NumberSeriesResetPolicy resetPolicy,
        DateOnly indiaLocalDate,
        DateTime indiaLocalNow,
        long? updatedByUserId = null)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));

        var shouldReset = ShouldReset(resetPolicy, indiaLocalDate);

        var next = shouldReset || LastUsedNumber < StartingNumber
            ? StartingNumber
            : LastUsedNumber + IncrementBy;

        LastUsedNumber = next;
        LastUsedAt = indiaLocalNow;
        UpdatedByUserId = updatedByUserId;
        return next;
    }

    /// <summary>
    /// Computes the number that WOULD be allocated next without mutating the series.
    /// Used by previews so the live sequence is never consumed by a look-ahead.
    /// </summary>
    public long PeekNextNumber(NumberSeriesResetPolicy resetPolicy, DateOnly indiaLocalDate)
    {
        var shouldReset = ShouldReset(resetPolicy, indiaLocalDate);
        return shouldReset || LastUsedNumber < StartingNumber
            ? StartingNumber
            : LastUsedNumber + IncrementBy;
    }

    public void Configure(
        string description,
        string template,
        long startingNumber,
        int incrementBy,
        NumberSeriesResetPolicy resetPolicy,
        DateTime indiaLocalNow,
        long? updatedByUserId)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));

        Description = description.Trim();
        Template = template.Trim();
        StartingNumber = startingNumber;
        IncrementBy = incrementBy;
        ResetPolicy = resetPolicy;
        UpdatedByUserId = updatedByUserId;
        SetUpdated(indiaLocalNow);
    }

    public void Activate(long? updatedByUserId, DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        IsActive = true;
        UpdatedByUserId = updatedByUserId;
        SetUpdated(indiaLocalNow);
    }

    public void Deactivate(long? updatedByUserId, DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        IsActive = false;
        UpdatedByUserId = updatedByUserId;
        SetUpdated(indiaLocalNow);
    }

    public void SetCreatedBy(long? userId, DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        CreatedByUserId = userId;
        SetCreated(indiaLocalNow);
    }

    private bool ShouldReset(NumberSeriesResetPolicy resetPolicy, DateOnly indiaLocalDate)
    {
        if (resetPolicy == NumberSeriesResetPolicy.Never || LastUsedAt is null)
        {
            return false;
        }

        var lastUsedDate = DateOnly.FromDateTime(LastUsedAt.Value);
        return resetPolicy switch
        {
            NumberSeriesResetPolicy.Daily => lastUsedDate != indiaLocalDate,
            NumberSeriesResetPolicy.Monthly => lastUsedDate.Year != indiaLocalDate.Year || lastUsedDate.Month != indiaLocalDate.Month,
            NumberSeriesResetPolicy.CalendarYear => lastUsedDate.Year != indiaLocalDate.Year,
            NumberSeriesResetPolicy.FinancialYear => FinancialYear(lastUsedDate) != FinancialYear(indiaLocalDate),
            _ => false
        };
    }

    public static (int StartYear, int EndYear) FinancialYear(DateOnly date)
    {
        var startYear = date.Month >= 4 ? date.Year : date.Year - 1;
        return (startYear, startYear + 1);
    }

    public static string FormatFinancialYear(DateOnly date)
    {
        var (startYear, endYear) = FinancialYear(date);
        return $"{startYear % 100:00}-{endYear % 100:00}";
    }

    private static void EnsureIndiaLocal(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "The timestamp must be India-local with an unspecified DateTime kind.",
                parameterName);
        }
    }

    private static string NormalizeScope(string? scopeKey) =>
        string.IsNullOrWhiteSpace(scopeKey) ? string.Empty : scopeKey.Trim().ToUpperInvariant();
}
