import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/features/branding/branding_controller.dart';
import 'package:doodh_direct_mobile/features/branding/branding_gate.dart';
import 'package:doodh_direct_mobile/features/branding/branding_models.dart';
import 'package:doodh_direct_mobile/features/branding/doodh_brand_mark.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

class _SeededBrandingController extends BrandingController {
  _SeededBrandingController(this.seed);

  final BrandingState seed;

  @override
  BrandingState build() => seed;
}

Future<ProviderContainer> _pump(
  WidgetTester tester, {
  required BrandingState seed,
  Widget? child,
}) async {
  final container = ProviderContainer(
    overrides: [
      brandingControllerProvider.overrideWith(
        () => _SeededBrandingController(seed),
      ),
    ],
  );
  addTearDown(container.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        theme: ThemeData(useMaterial3: true),
        // Mirror the production wiring: the gate sits above the router
        // content via MaterialApp.builder.
        builder: (context, wrappedChild) =>
            BrandingGate(child: wrappedChild ?? const SizedBox.shrink()),
        home: child ?? const Scaffold(body: Text('app content')),
      ),
    ),
  );
  return container;
}

/// A valid 1×1 PNG so test decodes succeed (arbitrary bytes would hit the
/// errorBuilder and mask what is actually under test).
const List<int> _validPngBytes = [
  0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
  0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
  0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00,
  0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x62, 0x00, 0x01, 0x00, 0x00,
  0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49,
  0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
];

BrandingState _stateWithAnimation() => BrandingState(
  configuration: BrandingConfiguration(
    logo: BrandingAssetInfo(
      kind: BrandingAssetKind.logo,
      fileName: 'logo.png',
      contentType: 'image/png',
      fileSize: _validPngBytes.length,
      updatedAt: DateTime(2026, 9, 28),
      url: '/api/v1/branding/logo',
    ),
    startupAnimation: BrandingAssetInfo(
      kind: BrandingAssetKind.startupAnimation,
      fileName: 'intro.gif',
      contentType: 'image/gif',
      fileSize: _validPngBytes.length,
      updatedAt: DateTime(2026, 9, 28),
      url: '/api/v1/branding/startup-animation',
    ),
  ),
  logoBytes: Uint8List.fromList(_validPngBytes),
  animationBytes: Uint8List.fromList(_validPngBytes),
  source: BrandingSource.remote,
  isReady: true,
);

