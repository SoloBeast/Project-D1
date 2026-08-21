import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/home/role_home_screen.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_models.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_screen.dart';
import 'package:doodh_direct_mobile/features/notifications/push_notification_gateway.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

void main() {
  group('notification inbox', () {
    testWidgets('shows loading, empty, offline, and API error states', (
      tester,
    ) async {
      await _pumpInbox(
        tester,
        _SeededNotificationController(const NotificationState(isLoading: true)),
      );
      expect(find.bySemanticsLabel('Loading notifications'), findsOneWidget);
      expect(find.byType(CircularProgressIndicator), findsOneWidget);

      await _pumpInbox(
        tester,
        _SeededNotificationController(const NotificationState()),
      );
      expect(find.text('No notifications'), findsOneWidget);
      expect(find.text('Refresh'), findsOneWidget);

      final offlineController = _SeededNotificationController(
        const NotificationState(
          isOffline: true,
          errorMessage: 'Unable to reach DoodhDirect. Check your connection and try again.',
        ),
      );
      await _pumpInbox(tester, offlineController);
      expect(find.text('You are offline'), findsOneWidget);
      await tester.tap(find.text('Retry'));
      await tester.pump();
      expect(offlineController.loadInitialCalls, 1);

      final errorController = _SeededNotificationController(
        const NotificationState(errorMessage: 'Notifications unavailable.'),
      );
      await _pumpInbox(tester, errorController);
      expect(find.text('Notifications unavailable.'), findsOneWidget);
      await tester.tap(find.text('Retry'));
      await tester.pump();
      expect(errorController.loadInitialCalls, 1);
    });

    testWidgets(
      'renders notifications and marks an item read before navigation',
      (tester) async {
        final controller = _SeededNotificationController(
          NotificationState(
            items: [
              _notification(
                'notification-1',
                title: 'Order confirmed',
                body: 'Order DD-1001 is confirmed.',
                deepLink: '/destination',
              ),
              _notification(
                'notification-2',
                title: 'Delivery complete',
                body: 'Your delivery was completed.',
                isRead: true,
              ),
            ],
            unreadCount: 1,
            page: 1,
          ),
        );
        await _pumpInbox(tester, controller, withDestination: true);

        expect(find.text('Order confirmed'), findsOneWidget);
        expect(find.text('Order DD-1001 is confirmed.'), findsOneWidget);
        expect(find.text('Delivery complete'), findsOneWidget);
        expect(
          find.byIcon(Icons.notifications_active_outlined),
          findsOneWidget,
        );
        expect(find.byIcon(Icons.notifications_none), findsOneWidget);
        expect(find.byIcon(Icons.chevron_right), findsOneWidget);

        await tester.tap(find.text('Order confirmed'));
        await tester.pumpAndSettle();

        expect(controller.markedReadIds, ['notification-1']);
        expect(find.text('Notification destination'), findsOneWidget);
      },
    );

    testWidgets('requests permission only after tapping Enable', (
      tester,
    ) async {
      final controller = _SeededNotificationController(
        const NotificationState(
          permissionStatus: PushPermissionStatus.notDetermined,
        ),
      );
      await _pumpInbox(tester, controller);

      expect(
        find.text('Enable push notifications for timely updates.'),
        findsOneWidget,
      );
      expect(controller.permissionRequestCalls, 0);

      await tester.tap(find.text('Enable'));
      await tester.pump();

      expect(controller.permissionRequestCalls, 1);
    });

    testWidgets('notification cards remain usable on a narrow screen', (
      tester,
    ) async {
      final controller = _SeededNotificationController(
        NotificationState(
          items: [
            _notification(
              'notification-1',
              title: 'Order confirmed',
              body: 'Order DD-1001 is confirmed and ready for delivery.',
            ),
            _notification(
              'notification-2',
              title: 'Delivery complete',
              body: 'Your delivery was completed.',
              isRead: true,
            ),
          ],
          unreadCount: 1,
          permissionStatus: PushPermissionStatus.notDetermined,
          page: 1,
        ),
      );

      await _pumpInbox(tester, controller, surfaceSize: const Size(360, 800));

      expect(find.text('Order confirmed'), findsOneWidget);
      expect(find.text('Delivery complete'), findsOneWidget);
      expect(find.text('Enable'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('invokes refresh and pagination controls', (tester) async {
      final controller = _SeededNotificationController(
        NotificationState(
          items: List.generate(
            20,
            (index) => _notification(
              'notification-$index',
              title: 'Notification $index',
              body: 'Notification body $index',
            ),
          ),
          page: 1,
          hasMore: true,
        ),
      );
      await _pumpInbox(tester, controller);

      await tester.tap(find.byTooltip('Refresh notifications'));
      await tester.pump();
      expect(controller.refreshCalls, 1);

      await tester.drag(find.byType(ListView), const Offset(0, -1200));
      await tester.pump();
      expect(controller.loadMoreCalls, greaterThan(0));
    });
  });

  group('home notification badge', () {
    testWidgets('hides zero count and displays unread count', (tester) async {
      await _pumpHome(
        tester,
        _SeededNotificationController(const NotificationState()),
      );
      expect(find.byTooltip('Notifications'), findsOneWidget);
      expect(find.text('0'), findsNothing);

      await _pumpHome(
        tester,
        _SeededNotificationController(const NotificationState(unreadCount: 7)),
      );
      expect(find.text('7'), findsOneWidget);
    });

    testWidgets('caps large unread counts and opens the inbox', (tester) async {
      await _pumpHome(
        tester,
        _SeededNotificationController(
          const NotificationState(unreadCount: 120),
        ),
        withInbox: true,
      );

      expect(find.text('99+'), findsOneWidget);
      await tester.tap(find.byTooltip('Notifications'));
      await tester.pumpAndSettle();
      expect(find.text('Inbox destination'), findsOneWidget);
    });
  });
}

Future<void> _pumpInbox(
  WidgetTester tester,
  _SeededNotificationController controller, {
  bool withDestination = false,
  Size surfaceSize = const Size(800, 1200),
}) async {
  await tester.binding.setSurfaceSize(surfaceSize);
  addTearDown(() => tester.binding.setSurfaceSize(null));
  final router = GoRouter(
    initialLocation: '/notifications',
    routes: [
      GoRoute(
        path: '/notifications',
        builder: (context, state) => const NotificationInboxScreen(),
      ),
      if (withDestination)
        GoRoute(
          path: '/destination',
          builder: (context, state) => const Scaffold(
            body: Center(child: Text('Notification destination')),
          ),
        ),
    ],
  );
  addTearDown(router.dispose);
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        notificationControllerProvider.overrideWith(() => controller),
      ],
      child: MaterialApp.router(
        theme: ThemeData(useMaterial3: true),
        routerConfig: router,
      ),
    ),
  );
  await tester.pump();
}

Future<void> _pumpHome(
  WidgetTester tester,
  _SeededNotificationController controller, {
  bool withInbox = false,
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  final router = GoRouter(
    initialLocation: '/home',
    routes: [
      GoRoute(
        path: '/home',
        builder: (context, state) =>
            const RoleHomeScreen(role: UserRole.customer),
      ),
      if (withInbox)
        GoRoute(
          path: '/notifications',
          builder: (context, state) =>
              const Scaffold(body: Center(child: Text('Inbox destination'))),
        ),
    ],
  );
  addTearDown(router.dispose);
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        notificationControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(_SeededSessionController.new),
      ],
      child: MaterialApp.router(
        theme: ThemeData(useMaterial3: true),
        routerConfig: router,
      ),
    ),
  );
  await tester.pump();
}

