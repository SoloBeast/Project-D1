import 'country_codes.dart';
import 'india_mobile.dart';

/// Canonicalizes [value] to an E.164 number using [country]'s dialing code.
///
/// For India this delegates to [canonicalizeIndianMobile] so the exact
/// existing normalization (10/11/12/14 digit forms, `6..9` leading digit) is
/// preserved. For every other country the value is reduced to digits, any
/// leading occurrence of the country's dialing code (or its `00` international
/// prefix form) is removed to avoid a duplicated country code, and the result
/// is prefixed with `+<dialCode>`.
///
/// Non-Indian numbers are NEVER coerced to `+91` — they keep their own
/// dialing code. Returns `null` when the value is empty or not a plausible
/// national number for the selected country.
String? canonicalizeMobile(String? value, CountryCode country) {
  if (value == null || value.trim().isEmpty) return null;
  if (country.isoCode == 'IN') return canonicalizeIndianMobile(value);
  final digits = value.replaceAll(RegExp(r'[^0-9]'), '');
  if (digits.isEmpty) return null;
  var national = digits;
  if (national.length > country.dialCode.length &&
      national.startsWith(country.dialCode)) {
    national = national.substring(country.dialCode.length);
  } else if (national.length > country.dialCode.length + 2 &&
      national.startsWith('00${country.dialCode}')) {
    national = national.substring(country.dialCode.length + 2);
  }
  // A plausible national number must carry a few digits beyond the dial code.
  if (national.length < 4) return null;
  return '+${country.dialCode}$national';
}

/// The validation message shown when [value] is not a valid mobile number for
/// [country]. India keeps the existing strict 10-digit wording so screens and
/// tests that reference it keep working.
String mobileNumberErrorMessage(CountryCode country) {
  if (country.isoCode == 'IN') {
    return 'Enter a valid 10-digit mobile number.';
  }
  return 'Enter a valid ${country.name} mobile number.';
}

/// Attempts to split an existing E.164-style value (e.g. `+919876543210`) into
/// the matching [CountryCode] and the national number (digits only).
///
/// Used to prefill the country selector + national input from a stored
/// canonical value. Longest dialing code wins to avoid mis-splitting shared
/// codes (e.g. `+44` UK vs `+441624` Guernsey). Returns `null` when no
/// bundled country matches.
(CountryCode, String)? parseMobileCountry(String? value) {
  if (value == null || value.trim().isEmpty) return null;
  final digits = value.replaceAll(RegExp(r'[^0-9]'), '');
  if (digits.length < 4) return null;
  CountryCode? best;
  var bestLen = 0;
  for (final country in CountryCodes.all) {
    final code = country.dialCode;
    if (code.length > bestLen && digits.startsWith(code)) {
      best = country;
      bestLen = code.length;
    }
  }
  if (best == null) return null;
  final national = digits.substring(bestLen);
  if (national.length < 4) return null;
  return (best, national);
}
