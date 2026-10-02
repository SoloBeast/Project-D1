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
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

/// Tax & Charges master with one charge of every mode so option filtering can
/// be asserted: active item-level (selectable), active global (never
/// selectable), inactive item-level (never selectable).
class _FakeChargeMasterRepo implements ChargeRepository {
  _FakeChargeMasterRepo();

  ApiException? listException;

  Charge _charge(
    String publicId,
    String chargeCode, {
    required bool isActive,
    required bool applicableOnAll,
    required String chargeType,
    required double percentage,
  }) => Charge(
    publicId: publicId,
    chargeType: chargeType,
    chargeCode: chargeCode,
    description: 'Desc $chargeCode',
    percentage: percentage,
    isActive: isActive,
    applicableOnAll: applicableOnAll,
    isUsed: false,
    createdAt: DateTime(2026, 9, 28),
    updatedAt: DateTime(2026, 9, 28),
  );

  @override
  Future<List<Charge>> list(String accessToken) async {
    final error = listException;
    if (error != null) throw error;
    return [
      _charge(
        'chg-item-1',
        'GST-ITEM',
        chargeType: 'GST',
        percentage: 5,
        isActive: true,
        applicableOnAll: false,
      ),
      _charge(
        'chg-item-2',
        'TDS-FEE',
        chargeType: 'Tax',
        percentage: 12,
        isActive: true,
        applicableOnAll: false,
      ),
      _charge(
        'chg-global',
        'GST-ALL',
        chargeType: 'GST',
        percentage: 18,
        isActive: true,
        applicableOnAll: true,
      ),
      _charge(
        'chg-inactive',
        'SC-OFF',
        chargeType: 'Service',
        percentage: 2,
        isActive: false,
        applicableOnAll: false,
      ),
    ];
  }

  @override
  Future<Charge> create(String accessToken, CreateChargeRequest request) =>
      throw UnimplementedError();

  @override
  Future<void> delete(String accessToken, String publicId) =>
      throw UnimplementedError();

  @override
  Future<Charge> get(String accessToken, String publicId) =>
      throw UnimplementedError();

  @override
  Future<Charge> setActive(
    String accessToken,
    String publicId,
    bool isActive,
  ) => throw UnimplementedError();

  @override
  Future<Charge> setApplicability(
    String accessToken,
    String publicId,
    bool applicableOnAll,
  ) => throw UnimplementedError();

  @override
  Future<Charge> update(
    String accessToken,
    String publicId,
    UpdateChargeRequest request,
  ) => throw UnimplementedError();
}

class _FakeCatalogueApi extends CatalogueRepository {
  _FakeCatalogueApi()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  List<CatalogueProduct> products = [];

  /// Drafts received by create/update — recorded even when the save throws so
  /// rejection tests can assert the payload the client attempted.
  final List<ProductDraft> savedDrafts = [];
  ApiException? saveError;

  @override
  Future<List<CatalogueProduct>> getAdminProducts(String accessToken) async =>
      products;

  @override
  Future<List<ProductCategory>> getAdminCategories(String accessToken) async =>
      [_category()];

  @override
  Future<List<CatalogueBranch>> getBranches(String accessToken) async => [
    _branch(),
  ];

  @override
  Future<CatalogueProduct> createProduct(
    ProductDraft draft,
    String accessToken,
  ) async {
    savedDrafts.add(draft);
    final error = saveError;
    if (error != null) throw error;
    final saved = _product(
      publicId: 'p-${savedDrafts.length}',
      sku: draft.sku.trim().toUpperCase(),
    );
    products = [...products, saved];
    return saved;
  }

  @override
  Future<CatalogueProduct> updateProduct(
    String productId,
    ProductDraft draft,
    String accessToken,
  ) async {
    savedDrafts.add(draft);
    final error = saveError;
    if (error != null) throw error;
    final saved = _product(publicId: productId);
    products = [
      for (final item in products)
        if (item.publicId == productId) saved else item,
    ];
    return saved;
  }
}

ProductCategory _category() => const ProductCategory(
  publicId: 'cat-1',
  code: 'MILK',
  name: 'Milk',
  description: null,
  isActive: true,
);

