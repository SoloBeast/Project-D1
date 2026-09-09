import 'package:doodh_direct_mobile/core/utils/country_codes.dart';
import 'package:doodh_direct_mobile/core/utils/mobile_number.dart';
import 'package:doodh_direct_mobile/core/widgets/country_code_mobile_field.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('CountryCodes metadata', () {
    test('bundles a comprehensive dataset (not a tiny hard-coded list)', () {
      expect(CountryCodes.all.length, greaterThan(200));
      // India must be present with the +91 dialing code.
      final india = CountryCodes.byIso('IN');
      expect(india, isNotNull);
      expect(india!.dialCodeWithPlus, '+91');
    });

    test('India is the default selection', () {
      expect(CountryCodes.india.isoCode, 'IN');
      expect(CountryCodes.india.dialCode, '91');
      expect(CountryCodes.india.dialCodeWithPlus, '+91');
      expect(CountryCodes.india.flag, '🇮🇳');
    });

    test('byIso and byDialCode lookups are case/format tolerant', () {
      expect(CountryCodes.byIso('ae')?.name, 'United Arab Emirates');
      expect(CountryCodes.byDialCode('+971')?.isoCode, 'AE');
      expect(CountryCodes.byDialCode('971')?.isoCode, 'AE');
    });

    test('search matches name, dial code and ISO code', () {
      expect(
        CountryCodes.search('united arab').single.isoCode,
        'AE',
      );
      expect(
        CountryCodes.search('971').map((c) => c.isoCode),
        contains('AE'),
      );
      expect(
        CountryCodes.search('+971').map((c) => c.isoCode),
        contains('AE'),
      );
      expect(
        CountryCodes.search('germany').single.isoCode,
        'DE',
      );
      expect(CountryCodes.search('  '), hasLength(CountryCodes.all.length));
      expect(CountryCodes.search('zzzz-no-such-country'), isEmpty);
    });
  });

  group('canonicalizeMobile', () {
    test('India delegates to the strict existing normalization', () {
      expect(
        canonicalizeMobile('9876543210', CountryCodes.india),
        '+919876543210',
      );
      expect(
        canonicalizeMobile('  +91 98765 43210 ', CountryCodes.india),
        '+919876543210',
      );
      expect(canonicalizeMobile('12345', CountryCodes.india), isNull);
      expect(canonicalizeMobile('', CountryCodes.india), isNull);
      expect(canonicalizeMobile(null, CountryCodes.india), isNull);
    });

    test('non-India keeps its own dialing code, never coerced to +91', () {
      final uae = CountryCodes.byIso('AE')!;
      expect(canonicalizeMobile('501234567', uae), '+971501234567');
      // A duplicated dialing code is stripped once.
      expect(canonicalizeMobile('971501234567', uae), '+971501234567');
      // The 00 international prefix form is also handled.
      expect(canonicalizeMobile('00971501234567', uae), '+971501234567');
      expect(canonicalizeMobile('', uae), isNull);
      expect(canonicalizeMobile('1', uae), isNull);
    });

    test('validation message keeps the existing India wording', () {
      expect(
        mobileNumberErrorMessage(CountryCodes.india),
        'Enter a valid 10-digit mobile number.',
      );
      expect(
        mobileNumberErrorMessage(CountryCodes.byIso('AE')!),
        'Enter a valid United Arab Emirates mobile number.',
      );
    });
  });

  group('parseMobileCountry', () {
    test('splits a stored canonical value into country and national number', () {
      final parsed = parseMobileCountry('+919876543210');
      expect(parsed, isNotNull);
      final (country, national) = parsed!;
      expect(country.isoCode, 'IN');
      expect(national, '9876543210');

      final uae = parseMobileCountry('+971501234567');
      expect(uae, isNotNull);
      expect(uae!.$1.isoCode, 'AE');
      expect(uae.$2, '501234567');
    });

    test('returns null for empty, short or unparseable values', () {
      expect(parseMobileCountry(null), isNull);
      expect(parseMobileCountry(''), isNull);
      expect(parseMobileCountry('   '), isNull);
      expect(parseMobileCountry('12'), isNull);
    });
  });

  group('CountryCodeMobileField', () {
    Finder searchField() => find.ancestor(
      of: find.byIcon(Icons.search),
      matching: find.byType(TextField),
    );

    /// Opens the country picker by focusing the field (floating the label above
    /// the prefix so it no longer overlaps the tappable flag) and then tapping
    /// the flag.
    Future<void> openPicker(WidgetTester tester) async {
      await tester.tap(find.byType(TextFormField));
      await tester.pumpAndSettle();
      await tester.tap(find.text('🇮🇳'));
      await tester.pumpAndSettle();
    }

    Future<GlobalKey<CountryCodeMobileFieldState>> pumpField(
      WidgetTester tester, {
      CountryCode initialCountry = CountryCodes.india,
      bool optional = false,
      ValueChanged<CountryCode>? onCountryChanged,
    }) async {
      final key = GlobalKey<CountryCodeMobileFieldState>();
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: Form(
              child: CountryCodeMobileField(
                key: key,
                controller: TextEditingController(),
                initialCountry: initialCountry,
                optional: optional,
                onCountryChanged: onCountryChanged,
              ),
            ),
          ),
        ),
      );
      return key;
    }

    testWidgets('defaults to India (+91) with a 10-digit limit', (tester) async {
      await pumpField(tester);

      expect(find.text('🇮🇳'), findsOneWidget);
      expect(find.text('+91'), findsOneWidget);
      // The TextFormField forwards maxLength to the underlying TextField.
      final field = tester.widget<TextField>(find.byType(TextField));
      expect(field.maxLength, 10);
    });

    testWidgets('exposes the canonical +91 value for an Indian 10-digit input', (
      tester,
    ) async {
      final key = await pumpField(tester);

      await tester.enterText(find.byType(TextFormField), '9876543210');

      expect(key.currentState?.currentCountry.isoCode, 'IN');
      expect(key.currentState?.canonicalValue, '+919876543210');
    });

    testWidgets('tapping the country prefix opens the searchable picker', (
      tester,
    ) async {
      await pumpField(tester);

      await openPicker(tester);

      expect(find.text('Select country'), findsOneWidget);
      expect(find.text('Search country or dialing code'), findsOneWidget);
    });

    testWidgets('picker searches by country name', (tester) async {
      await pumpField(tester);

      await openPicker(tester);

      await tester.enterText(searchField(), 'united arab');
      await tester.pumpAndSettle();

      expect(find.text('United Arab Emirates'), findsOneWidget);
      expect(find.text('India'), findsNothing);
    });

    testWidgets('picker searches by dialing code', (tester) async {
      await pumpField(tester);

      await openPicker(tester);

      await tester.enterText(searchField(), '971');
      await tester.pumpAndSettle();

      expect(find.text('United Arab Emirates'), findsOneWidget);
    });

    testWidgets(
      'selecting another country updates the prefix and keeps its dialing code',
      (tester) async {
        final key = await pumpField(tester);

        await openPicker(tester);
        await tester.enterText(searchField(), 'united arab');
        await tester.pumpAndSettle();
        await tester.tap(find.text('United Arab Emirates'));
        await tester.pumpAndSettle();

        expect(key.currentState?.currentCountry.isoCode, 'AE');
        expect(find.text('🇦🇪'), findsOneWidget);
        expect(find.text('🇮🇳'), findsNothing);
        expect(find.text('+971'), findsOneWidget);

        await tester.enterText(find.byType(TextFormField), '501234567');
        // No silent +91 conversion for non-India numbers.
        expect(key.currentState?.canonicalValue, '+971501234567');
      },
    );

    testWidgets('initialCountry prefills a non-India country', (tester) async {
      final key = await pumpField(tester, initialCountry: CountryCodes.byIso('AE')!);

      expect(key.currentState?.currentCountry.isoCode, 'AE');
      expect(find.text('🇦🇪'), findsOneWidget);
      expect(find.text('+971'), findsOneWidget);
      expect(find.text('🇮🇳'), findsNothing);
    });

    testWidgets('onCountryChanged fires with the picked country', (tester) async {
      CountryCode? changed;
      await pumpField(tester, onCountryChanged: (c) => changed = c);

      await openPicker(tester);
      await tester.enterText(searchField(), 'united arab');
      await tester.pumpAndSettle();
      await tester.tap(find.text('United Arab Emirates'));
      await tester.pumpAndSettle();

      expect(changed?.isoCode, 'AE');
    });

    testWidgets('required empty input shows the India validation message', (
      tester,
    ) async {
      final formKey = GlobalKey<FormState>();
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: Form(
              key: formKey,
              child: CountryCodeMobileField(
                controller: TextEditingController(),
              ),
            ),
          ),
        ),
      );

      formKey.currentState!.validate();
      await tester.pump();

      expect(find.text('Enter a valid 10-digit mobile number.'), findsOneWidget);
    });

    testWidgets('optional empty input passes null to the caller validator', (
      tester,
    ) async {
      final formKey = GlobalKey<FormState>();
      String? received;
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: Form(
              key: formKey,
              child: CountryCodeMobileField(
                controller: TextEditingController(),
                optional: true,
                validator: (canonical) {
                  received = canonical;
                  return null;
                },
              ),
            ),
          ),
        ),
      );

      formKey.currentState!.validate();
      await tester.pump();

      expect(received, isNull);
      expect(find.text('Enter a valid 10-digit mobile number.'), findsNothing);
    });
  });
}
