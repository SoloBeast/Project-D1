using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DoodhDirect.Api.Serialization;

public sealed class IndiaLocalDateTimeJsonConverter(TimeZoneInfo timeZone) : JsonConverter<DateTime>
{
    public override DateTime Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var value = reader.GetDateTime();
        var indiaLocal = value.Kind == DateTimeKind.Utc
            ? TimeZoneInfo.ConvertTimeFromUtc(value, timeZone)
            : value;
        return DateTime.SpecifyKind(indiaLocal, DateTimeKind.Unspecified);
    }

    public override void Write(
        Utf8JsonWriter writer,
        DateTime value,
        JsonSerializerOptions options)
    {
        var localValue = value.Kind == DateTimeKind.Utc
            ? TimeZoneInfo.ConvertTimeFromUtc(value, timeZone)
            : DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        writer.WriteStringValue(localValue.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture));
    }
}

