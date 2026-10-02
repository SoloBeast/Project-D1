import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/product_widgets.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

Widget _wrap(Widget child, {double width = 400}) => MaterialApp(
  theme: buildDoodhTheme(),
  home: Scaffold(
    body: Center(
      child: SizedBox(width: width, child: child),
    ),
  ),
);

void main() {
  group('DoodhProductImage', () {
    testWidgets('renders branded fallback when image is absent', (
      tester,
    ) async {
      await tester.pumpWidget(
        _wrap(const DoodhProductImage(fallbackLabel: 'Fresh dairy')),
      );

      expect(find.text('Fresh dairy'), findsOneWidget);
      expect(find.byIcon(Icons.water_drop_rounded), findsOneWidget);
    });

    testWidgets('uses an image provider when supplied', (tester) async {
      await tester.pumpWidget(
        _wrap(
          const DoodhProductImage(
            imageProvider: AssetImage('test_assets/product.png'),
          ),
        ),
      );
      await tester.pump();

      expect(find.byType(Image), findsOneWidget);
    });
  });

  group('DoodhProductCard', () {
    testWidgets('shows price hierarchy, availability and actions', (
      tester,
    ) async {
      final handle = tester.ensureSemantics();
      var added = false;
      var subscribed = false;
      await tester.pumpWidget(
        _wrap(
          DoodhProductCard(
            name: 'Whole Milk',
            categoryName: 'Milk',
            price: 58,
            originalAmount: 65,
            unitLabel: 'litre',
            isAvailable: true,
            onAddToCart: () => added = true,
            onSubscribe: () => subscribed = true,
          ),
        ),
      );

      expect(find.text('Whole Milk'), findsOneWidget);
      expect(
        find.bySemanticsLabel(
          'Whole Milk, Milk, 58 rupees per litre, 11 percent off, available',
        ),
        findsOneWidget,
      );
      expect(find.byType(DoodhPriceTag), findsOneWidget);
      expect(find.text('Available now'), findsOneWidget);

      await tester.tap(find.text('Add to cart'));
      await tester.tap(find.text('Subscribe'));
      expect(added, isTrue);
      expect(subscribed, isTrue);
      handle.dispose();
    });

    testWidgets('shows out of stock state and keeps touch targets usable', (
      tester,
    ) async {
      await tester.pumpWidget(
        _wrap(
          DoodhProductCard(
            name: 'Paneer',
            categoryName: 'Dairy',
            price: 240,
            unitLabel: 'kg',
            isAvailable: false,
            onAddToCart: () {},
          ),
        ),
      );

      expect(find.text('Out of stock'), findsOneWidget);
      final addButton = find.widgetWithText(FilledButton, 'Unavailable');
      expect(tester.getSize(addButton).height, greaterThanOrEqualTo(48));
      expect(tester.widget<FilledButton>(addButton).onPressed, isNull);
    });
  });

  testWidgets('search is labelled and offers a clear affordance', (
    tester,
  ) async {
    final controller = TextEditingController();
    var query = '';
    await tester.pumpWidget(
      _wrap(
        DoodhSearchBar(
          controller: controller,
          onChanged: (value) => query = value,
        ),
      ),
    );

    expect(find.bySemanticsLabel('Search catalogue products'), findsOneWidget);
    await tester.enterText(find.byType(TextField), 'paneer');
    await tester.pump();
    expect(query, 'paneer');
    expect(find.byTooltip('Clear search'), findsOneWidget);

    await tester.tap(find.byTooltip('Clear search'));
    await tester.pump();
    expect(controller.text, isEmpty);
    expect(query, isEmpty);
  });

  testWidgets('product image has an accessible semantic label', (tester) async {
    final handle = tester.ensureSemantics();
    await tester.pumpWidget(
      _wrap(const DoodhProductImage(semanticLabel: 'Whole milk product image')),
    );

    expect(find.bySemanticsLabel('Whole milk product image'), findsOneWidget);
    handle.dispose();
  });
}
