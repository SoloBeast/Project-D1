using System.Globalization;
using System.Text;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Setup;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Setup;

/// <summary>
/// Centralized numbering service. Owns template parsing/formatting, reset detection,
/// allocation, previews, CRUD, and audit. Business callers request the next number
/// inside their own serializable transaction so a rolled-back save also rolls back
/// the counter.
/// </summary>
/// <remarks>
/// A series may be scoped by <see cref="NumberSeries.ScopeKey"/>. The same
/// <see cref="NumberSeries.Code"/> can exist once per scope with an independent
/// counter. An empty <see cref="NumberSeries.ScopeKey"/> is the legacy global series.
/// The <c>{SCOPE}</c> token renders the scope key so scoped numbers stay unique
/// (e.g. <c>ORD/{SCOPE}/{NUMBER:000000}</c>).
/// </remarks>
public sealed class NumberSeriesService(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider) : INumberSeriesService
{
    public const string ActionCreated = "NUMBER_SERIES.CREATED";
    public const string ActionUpdated = "NUMBER_SERIES.UPDATED";
    public const string ActionActivated = "NUMBER_SERIES.ACTIVATED";
    public const string ActionDeactivated = "NUMBER_SERIES.DEACTIVATED";

    public async Task<string> GetNextNumberAsync(
        string seriesCode,
        long? actorUserId,
        CancellationToken cancellationToken,
        string? scopeKey = null)
    {
        if (string.IsNullOrWhiteSpace(seriesCode))
        {
            throw new ValidationAppException("A series code is required.", "code");
        }

        var normalizedCode = seriesCode.Trim().ToUpperInvariant();
        var normalizedScope = NormalizeScope(scopeKey);
        var series = await FindAsync(normalizedCode, normalizedScope, cancellationToken);

        if (!series.IsActive)
        {
            throw new BusinessRuleException(
                $"Number series '{normalizedCode}' is inactive and cannot allocate numbers.");
        }

        var now = timeProvider.Now;
        var next = series.NextNumber(
            series.ResetPolicy,
            DateOnly.FromDateTime(now),
            now,
            actorUserId);

        await dbContext.SaveChangesAsync(cancellationToken);

        return Format(normalizedCode, series.Template, next, now, normalizedScope);
    }

    public async Task<NumberSeriesPreviewResult> PreviewNextNumberAsync(
        string seriesCode,
        CancellationToken cancellationToken,
        string? scopeKey = null)
    {
        if (string.IsNullOrWhiteSpace(seriesCode))
        {
            throw new ValidationAppException("A series code is required.", "code");
        }

        var normalizedCode = seriesCode.Trim().ToUpperInvariant();
        var normalizedScope = NormalizeScope(scopeKey);
        var series = await FindAsync(normalizedCode, normalizedScope, cancellationToken);

        var now = timeProvider.Now;
        var next = series.PeekNextNumber(series.ResetPolicy, DateOnly.FromDateTime(now));

        return new NumberSeriesPreviewResult(
            normalizedCode,
            series.Template,
            next,
            Format(normalizedCode, series.Template, next, now, normalizedScope),
            normalizedScope);
    }

    public NumberSeriesPreviewResult PreviewTemplate(
        string code,
        string template,
        long nextNumber,
        string? scopeKey = null)
    {
        var normalizedCode = NormalizeCode(code);
        var normalizedScope = NormalizeScope(scopeKey);
        var validatedTemplate = ValidateAndNormalizeTemplate(template, normalizedCode, normalizedScope);
        var now = timeProvider.Now;

        return new NumberSeriesPreviewResult(
            normalizedCode,
            validatedTemplate,
            nextNumber,
            Format(normalizedCode, validatedTemplate, nextNumber, now, normalizedScope),
            normalizedScope);
    }

