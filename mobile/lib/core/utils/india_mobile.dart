/// Canonicalizes an Indian mobile number to E.164 `+91XXXXXXXXXX`.
///
/// Accepted input forms:
///   * `9876543210`            (10-digit national)
///   * `09876543210`           (trunk-prefixed national)
///   * `919876543210`          (`91` country code, no plus)
///   * `+91 98765 43210`       (E.164 with separators/spaces)
///   * `0091 9876543210`       (international prefix)
///
/// Returns `null` when the value is not a valid Indian mobile number (e.g.
/// too short, non-`6..9` leading digit, or a non-Indian country code). Empty
/// and whitespace-only values also return `null`.
String? canonicalizeIndianMobile(String? value) {
  if (value == null || value.trim().isEmpty) return null;
  final digits = value.replaceAll(RegExp(r'[^0-9]'), '');
  return switch (digits.length) {
    10 when _isValidLocal(digits) => '+91$digits',
    11 when digits.startsWith('0') && _isValidLocal(digits.substring(1)) =>
      '+91${digits.substring(1)}',
    12 when digits.startsWith('91') && _isValidLocal(digits.substring(2)) =>
      '+91${digits.substring(2)}',
    14 when digits.startsWith('0091') && _isValidLocal(digits.substring(4)) =>
      '+91${digits.substring(4)}',
    _ => null,
  };
}

bool _isValidLocal(String digits) =>
    digits.length == 10 && _isIndianLeading(digits.codeUnitAt(0));

/// Indian mobile numbers begin with `6`–`9` (telecom numbering plan).
bool _isIndianLeading(int codeUnit) => codeUnit >= 0x36 && codeUnit <= 0x39;