void main() {
  testWidgets('gate passes the app straight through when no animation is set',
      (tester) async {
    await _pump(
      tester,
      seed: const BrandingState(source: BrandingSource.fallback),
    );

    expect(find.text('app content'), findsOneWidget);
    expect(find.text('Tap to continue'), findsNothing);
  });

  testWidgets('gate holds the animation and blocks the app until skipped',
      (tester) async {
    final container = await _pump(tester, seed: _stateWithAnimation());
    await tester.pump();

    // The app content is mounted underneath but not interactive; the gate is
    // on top with its skip affordance.
    expect(find.text('Tap to continue'), findsOneWidget);
    expect(
      find.byWidgetPredicate(
        (widget) => widget is IgnorePointer && widget.ignoring,
      ),
      findsOneWidget,
    );

    // Tap anywhere → immediate reveal, latched for the process lifetime.
    await tester.tap(find.byType(GestureDetector).last);
    await tester.pump();

    expect(container.read(brandingGateCompletedProvider), isTrue);
    expect(find.text('Tap to continue'), findsNothing);
    expect(find.text('app content'), findsOneWidget);
  });

  testWidgets('gate auto-dismisses after the hold duration', (tester) async {
    final container = await _pump(tester, seed: _stateWithAnimation());
    await tester.pump();

    expect(find.text('Tap to continue'), findsOneWidget);

    await tester.pump(brandingAnimationHoldDuration);
    await tester.pump();

    expect(container.read(brandingGateCompletedProvider), isTrue);
    expect(find.text('app content'), findsOneWidget);
  });

  testWidgets('gate stays dismissed once latched even if animation is present',
      (tester) async {
    final container = await _pump(tester, seed: _stateWithAnimation());
    await tester.pump();
    await tester.tap(find.byType(GestureDetector).last);
    await tester.pump();
    expect(find.text('app content'), findsOneWidget);

    // A rebuild with fresh animation bytes (e.g. a background refresh) must
    // not re-show the startup gate.
    container.read(brandingControllerProvider.notifier);
    await tester.pump();
    expect(find.text('Tap to continue'), findsNothing);
  });

  testWidgets('brand mark falls back to drop icon and wordmark',
      (tester) async {
    await _pump(
      tester,
      seed: const BrandingState(source: BrandingSource.fallback),
      child: const Scaffold(
        body: Center(child: DoodhBrandMark()),
      ),
    );

    expect(find.byIcon(Icons.water_drop_rounded), findsOneWidget);
    expect(find.text('DoodhDirect'), findsOneWidget);
  });

  testWidgets('brand mark renders the uploaded logo when branding is cached',
      (tester) async {
    await _pump(
      tester,
      seed: _stateWithAnimation(),
      child: const Scaffold(
        body: Center(child: DoodhBrandMark()),
      ),
    );
    await tester.pump();

    // Business ask: the uploaded logo renders BESIDE the DoodhDirect name —
    // both must be present inside the mark, and the fallback icon must not.
    // (The gate is also mounted above this scaffold and shows the animation
    // image, so scope the finders to the brand mark itself.)
    expect(
      find.descendant(
        of: find.byType(DoodhBrandMark),
        matching: find.byType(Image),
      ),
      findsOneWidget,
    );
    expect(
      find.descendant(
        of: find.byType(DoodhBrandMark),
        matching: find.byIcon(Icons.water_drop_rounded),
      ),
      findsNothing,
    );
    expect(
      find.descendant(
        of: find.byType(DoodhBrandMark),
        matching: find.text('DoodhDirect'),
      ),
      findsOneWidget,
    );
  });

  testWidgets(
    'brand mark loads the network logo when the cached bytes are missing',
    (tester) async {
      // Startup preload missed (cache miss / timeout) but the server HAS a
      // configured logo — the mark must not permanently downgrade to the
      // generic drop icon while branding exists.
      await _pump(
        tester,
        seed: BrandingState(
          configuration: BrandingConfiguration(
            logo: BrandingAssetInfo(
              kind: BrandingAssetKind.logo,
              fileName: 'logo.png',
              contentType: 'image/png',
              fileSize: _validPngBytes.length,
              updatedAt: DateTime(2026, 9, 28),
              url: '/api/v1/branding/logo',
            ),
          ),
          source: BrandingSource.fallback,
          isReady: true,
        ),
        child: const Scaffold(
          body: Center(child: DoodhBrandMark(showWordmark: false)),
        ),
      );

      // The network attempt is mounted immediately (the error fallback only
      // appears after the request actually fails, i.e. a later frame).
      expect(
        find.descendant(
          of: find.byType(DoodhBrandMark),
          matching: find.byWidgetPredicate(
            (widget) => widget is Image && widget.image is NetworkImage,
          ),
        ),
        findsOneWidget,
      );
    },
  );

  testWidgets('customer shell header shows the uploaded logo', (tester) async {
    await _pump(
      tester,
      seed: _stateWithAnimation(),
      child: const CustomerShell(
        currentPath: '/home',
        title: 'Home',
        child: SizedBox.shrink(),
      ),
    );
    await tester.pump();
    await tester.pump();

    expect(find.byType(DoodhBrandMark), findsOneWidget);
    expect(
      find.descendant(
        of: find.byType(DoodhBrandMark),
        matching: find.byType(Image),
      ),
      findsOneWidget,
    );
  });
}
