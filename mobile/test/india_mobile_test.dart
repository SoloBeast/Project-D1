import 'package:doodh_direct_mobile/core/utils/india_mobile.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('canonicalizeIndianMobile', () {
    test('canonicalizes accepted input forms to E.164 +91XXXXXXXXXX', () {
      const cases = <String, String>{
        '9876543210': '+919876543210',
        '09876543210': '+919876543210',
        '919876543210': '+919876543210',
        '+91 98765 43210': '+919876543210',
        '0091 9876543210': '+919876543210',
        '  +91  (98765)  43210  ': '+919876543210',
      };

      cases.forEach((input, expected) {
        expect(canonicalizeIndianMobile(input), expected, reason: 'for $input');
      });
    });

    test('returns null for invalid or non-Indian numbers', () {
      const invalid = <String?>[
        null,
        '',
        '   ',
        '12345',
        '98765432',
        '5876543210',
        '0876543210',
        '+91987654321',
        '+91998765432',
        '99987654321',
      ];

      for (final input in invalid) {
        expect(canonicalizeIndianMobile(input), isNull, reason: 'for $input');
      }
    });
  });
}
