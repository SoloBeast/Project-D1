import 'dart:async';

import 'package:doodh_direct_mobile/core/device/device_metadata_service.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_models.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_repository.dart';
import 'package:doodh_direct_mobile/features/notifications/push_notification_gateway.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('notification controller', () {
    test('loads the authenticated inbox and unread count on startup', () async {
      final repository = _FakeNotificationRepository(
        pages: {
          1: _page([_notification('notification-1')], totalCount: 1),
        },
        unreadCount: 1,
      );
      final gateway = _FakePushGateway(
        permission: PushPermissionStatus.notDetermined,
      );
      final container = await _authenticatedContainer(repository, gateway);
      addTearDown(container.dispose);
      addTearDown(gateway.dispose);

      final state = container.read(notificationControllerProvider);
      expect(state.items.single.notificationId, 'notification-1');
      expect(state.unreadCount, 1);
      expect(state.isLoading, isFalse);
      expect(state.permissionStatus, PushPermissionStatus.notDetermined);
      expect(repository.lastToken, 'notification-token');
      expect(gateway.requestPermissionCalls, 0);
      expect(repository.registeredPushTokens, isEmpty);
    });

    test(
      'registers existing and refreshed tokens only when authorized',
      () async {
        final repository = _FakeNotificationRepository();
        final gateway = _FakePushGateway(
          permission: PushPermissionStatus.authorized,
          currentToken: ' push-token-1 ',
        );
        final container = await _authenticatedContainer(repository, gateway);
        addTearDown(container.dispose);
        addTearDown(gateway.dispose);

        expect(repository.registeredPushTokens, ['push-token-1']);
        expect(
          repository.registeredDevices.single.deviceIdentifier,
          'device-1',
        );

        gateway.emitToken('push-token-1');
        await _drain();
        expect(repository.registeredPushTokens, ['push-token-1']);

        gateway.emitToken('push-token-2');
        await _drain();
        expect(repository.registeredPushTokens, [
          'push-token-1',
          'push-token-2',
        ]);
      },
    );

    test('requests permission only through the explicit action', () async {
      final repository = _FakeNotificationRepository();
      final gateway = _FakePushGateway(
        permission: PushPermissionStatus.notDetermined,
        requestedPermission: PushPermissionStatus.provisional,
        currentToken: 'push-token-1',
      );
      final container = await _authenticatedContainer(repository, gateway);
      addTearDown(container.dispose);
      addTearDown(gateway.dispose);

      expect(gateway.requestPermissionCalls, 0);
      expect(repository.registeredPushTokens, isEmpty);

      final status = await container
          .read(notificationControllerProvider.notifier)
          .requestPushPermission();

      expect(status, PushPermissionStatus.provisional);
      expect(gateway.requestPermissionCalls, 1);
      expect(repository.registeredPushTokens, ['push-token-1']);
      expect(
        container.read(notificationControllerProvider).permissionStatus,
        PushPermissionStatus.provisional,
      );
    });

    test('paginates with deduplication and updates unread state', () async {
      final first = _notification('notification-1');
      final second = _notification('notification-2');
      final repository = _FakeNotificationRepository(
        pages: {
          1: _page([first], totalCount: 21),
          2: _page([first, second], page: 2, totalCount: 21),
        },
        unreadCount: 2,
      );
      final gateway = _FakePushGateway();
      final container = await _authenticatedContainer(repository, gateway);
      addTearDown(container.dispose);
      addTearDown(gateway.dispose);
      final controller = container.read(
        notificationControllerProvider.notifier,
      );

      await controller.loadMore();
      var state = container.read(notificationControllerProvider);
      expect(state.items.map((item) => item.notificationId), [
        'notification-1',
        'notification-2',
      ]);
      expect(repository.requestedPages, [1, 2]);

      expect(await controller.markRead(first), '/orders/order-1');
      state = container.read(notificationControllerProvider);
      expect(state.items.first.isRead, isTrue);
      expect(state.unreadCount, 1);
      expect(repository.markedReadIds, ['notification-1']);
    });

    test('does not change read state when marking read fails', () async {
      final notification = _notification('notification-1');
      final repository = _FakeNotificationRepository(
        pages: {
          1: _page([notification], totalCount: 1),
        },
        unreadCount: 1,
        markReadFailure: const ApiException(500, 'READ_FAILED', 'Read failed.'),
      );
      final gateway = _FakePushGateway();
      final container = await _authenticatedContainer(repository, gateway);
      addTearDown(container.dispose);
      addTearDown(gateway.dispose);

      final link = await container
          .read(notificationControllerProvider.notifier)
          .markRead(notification);
      final state = container.read(notificationControllerProvider);

      expect(link, isNull);
      expect(state.items.single.isRead, isFalse);
      expect(state.unreadCount, 1);
      expect(state.errorMessage, 'Read failed.');
      expect(state.isOffline, isFalse);
    });

    test(
      'maps transport failures to offline state and API failures to messages',
      () async {
        final offlineRepository = _FakeNotificationRepository(
          loadFailure: Exception('socket closed'),
        );
        final offlineGateway = _FakePushGateway();
        final offlineContainer = await _authenticatedContainer(
          offlineRepository,
          offlineGateway,
        );
        addTearDown(offlineContainer.dispose);
        addTearDown(offlineGateway.dispose);

        expect(
          offlineContainer.read(notificationControllerProvider).isOffline,
          isTrue,
        );
        expect(
          offlineContainer.read(notificationControllerProvider).errorMessage,
          contains('Check your connection'),
        );

        final apiRepository = _FakeNotificationRepository(
          loadFailure: const ApiException(503, 'UNAVAILABLE', 'Try later.'),
        );
        final apiGateway = _FakePushGateway();
        final apiContainer = await _authenticatedContainer(
          apiRepository,
          apiGateway,
        );
        addTearDown(apiContainer.dispose);
        addTearDown(apiGateway.dispose);

        expect(
          apiContainer.read(notificationControllerProvider).isOffline,
          isFalse,
        );
        expect(
          apiContainer.read(notificationControllerProvider).errorMessage,
          'Try later.',
        );
      },
    );

    test('handles initial, opened, and consumed internal deep links', () async {
      final repository = _FakeNotificationRepository();
      final gateway = _FakePushGateway(
        initialLink: '/orders/order-1?source=push',
      );
      final container = await _authenticatedContainer(repository, gateway);
      addTearDown(container.dispose);
      addTearDown(gateway.dispose);
      final controller = container.read(
        notificationControllerProvider.notifier,
      );

      expect(controller.takePendingDeepLink(), '/orders/order-1?source=push');
      expect(controller.takePendingDeepLink(), isNull);

      gateway.emitOpenedLink('https://evil.example/orders/order-1');
      await _drain();
      expect(controller.takePendingDeepLink(), isNull);

      gateway.emitOpenedLink('/wallet');
      await _drain();
      expect(controller.takePendingDeepLink(), '/wallet');
    });

    test('refreshes the inbox after a foreground push signal', () async {
      final repository = _FakeNotificationRepository();
      final gateway = _FakePushGateway();
      final container = await _authenticatedContainer(repository, gateway);
      addTearDown(container.dispose);
      addTearDown(gateway.dispose);
      expect(repository.requestedPages, [1]);

      gateway.emitForegroundMessage();
      await _drain();

      expect(repository.requestedPages, [1, 1]);
      expect(repository.unreadCountCalls, 2);
    });

    test(
      'does not access notification services without authentication',
      () async {
        final repository = _FakeNotificationRepository();
        final gateway = _FakePushGateway();
        final container = ProviderContainer(
          overrides: [
            authRepositoryProvider.overrideWithValue(
              _UnauthenticatedRepository(),
            ),
            notificationRepositoryProvider.overrideWithValue(repository),
            pushNotificationGatewayProvider.overrideWithValue(gateway),
          ],
        );
        addTearDown(container.dispose);
        addTearDown(gateway.dispose);
        container.read(notificationControllerProvider);
        await _drain();

        await container
            .read(notificationControllerProvider.notifier)
            .loadInitial();
        final status = await container
            .read(notificationControllerProvider.notifier)
            .requestPushPermission();

        expect(repository.requestedPages, isEmpty);
        expect(gateway.initializeCalls, 0);
        expect(gateway.requestPermissionCalls, 0);
        expect(status, PushPermissionStatus.unavailable);
      },
    );
  });

  group('notification deep links', () {
    test(
      'accepts application routes and rejects external or ambiguous links',
      () {
        expect(
          normalizeNotificationDeepLink(' /deliveries/delivery-1 '),
          '/deliveries/delivery-1',
        );
        expect(
          normalizeNotificationDeepLink('/admin?tab=notifications'),
          '/admin?tab=notifications',
        );
        expect(
          normalizeNotificationDeepLink('https://example.test/orders/1'),
          isNull,
        );
        expect(
          normalizeNotificationDeepLink('//example.test/orders/1'),
          isNull,
        );
        expect(normalizeNotificationDeepLink('/unknown/path'), isNull);
        expect(normalizeNotificationDeepLink('orders/1'), isNull);
        expect(normalizeNotificationDeepLink(null), isNull);
      },
    );
  });
}