CatalogueBranch _branch() => const CatalogueBranch(
  publicId: 'b-1',
  code: 'MAIN',
  name: 'Main Branch',
  city: 'Bengaluru',
  state: 'Karnataka',
  isActive: true,
);

ProductApplicableCharge _mapping(String chargeId, String chargeCode) =>
    ProductApplicableCharge(
      chargeId: chargeId,
      chargeCode: chargeCode,
      chargeType: 'GST',
      description: null,
      percentage: 5,
      isActive: false,
    );

CatalogueProduct _product({
  String publicId = 'p-1',
  String sku = 'MILK-001',
  List<ProductApplicableCharge> mappings = const [],
}) => CatalogueProduct(
  publicId: publicId,
  sku: sku,
  name: 'Whole Milk',
  description: 'Fresh whole milk.',
  category: _category(),
  unitOfMeasure: 'litre',
  price: 60,
  isActive: true,
  branchAvailability: [
    BranchAvailability(
      branchId: 'b-1',
      branchCode: 'MAIN',
      branchName: 'Main Branch',
      isAvailable: true,
      maxDailyQuantity: null,
    ),
  ],
  applicableCharges: mappings,
);

/// Temporary debug hook so a test can inspect the provider container.
class TesterHook {
  static ProviderContainer? lastContainer;
}

class _SeededSessionController extends SessionController {
  _SeededSessionController(this.session);

  final AuthSession session;

  @override
  SessionState build() => SessionState.authenticated(session);
}

Future<ProviderContainer> _pumpDialog(
  WidgetTester tester, {
  required _FakeCatalogueApi repository,
  required _FakeChargeMasterRepo chargeMaster,
  CatalogueProduct? product,
}) async {
  repository.products = product == null ? [] : [product];
  final session = AuthSession(
    user: const AuthUser(
      publicUserId: 'admin-1',
      displayName: 'Admin User',
      email: null,
      mobile: null,
      roles: ['SYSTEM_ADMIN'],
      permissions: ['CATALOGUE.MANAGE', 'SETUP.TAX_CHARGES.READ'],
      branchIds: [],
    ),
    accessToken: 'admin-token',
    refreshToken: 'refresh',
    accessTokenExpiresAtUtc: DateTime.utc(2099),
    refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
  );
  final container = ProviderContainer(
    overrides: [
      catalogueRepositoryProvider.overrideWithValue(repository),
      chargeRepositoryProvider.overrideWithValue(chargeMaster),
      sessionControllerProvider.overrideWith(
        () => _SeededSessionController(session),
      ),
    ],
  );
  addTearDown(container.dispose);
  TesterHook.lastContainer = container;
  // A tall, wide surface keeps the whole dialog (fields, chips, Save) and
  // the 1480px product grid (details entry sits at the far right)
  // on-screen so taps land without scrolling.
  await tester.binding.setSurfaceSize(const Size(1920, 1600));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: AdminCatalogueScreen()),
    ),
  );
  await tester.pumpAndSettle();

  if (product != null) {
    // Existing product: open the dialog through the row's details entry.
    // The wide grid scrolls horizontally — bring the trailing entry into
    // view first.
    final detailsControl = find.byKey(
      ValueKey('admin-product-details-${product.publicId}'),
    );
    expect(detailsControl, findsOneWidget);
    await tester.ensureVisible(detailsControl);
    await tester.pumpAndSettle();
    await tester.tap(detailsControl);
    await tester.pumpAndSettle();
    return container;
  }

  // New product: the Add action inserts an in-grid draft row whose details
  // entry opens the full create dialog.
  final addTrigger = find.byTooltip('Add product');
  expect(addTrigger, findsOneWidget);
  await tester.tap(addTrigger);
  await tester.pumpAndSettle();
  final draftDetails = find.byKey(
    const ValueKey('admin-product-details-draft'),
  );
  await tester.ensureVisible(draftDetails);
  await tester.pumpAndSettle();
  await tester.tap(draftDetails);
  await tester.pumpAndSettle();
  return container;
}

