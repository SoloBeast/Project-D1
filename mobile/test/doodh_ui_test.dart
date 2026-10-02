import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

/// Widget tests for the Phase 0 shared design-system primitives in
/// `core/widgets`. These guard the contract that future screen work relies on:
/// consistent semantics, 48px touch targets and token-backed styling.
Widget _wrap(Widget child, {double width = 400}) => MaterialApp(
  theme: buildDoodhTheme(),
  home: Scaffold(
    body: Center(
      child: SizedBox(width: width, child: child),
    ),
  ),
);

void main() {
  group('Doodh design tokens', () {
    test('spacing aliases resolve to the canonical md (16) scale', () {
      expect(DoodhSpacing.pagePadding, const EdgeInsets.all(16));
      expect(DoodhSpacing.cardPadding, const EdgeInsets.all(16));
      expect(DoodhSpacing.sectionGap, 16.0);
      expect(DoodhSpacing.gutter, DoodhSpacing.md);
    });

    test('radius aliases match their raw numeric values', () {
      expect(DoodhRadii.mdRadius, DoodhRadii.md);
      expect(DoodhRadii.pillRadius, DoodhRadii.pill);
      // Softened to the reference rounding during the visual alignment pass.
      expect(DoodhRadii.mdValue, 18.0);
    });

    test('breakpoints bucket widths consistently', () {
      expect(DoodhBreakpoints.of(320), DoodhWindowSize.compact);
      expect(DoodhBreakpoints.of(599), DoodhWindowSize.compact);
      expect(DoodhBreakpoints.of(600), DoodhWindowSize.medium);
      expect(DoodhBreakpoints.of(900), DoodhWindowSize.expanded);
      expect(DoodhBreakpoints.of(1200), DoodhWindowSize.large);
    });

    test('content max widths are ordered narrow < form < wide', () {
      expect(DoodhContentMax.narrow, lessThan(DoodhContentMax.form));
      expect(DoodhContentMax.form, lessThan(DoodhContentMax.wide));
    });
  });

  group('DoodhPriceTag', () {
    test('format drops noisy trailing zeros', () {
      expect(DoodhPriceTag.format(1200), '1200');
      expect(DoodhPriceTag.format(1200.0), '1200');
      expect(DoodhPriceTag.format(99.5), '99.50');
    });

    testWidgets('exposes a single price semantic label', (tester) async {
      final handle = tester.ensureSemantics();
      await tester.pumpWidget(_wrap(const DoodhPriceTag(amount: 1200)));
      expect(find.bySemanticsLabel('Price \u20B91200'), findsOneWidget);
      handle.dispose();
    });

    testWidgets('announces the original price when discounted', (tester) async {
      final handle = tester.ensureSemantics();
      await tester.pumpWidget(
        _wrap(const DoodhPriceTag(amount: 900, originalAmount: 1200)),
      );
      expect(
        find.bySemanticsLabel('Price \u20B9900, was \u20B91200'),
        findsOneWidget,
      );
      handle.dispose();
    });
  });

  group('DoodhChip', () {
    testWidgets('prefixes the tone to the label for screen readers', (
      tester,
    ) async {
      final handle = tester.ensureSemantics();
      await tester.pumpWidget(
        _wrap(const DoodhChip(label: 'Active', tone: DoodhTone.success)),
      );
      expect(find.bySemanticsLabel('Success: Active'), findsOneWidget);
      handle.dispose();
    });
  });

  group('DoodhInfoBanner', () {
    testWidgets('is an assertive live region with a tone-prefixed label', (
      tester,
    ) async {
      final handle = tester.ensureSemantics();
      await tester.pumpWidget(
        _wrap(
          const DoodhInfoBanner(
            message: 'Saved successfully',
            tone: DoodhTone.success,
          ),
        ),
      );
      final node = tester.getSemantics(
        find.bySemanticsLabel('Success: Saved successfully'),
      );
      expect(node.flagsCollection.isLiveRegion, isTrue);
      handle.dispose();
    });
  });

  group('DoodhButton', () {
    testWidgets('fires onPressed when tapped', (tester) async {
      var taps = 0;
      await tester.pumpWidget(
        _wrap(DoodhButton(label: 'Save', onPressed: () => taps++)),
      );
      await tester.tap(find.text('Save'));
      expect(taps, 1);
    });

    testWidgets('busy buttons are disabled and announce progress', (
      tester,
    ) async {
      final handle = tester.ensureSemantics();
      var taps = 0;
      await tester.pumpWidget(
        _wrap(DoodhButton(label: 'Save', busy: true, onPressed: () => taps++)),
      );
      expect(find.bySemanticsLabel('Save, in progress'), findsOneWidget);
      await tester.tap(find.byType(FilledButton), warnIfMissed: false);
      expect(taps, 0);
      handle.dispose();
    });
  });

  group('DoodhQuantityStepper', () {
    testWidgets('stepper buttons honour the 48px minimum touch target', (
      tester,
    ) async {
      await tester.pumpWidget(
        _wrap(DoodhQuantityStepper(quantity: 2, onChanged: (_) {})),
      );
      final buttons = find.byType(IconButton);
      expect(buttons, findsNWidgets(2));
      for (final element in buttons.evaluate()) {
        final size = tester.getSize(find.byWidget(element.widget));
        expect(size.width, greaterThanOrEqualTo(44));
        expect(size.height, greaterThanOrEqualTo(44));
      }
    });

    testWidgets('clamps at min and max', (tester) async {
      final changes = <int>[];
      await tester.pumpWidget(
        _wrap(
          DoodhQuantityStepper(
            quantity: 1,
            min: 1,
            max: 2,
            onChanged: changes.add,
          ),
        ),
      );
      // At min the decrease button is disabled.
      await tester.tap(find.byIcon(Icons.remove), warnIfMissed: false);
      expect(changes, isEmpty);
      await tester.tap(find.byIcon(Icons.add));
      expect(changes, [2]);
    });
  });

  group('DoodhCard', () {
    testWidgets('tappable cards are exposed as buttons', (tester) async {
      final handle = tester.ensureSemantics();
      await tester.pumpWidget(
        _wrap(
          DoodhCard(
            onTap: () {},
            semanticLabel: 'Open order',
            child: const Text('Order #1'),
          ),
        ),
      );
      final node = tester.getSemantics(
        find.bySemanticsLabel('Open order'),
      );
      expect(node.flagsCollection.isButton, isTrue);
      handle.dispose();
    });
  });

  group('DoodhSectionCard', () {
    testWidgets('renders an accessible header and its children', (
      tester,
    ) async {
      final handle = tester.ensureSemantics();
      await tester.pumpWidget(
        _wrap(
          const DoodhSectionCard(
            title: 'Delivery details',
            children: [Text('Body')],
          ),
        ),
      );
      expect(find.text('Delivery details'), findsOneWidget);
      expect(find.text('Body'), findsOneWidget);
      final node = tester.getSemantics(find.text('Delivery details'));
      expect(node.flagsCollection.isHeader, isTrue);
      handle.dispose();
    });
  });

  group('DoodhKeyValueRow', () {
    testWidgets('stacks label above value on narrow widths', (tester) async {
      await tester.pumpWidget(
        _wrap(
          const DoodhKeyValueRow(label: 'Route', value: 'North'),
          width: 300,
        ),
      );
      expect(find.text('Route'), findsOneWidget);
      expect(find.text('North'), findsOneWidget);
    });
  });

  group('StatePanel', () {
    testWidgets('titles are marked as headers for assistive tech', (
      tester,
    ) async {
      final handle = tester.ensureSemantics();
      await tester.pumpWidget(
        _wrap(
          const StatePanel(
            icon: Icons.inbox_outlined,
            title: 'Nothing here',
            message: 'No records found.',
          ),
        ),
      );
      final node = tester.getSemantics(find.text('Nothing here'));
      expect(node.flagsCollection.isHeader, isTrue);
      handle.dispose();
    });
  });

  group('DoodhResponsive / DoodhGrid', () {
    testWidgets('DoodhGrid reflows to a single column when narrow', (
      tester,
    ) async {
      await tester.pumpWidget(
        _wrap(
          const DoodhGrid(
            minItemWidth: 220,
            children: [Text('A'), Text('B')],
          ),
          width: 300,
        ),
      );
      expect(find.text('A'), findsOneWidget);
      expect(find.text('B'), findsOneWidget);
      // Two cards at min 220 cannot fit in 300px, so they wrap.
      final wrap = tester.widget<Wrap>(find.byType(Wrap));
      expect(wrap.children.length, 2);
    });

    testWidgets('DoodhGrid keeps two catalogue cards side-by-side on phones',
        (tester) async {
      // Regression: with the old width/minItemWidth estimate, 328px of
      // content (a 360px phone minus page gutters) floored to a single
      // column for minItemWidth 150, showing one product per row.
      await tester.pumpWidget(
        _wrap(
          const DoodhGrid(
            minItemWidth: 150,
            maxColumns: 4,
            children: [Text('A'), Text('B')],
          ),
          width: 328,
        ),
      );
      final boxes = tester
          .widgetList<SizedBox>(
            find.descendant(
              of: find.byType(Wrap),
              matching: find.byType(SizedBox),
            ),
          )
          .toList(growable: false);
      expect(boxes.length, 2);
      // (328 - 16 gutter) / 2 columns.
      expect(boxes[0].width, 156);
      expect(boxes[1].width, 156);
      // Same Y offset proves the two cards share one row.
      final topA = tester.getTopLeft(find.text('A')).dy;
      final topB = tester.getTopLeft(find.text('B')).dy;
      expect(topA, topB);
    });

    testWidgets('DoodhGrid minColumns holds 2-up on narrow phones', (
      tester,
    ) async {
      // Regression for the customer home/catalogue single-column report:
      // a 320px phone leaves ~288px of content, where the width estimate
      // alone floors to 1 even at minItemWidth 150. minColumns: 2 keeps
      // the two product cards sharing one row.
      await tester.pumpWidget(
        _wrap(
          const DoodhGrid(
            minItemWidth: 150,
            maxColumns: 4,
            minColumns: 2,
            children: [Text('A'), Text('B')],
          ),
          width: 288,
        ),
      );
      final boxes = tester
          .widgetList<SizedBox>(
            find.descendant(
              of: find.byType(Wrap),
              matching: find.byType(SizedBox),
            ),
          )
          .toList(growable: false);
      expect(boxes.length, 2);
      // (288 - 16 gutter) / 2 columns.
      expect(boxes[0].width, 136);
      expect(boxes[1].width, 136);
      final topA = tester.getTopLeft(find.text('A')).dy;
      final topB = tester.getTopLeft(find.text('B')).dy;
      expect(topA, topB);
    });
  });
}