class _SeededNotificationController extends NotificationController {
  _SeededNotificationController(this.initialState);

  final NotificationState initialState;
  int loadInitialCalls = 0;
  int refreshCalls = 0;
  int loadMoreCalls = 0;
  int permissionRequestCalls = 0;
  final List<String> markedReadIds = [];

  @override
  NotificationState build() => initialState;

  @override
  Future<void> loadInitial() async {
    loadInitialCalls++;
  }

  @override
  Future<void> refresh() async {
    refreshCalls++;
  }

  @override
  Future<void> loadMore() async {
    loadMoreCalls++;
  }

  @override
  Future<PushPermissionStatus> requestPushPermission() async {
    permissionRequestCalls++;
    return PushPermissionStatus.authorized;
  }

  @override
  Future<String?> markRead(AppNotification notification) async {
    markedReadIds.add(notification.notificationId);
    return notification.deepLink;
  }
}

class _SeededSessionController extends SessionController {
  @override
  SessionState build() => SessionState.authenticated(
    AuthSession(
      user: const AuthUser(
        publicUserId: 'notification-widget-user',
        displayName: 'Notification User',
        email: null,
        mobile: '9999999999',
        roles: ['CUSTOMER'],
        permissions: [],
        branchIds: [],
      ),
      accessToken: 'notification-widget-token',
      refreshToken: 'notification-widget-refresh-token',
      accessTokenExpiresAtUtc: DateTime.utc(2099),
      refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
    ),
  );
}

AppNotification _notification(
  String id, {
  required String title,
  required String body,
  String? deepLink,
  bool isRead = false,
}) => AppNotification(
  notificationId: id,
  eventType: 'ORDER_CONFIRMED',
  title: title,
  body: body,
  deepLink: deepLink,
  isRead: isRead,
  createdAt: DateTime(2026, 8, 17, 10),
  readAt: isRead ? DateTime(2026, 8, 17, 11) : null,
);
