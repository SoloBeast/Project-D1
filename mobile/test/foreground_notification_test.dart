import 'dart:async';

import 'package:doodh_direct_mobile/core/device/device_metadata_service.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/foreground_notification_presenter.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_models.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_repository.dart';
import 'package:doodh_direct_mobile/features/notifications/push_notification_gateway.dart';
import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('resolveForegroundDisplay', () {
    test('renders incoming title/body with a stable notification id', () {
      const data = {
        'notificationId': '3fa85f64-5717-4562-b3fc-2c963f66afa6',
        'eventType': 'ORDER_CREATED',
        'deepLink': '/orders/100',
        'deep_link': '/orders/100',
      };

      final first = resolveForegroundDisplay(
        title: 'Order update',
        body: 'Your order is on its way.',
        data: data,
      );
      final second = resolveForegroundDisplay(
        title: 'Order update',
        body: 'Your order is on its way.',
        data: data,
      );

      expect(first.shouldDisplay, isTrue);
      expect(first.title, 'Order update');
      expect(first.body, 'Your order is on its way.');
      expect(first.deepLink, '/orders/100');
      expect(first.notificationId, second.notificationId);
      expect(first.notificationId, greaterThanOrEqualTo(0));
    });

    test('reads the deep_link data key variant', () {
      final display = resolveForegroundDisplay(
        title: 't',
        body: 'b',
        data: const {'deep_link': '/wallet'},
      );

      expect(display.deepLink, '/wallet');
    });

    test('never renders OTP text for delivery OTP events', () {
      final display = resolveForegroundDisplay(
        title: 'Your delivery OTP is 731946',
        body: 'Share 731946 only with the assigned delivery staff.',
        data: const {'eventType': 'DELIVERY_OTP_ISSUED'},
      );

      expect(display.title, otpFallbackTitle);
      expect(display.body, otpFallbackBody);
      expect(display.title, isNot(contains('731946')));
      expect(display.body, isNot(contains('731946')));
    });

    test('stays hidden when there is nothing to display', () {
      final display = resolveForegroundDisplay(
        title: null,
        body: '   ',
        data: const {'eventType': 'ORDER_CREATED'},
      );

      expect(display.shouldDisplay, isFalse);
    });

    test('derives distinct ids for distinct notifications', () {
      final first = resolveForegroundDisplay(
        title: 't',
        body: 'b',
        data: const {'notificationId': 'notification-a'},
      );
      final second = resolveForegroundDisplay(
        title: 't',
        body: 'b',
        data: const {'notificationId': 'notification-b'},
      );

      expect(first.notificationId, isNot(second.notificationId));
    });
  });

  group('foreground heads-up integration', () {
    test('shows a heads-up and preserves the inbox refresh', () async {
      final repository = _FakeNotificationRepository();
      final gateway = _FakePushGateway(
        permission: PushPermissionStatus.authorized,
      );
      final presenter = _FakePresenter();
      final container = await _authenticatedContainer(
        repository,
        gateway,
        presenter,
      );
      addTearDown(container.dispose);
      addTearDown(gateway.dispose);
      addTearDown(presenter.dispose);
      expect(repository.requestedPages, [1]);

      gateway.emitForegroundMessage();
      gateway.emitForegroundPayload(
        RemoteMessage(
          notification: const RemoteNotification(
            title: 'Delivery started',
            body: 'Your milk is on its way.',
          ),
          data: const {
            'notificationId': 'notif-1',
            'eventType': 'DELIVERY_STARTED',
            'deepLink': '/deliveries/delivery-1',
          },
        ),
      );
      await _drain();

      // Inbox refresh path is unchanged.
      expect(repository.requestedPages, [1, 1]);
      // Exactly one heads-up with the server text (background payloads are
      // never duplicated by this path).
      expect(presenter.shown, hasLength(1));
      expect(presenter.shown.single.title, 'Delivery started');
      expect(presenter.shown.single.body, 'Your milk is on its way.');
      expect(
        presenter.shown.single.deepLink,
        '/deliveries/delivery-1',
      );
    });

    test('routes foreground notification taps through the deep-link allowlist',
        () async {
      final repository = _FakeNotificationRepository();
      final gateway = _FakePushGateway(
        permission: PushPermissionStatus.authorized,
      );
      final presenter = _FakePresenter();
      final container = await _authenticatedContainer(
        repository,
        gateway,
        presenter,
      );
      addTearDown(container.dispose);
      addTearDown(gateway.dispose);
      addTearDown(presenter.dispose);

      presenter.emitTap('/deliveries/delivery-1');
      await _drain();
      expect(
        container.read(notificationControllerProvider).pendingDeepLink,
        '/deliveries/delivery-1',
      );

      presenter.emitTap('https://example.test/evil');
      await _drain();
      expect(
        container.read(notificationControllerProvider).pendingDeepLink,
        '/deliveries/delivery-1',
        reason: 'external tap payloads must not replace the pending link',
      );
    });

    test('degrades gracefully when presentation is unavailable', () async {
      final repository = _FakeNotificationRepository();
      final gateway = _FakePushGateway(
        permission: PushPermissionStatus.denied,
      );
      final presenter = _FakePresenter()..available = false;
      final container = await _authenticatedContainer(
        repository,
        gateway,
        presenter,
      );
      addTearDown(container.dispose);
      addTearDown(gateway.dispose);
      addTearDown(presenter.dispose);

      gateway.emitForegroundPayload(
        RemoteMessage(
          notification: const RemoteNotification(
            title: 'Delivery started',
            body: 'Your milk is on its way.',
          ),
          data: const {'eventType': 'DELIVERY_STARTED'},
        ),
      );
      await _drain();

      expect(presenter.shown, isEmpty);
      // Inbox access keeps working when foreground display cannot.
      expect(repository.requestedPages, isNotEmpty);
    });
  });
}

