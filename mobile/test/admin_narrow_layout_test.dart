import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_repository.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_screens.dart';
import 'package:doodh_direct_mobile/features/setup/charge_controller.dart';
import 'package:doodh_direct_mobile/features/setup/charge_models.dart';
import 'package:doodh_direct_mobile/features/setup/charge_repository.dart';
import 'package:doodh_direct_mobile/features/setup/charge_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

/// Regression tests for the admin dashboard overflow reported on phones:
/// card title rows (title + wide action buttons) and pagination footers must
/// wrap/stack instead of overflowing a ~296px card content width.
/// Pumped at 360x740, the width that produced
/// "A RenderFlex overflowed by 98 pixels on the right".
class _FakeChargeRepo extends ChargeRepository {
  _FakeChargeRepo()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<List<Charge>> list(String accessToken) async => [
    Charge(
      publicId: 'c-1',
      chargeType: 'GST',
      chargeCode: 'CGST-LONG-CODE-5',
      description: 'A fairly long description that wraps a lot of text here',
      percentage: 12.5,
      isActive: true,
      applicableOnAll: true,
      isUsed: false,
      createdAt: DateTime(2026, 9, 28),
      updatedAt: DateTime(2026, 9, 28),
    ),
    Charge(
      publicId: 'c-2',
      chargeType: 'Service',
      chargeCode: 'SC-2',
      description: null,
      percentage: 5,
      isActive: false,
      applicableOnAll: false,
      isUsed: false,
      createdAt: DateTime(2026, 9, 28),
      updatedAt: DateTime(2026, 9, 28),
    ),
  ];
}

/// Seeded like the reported screenshot: long product names/SKUs and a few
/// categories, so row-level overflows surface instead of hiding behind the
/// empty panel.
class _SeededCatalogueRepo extends CatalogueRepository {
  _SeededCatalogueRepo()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  static const _milk = ProductCategory(
    publicId: 'cat-milk',
    code: 'MILK',
    name: 'Milk',
    description: 'Milk',
    isActive: true,
  );
  static const _paneer = ProductCategory(
    publicId: 'cat-paneer',
    code: 'PANEER',
    name: 'Paneer',
    description: 'Paneer',
    isActive: true,
  );
  static const _ghee = ProductCategory(
    publicId: 'cat-ghee',
    code: 'GHEE',
    name: 'GHEE',
    description: 'GHEE',
    isActive: true,
  );

  @override
  Future<List<CatalogueProduct>> getAdminProducts(String accessToken) async =>
      const [
        CatalogueProduct(
          publicId: 'p-1',
          sku: 'MLK001',
          name: 'Cow Milk',
          description: 'Cow Milk',
          category: _milk,
          unitOfMeasure: 'litre',
          price: 60,
          isActive: true,
          branchAvailability: [],
        ),
        CatalogueProduct(
          publicId: 'p-2',
          sku: 'FRESH-BUFFALO-MILK-001',
          name: 'Fresh Buffalo Milk With A Very Long Name',
          description: 'Fresh buffalo milk sold in large cans',
          category: _milk,
          unitOfMeasure: 'litre',
          price: 80,
          isActive: true,
          branchAvailability: [],
        ),
        CatalogueProduct(
          publicId: 'p-3',
          sku: 'PNR001',
          name: 'Paneer',
          description: 'Paneer',
          category: _paneer,
          unitOfMeasure: 'kilogram',
          price: 320,
          isActive: false,
          branchAvailability: [],
        ),
      ];

  @override
  Future<List<ProductCategory>> getAdminCategories(String accessToken) async =>
      const [_ghee, _milk, _paneer];

  @override
  Future<List<CatalogueBranch>> getBranches(String accessToken) async =>
      const [
        CatalogueBranch(
          publicId: 'b-1',
          code: 'MAIN',
          name: 'Main Branch',
          city: 'Bengaluru',
          state: 'Karnataka',
          isActive: true,
        ),
      ];
}

class _SeededSession extends SessionController {
  _SeededSession(this.permissions);

  final List<String> permissions;

  @override
  SessionState build() => SessionState.authenticated(
    AuthSession(
      user: AuthUser(
        publicUserId: 'admin-1',
        displayName: 'Admin',
        email: null,
        mobile: null,
        roles: const ['SYSTEM_ADMIN'],
        permissions: permissions,
        branchIds: const [],
      ),
      accessToken: 'token',
      refreshToken: 'refresh',
      accessTokenExpiresAtUtc: DateTime.utc(2099),
      refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
    ),
  );
}

Future<void> _useNarrow(WidgetTester tester) async {
  await tester.binding.setSurfaceSize(const Size(360, 740));
  addTearDown(() => tester.binding.setSurfaceSize(null));
}

void main() {
  testWidgets('charge list renders without overflow on a narrow phone', (
    tester,
  ) async {
    await _useNarrow(tester);
    final container = ProviderContainer(
      overrides: [
        chargeRepositoryProvider.overrideWithValue(_FakeChargeRepo()),
        sessionControllerProvider.overrideWith(
          () => _SeededSession(const [
            'SETUP.TAX_CHARGES.READ',
            'SETUP.TAX_CHARGES.MANAGE',
          ]),
        ),
      ],
    );
    addTearDown(container.dispose);
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: ChargeListScreen()),
      ),
    );
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    // Force the table rows and the pagination footer through layout.
    await tester.drag(find.byType(ListView), const Offset(0, -2000));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    // Both rows and the footer range render.
    expect(find.text('CGST-LONG-CODE-5'), findsOneWidget);
    expect(find.text('1-2 of 2'), findsOneWidget);
  });

  testWidgets('catalogue management renders without overflow on a narrow phone', (
    tester,
  ) async {
    await _useNarrow(tester);
    final container = ProviderContainer(
      overrides: [
        catalogueRepositoryProvider.overrideWithValue(_SeededCatalogueRepo()),
        sessionControllerProvider.overrideWith(
          () => _SeededSession(const ['CATALOGUE.MANAGE']),
        ),
      ],
    );
    addTearDown(container.dispose);
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: AdminCatalogueScreen()),
      ),
    );
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    // Header asserts before scrolling: far off-screen sliver children are
    // unmounted after a deep scroll, so check it while it is laid out.
    expect(find.text('Catalogue management'), findsOneWidget);
    await tester.drag(find.byType(ListView), const Offset(0, -2000));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    // Footer survived the scroll with no overflow (3 seeded products).
    expect(find.text('1-3 of 3'), findsOneWidget);
    // Same contract on the Categories tab (standalone card). The deep
    // scroll unmounted the tab bar, so bring it back into view first.
    await tester.drag(find.byType(ListView), const Offset(0, 3000));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    await tester.tap(find.text('Categories'));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    await tester.drag(find.byType(ListView), const Offset(0, -2000));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    expect(find.text('1-3 of 3'), findsOneWidget);
  });
}
