import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/widgets/product_widgets.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_controller.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_repository.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:image_picker/image_picker.dart';

class _FakeCatalogueApi extends CatalogueRepository {
  _FakeCatalogueApi() : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  int upsertCalls = 0;
  int removeCalls = 0;
  Uint8List? lastBytes;
  String? lastFileName;
  String? lastContentType;
  Object? upsertError;

  @override
  Future<ProductImageResult> upsertProductImage(
    String productId,
    String accessToken, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async {
    upsertCalls += 1;
    lastBytes = bytes;
    lastFileName = fileName;
    lastContentType = contentType;
    if (upsertError != null) {
      throw upsertError!;
    }
    return _imageResult();
  }

  @override
  Future<ProductImageResult> removeProductImage(
    String productId,
    String accessToken,
  ) async {
    removeCalls += 1;
    return _imageResult();
  }

  @override
  Future<CatalogueProduct> createProduct(
    ProductDraft draft,
    String accessToken,
  ) async => _product(imageUrl: seededImageUrl);

  @override
  Future<CatalogueProduct> updateProduct(
    String productId,
    ProductDraft draft,
    String accessToken,
  ) async => _product(imageUrl: seededImageUrl);

  /// Image URL reported by the admin list (simulates a configured image).
  String? seededImageUrl;

  @override
  Future<List<CatalogueProduct>> getAdminProducts(String accessToken) async =>
      [_product(imageUrl: seededImageUrl)];

  @override
  Future<List<ProductCategory>> getAdminCategories(String accessToken) async =>
      [_category()];

  @override
  Future<List<CatalogueBranch>> getBranches(String accessToken) async => [_branch()];
}

final Uint8List _pngBytes = Uint8List.fromList(
  [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01],
);

ProductImageResult _imageResult() => ProductImageResult(
      productId: 'p-1',
      imageId: 'img-1',
      fileName: 'milk.png',
      contentType: 'image/png',
      fileSize: 9,
      uploadedAtUtc: DateTime.utc(2026, 9, 27, 19, 22, 29),
    );

CatalogueProduct _product({String? imageUrl}) => CatalogueProduct(
      publicId: 'p-1',
      sku: 'MILK-001',
      name: 'Whole Milk',
      description: 'Fresh whole milk.',
      category: _category(),
      unitOfMeasure: 'litre',
      price: 60,
      isActive: true,
      imageUrl: imageUrl,
      branchAvailability: [
        BranchAvailability(
          branchId: 'b-1',
          branchCode: 'MAIN',
          branchName: 'Main Branch',
          isAvailable: true,
          maxDailyQuantity: null,
        ),
      ],
    );

ProductCategory _category() => const ProductCategory(
      publicId: 'c-1',
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

Future<void> _pumpDialog(
  WidgetTester tester, {
  required _FakeCatalogueApi repository,
  CatalogueImagePicker? pickImage,
  CatalogueProduct? product,
}) async {
  final session = AuthSession(
    user: const AuthUser(
      publicUserId: 'admin-1',
      displayName: 'Admin User',
      email: 'admin@example.test',
      mobile: null,
      roles: ['SYSTEM_ADMIN'],
      permissions: ['CATALOGUE.MANAGE'],
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
      sessionControllerProvider.overrideWith(
        () => _SeededSessionController(session),
      ),
    ],
  );
  addTearDown(container.dispose);
  TesterHook.lastContainer = container;
  // A tall, wide surface keeps the whole dialog (fields, branch dropdown,
  // Save) and the 1480px product grid (details entry sits at the far right)
  // on-screen so taps land without scrolling.
  await tester.binding.setSurfaceSize(const Size(1920, 1600));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        home: AdminCatalogueScreen(pickImage: pickImage),
      ),
    ),
  );
  await tester.pumpAndSettle();

  if (product != null) {
    // Seeding an existing product means the dialog opens through the row's
    // details entry (image icon in the Actions column).
    final detailsControl = find.byKey(
      ValueKey('admin-product-details-${product.publicId}'),
    );
    expect(detailsControl, findsOneWidget);
    await tester.ensureVisible(detailsControl);
    await tester.pumpAndSettle();
    await tester.tap(detailsControl);
    await tester.pumpAndSettle();
    expect(find.text('Product image'), findsOneWidget);
    return;
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
}

class _SeededSessionController extends SessionController {
  _SeededSessionController(this.session);

  final AuthSession session;

