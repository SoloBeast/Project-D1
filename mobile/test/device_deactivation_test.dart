import 'dart:async';
import 'dart:convert';

import 'package:doodh_direct_mobile/core/device/device_metadata_service.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_models.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_repository.dart';
import 'package:doodh_direct_mobile/features/notifications/push_notification_gateway.dart';
import 'package:doodh_direct_mobile/features/orders/guest_cart_storage.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('device deactivation on sign-out', () {
    test(
      'logout deactivates the installation before logging out and clears the session',
      () async {
        final requests = <http.Request>[];
        final storage = _FakeSecureStorage();
        storage.values[DeviceMetadataService.deviceIdentifierStorageKey] =
            'install-1';
        storage.values['identity.session.v1'] = jsonEncode({
          'seeded': true,
        });
        final repository = AuthRepository(
          api: ApiClient(
            client: MockClient((request) async {
              requests.add(request);
              return http.Response(
                '{"success":true,"data":{},"errors":[]}',
                200,
                headers: const {'content-type': 'application/json'},
              );
            }),
            baseUrl: 'https://api.example.test',
          ),
          storage: storage,
          deviceMetadata: DeviceMetadataService(storage: storage),
        );

        await repository.logout(_session('customer-1', 'token-a'));

        expect(requests, hasLength(2));
        expect(requests[0].method, 'POST');
        expect(
          requests[0].url.path,
          '/api/v1/devices/install-1/deactivate',
        );
        expect(
          requests[0].headers['authorization'],
          contains('token-a'),
        );
        expect(requests[1].url.path, '/api/v1/auth/logout');
        expect(
          requests[1].headers['authorization'],
          contains('token-a'),
        );
        expect(storage.values.containsKey('identity.session.v1'), isFalse);
      },
    );

    test(
      'deactivation failure does not prevent logout or local session clear',
      () async {
        final requests = <http.Request>[];
        final storage = _FakeSecureStorage();
        storage.values[DeviceMetadataService.deviceIdentifierStorageKey] =
            'install-1';
        final repository = AuthRepository(
          api: ApiClient(
            client: MockClient((request) async {
              requests.add(request);
              if (request.url.path.contains('/devices/')) {
                return http.Response('{"success":false}', 500);
              }
              return http.Response(
                '{"success":true,"data":{},"errors":[]}',
                200,
                headers: const {'content-type': 'application/json'},
              );
            }),
            baseUrl: 'https://api.example.test',
          ),
          storage: storage,
          deviceMetadata: DeviceMetadataService(storage: storage),
        );

        await repository.logout(_session('customer-1', 'token-a'));

        expect(
          requests
              .where(
                (request) => request.url.path.contains('/devices/'),
              )
              .single
              .url
              .path,
          '/api/v1/devices/install-1/deactivate',
        );
        expect(
          requests.map((request) => request.url.path),
          contains('/api/v1/auth/logout'),
        );
      },
    );

    test(
      'logout failure still clears the local session while surfacing the error',
      () async {
        final storage = _FakeSecureStorage();
        storage.values[DeviceMetadataService.deviceIdentifierStorageKey] =
            'install-1';
        storage.values['identity.session.v1'] = jsonEncode({
          'seeded': true,
        });
        final repository = AuthRepository(
          api: ApiClient(
            client: MockClient(
              (_) async => http.Response('{"success":false}', 500),
            ),
            baseUrl: 'https://api.example.test',
          ),
          storage: storage,
          deviceMetadata: DeviceMetadataService(storage: storage),
        );

        // The logout request error propagates (existing caller-handled
        // semantics) but the session is cleared in the finally block.
        await expectLater(
          repository.logout(_session('customer-1', 'token-a')),
          throwsA(isA<ApiException>()),
        );

        expect(storage.values.containsKey('identity.session.v1'), isFalse);
      },
    );

    test('concurrent sign-out shares a single logout request', () async {
      final auth = _CountingAuthRepository(_session('customer-1', 'token-a'));
      final container = ProviderContainer(
        overrides: [
          authRepositoryProvider.overrideWithValue(auth),
          guestCartStorageProvider.overrideWithValue(
            GuestCartStorage(storage: _FakeSecureStorage()),
          ),
        ],
      );
      addTearDown(container.dispose);
      container.read(sessionControllerProvider);
      await _drain();
      expect(
        container.read(sessionControllerProvider).isAuthenticated,
        isTrue,
      );

      final notifier = container.read(sessionControllerProvider.notifier);
      await Future.wait([notifier.signOut(), notifier.signOut()]);

      expect(auth.logoutCalls, 1);
      expect(
        container.read(sessionControllerProvider).isAuthenticated,
        isFalse,
      );

      await notifier.signOut();
      expect(auth.logoutCalls, 1);
    });

    test(
      'signing in as another user registers the installation for the new user',
      () async {
        final repository = _RecordingNotificationRepository();
        final gateway = _StubPushGateway();
        final sessionA = _session('customer-a', 'token-a');
        final sessionB = _session('customer-b', 'token-b');

        final containerA = await _containerFor(
          sessionA,
          repository,
          gateway,
        );
        addTearDown(containerA.dispose);
        final containerB = await _containerFor(
          sessionB,
          repository,
          gateway,
        );
        addTearDown(containerB.dispose);

        expect(repository.registeredPushTokens, ['push-1', 'push-1']);
        expect(repository.registrationTokens, ['token-a', 'token-b']);
        expect(
          repository.registeredDevices
              .map((device) => device.deviceIdentifier)
              .toSet(),
          {'device-1'},
        );
      },
    );
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

