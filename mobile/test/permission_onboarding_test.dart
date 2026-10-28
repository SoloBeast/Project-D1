import 'dart:async';

import 'package:doodh_direct_mobile/core/device/device_metadata_service.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/customer/google_map_coordinate_picker.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_models.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_repository.dart';
import 'package:doodh_direct_mobile/features/notifications/permission_onboarding.dart';
import 'package:doodh_direct_mobile/features/notifications/push_notification_gateway.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_maps_flutter/google_maps_flutter.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('notification permission rationale', () {
    test('is not shown without authentication', () async {
      final container = await _container(
        session: null,
        gateway: _TestPushGateway(
          permission: PushPermissionStatus.notDetermined,
        ),
        store: _freshStore(),
      );
      addTearDown(container.dispose);

      final controller = container.read(
        notificationControllerProvider.notifier,
      );
      expect(await controller.shouldShowPermissionRationale(), isFalse);
    });

    test('is not shown when Firebase is unavailable', () async {
      final container = await _container(
        session: _session('customer-1', 'token-1'),
        gateway: _TestPushGateway(unavailable: true),
        store: _freshStore(),
      );
      addTearDown(container.dispose);

      final controller = container.read(
        notificationControllerProvider.notifier,
      );
      expect(
        container.read(notificationControllerProvider).permissionStatus,
        PushPermissionStatus.unavailable,
      );
      expect(await controller.shouldShowPermissionRationale(), isFalse);
    });

    test(
      'is shown once when undecided, then never again after engagement',
      () async {
        final store = _freshStore();
        final container = await _container(
          session: _session('customer-1', 'token-1'),
          gateway: _TestPushGateway(
            permission: PushPermissionStatus.notDetermined,
            requestedPermission: PushPermissionStatus.denied,
          ),
          store: store,
        );
        addTearDown(container.dispose);

        final controller = container.read(
          notificationControllerProvider.notifier,
        );
        expect(await controller.shouldShowPermissionRationale(), isTrue);

        // Engaging the request (even with denial) retires the prompt.
        expect(
          await controller.requestPushPermission(),
          PushPermissionStatus.denied,
        );
        expect(await controller.shouldShowPermissionRationale(), isFalse);
        expect(await store.hasShownRationale(), isTrue);
      },
    );

    test('explicit marking retires the prompt', () async {
      final store = _freshStore();
      final container = await _container(
        session: _session('customer-1', 'token-1'),
        gateway: _TestPushGateway(
          permission: PushPermissionStatus.notDetermined,
        ),
        store: store,
      );
      addTearDown(container.dispose);

      final controller = container.read(
        notificationControllerProvider.notifier,
      );
      expect(await controller.shouldShowPermissionRationale(), isTrue);
      await controller.markPermissionRationaleShown();
      expect(await controller.shouldShowPermissionRationale(), isFalse);
    });

    testWidgets('host presents the sheet once and honors Not now', (
      tester,
    ) async {
      final store = _freshStore();
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            authRepositoryProvider.overrideWithValue(
              _RestoringAuthRepository(_session('customer-1', 'token-1')),
            ),
            notificationRepositoryProvider.overrideWithValue(
              _TestNotificationRepository(),
            ),
            pushNotificationGatewayProvider.overrideWithValue(
              _TestPushGateway(
                permission: PushPermissionStatus.notDetermined,
                requestedPermission: PushPermissionStatus.denied,
              ),
            ),
            deviceMetadataServiceProvider.overrideWithValue(
              _FixedDeviceMetadata(),
            ),
            permissionOnboardingStoreProvider.overrideWithValue(store),
          ],
          child: const MaterialApp(
            home: Scaffold(body: PermissionOnboardingHost()),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Stay updated on your milk'), findsOneWidget);
      await tester.tap(find.text('Not now'));
      await tester.pumpAndSettle();

      expect(find.text('Stay updated on your milk'), findsNothing);
      expect(await store.hasShownRationale(), isTrue);

      // Ordinary rebuilds never re-prompt.
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            authRepositoryProvider.overrideWithValue(
              _RestoringAuthRepository(_session('customer-1', 'token-1')),
            ),
            notificationRepositoryProvider.overrideWithValue(
              _TestNotificationRepository(),
            ),
            pushNotificationGatewayProvider.overrideWithValue(
              _TestPushGateway(
                permission: PushPermissionStatus.notDetermined,
              ),
            ),
            deviceMetadataServiceProvider.overrideWithValue(
              _FixedDeviceMetadata(),
            ),
            permissionOnboardingStoreProvider.overrideWithValue(store),
          ],
          child: const MaterialApp(
            home: Scaffold(body: PermissionOnboardingHost()),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('Stay updated on your milk'), findsNothing);
      expect(tester.takeException(), isNull);
    });
  });

  group('location permission in the address picker', () {
    const channel = MethodChannel('flutter.baseflow.com/geolocator');

    void mockPermission(int status) {
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(channel, (call) async {
            if (call.method == 'checkPermission') return status;
            if (call.method == 'openAppSettings') return true;
            return null;
          });
      addTearDown(
        () => TestDefaultBinaryMessengerBinding
            .instance
            .defaultBinaryMessenger
            .setMockMethodCallHandler(channel, null),
      );
    }

    Future<void> pumpPicker(
      WidgetTester tester, {
      required Future<LatLng> Function() provider,
      required void Function(LatLng) onSelected,
    }) async {
      await tester.pumpWidget(
        MaterialApp(
          home: GoogleMapCoordinatePicker(
            onLocationSelected: onSelected,
            currentLocationProvider: provider,
            mapsLoader: () async {},
            mapBuilder: (_, _, _) => const SizedBox.expand(),
          ),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('Use my current location'));
      await tester.pumpAndSettle();
    }

    testWidgets('explains before the first prompt and proceeds on approval', (
      tester,
    ) async {
      mockPermission(0); // LocationPermission.denied
      const current = LatLng(28.6129, 77.2295);
      LatLng? selected;
      var providerCalls = 0;

      await pumpPicker(
        tester,
        provider: () async {
          providerCalls++;
          return current;
        },
        onSelected: (location) => selected = location,
      );

      expect(
        find.text('Use your current location?'),
        findsOneWidget,
      );
      expect(providerCalls, 0);
      await tester.tap(find.text('Use my location'));
      await tester.pumpAndSettle();

      expect(providerCalls, 1);
      expect(selected, current);
      expect(tester.takeException(), isNull);
    });

    testWidgets('manual choice skips the location request', (tester) async {
      mockPermission(0); // LocationPermission.denied
      var providerCalls = 0;
      LatLng? selected;

      await pumpPicker(
        tester,
        provider: () async {
          providerCalls++;
          return const LatLng(28.6, 77.4);
        },
        onSelected: (location) => selected = location,
      );

      await tester.tap(find.text('Choose manually'));
      await tester.pumpAndSettle();

      expect(providerCalls, 0);
      expect(selected, isNull);
      expect(find.text('Use my current location'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('permanent denial routes to settings, never to a request', (
      tester,
    ) async {
      mockPermission(1); // LocationPermission.deniedForever
      var providerCalls = 0;

      await pumpPicker(
        tester,
        provider: () async {
          providerCalls++;
          return const LatLng(28.6, 77.4);
        },
        onSelected: (_) {},
      );

      expect(find.text('Location is turned off'), findsOneWidget);
      expect(providerCalls, 0);
      await tester.tap(find.text('Open settings'));
      await tester.pumpAndSettle();

      expect(providerCalls, 0);
      expect(find.text('Location is turned off'), findsNothing);
      expect(find.text('Use my current location'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('granted permission proceeds without any sheet', (
      tester,
    ) async {
      mockPermission(2); // LocationPermission.whileInUse
      const current = LatLng(28.6129, 77.2295);
      LatLng? selected;
      var providerCalls = 0;

      await pumpPicker(
        tester,
        provider: () async {
          providerCalls++;
          return current;
        },
        onSelected: (location) => selected = location,
      );

      expect(find.text('Use your current location?'), findsNothing);
      expect(providerCalls, 1);
      expect(selected, current);
      expect(tester.takeException(), isNull);
    });
  });
}

AuthSession _session(String publicUserId, String accessToken) => AuthSession(
  user: AuthUser(
    publicUserId: publicUserId,
    displayName: 'Customer',
    email: 'customer@example.test',
    mobile: null,
    roles: const ['CUSTOMER'],
    permissions: const [],
    branchIds: const [],
  ),
  accessToken: accessToken,
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

PermissionOnboardingStore _freshStore() =>
    PermissionOnboardingStore(storage: _FakeSecureStorage());

Future<void> _drain() async {
  for (var index = 0; index < 12; index++) {
    await Future<void>.delayed(Duration.zero);
  }
}

Future<ProviderContainer> _container({
  required AuthSession? session,
  required _TestPushGateway gateway,
  required PermissionOnboardingStore store,
}) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(
        _RestoringAuthRepository(session),
      ),
      notificationRepositoryProvider.overrideWithValue(
        _TestNotificationRepository(),
      ),
      pushNotificationGatewayProvider.overrideWithValue(gateway),
      deviceMetadataServiceProvider.overrideWithValue(
        _FixedDeviceMetadata(),
      ),
      permissionOnboardingStoreProvider.overrideWithValue(store),
    ],
  );
  container.read(sessionControllerProvider);
  addTearDown(gateway.dispose);
  container.read(notificationControllerProvider);
  await _drain();
  return container;
}

class _RestoringAuthRepository extends AuthRepository {
  _RestoringAuthRepository(this._session);

  final AuthSession? _session;

  @override
  Future<AuthSession?> restore() async => _session;
}

class _FixedDeviceMetadata extends DeviceMetadataService {
  @override
  Future<DeviceMetadata> get() async => const DeviceMetadata(
    deviceIdentifier: 'device-1',
    deviceName: 'Test device',
    platform: 'android',
  );
}

class _TestPushGateway implements PushNotificationGateway {
  _TestPushGateway({
    this.permission = PushPermissionStatus.denied,
    this.requestedPermission = PushPermissionStatus.denied,
    this.unavailable = false,
  });

  final PushPermissionStatus permission;
  final PushPermissionStatus requestedPermission;
  final bool unavailable;
  final _tokens = StreamController<String>.broadcast();
  final _links = StreamController<String>.broadcast();
  final _foreground = StreamController<void>.broadcast();

  @override
  Stream<String> get tokenRefreshes => _tokens.stream;

  @override
  Stream<String> get openedDeepLinks => _links.stream;

  @override
  Stream<void> get foregroundMessages => _foreground.stream;

  @override
  Future<bool> initialize() async => !unavailable;

  @override
  Future<String?> initialDeepLink() async => null;

  @override
  Future<PushPermissionStatus> permissionStatus() async =>
      unavailable ? PushPermissionStatus.unavailable : permission;

  @override
  Future<PushPermissionStatus> requestPermission() async =>
      unavailable ? PushPermissionStatus.unavailable : requestedPermission;

  @override
  Future<String?> token() async => null;

  Future<void> dispose() async {
    await _tokens.close();
    await _links.close();
    await _foreground.close();
  }
}

class _TestNotificationRepository extends NotificationRepository {
  _TestNotificationRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<NotificationPage> getNotifications(
    String token, {
    int page = 1,
    int pageSize = 20,
    bool? isRead,
  }) async => NotificationPage(
    items: const [],
    page: page,
    pageSize: pageSize,
    totalCount: 0,
  );

  @override
  Future<int> getUnreadCount(String token) async => 0;
}

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
  }) async => values[key];
}
