using System.Globalization;

namespace DoodhDirect.Application.Abstractions;

public interface IIndiaTimeProvider
{
    DateTime Now { get; }

    DateTime ToUtc(DateTime indiaLocal);

    DateOnly Today { get; }

    DateOnly CurrentDate { get; }

    DateTime CurrentDateTime { get; }

    string FormatDateTime(DateTime value);

    string FormatDate(DateOnly value);

    DateTime ParseApplicationDateTime(string value);
}

public sealed class IndiaTimeProvider(TimeZoneInfo timeZone) : IIndiaTimeProvider
{
    public DateTime ToUtc(DateTime indiaLocal) => TimeZoneInfo.ConvertTimeToUtc(
        DateTime.SpecifyKind(indiaLocal, DateTimeKind.Unspecified),
        timeZone);

    public DateTime Now => DateTime.SpecifyKind(
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone),
        DateTimeKind.Unspecified);

    public DateOnly Today => DateOnly.FromDateTime(Now);

    public DateOnly CurrentDate => Today;

    public DateTime CurrentDateTime => Now;

    public string FormatDateTime(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
        .ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string FormatDate(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public DateTime ParseApplicationDateTime(string value) => DateTime.SpecifyKind(
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None),
        DateTimeKind.Unspecified);
}