Future<ProviderContainer> _authenticatedContainer(
  _FakeNotificationRepository repository,
  _FakePushGateway gateway,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedRepository()),
      notificationRepositoryProvider.overrideWithValue(repository),
      pushNotificationGatewayProvider.overrideWithValue(gateway),
      deviceMetadataServiceProvider.overrideWithValue(
        _FakeDeviceMetadataService(),
      ),
    ],
  );
  container.read(sessionControllerProvider);
  container.read(notificationControllerProvider);
  await _drain();
  return container;
}

Future<void> _drain() async {
  for (var index = 0; index < 12; index++) {
    await Future<void>.delayed(Duration.zero);
  }
}

class _AuthenticatedRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _session;
}

class _UnauthenticatedRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => null;
}

class _FakeDeviceMetadataService extends DeviceMetadataService {
  @override
  Future<DeviceMetadata> get() async => const DeviceMetadata(
    deviceIdentifier: 'device-1',
    deviceName: 'Test device',
    platform: 'android',
  );
}

class _FakePushGateway implements PushNotificationGateway {
  _FakePushGateway({
    this.permission = PushPermissionStatus.denied,
    this.requestedPermission = PushPermissionStatus.denied,
    this.currentToken,
    this.initialLink,
  });

  final PushPermissionStatus permission;
  final PushPermissionStatus requestedPermission;
  final String? currentToken;
  final String? initialLink;
  final _tokens = StreamController<String>.broadcast();
  final _links = StreamController<String>.broadcast();
  final _foreground = StreamController<void>.broadcast();
  int initializeCalls = 0;
  int requestPermissionCalls = 0;