/// Dialog text field by label, scoped to the open dialog — the in-grid draft
/// row behind it reuses the same hints/labels.
Finder _dialogField(String label) => find.descendant(
  of: find.byType(AlertDialog),
  matching: find.widgetWithText(TextField, label),
);

/// Opens the Branches multi-select dropdown inside the product dialog.
Future<void> _openBranchDropdown(WidgetTester tester) async {
  await tester.tap(find.byKey(const ValueKey('product-dialog-branches')));
  await tester.pumpAndSettle();
}

/// Opens the Applicable-taxes multi-select dropdown inside the product
/// dialog; option chips render in this second dialog.
Future<void> _openTaxDropdown(WidgetTester tester) async {
  await tester.tap(find.byKey(const ValueKey('product-dialog-taxes')));
  await tester.pumpAndSettle();
}

Future<void> _closeSelectionDialog(WidgetTester tester) async {
  await tester.tap(find.widgetWithText(FilledButton, 'Done'));
  await tester.pumpAndSettle();
}

/// Fulfils the dialog's mandatory fields (branch selection is required) so
/// Save succeeds, mirroring what an admin does for the product basics.
Future<void> _fillBasics(WidgetTester tester) async {
  await tester.enterText(_dialogField('SKU'), 'MILK-9');
  await tester.enterText(_dialogField('Name'), 'Test Milk');
  await tester.enterText(_dialogField('Price'), '60');
  await _openBranchDropdown(tester);
  await tester.tap(
    find.byKey(const ValueKey('product-branch-option-b-1')),
  );
  await tester.pump();
  await _closeSelectionDialog(tester);
}

FilterChip _optionChip(WidgetTester tester, String chargeId) =>
    tester.widget<FilterChip>(
      find.byKey(ValueKey('product-charge-option-$chargeId')),
    );