    public async Task<IReadOnlyList<NumberSeriesResult>> ListAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.Now;
        var series = await dbContext.NumberSeries
            .OrderBy(item => item.Code)
            .ThenBy(item => item.ScopeKey)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return series
            .Select(item => ToResult(item, DateOnly.FromDateTime(now)))
            .ToArray();
    }

    public async Task<NumberSeriesResult> GetAsync(
        string code,
        CancellationToken cancellationToken,
        string? scopeKey = null)
    {
        var series = await FindAsync(code, NormalizeScope(scopeKey), cancellationToken);
        return ToResult(series, DateOnly.FromDateTime(timeProvider.Now));
    }

    public async Task<NumberSeriesResult> CreateAsync(
        CreateNumberSeriesRequest request,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var normalizedCode = NormalizeCode(request.Code);
        var normalizedScope = NormalizeScope(request.ScopeKey);
        var template = ValidateAndNormalizeTemplate(request.Template, normalizedCode, normalizedScope);

        if (request.StartingNumber < 1)
        {
            throw new ValidationAppException("The starting number must be at least 1.", "StartingNumber");
        }

        if (request.IncrementBy < 1)
        {
            throw new ValidationAppException("The increment must be at least 1.", "IncrementBy");
        }

        ValidateResetPolicy(request.ResetPolicy, template);

        var exists = await dbContext.NumberSeries
            .AnyAsync(item => item.Code == normalizedCode && item.ScopeKey == normalizedScope, cancellationToken);
        if (exists)
        {
            throw new ConflictException(
                normalizedScope.Length == 0
                    ? $"A number series with code '{normalizedCode}' already exists."
                    : $"A number series with code '{normalizedCode}' already exists for scope '{normalizedScope}'.");
        }

        var now = timeProvider.Now;
        var series = new NumberSeries(
            normalizedCode,
            request.Description,
            template,
            request.StartingNumber,
            request.IncrementBy,
            request.ResetPolicy,
            normalizedScope);
        series.SetCreatedBy(actorUserId, now);

        dbContext.NumberSeries.Add(series);
        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            ActionCreated,
            "NumberSeries",
            NormalizeScope(request.ScopeKey).Length == 0
                ? normalizedCode
                : $"{normalizedCode}@{NormalizeScope(request.ScopeKey)}",
            null,
            ToSnapshotJson(series),
            null,
            null,
            null,
            now));

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResult(series, DateOnly.FromDateTime(now));
    }

    public async Task<NumberSeriesResult> UpdateAsync(
        string code,
        UpdateNumberSeriesRequest request,
        long actorUserId,
        CancellationToken cancellationToken,
        string? scopeKey = null)
    {
        var normalizedCode = NormalizeCode(code);
        var normalizedScope = NormalizeScope(scopeKey);
        var template = ValidateAndNormalizeTemplate(request.Template, normalizedCode, normalizedScope);

        if (request.StartingNumber < 1)
        {
            throw new ValidationAppException("The starting number must be at least 1.", "StartingNumber");
        }

        if (request.IncrementBy < 1)
        {
            throw new ValidationAppException("The increment must be at least 1.", "IncrementBy");
        }

        ValidateResetPolicy(request.ResetPolicy, template);

        var series = await FindAsync(normalizedCode, normalizedScope, cancellationToken);
        EnsureSafeEdit(series, request);

        var now = timeProvider.Now;
        var oldSnapshot = ToSnapshotJson(series);

        series.Configure(
            request.Description,
            template,
            request.StartingNumber,
            request.IncrementBy,
            request.ResetPolicy,
            now,
            actorUserId);

        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            ActionUpdated,
            "NumberSeries",
            normalizedScope.Length == 0 ? normalizedCode : $"{normalizedCode}@{normalizedScope}",
            oldSnapshot,
            ToSnapshotJson(series),
            null,
            null,
            null,
            now));

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResult(series, DateOnly.FromDateTime(now));
    }

    public async Task<NumberSeriesResult> SetActiveAsync(
        string code,
        bool isActive,
        long actorUserId,
        CancellationToken cancellationToken,
        string? scopeKey = null)
    {
        var normalizedCode = NormalizeCode(code);
        var normalizedScope = NormalizeScope(scopeKey);
        var series = await FindAsync(normalizedCode, normalizedScope, cancellationToken);

        if (series.IsActive == isActive)
        {
            return ToResult(series, DateOnly.FromDateTime(timeProvider.Now));
        }

        var now = timeProvider.Now;
        var oldSnapshot = ToSnapshotJson(series);

        if (isActive)
        {
            series.Activate(actorUserId, now);
        }
        else
        {
            series.Deactivate(actorUserId, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            isActive ? ActionActivated : ActionDeactivated,
            "NumberSeries",
            normalizedScope.Length == 0 ? normalizedCode : $"{normalizedCode}@{normalizedScope}",
            oldSnapshot,
            ToSnapshotJson(series),
            null,
            null,
            null,
            now));

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResult(series, DateOnly.FromDateTime(now));
    }

    private async Task<NumberSeries> FindAsync(
        string code,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        var normalizedCode = NormalizeCode(code);
        var series = await dbContext.NumberSeries
            .SingleOrDefaultAsync(
                item => item.Code == normalizedCode && item.ScopeKey == scopeKey,
                cancellationToken);
        if (series is null)
        {
            throw new NotFoundException(
                scopeKey.Length == 0
                    ? $"Number series '{normalizedCode}' was not found."
                    : $"Number series '{normalizedCode}' for scope '{scopeKey}' was not found.");
        }

        return series;
    }

    private static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ValidationAppException("A series code is required.", "code");
        }

        var normalized = code.Trim().ToUpperInvariant();
        if (normalized.Length > 50)
        {
            throw new ValidationAppException("The series code must be at most 50 characters.", "code");
        }

        if (normalized.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '_' || ch == '-')))
        {
            throw new ValidationAppException(
                "The series code may contain only letters, digits, underscores, and hyphens.",
                "code");
        }

        return normalized;
    }

    private static string NormalizeScope(string? scopeKey) =>
        string.IsNullOrWhiteSpace(scopeKey) ? string.Empty : scopeKey.Trim().ToUpperInvariant();

    /// <summary>
    /// Validates a template and returns its normalized (trimmed) form.
    /// Rules: exactly one mandatory <c>{NUMBER:NNNN}</c> token; only supported tokens
    /// (including <c>{SCOPE}</c>); DATE tokens must carry a valid format; generated
    /// length ≤ 40; a reset policy other than Never requires at least one period token.
    /// </summary>
    private static string ValidateAndNormalizeTemplate(string? template, string code, string scopeKey)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new ValidationAppException("A template is required.", "Template");
        }

        var normalized = template.Trim();
        if (normalized.Length > 120)
        {
            throw new ValidationAppException("The template must be at most 120 characters.", "Template");
        }

        var tokens = new List<(int Start, int End, string Body)>();
        for (var i = 0; i < normalized.Length;)
        {
            var open = normalized.IndexOf('{', i);
            if (open < 0)
            {
                break;
            }

            var close = normalized.IndexOf('}', open);
            if (close < 0)
            {
                throw new ValidationAppException(
                    $"Template contains an opening '{{' without a closing '}}'.",
                    "Template");
            }

            tokens.Add((open, close, normalized[(open + 1)..close]));
            i = close + 1;
        }

        var numberTokens = tokens.Where(token =>
            token.Body.StartsWith("NUMBER:", StringComparison.OrdinalIgnoreCase)).ToList();

        if (numberTokens.Count != 1)
        {
            throw new ValidationAppException(
                "A template must contain exactly one {NUMBER:NNNN} token.",
                "Template");
        }

        // The width is the count of digit characters ({NUMBER:0000} -> width 4), so a
        // single "0" is the only degenerate case: it declares a zero-padded width of 0.
        var numberDigits = numberTokens[0].Body[(numberTokens[0].Body.IndexOf(':') + 1)..];
        if (numberDigits.Length is < 1 or > 9
            || !numberDigits.All(char.IsAsciiDigit)
            || numberDigits == "0")
        {
            throw new ValidationAppException(
                "The {NUMBER:NNNN} token must specify a zero-padded width of 1 to 9 digits.",
                "Template");
        }

        foreach (var token in tokens)
        {
            var body = token.Body;
            if (body.Equals("NUMBER:" + numberDigits, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (body.Equals("PREFIX", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (body.Equals("SCOPE", StringComparison.OrdinalIgnoreCase))
            {
                if (scopeKey.Length == 0)
                {
                    throw new ValidationAppException(
                        "The {SCOPE} token requires a scoped series. Provide a scope key or remove the token.",
                        "Scope");
                }

                continue;
            }

            if (body.Equals("FY", StringComparison.OrdinalIgnoreCase) ||
                body.Equals("YEAR", StringComparison.OrdinalIgnoreCase) ||
                body.Equals("YY", StringComparison.OrdinalIgnoreCase) ||
                body.Equals("MONTH", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (body.StartsWith("DATE:", StringComparison.OrdinalIgnoreCase))
            {
                var format = body[(body.IndexOf(':') + 1)..];
                if (string.IsNullOrWhiteSpace(format))
                {
                    throw new ValidationAppException(
                        "The {DATE:...} token must specify a date format, e.g. {DATE:yyyyMMdd}.",
                        "Template");
                }

                try
                {
                    _ = new DateOnly(2026, 8, 28).ToString(format, CultureInfo.InvariantCulture);
                }
                catch (FormatException)
                {
                    throw new ValidationAppException(
                        $"The {{DATE:...}} token has an unsupported date format '{format}'.",
                        "Template");
                }

                continue;
            }

            throw new ValidationAppException(
                $"Template contains an unsupported token '{{{body}}}'. Supported tokens are " +
                "{NUMBER:NNNN}, {PREFIX}, {SCOPE}, {FY}, {YEAR}, {YY}, {MONTH} and {DATE:yyyyMMdd}.",
                "Template");
        }

        var generatedLength = EvaluateGeneratedLength(normalized, numberDigits.Length, scopeKey);
        if (generatedLength > 40)
        {
            throw new ValidationAppException(
                $"The template produces numbers longer than 40 characters.",
                "Template");
        }

        return normalized;
    }

    private static int EvaluateGeneratedLength(string template, int numberWidth, string scopeKey)
    {
        var length = 0;
        for (var i = 0; i < template.Length;)
        {
            if (template[i] == '{')
            {
                var close = template.IndexOf('}', i);
                var body = template[(i + 1)..close];
                length += body.ToUpperInvariant() switch
                {
                    _ when body.StartsWith("NUMBER:", StringComparison.OrdinalIgnoreCase) => numberWidth,
                    "PREFIX" => 1,
                    "SCOPE" => scopeKey.Length,
                    "FY" => 5,
                    "YEAR" => 4,
                    "YY" => 2,
                    "MONTH" => 2,
                    _ when body.StartsWith("DATE:", StringComparison.OrdinalIgnoreCase) =>
                        new DateOnly(2026, 8, 28)
                            .ToString(body[(body.IndexOf(':') + 1)..], CultureInfo.InvariantCulture)
                            .Length,
                    _ => 0
                };
                i = close + 1;
            }
            else
            {
                length++;
                i++;
            }
        }

        return length;
    }

    private static void ValidateResetPolicy(NumberSeriesResetPolicy resetPolicy, string template)
    {
        if (resetPolicy == NumberSeriesResetPolicy.Never)
        {
            return;
        }

        var hasPeriodToken = template.Contains("{FY}", StringComparison.OrdinalIgnoreCase)
            || template.Contains("{YEAR}", StringComparison.OrdinalIgnoreCase)
            || template.Contains("{YY}", StringComparison.OrdinalIgnoreCase)
            || template.Contains("{MONTH}", StringComparison.OrdinalIgnoreCase)
            || template.Contains("{DATE:", StringComparison.OrdinalIgnoreCase);

        if (!hasPeriodToken)
        {
            throw new ValidationAppException(
                $"Reset policy '{resetPolicy}' requires a period token such as {{FY}}, {{YEAR}}, " +
                "{{YY}}, {{MONTH}} or {DATE:yyyyMMdd} in the template.",
                "ResetPolicy");
        }
    }

    /// <summary>
    /// Renders a template. The mandatory <c>{NUMBER:NNNN}</c> token is zero-padded to the
    /// declared width; <c>{PREFIX}</c> renders the series code; <c>{SCOPE}</c> renders the
    /// scoped series key. Throws for malformed input so callers always receive a clear
    /// validation error.
    /// </summary>
    public static string Format(
        string code,
        string template,
        long nextNumber,
        DateTime indiaLocalNow,
        string? scopeKey = null)
    {
        if (indiaLocalNow.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "The timestamp must be India-local with an unspecified DateTime kind.",
                nameof(indiaLocalNow));
        }

        var normalizedScope = NormalizeScope(scopeKey);
        var dateOnly = DateOnly.FromDateTime(indiaLocalNow);
        var builder = new StringBuilder();
        var numberWidth = 0;

        for (var i = 0; i < template.Length;)
        {
            if (template[i] != '{')
            {
                builder.Append(template[i]);
                i++;
                continue;
            }

            var close = template.IndexOf('}', i);
            if (close < 0)
            {
                throw new ValidationAppException("Malformed template token: missing closing '}'.", "Template");
            }

            var body = template[(i + 1)..close];
            var upper = body.ToUpperInvariant();

            if (upper.StartsWith("NUMBER:", StringComparison.Ordinal))
            {
                var digits = body[(body.IndexOf(':') + 1)..];
                numberWidth = digits.Length;
                builder.Append(nextNumber.ToString(
                    new string('0', numberWidth),
                    CultureInfo.InvariantCulture));
            }
            else if (upper == "PREFIX")
            {
                builder.Append(code);
            }
            else if (upper == "SCOPE")
            {
                builder.Append(normalizedScope);
            }
            else if (upper == "FY")
            {
                builder.Append(NumberSeries.FormatFinancialYear(dateOnly));
            }
            else if (upper == "YEAR")
            {
                builder.Append(dateOnly.Year.ToString("0000", CultureInfo.InvariantCulture));
            }
            else if (upper == "YY")
            {
                builder.Append((dateOnly.Year % 100).ToString("00", CultureInfo.InvariantCulture));
            }
            else if (upper == "MONTH")
            {
                builder.Append(dateOnly.Month.ToString("00", CultureInfo.InvariantCulture));
            }
            else if (upper.StartsWith("DATE:", StringComparison.Ordinal))
            {
                var format = body[(body.IndexOf(':') + 1)..];
                builder.Append(dateOnly.ToString(format, CultureInfo.InvariantCulture));
            }
            else
            {
                throw new ValidationAppException(
                    $"Template contains an unsupported token '{{{body}}}'.",
                    "Template");
            }

            i = close + 1;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Safe-edit guard: before a series has issued its first number the configuration can be
    /// freely edited; after first use, a series must not move its sequence backwards and must
    /// not be reconfigured in a way that would re-issue numbers that already exist.
    /// </summary>
    private static void EnsureSafeEdit(NumberSeries series, UpdateNumberSeriesRequest request)
    {
        if (series.LastUsedNumber < series.StartingNumber)
        {
            return; // Never used yet — free editing.
        }

        if (request.StartingNumber > series.StartingNumber)
        {
            throw new BusinessRuleException(
                $"Series '{series.Code}' has already issued numbers. The starting number cannot be " +
                "moved forward because historical numbers must be preserved.");
        }

        if (request.StartingNumber < series.StartingNumber)
        {
            throw new BusinessRuleException(
                $"Series '{series.Code}' has already issued numbers. The starting number cannot be " +
                "moved backwards because existing numbers must remain unique.");
        }

        if (request.IncrementBy != series.IncrementBy)
        {
            throw new BusinessRuleException(
                $"Series '{series.Code}' has already issued numbers. The increment cannot be changed " +
                "because it could re-issue an existing number.");
        }

        if (request.StartingNumber <= series.LastUsedNumber && request.Template != series.Template)
        {
            throw new BusinessRuleException(
                $"Series '{series.Code}' has already issued numbers. The template cannot be changed " +
                "once numbers have been allocated.");
        }

        if (request.ResetPolicy != series.ResetPolicy)
        {
            throw new BusinessRuleException(
                $"Series '{series.Code}' has already issued numbers. The reset policy cannot be changed " +
                "once numbers have been allocated.");
        }
    }

    private static string ToSnapshotJson(NumberSeries series) => JsonSerializer.Serialize(new
    {
        series.Code,
        series.ScopeKey,
        series.Description,
        series.Template,
        series.StartingNumber,
        series.LastUsedNumber,
        series.IncrementBy,
        series.ResetPolicy,
        series.IsActive
    });

    private static NumberSeriesResult ToResult(NumberSeries series, DateOnly indiaLocalDate)
    {
        var next = series.PeekNextNumber(series.ResetPolicy, indiaLocalDate);
        return new NumberSeriesResult(
            series.Code,
            series.Description,
            series.Template,
            series.StartingNumber,
            series.LastUsedNumber,
            series.IncrementBy,
            series.ResetPolicy,
            series.IsActive,
            Format(series.Code, series.Template, next, indiaLocalDate.ToDateTime(TimeOnly.MinValue), series.ScopeKey),
            series.LastUsedAt,
            series.CreatedByUserId,
            series.UpdatedByUserId,
            series.ScopeKey);
    }
}