  @override
  Stream<void> get foregroundMessages => _foreground.stream;

  @override
  Stream<String> get openedDeepLinks => _links.stream;

  @override
  Stream<String> get tokenRefreshes => _tokens.stream;

  @override
  Future<bool> initialize() async {
    initializeCalls++;
    return true;
  }

  @override
  Future<String?> initialDeepLink() async => initialLink;

  @override
  Future<PushPermissionStatus> permissionStatus() async => permission;

  @override
  Future<PushPermissionStatus> requestPermission() async {
    requestPermissionCalls++;
    return requestedPermission;
  }

  @override
  Future<String?> token() async => currentToken;

  void emitToken(String token) => _tokens.add(token);
  void emitOpenedLink(String link) => _links.add(link);
  void emitForegroundMessage() => _foreground.add(null);

  Future<void> dispose() async {
    await _tokens.close();
    await _links.close();
    await _foreground.close();
  }
}

class _FakeNotificationRepository extends NotificationRepository {
  _FakeNotificationRepository({
    this.pages = const {},
    this.unreadCount = 0,
    this.loadFailure,
    this.markReadFailure,
  }) : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final Map<int, NotificationPage> pages;
  final int unreadCount;
  final Object? loadFailure;
  final Object? markReadFailure;
  final List<int> requestedPages = [];
  final List<String> markedReadIds = [];
  final List<String> registeredPushTokens = [];
  final List<DeviceMetadata> registeredDevices = [];
  String? lastToken;
  int unreadCountCalls = 0;

  @override
  Future<NotificationPage> getNotifications(
    String token, {
    int page = 1,
    int pageSize = 20,
    bool? isRead,
  }) async {
    lastToken = token;
    requestedPages.add(page);
    if (loadFailure != null) throw loadFailure!;
    return pages[page] ?? _page(const []);
  }

  @override
  Future<int> getUnreadCount(String token) async {
    lastToken = token;
    unreadCountCalls++;
    return unreadCount;
  }

  @override
  Future<void> markRead(String token, String notificationId) async {
    lastToken = token;
    if (markReadFailure != null) throw markReadFailure!;
    markedReadIds.add(notificationId);
  }

  @override
  Future<RegisteredNotificationDevice> registerDevice({
    required String token,
    required DeviceMetadata device,
    required String pushToken,
  }) async {
    lastToken = token;
    registeredDevices.add(device);
    registeredPushTokens.add(pushToken);
    return RegisteredNotificationDevice(
      deviceId: 'registered-device-1',
      platform: device.platform,
      deviceName: device.deviceName,
      isActive: true,
      lastSeenAtUtc: DateTime.utc(2026, 8, 17),
    );
  }
}

NotificationPage _page(
  List<AppNotification> items, {
  int page = 1,
  int totalCount = 0,
}) => NotificationPage(
  items: items,
  page: page,
  pageSize: 20,
  totalCount: totalCount,
);

AppNotification _notification(String id) => AppNotification(
  notificationId: id,
  eventType: 'ORDER_CONFIRMED',
  title: 'Order confirmed',
  body: 'Your order has been confirmed.',
  deepLink: '/orders/order-1',
  isRead: false,
  createdAtUtc: DateTime.utc(2026, 8, 17, 10),
  readAtUtc: null,
);

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'customer-1',
    displayName: 'Customer',
    email: 'customer@example.test',
    mobile: null,
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
  ),
  accessToken: 'notification-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
