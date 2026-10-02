import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/branding/admin_branding_controller.dart';
import 'package:doodh_direct_mobile/features/branding/admin_branding_screen.dart';
import 'package:doodh_direct_mobile/features/branding/branding_controller.dart';
import 'package:doodh_direct_mobile/features/branding/branding_models.dart';
import 'package:doodh_direct_mobile/features/branding/branding_repository.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

BrandingConfiguration _configuration({
  BrandingAssetInfo? logo,
  BrandingAssetInfo? animation,
}) =>
    BrandingConfiguration(logo: logo, startupAnimation: animation);

BrandingAssetInfo _asset({
  BrandingAssetKind kind = BrandingAssetKind.logo,
}) =>
    BrandingAssetInfo(
      kind: kind,
      fileName: kind == BrandingAssetKind.startupAnimation
          ? 'intro.gif'
          : 'milk.png',
      contentType:
          kind == BrandingAssetKind.startupAnimation ? 'image/gif' : 'image/png',
      fileSize: 4,
      updatedAt: DateTime(2026, 9, 28, 10, 30),
      url: kind == BrandingAssetKind.startupAnimation
          ? '/api/v1/branding/startup-animation'
          : '/api/v1/branding/logo',
    );

class _FakeAdminBrandingRepository implements BrandingRepository {
  _FakeAdminBrandingRepository({
    this.adminResult,
    this.adminError,
    this.uploadError,
  });

  BrandingConfiguration? adminResult;
  Object? adminError;
  BrandingConfiguration? lastUploadResult;
  Object? uploadError;
  int uploadLogoCalls = 0;
  int uploadAnimationCalls = 0;
  int removeLogoCalls = 0;
  int removeAnimationCalls = 0;
  String? lastUploadContentType;

  @override
  Future<BrandingConfiguration> getForAdministration(String accessToken) async {
    final error = adminError;
    if (error != null) throw error;
    return adminResult ?? _configuration();
  }

  @override
  Future<BrandingConfiguration> uploadLogo(
    String accessToken, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async {
    uploadLogoCalls += 1;
    lastUploadContentType = contentType;
    final error = uploadError;
    if (error != null) throw error;
    return lastUploadResult ??
        _configuration(logo: _asset(), animation: _asset(
          kind: BrandingAssetKind.startupAnimation,
        ));
  }

  @override
  Future<BrandingConfiguration> uploadStartupAnimation(
    String accessToken, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async {
    uploadAnimationCalls += 1;
    lastUploadContentType = contentType;
    final error = uploadError;
    if (error != null) throw error;
    return lastUploadResult ?? _configuration(
      logo: _asset(),
      animation: _asset(kind: BrandingAssetKind.startupAnimation),
    );
  }

  @override
  Future<BrandingConfiguration> removeLogo(String accessToken) async {
    removeLogoCalls += 1;
    return _configuration(
      animation: _asset(kind: BrandingAssetKind.startupAnimation),
    );
  }

  @override
  Future<BrandingConfiguration> removeStartupAnimation(
    String accessToken,
  ) async {
    removeAnimationCalls += 1;
    return _configuration(logo: _asset());
  }

  @override
  Future<BrandingConfiguration> getPublic() async => _configuration();

  @override
  Future<ApiByteResponse> getAssetBytes(String relativeUrl) =>
      throw UnimplementedError('not needed for admin screen tests');
}

class _SeededSessionController extends SessionController {
  _SeededSessionController(this.permissions);

  final List<String> permissions;

  @override
  SessionState build() => SessionState.authenticated(
    AuthSession(
      user: AuthUser(
        publicUserId: 'branding-admin-1',
        displayName: 'Branding Admin',
        email: null,
        mobile: '9999999999',
        roles: const ['OWNER'],
        permissions: permissions,
        branchIds: const [],
      ),
      accessToken: 'branding-token',
      refreshToken: 'refresh-token',
      accessTokenExpiresAtUtc: DateTime.utc(2099),
      refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
    ),
  );
}

Future<ProviderContainer> _pumpScreen(
  WidgetTester tester, {
  required _FakeAdminBrandingRepository repository,
  List<String> permissions = const [
    'SETUP.BRANDING.READ',
    'SETUP.BRANDING.MANAGE',
  ],
  Future<Uint8List?> Function({required String acceptHint})? pickFile,
}) async {
  final container = ProviderContainer(
    overrides: [
      sessionControllerProvider.overrideWith(
        () => _SeededSessionController(permissions),
      ),
      adminBrandingRepositoryProvider.overrideWithValue(repository),
      brandingRepositoryProvider.overrideWithValue(repository),
      brandingFetchTimeoutProvider.overrideWithValue(
        const Duration(milliseconds: 50),
      ),
    ],
  );
  addTearDown(container.dispose);
  // A tall surface keeps both asset cards (and the read-only notice) inside
  // the viewport so taps and text finders reach the second card.
  await tester.binding.setSurfaceSize(const Size(800, 1600));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        theme: ThemeData(useMaterial3: true),
        home: AdminBrandingScreen(
          pickFile: pickFile ??
              ({required String acceptHint}) async => null,
        ),
      ),
    ),
  );
  // One pump for initState microtask, one for the load completion, one more
  // for the post-frame snackbar callback.
  await tester.pump();
  await tester.pump();
  await tester.pump();
  return container;
}

