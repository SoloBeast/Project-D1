import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/branding/branding_cache.dart';
import 'package:doodh_direct_mobile/features/branding/branding_controller.dart';
import 'package:doodh_direct_mobile/features/branding/branding_models.dart';
import 'package:doodh_direct_mobile/features/branding/branding_repository.dart';
import 'package:doodh_direct_mobile/features/branding/branding_splash.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';

BrandingConfiguration _configuration({
  BrandingAssetInfo? logo,
  BrandingAssetInfo? animation,
}) =>
    BrandingConfiguration(logo: logo, startupAnimation: animation);

BrandingAssetInfo _asset({
  BrandingAssetKind kind = BrandingAssetKind.logo,
  String url = '/api/v1/branding/logo',
}) =>
    BrandingAssetInfo(
      kind: kind,
      fileName: 'asset.bin',
      contentType:
          kind == BrandingAssetKind.startupAnimation ? 'image/gif' : 'image/png',
      fileSize: 4,
      updatedAt: DateTime(2026, 9, 28, 10),
      url: url,
    );

class _FakeSecureStorage extends FlutterSecureStorage {
  final Map<String, String> values = {};

  @override
  Future<void> write({
    required String key,
    required String? value,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    if (value == null) {
      values.remove(key);
    } else {
      values[key] = value;
    }
  }

  @override
  Future<String?> read({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async =>
      values[key];

  @override
  Future<void> delete({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    values.remove(key);
  }
}

class _FakeBrandingRepository implements BrandingRepository {
  _FakeBrandingRepository({
    this.publicResult,
    this.publicError,
    Map<String, Uint8List>? assets,
  }) : assets = assets ?? {};

  BrandingConfiguration? publicResult;
  Object? publicError;
  final Map<String, Uint8List> assets;
  int publicCalls = 0;

  @override
  Future<BrandingConfiguration> getPublic() async {
    publicCalls += 1;
    final error = publicError;
    if (error != null) throw error;
    return publicResult ?? _configuration();
  }

  @override
  Future<ApiByteResponse> getAssetBytes(String relativeUrl) async {
    final bytes = assets[relativeUrl];
    if (bytes == null) {
      throw ApiException(404, 'NOT_FOUND', 'The asset was not found.');
    }
    return ApiByteResponse(
      bytes: bytes,
      contentType: 'image/png',
      fileName: 'asset.bin',
    );
  }

  @override
  dynamic noSuchMethod(Invocation invocation) =>
      super.noSuchMethod(invocation);
}

Future<ProviderContainer> _pumpContainer(
  WidgetTester tester, {
  required BrandingRepository repository,
  _FakeSecureStorage? storage,
  Duration fetchTimeout = const Duration(milliseconds: 50),
}) async {
  final container = ProviderContainer(
    overrides: [
      brandingRepositoryProvider.overrideWithValue(repository),
      brandingCacheProvider.overrideWithValue(
        BrandingCache(storage: storage ?? _FakeSecureStorage()),
      ),
      brandingFetchTimeoutProvider.overrideWithValue(fetchTimeout),
    ],
  );
  addTearDown(container.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        theme: ThemeData(useMaterial3: true),
        home: const BrandingSplashScreen(),
      ),
    ),
  );
  await tester.pump();
  return container;
}

void main() {
  test('BrandingConfiguration json round-trips', () {
    final configuration = _configuration(
      logo: _asset(),
      animation: _asset(
        kind: BrandingAssetKind.startupAnimation,
        url: '/api/v1/branding/startup-animation',
      ),
    );

    final decoded = BrandingConfiguration.fromJson(
      jsonDecode(jsonEncode(configuration.toJson())) as Map<String, dynamic>,
    );

    expect(decoded.logo?.url, '/api/v1/branding/logo');
    expect(decoded.startupAnimation?.contentType, 'image/gif');
    expect(decoded.startupAnimation?.isAnimation, isTrue);
    expect(decoded.logo?.isAnimation, isFalse);
  });

  testWidgets('startup with no backend and no cache renders the bundled fallback',
      (tester) async {
    final repository = _FakeBrandingRepository(
      publicError: const ApiNetworkException('offline'),
    );
    final container = await _pumpContainer(tester, repository: repository);
    // The splash always shows a progress indicator (it never "settles"),
    // so advance the pump clock explicitly instead of pumpAndSettle.
    await tester.pump(const Duration(milliseconds: 200));
    await tester.pump(const Duration(milliseconds: 200));

    final state = container.read(brandingControllerProvider);
    expect(state.isReady, isTrue);
    expect(state.source, BrandingSource.fallback);
    expect(state.configuration, isNull);
    expect(find.byIcon(Icons.water_drop_rounded), findsOneWidget);
    expect(find.text('DoodhDirect'), findsOneWidget);
  });

  testWidgets('remote branding renders the animation and logo bytes',
      (tester) async {
    final repository = _FakeBrandingRepository(
      publicResult: _configuration(
        logo: _asset(),
        animation: _asset(
          kind: BrandingAssetKind.startupAnimation,
          url: '/api/v1/branding/startup-animation',
        ),
      ),
    )
      ..assets['/api/v1/branding/logo'] = Uint8List.fromList([1, 2, 3])
      ..assets['/api/v1/branding/startup-animation'] =
          Uint8List.fromList([4, 5, 6]);
    final storage = _FakeSecureStorage();

    final container = await _pumpContainer(
      tester,
      repository: repository,
      storage: storage,
    );
    // The splash always shows a progress indicator (it never "settles"),
    // so advance the pump clock explicitly instead of pumpAndSettle.
    await tester.pump(const Duration(milliseconds: 200));
    await tester.pump(const Duration(milliseconds: 200));

    final state = container.read(brandingControllerProvider);
    expect(state.isReady, isTrue);
    expect(state.source, BrandingSource.remote);
    expect(state.logoBytes, Uint8List.fromList([1, 2, 3]));
    expect(state.animationBytes, Uint8List.fromList([4, 5, 6]));
    // Last-known-good is persisted for offline launches.
    expect(storage.values.containsKey('branding.logo.bytes.v1'), isTrue);
    expect(
      storage.values.containsKey('branding.animation.bytes.v1'),
      isTrue,
    );
    expect(storage.values.containsKey('branding.config.v1'), isTrue);
  });

  testWidgets('cached branding is painted immediately and revalidated offline',
      (tester) async {
    final repository = _FakeBrandingRepository(
      publicError: const ApiNetworkException('offline'),
    );
    final storage = _FakeSecureStorage();
    storage.values['branding.config.v1'] = jsonEncode({
      'version': 1,
      'data': _configuration(logo: _asset()).toJson(),
    });
    storage.values['branding.logo.bytes.v1'] =
        base64Encode(Uint8List.fromList([9, 9]));

    final container = await _pumpContainer(
      tester,
      repository: repository,
      storage: storage,
    );
    // The splash always shows a progress indicator (it never "settles"),
    // so advance the pump clock explicitly instead of pumpAndSettle.
    await tester.pump(const Duration(milliseconds: 200));
    await tester.pump(const Duration(milliseconds: 200));

    final state = container.read(brandingControllerProvider);
    expect(state.isReady, isTrue);
    expect(state.source, BrandingSource.fallback);
    expect(state.logoBytes, Uint8List.fromList([9, 9]));
    expect(state.configuration?.logo?.url, '/api/v1/branding/logo');
  });

  testWidgets('a 404 branding response clears stale cached bytes',
      (tester) async {
    final repository = _FakeBrandingRepository(
      publicError: const ApiException(404, 'NOT_FOUND', 'not configured'),
    );
    final storage = _FakeSecureStorage();
    storage.values['branding.config.v1'] = jsonEncode({
      'version': 1,
      'data': _configuration(logo: _asset()).toJson(),
    });
    storage.values['branding.logo.bytes.v1'] =
        base64Encode(Uint8List.fromList([7]));

    final container = await _pumpContainer(
      tester,
      repository: repository,
      storage: storage,
    );
    // The splash always shows a progress indicator (it never "settles"),
    // so advance the pump clock explicitly instead of pumpAndSettle.
    await tester.pump(const Duration(milliseconds: 200));
    await tester.pump(const Duration(milliseconds: 200));

    final state = container.read(brandingControllerProvider);
    expect(state.isReady, isTrue);
    expect(state.source, BrandingSource.fallback);
    // Server says the asset no longer exists — stale bytes must not linger.
    expect(state.configuration, isNull);
    expect(state.logoBytes, isNull);
    expect(storage.values.containsKey('branding.logo.bytes.v1'), isFalse);
  });

  testWidgets('a hanging branding request cannot stall startup past the timeout',
      (tester) async {
    final repository = _HangingRepository();
    final container = await _pumpContainer(tester, repository: repository);
    await tester.pump(const Duration(milliseconds: 100));
    // The splash always shows a progress indicator (it never "settles"),
    // so advance the pump clock explicitly instead of pumpAndSettle.
    await tester.pump(const Duration(milliseconds: 200));
    await tester.pump(const Duration(milliseconds: 200));

    final state = container.read(brandingControllerProvider);
    expect(state.isReady, isTrue);
    expect(state.source, BrandingSource.fallback);
  });

  testWidgets('a failed asset download degrades to the static fallback',
      (tester) async {
    final repository = _FakeBrandingRepository(
      publicResult: _configuration(logo: _asset()),
    );
    // No bytes registered → getAssetBytes answers 404.
    final container = await _pumpContainer(tester, repository: repository);
    // The splash always shows a progress indicator (it never "settles"),
    // so advance the pump clock explicitly instead of pumpAndSettle.
    await tester.pump(const Duration(milliseconds: 200));
    await tester.pump(const Duration(milliseconds: 200));

    final state = container.read(brandingControllerProvider);
    expect(state.source, BrandingSource.remote);
    expect(state.logoBytes, isNull);
    expect(find.byIcon(Icons.water_drop_rounded), findsOneWidget);
  });
}

class _HangingRepository implements BrandingRepository {
  @override
  Future<BrandingConfiguration> getPublic() =>
      Completer<BrandingConfiguration>().future;

  @override
  dynamic noSuchMethod(Invocation invocation) =>
      super.noSuchMethod(invocation);
}