Future<void> _drain() async {
  for (var index = 0; index < 12; index++) {
    await Future<void>.delayed(Duration.zero);
  }
}

Future<ProviderContainer> _containerFor(
  AuthSession session,
  NotificationRepository repository,
  PushNotificationGateway gateway,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(
        _RestoringAuthRepository(session),
      ),
      notificationRepositoryProvider.overrideWithValue(repository),
      pushNotificationGatewayProvider.overrideWithValue(gateway),
      deviceMetadataServiceProvider.overrideWithValue(
        _FixedDeviceMetadata(),
      ),
    ],
  );
  container.read(sessionControllerProvider);
  container.read(notificationControllerProvider);
  await _drain();
  return container;
}

class _RestoringAuthRepository extends AuthRepository {
  _RestoringAuthRepository(this._session);

  final AuthSession _session;

  @override
  Future<AuthSession?> restore() async => _session;
}

class _CountingAuthRepository extends AuthRepository {
  _CountingAuthRepository(this._session);

  final AuthSession _session;
  int logoutCalls = 0;

  @override
  Future<AuthSession?> restore() async => _session;

  @override
  Future<void> logout(AuthSession session) async {
    logoutCalls++;
    await Future<void>.delayed(const Duration(milliseconds: 20));
  }
}

class _RecordingNotificationRepository extends NotificationRepository {
  _RecordingNotificationRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final List<String> registeredPushTokens = [];
  final List<String> registrationTokens = [];
  final List<DeviceMetadata> registeredDevices = [];

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

  @override
  Future<RegisteredNotificationDevice> registerDevice({
    required String token,
    required DeviceMetadata device,
    required String pushToken,
  }) async {
    registrationTokens.add(token);
    registeredDevices.add(device);
    registeredPushTokens.add(pushToken);
    return RegisteredNotificationDevice(
      deviceId: 'registered-device-1',
      platform: device.platform,
      deviceName: device.deviceName,
      isActive: true,
      registeredAt: DateTime(2026, 8, 17, 8),
      lastSeenAt: DateTime(2026, 8, 17),
    );
  }
}

class _StubPushGateway implements PushNotificationGateway {
  @override
  Stream<String> get tokenRefreshes => const Stream<String>.empty();

  @override
  Stream<String> get openedDeepLinks => const Stream<String>.empty();

  @override
  Stream<void> get foregroundMessages => const Stream<void>.empty();

  @override
  Future<bool> initialize() async => true;

  @override
  Future<PushPermissionStatus> permissionStatus() async =>
      PushPermissionStatus.authorized;

  @override
  Future<PushPermissionStatus> requestPermission() async =>
      PushPermissionStatus.authorized;

  @override
  Future<String?> token() async => 'push-1';

  @override
  Future<String?> initialDeepLink() async => null;
}

class _FixedDeviceMetadata extends DeviceMetadataService {
  @override
  Future<DeviceMetadata> get() async => const DeviceMetadata(
    deviceIdentifier: 'device-1',
    deviceName: 'Test device',
    platform: 'android',
  );
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