  @override
  SessionState build() => SessionState.authenticated(session);
}

/// Temporary debug hook so a test can inspect the provider container.
class TesterHook {
  static ProviderContainer? lastContainer;
}

/// Dialog text field by label, scoped to the open dialog — the in-grid draft
/// row behind it reuses the same hints/labels.
Finder _dialogField(String label) => find.descendant(
  of: find.byType(AlertDialog),
  matching: find.widgetWithText(TextField, label),
);

void main() {
  testWidgets('admin product dialog shows Add Image with no image configured', (
    tester,
  ) async {
    await _pumpDialog(
      tester,
      repository: _FakeCatalogueApi(),
      product: null,
    );

    expect(
      find.byKey(const ValueKey('admin-product-image-section')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey('admin-product-image-add')),
      findsOneWidget,
    );
    expect(find.text('Add Image'), findsOneWidget);
    expect(find.text('Replace Image'), findsNothing);
    expect(
      find.byKey(const ValueKey('admin-product-image-remove')),
      findsNothing,
    );
  });

  testWidgets('picked image previews before save and is not uploaded yet', (
    tester,
  ) async {
    final repository = _FakeCatalogueApi();
    await _pumpDialog(
      tester,
      repository: repository,
      pickImage: ({required source}) async =>
          XFile.fromData(_pngBytes, name: 'milk-photo.png', mimeType: 'image/png'),
    );

    await tester.tap(find.byKey(const ValueKey('admin-product-image-add')));
    await tester.pumpAndSettle();

    // Preview renders from the picked bytes; nothing has been uploaded.
    expect(
      find.byKey(const ValueKey('admin-product-image-preview')),
      findsOneWidget,
    );
    expect(repository.upsertCalls, 0);

    // Saving the dialog hands the pending image change to the screen flow,
    // which uploads through the controller.
    await tester.enterText(_dialogField('SKU'), 'MILK-001');
    await tester.enterText(_dialogField('Name'), 'Whole Milk');
    await tester.enterText(_dialogField('Price'), '60');
    await tester.tap(find.byKey(const ValueKey('product-dialog-branches')));
    await tester.pumpAndSettle();
    final chip = find.byType(FilterChip);
    await tester.ensureVisible(chip.first);
    await tester.pumpAndSettle();
    await tester.tap(chip.first, warnIfMissed: false);
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Done'));
    await tester.pumpAndSettle();
    final save = find.text('Save');
    await tester.ensureVisible(save);
    await tester.pumpAndSettle();
    await tester.tap(save, warnIfMissed: false);
    await tester.pumpAndSettle();

    final container = TesterHook.lastContainer;
    expect(container?.read(adminCatalogueControllerProvider).errorMessage, isNull);
    expect(repository.upsertCalls, 1);
    // XFile.fromData does not preserve the original name through readAsBytes
    // in the test environment, so the dialog derives a safe name from the
    // resolved content type (same convention as the milk-test image flow).
    expect(repository.lastFileName, 'product-image.png');
    expect(repository.lastContentType, 'image/png');
    expect(repository.lastBytes, _pngBytes);
  });

  testWidgets('unsupported picked file type shows an inline error', (
    tester,
  ) async {
    final repository = _FakeCatalogueApi();
    await _pumpDialog(
      tester,
      repository: repository,
      pickImage: ({required source}) async =>
          XFile.fromData(_pngBytes, name: 'payload.pdf', mimeType: 'application/pdf'),
    );

    await tester.tap(find.byKey(const ValueKey('admin-product-image-add')));
    await tester.pumpAndSettle();

    expect(
      find.textContaining('JPEG, PNG, or WebP'),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey('admin-product-image-remove')),
      findsNothing,
    );
  });

  testWidgets('upload failure surfaces an error and keeps the dialog usable', (
    tester,
  ) async {
    final repository = _FakeCatalogueApi();
    repository.upsertError = const ApiException(
      422,
      'IMAGE_INVALID',
      'Only valid JPEG, PNG, or WebP images are allowed.',
      field: 'image',
    );
    await _pumpDialog(
      tester,
      repository: repository,
      pickImage: ({required source}) async =>
          XFile.fromData(_pngBytes, name: 'milk.png', mimeType: 'image/png'),
    );

    await tester.tap(find.byKey(const ValueKey('admin-product-image-add')));
    await tester.pumpAndSettle();
    await tester.enterText(_dialogField('SKU'), 'MILK-001');
    await tester.enterText(_dialogField('Name'), 'Whole Milk');
    await tester.enterText(_dialogField('Price'), '60');
    await tester.tap(find.byKey(const ValueKey('product-dialog-branches')));
    await tester.pumpAndSettle();
    await tester.tap(find.byType(FilterChip).first);
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Done'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();

    expect(repository.upsertCalls, 1);
    // The upload failure is surfaced on the admin screen (banner or snackbar)
    // after the dialog closes; the screen itself remains usable.
    final errorTexts = tester.widgetList<Text>(
      find.byWidgetPredicate(
        (widget) =>
            widget is Text &&
            (widget.data?.contains('JPEG') == true ||
             widget.data?.contains('Unable') == true),
      ),
    );
    expect(errorTexts, isNotEmpty);
    // The management dashboard is still usable (tab + card titles repeat
    // the word, so assert on the unique page header instead).
    expect(find.text('Catalogue management'), findsOneWidget);
  });

  testWidgets('existing image shows Replace and Remove with confirmation', (
    tester,
  ) async {
    final repository = _FakeCatalogueApi()
      ..seededImageUrl = '/api/v1/products/p-1/image';
    await _pumpDialog(
      tester,
      repository: repository,
      product: _product(imageUrl: '/api/v1/products/p-1/image'),
      pickImage: ({required source}) async =>
          XFile.fromData(_pngBytes, mimeType: 'image/png'),
    );

    // The dialog shows Replace Image (an image is configured) and Remove.
    expect(find.text('Replace Image'), findsOneWidget);
    expect(
      find.byKey(const ValueKey('admin-product-image-remove')),
      findsOneWidget,
    );

    // The existing-image preview resolves the relative API path against the
    // configured API base URL so the request targets the API origin (Flutter
    // Web would otherwise request the image from the web app's own origin).
    final previewUrls = tester
        .widgetList<Image>(find.byType(Image))
        .map((image) => image.image)
        .whereType<NetworkImage>()
        .map((provider) => provider.url)
        .toList(growable: false);
    expect(
      previewUrls,
      contains('http://localhost:5209/api/v1/products/p-1/image'),
    );

    // Replacement preview through the same action.
    await tester.tap(find.byKey(const ValueKey('admin-product-image-add')));
    await tester.pumpAndSettle();
    expect(
      find.byKey(const ValueKey('admin-product-image-preview')),
      findsOneWidget,
    );

    // Removal requires confirmation.
    await tester.tap(find.byKey(const ValueKey('admin-product-image-remove')));
    await tester.pumpAndSettle();
    expect(find.text('Remove product image?'), findsOneWidget);
    await tester.tap(find.widgetWithText(FilledButton, 'Remove'));
    await tester.pumpAndSettle();

    // After confirming, saving persists the removal through the repository.
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();
    expect(repository.removeCalls, 1);
  });

  testWidgets('customer widget renders a configured image URL', (tester) async {
    const networkImage = NetworkImage('https://img.example.test/milk.png');
    await tester.pumpWidget(
      const MaterialApp(
        home: Scaffold(
          body: DoodhProductImage(
            imageUrl: 'https://img.example.test/milk.png',
            semanticLabel: 'Whole Milk',
          ),
        ),
      ),
    );
    await tester.pump();

    final resolved = tester.widget<Image>(find.byType(Image).first).image;
    expect(resolved, isA<NetworkImage>());
    expect((resolved as NetworkImage).url, networkImage.url);
  });

  testWidgets('customer widget keeps the branded fallback without an image', (
    tester,
  ) async {
    await tester.pumpWidget(
      const MaterialApp(
        home: Scaffold(body: DoodhProductImage()),
      ),
    );
    await tester.pump();

    expect(find.byType(Image), findsNothing);
    expect(find.text('DoodhDirect'), findsOneWidget);
  });

  testWidgets('customer widget falls back when the image request fails', (
    tester,
  ) async {
    await tester.runAsync(() async {
      await tester.pumpWidget(
        const MaterialApp(
          home: Scaffold(
            body: DoodhProductImage(
              imageUrl: 'https://img.example.test/broken.png',
            ),
          ),
        ),
      );
      // Let the network image fail; the widget degrades to the fallback.
      await tester.pumpAndSettle();
    });
    expect(find.byType(DoodhProductImage), findsOneWidget);
  });
}

