const Duration indiaUtcOffset = Duration(hours: 5, minutes: 30);

/// Converts an absolute timestamp to an India-local wall-clock value.
///
/// The returned value is deliberately non-UTC because the application treats
/// India-local business values as calendar components, independent of the
/// device timezone.
DateTime toIndiaTime(DateTime value) {
  final india = value.toUtc().add(indiaUtcOffset);
  return DateTime(
    india.year,
    india.month,
    india.day,
    india.hour,
    india.minute,
    india.second,
    india.millisecond,
    india.microsecond,
  );
}

/// Returns the current India-local wall-clock value.
DateTime indiaNow() => toIndiaTime(DateTime.now().toUtc());

/// Converts an India-local wall-clock value to an absolute UTC timestamp.
DateTime indiaToUtc(DateTime value) {
  final india = indiaWallClock(value);
  return DateTime.utc(
    india.year,
    india.month,
    india.day,
    india.hour,
    india.minute,
    india.second,
    india.millisecond,
    india.microsecond,
  ).subtract(indiaUtcOffset);
}

/// Preserves calendar components for a value selected by the user.
///
/// Date pickers produce device-local values. For this India-only application,
/// those components represent India calendar time and must not be shifted by
/// the device timezone.
DateTime indiaWallClock(DateTime value) => value.isUtc
    ? toIndiaTime(value)
    : DateTime(
        value.year,
        value.month,
        value.day,
        value.hour,
        value.minute,
        value.second,
        value.millisecond,
        value.microsecond,
      );
