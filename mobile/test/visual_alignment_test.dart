import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('shared alignment tokens', () {
    test('uses the reference-aligned forest/ivory/tan palette', () {
      // Primary deep teal #0B6B70 for primary actions/CTA fills (incl. the
      // product-card "Add to cart" button); warm ivory background, tan
      // delivery surfaces, deep gold accent per the reference images.
      expect(DoodhColors.teal, const Color(0xFF0B6B70));
      expect(DoodhColors.tealDark, const Color(0xFF0C2E2A));
      expect(DoodhColors.cream, const Color(0xFFFFF9EE));
      expect(DoodhColors.mint, const Color(0xFFEAF0DC));
      expect(DoodhColors.line, const Color(0xFFE8E0CD));
      expect(DoodhColors.tanSurface, const Color(0xFFEFDFB2));
      expect(DoodhColors.goldAccent, const Color(0xFF8A5B1F));
      expect(DoodhColors.highlightSurface, const Color(0xFFFBF3D9));
      expect(DoodhColors.deliveredGreen, const Color(0xFF17B34A));
    });

    test('scaffold background and primary follow the reference theme', () {
      final theme = buildDoodhTheme();
      expect(theme.scaffoldBackgroundColor, DoodhColors.cream);
      expect(theme.colorScheme.primary, DoodhColors.teal);
      // Semantic colors keep their meaning (surface/foreground pairs).
      expect(DoodhColors.successSurface, DoodhColors.mint);
      expect(DoodhColors.successForeground, DoodhColors.tealDark);
      expect(DoodhColors.errorForeground, DoodhColors.coral);
    });

    test('adds the reference tan/gold accents to the existing palette', () {
      expect(DoodhColors.tanSurface, const Color(0xFFEFDFB2));
      expect(DoodhColors.goldAccent, const Color(0xFF8A5B1F));
    });

    test('softens the shared card radius toward the reference ~20px feel', () {
      expect(DoodhRadii.mdValue, 18);
    });

    testWidgets('primary and secondary CTAs render as pill buttons', (
      tester,
    ) async {
      final theme = buildDoodhTheme();
      await tester.pumpWidget(
        MaterialApp(
          theme: theme,
          home: const Scaffold(
            body: Center(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  DoodhButton(label: 'Primary action'),
                  SizedBox(height: 12),
                  DoodhButton(
                    label: 'Secondary action',
                    variant: DoodhButtonVariant.secondary,
                  ),
                ],
              ),
            ),
          ),
        ),
      );
      await tester.pump();

      // The pill silhouette is a theme-level contract shared by every CTA.
      expect(
        theme.filledButtonTheme.style?.shape?.resolve({}),
        isA<StadiumBorder>(),
      );
      expect(
        theme.outlinedButtonTheme.style?.shape?.resolve({}),
        isA<StadiumBorder>(),
      );
      // Accessible minimum height is preserved on both variants.
      final primaryRect = tester.getRect(find.byType(FilledButton));
      expect(primaryRect.height, greaterThanOrEqualTo(48));
      final secondaryRect = tester.getRect(find.byType(OutlinedButton));
      expect(secondaryRect.height, greaterThanOrEqualTo(48));
    });

    test('cards use the softer radius with the subtle line border', () {
      final theme = buildDoodhTheme();
      final shape = theme.cardTheme.shape as RoundedRectangleBorder?;
      expect(shape?.borderRadius, DoodhRadii.mdRadius);
      expect(shape?.side.color, DoodhColors.line);
      expect(theme.cardTheme.elevation, DoodhElevation.none);
    });

    test('theme is stable across light surfaces', () {
      expect(buildDoodhTheme().useMaterial3, isTrue);
    });
  });

  group('bottom navigation active treatment', () {
    test('uses the tan pill indicator with gold selected icon and label', () {
      final theme = buildDoodhTheme();
      final nav = theme.navigationBarTheme;

      expect(nav.indicatorColor, DoodhColors.tanSurface);

      final iconColor = nav.iconTheme!.resolve({WidgetState.selected});
      expect(iconColor?.color, DoodhColors.goldAccent);
      final iconColorIdle = nav.iconTheme!.resolve({});
      expect(iconColorIdle?.color, DoodhColors.muted);

      final labelStyle = nav.labelTextStyle!.resolve({WidgetState.selected});
      expect(labelStyle?.color, DoodhColors.goldAccent);
      final labelStyleIdle = nav.labelTextStyle!.resolve({});
      expect(labelStyleIdle?.color, DoodhColors.muted);
    });
  });

  group('responsive breakpoints', () {
    test('theme tokens expose the existing compact/wide breakpoints', () {
      expect(DoodhBreakpoints.compact, 600);
      expect(DoodhBreakpoints.expanded, 1200);
    });
  });
}