void main() {
  testWidgets('admin screen shows the active assets with management actions',
      (tester) async {
    final repository = _FakeAdminBrandingRepository(
      adminResult: _configuration(
        logo: _asset(),
        animation: _asset(kind: BrandingAssetKind.startupAnimation),
      ),
    );

    await _pumpScreen(tester, repository: repository);

    expect(find.text('Logo'), findsOneWidget);
    expect(find.text('Startup animation'), findsOneWidget);
    expect(find.text('Active'), findsNWidgets(2));
    expect(find.text('Replace'), findsNWidgets(2));
    expect(find.text('Remove'), findsNWidgets(2));
  });

  testWidgets('unset assets show upload actions instead of remove',
      (tester) async {
    await _pumpScreen(
      tester,
      repository: _FakeAdminBrandingRepository(
        adminResult: _configuration(),
      ),
    );

    expect(find.text('Upload'), findsNWidgets(2));
    expect(find.text('Remove'), findsNothing);
    expect(
      find.text('Not configured — the app shows its bundled branding.'),
      findsNWidgets(2),
    );
  });

  testWidgets('uploading a PNG logo calls the repository and confirms',
      (tester) async {
    final repository = _FakeAdminBrandingRepository(
      adminResult: _configuration(),
    );
    final pngBytes = Uint8List.fromList(
      [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01],
    );

    await _pumpScreen(
      tester,
      repository: repository,
      pickFile: ({required String acceptHint}) async => pngBytes,
    );

    await tester.ensureVisible(find.widgetWithText(FilledButton, 'Upload').first);
    await tester.tap(find.widgetWithText(FilledButton, 'Upload').first);
    await tester.pump();
    await tester.pump();
    await tester.pump();

    expect(repository.uploadLogoCalls, 1);
    expect(repository.lastUploadContentType, 'image/png');
    expect(find.text('Logo updated.'), findsOneWidget);
  });

  testWidgets('a non-GIF animation pick is rejected locally with a message',
      (tester) async {
    final repository = _FakeAdminBrandingRepository(
      adminResult: _configuration(),
    );
    final pngBytes = Uint8List.fromList(
      [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01],
    );

    await _pumpScreen(
      tester,
      repository: repository,
      pickFile: ({required String acceptHint}) async => pngBytes,
    );

    await tester.ensureVisible(
      find.widgetWithText(FilledButton, 'Upload').last,
    );
    await tester.tap(find.widgetWithText(FilledButton, 'Upload').last);
    await tester.pump();
    await tester.pump();

    expect(repository.uploadAnimationCalls, 0);
    expect(
      find.text('The startup animation must be an animated GIF.'),
      findsOneWidget,
    );
  });

  testWidgets('read-only viewers get no management actions', (tester) async {
    await _pumpScreen(
      tester,
      repository: _FakeAdminBrandingRepository(
        adminResult: _configuration(logo: _asset()),
      ),
      permissions: const ['SETUP.BRANDING.READ'],
    );

    expect(find.text('Replace'), findsNothing);
    expect(find.text('Remove'), findsNothing);
    expect(find.text('Active'), findsOneWidget);
    expect(
      find.textContaining('Manage application branding permission'),
      findsOneWidget,
    );
  });

  testWidgets('a failed load surfaces the error panel with retry',
      (tester) async {
    await _pumpScreen(
      tester,
      repository: _FakeAdminBrandingRepository(
        adminError: const ApiException(403, 'FORBIDDEN', 'Not allowed.'),
      ),
    );

    expect(find.byType(ErrorStatePanel), findsOneWidget);
    expect(find.text('Not allowed.'), findsOneWidget);
    expect(find.text('Retry'), findsOneWidget);
  });

  testWidgets('a failed upload surfaces the backend message', (tester) async {
    final repository = _FakeAdminBrandingRepository(
      adminResult: _configuration(),
      uploadError: const ApiException(
        400,
        'VALIDATION_ERROR',
        'The startup animation must be declared as image/gif.',
      ),
    );
    final gifBytes = Uint8List.fromList(
      [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01],
    );

    await _pumpScreen(
      tester,
      repository: repository,
      pickFile: ({required String acceptHint}) async => gifBytes,
    );

    // Upload the animation (last Upload button) — the repository rejects it.
    await tester.ensureVisible(
      find.widgetWithText(FilledButton, 'Upload').last,
    );
    await tester.tap(find.widgetWithText(FilledButton, 'Upload').last);
    await tester.pump();
    await tester.pump();
    await tester.pump();

    expect(
      find.text('The startup animation must be declared as image/gif.'),
      findsOneWidget,
    );
  });
}
