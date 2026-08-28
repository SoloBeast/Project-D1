import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/home/role_home_screen.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

void main() {
  group('grouped administration home', () {
    testWidgets('shows all groups with full administration permissions', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const [
          'REPORTS.ADMINISTRATION.READ',
          'EMPLOYEES.READ',
          'EMPLOYEES.MANAGE',
          'BRANCHES.READ',
          'BRANCHES.MANAGE',
          'SETUP.NUMBER_SERIES.READ',
          'CAMERAS.READ',
        ],
        branchIds: const [7],
      );

      // Group headers.
      expect(find.text('Administration'), findsOneWidget);
      expect(find.text('User & Access'), findsOneWidget);
      expect(find.text('Master Data'), findsOneWidget);
      expect(find.text('System Setup'), findsOneWidget);
      expect(find.text('Monitoring & Operations'), findsOneWidget);

      // Menu tiles.
      expect(find.text('Employees'), findsOneWidget);
      expect(find.text('Branches'), findsOneWidget);
      expect(find.text('Catalogue'), findsOneWidget);
      expect(find.text('Preview catalogue'), findsOneWidget);
      expect(find.text('Number Series'), findsOneWidget);
      await tester.scrollUntilVisible(
        find.text('Deliveries'),
        200,
        scrollable: find.byType(Scrollable).first,
      );
      expect(find.text('Dashboard & Reports'), findsOneWidget);
      expect(find.text('Cameras'), findsOneWidget);
      expect(find.text('Dairy operations'), findsOneWidget);
      expect(find.text('Deliveries'), findsOneWidget);
    });

    testWidgets('renders the grid tiles inside the Administration home', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const [
          'EMPLOYEES.READ',
          'BRANCHES.READ',
          'SETUP.NUMBER_SERIES.READ',
          'CAMERAS.READ',
          'REPORTS.ADMINISTRATION.READ',
        ],
        branchIds: const [7],
      );

      // Each menu entry is a tappable Card tile (1 employee + 3 master data
      // + 1 number series + 4 monitoring = 9).
      final tiles = find.byWidgetPredicate(
        (w) => w is Card && w.child is InkWell,
      );
      expect(tiles, findsNWidgets(9));
      // Icons are rendered for each tile.
      expect(find.byIcon(Icons.group_outlined), findsOneWidget);
      expect(find.byIcon(Icons.storefront_outlined), findsOneWidget);
      expect(find.byIcon(Icons.inventory_2_outlined), findsOneWidget);
      expect(find.byIcon(Icons.numbers_outlined), findsOneWidget);
      expect(find.byIcon(Icons.dashboard_outlined), findsOneWidget);
      expect(find.byIcon(Icons.video_settings_outlined), findsOneWidget);
    });

    testWidgets('hides every group when no permissions are granted', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const [],
        branchIds: const [],
      );

      expect(find.text('Administration'), findsNothing);
      expect(find.text('User & Access'), findsNothing);
      expect(find.text('Master Data'), findsNothing);
      expect(find.text('System Setup'), findsNothing);
      expect(find.text('Monitoring & Operations'), findsNothing);
      expect(find.text('Branches'), findsNothing);
      expect(find.text('Employees'), findsNothing);
      expect(find.text('Number Series'), findsNothing);
    });

    testWidgets('shows Master Data group only when branches are readable', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const ['BRANCHES.READ'],
        branchIds: const [],
      );

      expect(find.text('Master Data'), findsOneWidget);
      expect(find.text('Branches'), findsOneWidget);
      expect(find.text('Catalogue'), findsOneWidget);
      expect(find.text('User & Access'), findsNothing);
      expect(find.text('System Setup'), findsNothing);
      expect(find.text('Monitoring & Operations'), findsNothing);
      expect(find.text('Administration'), findsNothing);
    });

    testWidgets('BRANCHES.MANAGE also exposes the Master Data group', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const ['BRANCHES.MANAGE'],
        branchIds: const [],
      );

      expect(find.text('Master Data'), findsOneWidget);
      expect(find.text('Branches'), findsOneWidget);
    });

    testWidgets('hides the User & Access group without employee permission', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const ['BRANCHES.READ'],
        branchIds: const [],
      );

      expect(find.text('Employees'), findsNothing);
      expect(find.text('User & Access'), findsNothing);
    });

    testWidgets('hides the System Setup group without number series permission', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const ['BRANCHES.READ'],
        branchIds: const [],
      );

      expect(find.text('System Setup'), findsNothing);
      expect(find.text('Number Series'), findsNothing);
    });

    testWidgets('shows Monitoring & Operations only when reports, cameras, or branches exist', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const ['CAMERAS.READ'],
        branchIds: const [],
      );

      expect(find.text('Monitoring & Operations'), findsOneWidget);
      expect(find.text('Cameras'), findsOneWidget);
      expect(find.text('Dairy operations'), findsNothing);
      expect(find.text('Deliveries'), findsNothing);
      expect(find.text('Dashboard & Reports'), findsNothing);
    });

    testWidgets('shows Dashboard & Reports tile inside Monitoring & Operations', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const ['REPORTS.FINANCIAL.READ'],
        branchIds: const [],
      );

      expect(find.text('Monitoring & Operations'), findsOneWidget);
      expect(find.text('Dashboard & Reports'), findsOneWidget);
      expect(find.text('Cameras'), findsNothing);
    });

    // Navigation: each authorized tile opens its existing route.
    testWidgets('tapping Employees navigates to employee management', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const ['EMPLOYEES.READ'],
        branchIds: const [],
        destinations: const {
          '/admin/employees': 'Employees destination',
        },
      );

      await tester.tap(find.text('Employees'));
      await tester.pumpAndSettle();

      expect(find.text('Employees destination'), findsOneWidget);
    });

    testWidgets('tapping Branches navigates to branch management', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const ['BRANCHES.READ', 'BRANCHES.MANAGE'],
        branchIds: const [],
        destinations: const {
          '/admin/branches': 'Branches destination',
        },
      );

      await tester.tap(find.text('Branches'));
      await tester.pumpAndSettle();

      expect(find.text('Branches destination'), findsOneWidget);
    });

    testWidgets('tapping Catalogue navigates to catalogue management', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const ['BRANCHES.READ'],
        branchIds: const [],
        destinations: const {
          '/admin/catalogue': 'Catalogue destination',
        },
      );

      await tester.tap(find.text('Catalogue'));
      await tester.pumpAndSettle();

      expect(find.text('Catalogue destination'), findsOneWidget);
    });

    testWidgets('tapping Number Series navigates to number series setup', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const ['SETUP.NUMBER_SERIES.READ'],
        branchIds: const [],
        destinations: const {
          '/admin/setup/number-series': 'Number series destination',
        },
      );

      await tester.tap(find.text('Number Series'));
      await tester.pumpAndSettle();

      expect(find.text('Number series destination'), findsOneWidget);
    });

    testWidgets('tapping Cameras navigates to camera management', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const ['CAMERAS.READ'],
        branchIds: const [],
        destinations: const {
          '/admin/cameras': 'Cameras destination',
        },
      );

      await tester.tap(find.text('Cameras'));
      await tester.pumpAndSettle();

      expect(find.text('Cameras destination'), findsOneWidget);
    });

    testWidgets('tapping Dashboard & Reports navigates to the admin dashboard', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const ['REPORTS.ADMINISTRATION.READ'],
        branchIds: const [],
        destinations: const {
          '/admin': 'Admin dashboard destination',
        },
      );

      await tester.tap(find.text('Dashboard & Reports'));
      await tester.pumpAndSettle();

      expect(find.text('Admin dashboard destination'), findsOneWidget);
    });

    testWidgets('tapping Preview catalogue navigates to the customer catalogue', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const ['BRANCHES.READ'],
        branchIds: const [],
        destinations: const {
          '/catalogue': 'Catalogue destination',
        },
      );

      await tester.scrollUntilVisible(
        find.text('Preview catalogue'),
        200,
        scrollable: find.byType(Scrollable).first,
      );
      await tester.tap(find.text('Preview catalogue'));
      await tester.pumpAndSettle();

      expect(find.text('Catalogue destination'), findsOneWidget);
    });

    // Responsive behavior.
    testWidgets('uses a 2-column grid at mobile width', (tester) async {
      await _pumpAdminHome(
        tester,
        permissions: const [
          'EMPLOYEES.READ',
          'BRANCHES.READ',
          'SETUP.NUMBER_SERIES.READ',
          'CAMERAS.READ',
          'REPORTS.ADMINISTRATION.READ',
        ],
        branchIds: const [7],
        surfaceSize: const Size(360, 800),
      );

      // First visible grid row: Employees (User & Access) is alone, then
      // Branches + Catalogue sit side by side in the Master Data grid.
      final grids = tester
          .widgetList<GridView>(find.byType(GridView))
          .toList();
      expect(grids, isNotEmpty);
      for (final grid in grids) {
        final delegate =
            grid.gridDelegate as SliverGridDelegateWithFixedCrossAxisCount;
        expect(delegate.crossAxisCount, 2);
      }
      expect(tester.takeException(), isNull);
    });

    testWidgets('uses 3 columns on a tablet-width surface', (tester) async {
      await _pumpAdminHome(
        tester,
        permissions: const [
          'EMPLOYEES.READ',
          'BRANCHES.READ',
          'SETUP.NUMBER_SERIES.READ',
          'CAMERAS.READ',
          'REPORTS.ADMINISTRATION.READ',
        ],
        branchIds: const [7],
        surfaceSize: const Size(760, 900),
      );

      final grids = tester
          .widgetList<GridView>(find.byType(GridView))
          .toList();
      expect(grids, isNotEmpty);
      for (final grid in grids) {
        final delegate =
            grid.gridDelegate as SliverGridDelegateWithFixedCrossAxisCount;
        expect(delegate.crossAxisCount, 3);
      }
      expect(tester.takeException(), isNull);
    });

    testWidgets('renders without overflow on a phone-sized screen', (
      tester,
    ) async {
      await _pumpAdminHome(
        tester,
        permissions: const [
          'REPORTS.ADMINISTRATION.READ',
          'EMPLOYEES.READ',
          'BRANCHES.READ',
          'SETUP.NUMBER_SERIES.READ',
          'CAMERAS.READ',
        ],
        branchIds: const [7],
        surfaceSize: const Size(360, 800),
      );

      // Scroll through the entire list so every child is laid out and any
      // overflow error would surface.
      await tester.scrollUntilVisible(
        find.text('Deliveries'),
        200,
        scrollable: find.byType(Scrollable).first,
      );
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
    });

    testWidgets('renders without overflow on a wide surface', (tester) async {
      await _pumpAdminHome(
        tester,
        permissions: const [
          'REPORTS.ADMINISTRATION.READ',
          'EMPLOYEES.READ',
          'BRANCHES.READ',
          'SETUP.NUMBER_SERIES.READ',
          'CAMERAS.READ',
        ],
        branchIds: const [7],
        surfaceSize: const Size(1200, 900),
      );

      await tester.scrollUntilVisible(
        find.text('Deliveries'),
        200,
        scrollable: find.byType(Scrollable).first,
      );
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
    });
  });
}

