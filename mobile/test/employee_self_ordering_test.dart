import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/home/role_home_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

void main() {
  group('employee self-ordering section', () {
    testWidgets('delivery workspace shows personal ordering with permission',
        (tester) async {
      final router = await _pumpHome(
        tester,
        role: UserRole.delivery,
        session: _employeeSession(
          roles: const ['DELIVERY_STAFF'],
          permissions: const ['ORDERS.CREATE_OWN'],
        ),
      );

      expect(find.text('Personal ordering'), findsOneWidget);
      expect(find.text('Order for yourself'), findsOneWidget);
      // Only the self-order entry stays: later screens are the normal
      // customer catalogue flow.
      expect(find.text('My orders'), findsNothing);
      expect(find.text('My subscriptions'), findsNothing);
      expect(find.text('My wallet'), findsNothing);

      await tester.tap(find.text('Order for yourself'));
      await tester.pumpAndSettle();
      expect(router.state.matchedLocation, '/catalogue');
    });

    testWidgets('delivery workspace hides personal ordering without permission',
        (tester) async {
      await _pumpHome(
        tester,
        role: UserRole.delivery,
        session: _employeeSession(
          roles: const ['DELIVERY_STAFF'],
          permissions: const ['DELIVERIES.OPERATE_ASSIGNED'],
        ),
      );

      expect(find.text('Personal ordering'), findsNothing);
      expect(find.text('Order for yourself'), findsNothing);
      // Staff duties stay visible.
      expect(find.text("Today's deliveries"), findsOneWidget);
    });

    testWidgets('support workspace shows personal ordering with permission',
        (tester) async {
      await _pumpHome(
        tester,
        role: UserRole.support,
        session: _employeeSession(
          roles: const ['CUSTOMER_SUPPORT'],
          permissions: const ['ORDERS.CREATE_OWN'],
        ),
      );

      expect(find.text('Personal ordering'), findsOneWidget);
      expect(find.text('Order for yourself'), findsOneWidget);
      // Administration stays separate.
      expect(find.text('Refund / replacement requests'), findsOneWidget);
    });

    testWidgets('dairy workspace shows personal ordering with permission',
        (tester) async {
      await _pumpHome(
        tester,
        role: UserRole.dairy,
        session: _employeeSession(
          roles: const ['DAIRY_MANAGER'],
          permissions: const ['ORDERS.CREATE_OWN'],
          branchIds: const [7],
        ),
      );

      expect(find.text('Personal ordering'), findsOneWidget);
      expect(find.text('Order for yourself'), findsOneWidget);
      expect(find.text('Dairy operations'), findsOneWidget);
    });

    testWidgets('accountant workspace shows personal ordering with permission',
        (tester) async {
      await _pumpHome(
        tester,
        role: UserRole.accountant,
        session: _employeeSession(
          roles: const ['ACCOUNTANT'],
          permissions: const ['ORDERS.CREATE_OWN'],
        ),
      );

      expect(find.text('Personal ordering'), findsOneWidget);
      expect(find.text('Accounting workspace ready'), findsOneWidget);
    });
  });

  group('staff bottom navigation on self-ordering screens', () {
    testWidgets('dashboard replaces cart for staff sessions', (tester) async {
      final router = await _pumpShellNav(
        tester,
        session: _employeeSession(
          roles: const ['DELIVERY_STAFF'],
          permissions: const ['ORDERS.CREATE_OWN'],
        ),
      );

      // The 4th slot is Dashboard (no Cart) for staff.
      expect(find.text('Dashboard'), findsOneWidget);
      expect(find.text('Cart'), findsNothing);

      await tester.tap(find.text('Dashboard'));
      await tester.pumpAndSettle();
      expect(router.state.matchedLocation, '/home');

      // Home opens the shopping catalogue; Dashboard returns to the
      // employee home screen.
      await tester.tap(find.text('Home'));
      await tester.pumpAndSettle();
      expect(router.state.matchedLocation, '/catalogue');
    });

    test('staff tab highlighting separates catalogue home and dashboard', () {
      expect(CustomerBottomNavigation.selectedIndexFor('/catalogue', true), 0);
      expect(CustomerBottomNavigation.selectedIndexFor('/home', true), 3);
      expect(CustomerBottomNavigation.selectedIndexFor('/catalogue', false), 0);
      expect(CustomerBottomNavigation.selectedIndexFor('/home', false), 0);
    });

    testWidgets('customers keep the cart destination', (tester) async {
      await _pumpShellNav(
        tester,
        session: _employeeSession(roles: const [], permissions: const []),
      );

      expect(find.text('Cart'), findsOneWidget);
      expect(find.text('Dashboard'), findsNothing);
    });
  });
}

Future<GoRouter> _pumpShellNav(
  WidgetTester tester, {
  required AuthSession session,
}) async {
  Widget shell(String currentPath) => Scaffold(
    body: const Text('catalogue body'),
    bottomNavigationBar: CustomerBottomNavigation(currentPath: currentPath),
  );
  final router = GoRouter(
    initialLocation: '/catalogue',
    routes: [
      GoRoute(path: '/home', builder: (context, state) => shell('/home')),
      GoRoute(
        path: '/catalogue',
        builder: (context, state) => shell('/catalogue'),
      ),
      GoRoute(path: '/orders', builder: (context, state) => shell('/orders')),
      GoRoute(
        path: '/subscriptions',
        builder: (context, state) => shell('/subscriptions'),
      ),
      GoRoute(
        path: '/checkout',
        builder: (context, state) => shell('/checkout'),
      ),
    ],
  );
  addTearDown(router.dispose);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(session),
        ),
      ],
      child: MaterialApp.router(routerConfig: router),
    ),
  );
  await tester.pumpAndSettle();
  return router;
}

AuthSession _employeeSession({
  required List<String> roles,
  required List<String> permissions,
  List<int> branchIds = const [],
}) =>
    AuthSession(
      user: AuthUser(
        publicUserId: 'employee-1',
        displayName: 'Staff Member',
        email: null,
        mobile: '+919888888888',
        roles: roles,
        permissions: permissions,
        branchIds: branchIds,
      ),
      accessToken: 'token',
      refreshToken: 'refresh',
      accessTokenExpiresAtUtc: DateTime.utc(2099),
      refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
    );

Future<GoRouter> _pumpHome(
  WidgetTester tester, {
  required UserRole role,
  required AuthSession session,
}) async {
  final router = GoRouter(
    initialLocation: '/',
    routes: [
      GoRoute(
        path: '/',
        builder: (context, state) => RoleHomeScreen(role: role),
      ),
      for (final path in const [
        '/catalogue',
        '/orders',
        '/subscriptions',
        '/wallet',
      ])
        GoRoute(
          path: path,
          builder: (context, state) => _StubPage(title: path),
        ),
    ],
  );
  addTearDown(router.dispose);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(session),
        ),
      ],
      child: MaterialApp.router(routerConfig: router),
    ),
  );
  await tester.pumpAndSettle();
  return router;
}

class _SeededSessionController extends SessionController {
  _SeededSessionController(this.session);

  final AuthSession session;

  @override
  SessionState build() => SessionState.authenticated(session);
}

class _StubPage extends StatelessWidget {
  const _StubPage({required this.title});

  final String title;

  @override
  Widget build(BuildContext context) => Scaffold(body: Text(title));
}