void main() {
  testWidgets('charge options exclude global and inactive charges', (
    tester,
  ) async {
    await _pumpDialog(
      tester,
      repository: _FakeCatalogueApi(),
      chargeMaster: _FakeChargeMasterRepo(),
    );

    expect(find.text('Applicable taxes'), findsOneWidget);
    await _openTaxDropdown(tester);
    // Only active item-level charges are offered.
    expect(
      find.byKey(const ValueKey('product-charge-option-chg-item-1')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey('product-charge-option-chg-item-2')),
      findsOneWidget,
    );
    // Global (Applicable on All) and inactive charges are absent.
    expect(
      find.byKey(const ValueKey('product-charge-option-chg-global')),
      findsNothing,
    );
    expect(
      find.byKey(const ValueKey('product-charge-option-chg-inactive')),
      findsNothing,
    );
  });

  testWidgets('multi-select sends every selected charge on save', (
    tester,
  ) async {
    final repository = _FakeCatalogueApi();
    await _pumpDialog(
      tester,
      repository: repository,
      chargeMaster: _FakeChargeMasterRepo(),
    );
    await _fillBasics(tester);

    await _openTaxDropdown(tester);
    await tester.tap(
      find.byKey(const ValueKey('product-charge-option-chg-item-1')),
    );
    await tester.pump();
    await tester.tap(
      find.byKey(const ValueKey('product-charge-option-chg-item-2')),
    );
    await tester.pump();
    expect(_optionChip(tester, 'chg-item-1').selected, isTrue);
    expect(_optionChip(tester, 'chg-item-2').selected, isTrue);
    await _closeSelectionDialog(tester);

    await tester.tap(find.widgetWithText(FilledButton, 'Save'));
    await tester.pumpAndSettle();

    expect(repository.savedDrafts, hasLength(1));
    expect(repository.savedDrafts.single.chargeIds, [
      'chg-item-1',
      'chg-item-2',
    ]);
    // The wire payload carries the backend's contract key.
    expect(repository.savedDrafts.single.toJson()['applicableChargeIds'], [
      'chg-item-1',
      'chg-item-2',
    ]);
  });

  testWidgets('empty tax selection saves with no charges', (tester) async {
    final repository = _FakeCatalogueApi();
    await _pumpDialog(
      tester,
      repository: repository,
      chargeMaster: _FakeChargeMasterRepo(),
    );
    await _fillBasics(tester);

    await tester.tap(find.widgetWithText(FilledButton, 'Save'));
    await tester.pumpAndSettle();

    expect(repository.savedDrafts, hasLength(1));
    expect(repository.savedDrafts.single.chargeIds, isEmpty);
  });

  testWidgets('existing assignments load selected on edit and are preserved', (
    tester,
  ) async {
    final repository = _FakeCatalogueApi();
    await _pumpDialog(
      tester,
      repository: repository,
      chargeMaster: _FakeChargeMasterRepo(),
      product: _product(mappings: [_mapping('chg-item-1', 'GST-ITEM')]),
    );

    // The existing mapping renders as a selected option chip inside the
    // taxes dropdown.
    await _openTaxDropdown(tester);
    expect(_optionChip(tester, 'chg-item-1').selected, isTrue);
    await _closeSelectionDialog(tester);

    await tester.tap(find.widgetWithText(FilledButton, 'Save'));
    await tester.pumpAndSettle();

    // Saving without changes keeps the assignment (replace semantics need
    // the complete set, including kept mappings).
    expect(repository.savedDrafts.single.chargeIds, ['chg-item-1']);
  });

  testWidgets('mapped-but-inactive charge shows as a locked reference and '
      'survives save', (tester) async {
    final repository = _FakeCatalogueApi();
    await _pumpDialog(
      tester,
      repository: repository,
      chargeMaster: _FakeChargeMasterRepo(),
      product: _product(mappings: [_mapping('chg-dead', 'SC-DEAD')]),
    );

    // The inactive mapping is visible as a non-selectable reference chip —
    // not silently dropped and not offered as a new option.
    expect(find.textContaining('SC-DEAD (inactive)'), findsOneWidget);
    expect(
      find.byKey(const ValueKey('product-charge-option-chg-dead')),
      findsNothing,
    );

    await tester.tap(find.widgetWithText(FilledButton, 'Save'));
    await tester.pumpAndSettle();

    // The mapping is sent back so the server preserves the configuration.
    expect(repository.savedDrafts.single.chargeIds, ['chg-dead']);
  });

  testWidgets('deselecting an existing mapping removes it on save', (
    tester,
  ) async {
    final repository = _FakeCatalogueApi();
    await _pumpDialog(
      tester,
      repository: repository,
      chargeMaster: _FakeChargeMasterRepo(),
      product: _product(mappings: [_mapping('chg-item-1', 'GST-ITEM')]),
    );

    await _openTaxDropdown(tester);
    await tester.tap(
      find.byKey(const ValueKey('product-charge-option-chg-item-1')),
    );
    await tester.pump();
    expect(_optionChip(tester, 'chg-item-1').selected, isFalse);
    await _closeSelectionDialog(tester);

    await tester.tap(find.widgetWithText(FilledButton, 'Save'));
    await tester.pumpAndSettle();

    expect(repository.savedDrafts.single.chargeIds, isEmpty);
  });

  testWidgets('server validation error is displayed on rejected save', (
    tester,
  ) async {
    final repository = _FakeCatalogueApi();
    final container = await _pumpDialog(
      tester,
      repository: repository,
      chargeMaster: _FakeChargeMasterRepo(),
    );
    repository.saveError = const ApiException(
      422,
      'BUSINESS_RULE',
      'This charge is configured as Applicable on All and cannot be assigned '
          'to individual products.',
    );

    await _fillBasics(tester);
    await _openTaxDropdown(tester);
    await tester.tap(
      find.byKey(const ValueKey('product-charge-option-chg-item-1')),
    );
    await tester.pump();
    await _closeSelectionDialog(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'Save'));
    await tester.pumpAndSettle();

    // The dialog closes and the admin screen surfaces the server message.
    expect(
      find.textContaining('Applicable on All and cannot be assigned'),
      findsOneWidget,
    );
    expect(repository.savedDrafts, hasLength(1));
    expect(repository.savedDrafts.single.chargeIds, ['chg-item-1']);
    expect(container.read(catalogueRepositoryProvider), same(repository));
  });
}