/// Pumps the owner/admin home with the supplied permissions and branch ids.
Future<void> _pumpAdminHome(
  WidgetTester tester, {
  required List<String> permissions,
  required List<int> branchIds,
  Size surfaceSize = const Size(800, 1200),
  Map<String, String> destinations = const {},
}) async {
  await tester.binding.setSurfaceSize(surfaceSize);
  addTearDown(() => tester.binding.setSurfaceSize(null));

  final router = GoRouter(
    initialLocation: '/home',
    routes: [
      GoRoute(
        path: '/home',
        builder: (context, state) => const RoleHomeScreen(role: UserRole.owner),
      ),
      for (final entry in destinations.entries)
        GoRoute(
          path: entry.key,
          builder: (context, state) => Scaffold(
            body: Center(child: Text(entry.value)),
          ),
        ),
      GoRoute(
        path: '/notifications',
        builder: (context, state) => const Scaffold(
          body: Center(child: Text('Notifications')),
        ),
      ),
    ],
  );
  addTearDown(router.dispose);

  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(
            permissions: permissions,
            branchIds: branchIds,
          ),
        ),
        notificationControllerProvider.overrideWith(
          _SeededNotificationController.new,
        ),
      ],
      child: MaterialApp.router(
        theme: ThemeData(useMaterial3: true),
        routerConfig: router,
      ),
    ),
  );
  await tester.pump();
  await tester.pump();
}

class _SeededSessionController extends SessionController {
  _SeededSessionController({
    required this.permissions,
    required this.branchIds,
  });

  final List<String> permissions;
  final List<int> branchIds;

  @override
  SessionState build() => SessionState.authenticated(
    AuthSession(
      user: AuthUser(
        publicUserId: 'admin-home-user',
        displayName: 'System Administrator',
        email: null,
        mobile: '9999999999',
        roles: const ['SYSTEM_ADMIN'],
        permissions: permissions,
        branchIds: branchIds,
      ),
      accessToken: 'admin-home-token',
      refreshToken: 'refresh-token',
      accessTokenExpiresAtUtc: DateTime.utc(2099),
      refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
    ),
  );
}

/// No-op notification controller: the home screen only watches unreadCount.
class _SeededNotificationController extends NotificationController {
  @override
  NotificationState build() => const NotificationState();
}