Future<ProviderContainer> _authenticatedContainer(
  _FakeNotificationRepository repository,
  _FakePushGateway gateway,
  _FakePresenter presenter,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedRepository()),
      notificationRepositoryProvider.overrideWithValue(repository),
      pushNotificationGatewayProvider.overrideWithValue(gateway),
      foregroundNotificationPresenterProvider.overrideWithValue(presenter),
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
  Future<AuthSession?> restore() async => AuthSession(
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
  _FakePushGateway({this.permission = PushPermissionStatus.denied});

  final PushPermissionStatus permission;
  final _foreground = StreamController<void>.broadcast();
  final _foregroundPayloads = StreamController<RemoteMessage>.broadcast();

  @override
  Stream<String> get tokenRefreshes => const Stream<String>.empty();

  @override
  Stream<String> get openedDeepLinks => const Stream<String>.empty();

  @override
  Stream<void> get foregroundMessages => _foreground.stream;

  @override
  Stream<RemoteMessage> get foregroundMessagePayloads =>
      _foregroundPayloads.stream;

  @override
  Future<bool> initialize() async => true;

  @override
  Future<String?> initialDeepLink() async => null;

  @override
  Future<PushPermissionStatus> permissionStatus() async => permission;

  @override
  Future<PushPermissionStatus> requestPermission() async => permission;

  @override
  Future<String?> token() async => null;

  void emitForegroundMessage() => _foreground.add(null);
  void emitForegroundPayload(RemoteMessage message) =>
      _foregroundPayloads.add(message);

  Future<void> dispose() async {
    await _foreground.close();
    await _foregroundPayloads.close();
  }
}

class _FakePresenter implements ForegroundNotificationPresenter {
  bool available = true;
  final shown = <ForegroundDisplay>[];
  final _taps = StreamController<String>.broadcast();

  @override
  Stream<String> get tapDeepLinks => _taps.stream;

  @override
  Future<bool> initialize() async => available;

  @override
  Future<void> show(ForegroundDisplay display) async {
    if (available) shown.add(display);
  }

  void emitTap(String link) => _taps.add(link);

  Future<void> dispose() => _taps.close();
}

class _FakeNotificationRepository extends NotificationRepository {
  _FakeNotificationRepository()
      : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final List<int> requestedPages = [];

  @override
  Future<NotificationPage> getNotifications(
    String token, {
    int page = 1,
    int pageSize = 20,
    bool? isRead,
  }) async {
    requestedPages.add(page);
    return NotificationPage(
      items: const [],
      page: page,
      pageSize: pageSize,
      totalCount: 0,
    );
  }

  @override
  Future<int> getUnreadCount(String token) async => 0;
}
