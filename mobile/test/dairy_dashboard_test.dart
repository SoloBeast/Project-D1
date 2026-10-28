import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/dairy/dairy_controller.dart';
import 'package:doodh_direct_mobile/features/dairy/dairy_models.dart';
import 'package:doodh_direct_mobile/features/dairy/dairy_repository.dart';
import 'package:doodh_direct_mobile/features/dairy/dairy_screens.dart';
import 'package:doodh_direct_mobile/features/home/role_home_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

void main() {
  group('multi-dairy home', () {
    testWidgets('shows one dashboard card per assigned dairy', (tester) async {
      await _pumpHome(tester, _twoDairySession);

      expect(find.text('Dabua dairy dashboard'), findsOneWidget);
      expect(find.text('NIT3 dairy dashboard'), findsOneWidget);
    });

    testWidgets('tapping a dairy card opens that dairy dashboard directly',
        (tester) async {
      final pumped = await _pumpHome(tester, _twoDairySession);

      await tester.tap(find.text('NIT3 dairy dashboard'));
      await tester.pumpAndSettle();

      expect(
        pumped.router.state.uri.path,
        '/dairy/branch/9/dashboard',
      );
      // Direct per-dairy entry: data for branch 9 loads with no picker.
      expect(find.text('Today'), findsOneWidget);
      expect(find.text('Branch'), findsNothing);
      expect(pumped.dairy.dashboardBranchIds, [9]);
    });
  });

  group('dairy dashboard Today cards', () {
    testWidgets('order Available batches, Production entries, Produced, Available',
        (tester) async {
      await _pumpDashboard(tester, branchId: 7);

      final labels = tester
          .widgetList<Text>(find.byType(Text))
          .map((text) => text.data ?? '')
          .toList(growable: false);
      final order = [
        labels.indexOf('Available batches'),
        labels.indexOf('Production entries'),
        labels.indexOf('Produced'),
        labels.indexOf('Available'),
      ];
      expect(order.every((index) => index >= 0), isTrue);
      expect(order, orderedEquals(order..sort()));
    });

    testWidgets('each card opens its relevant page', (tester) async {
      final router = (await _pumpDashboard(tester, branchId: 7)).router;

      await tester.tap(find.text('Available batches'));
      await tester.pumpAndSettle();
      expect(
        router.state.uri.path,
        '/dairy/branch/7/batches',
      );

      router.go('/dairy/branch/7/dashboard');
      await tester.pumpAndSettle();
      await tester.tap(find.text('Production entries'));
      await tester.pumpAndSettle();
      expect(
        router.state.uri.path,
        '/dairy/branch/7/production',
      );

      router.go('/dairy/branch/7/dashboard');
      await tester.pumpAndSettle();
      await tester.tap(find.text('Produced'));
      await tester.pumpAndSettle();
      expect(
        router.state.uri.path,
        '/dairy/branch/7/production',
      );

      router.go('/dairy/branch/7/dashboard');
      await tester.pumpAndSettle();
      await tester.tap(find.text('Available'));
      await tester.pumpAndSettle();
      expect(
        router.state.uri.path,
        '/dairy/branch/7/availability',
      );
    });

    testWidgets('generic entry still offers the branch picker', (tester) async {
      await _pumpDashboard(tester, branchId: null);

      expect(find.text('Branch'), findsOneWidget);
      expect(find.text('Today'), findsOneWidget);
    });

    testWidgets('direct entry hides the branch picker', (tester) async {
      await _pumpDashboard(tester, branchId: 7);

      expect(find.text('Branch'), findsNothing);
      expect(find.text('Today'), findsOneWidget);
    });
  });
}

/// Pumped harness: the router for navigation assertions and the fake dairy
/// repository recording which branches were loaded.
class _Pumped {
  const _Pumped({required this.router, required this.dairy});

  final GoRouter router;
  final _FakeDairyRepository dairy;
}

/// Pumps the real home screen for [session] with a stub dairy backend and
/// stub dairy routes. Returns the router for navigation assertions.
Future<_Pumped> _pumpHome(WidgetTester tester, AuthSession session) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  final dairy = _FakeDairyRepository();
  final router = GoRouter(
    initialLocation: '/',
    routes: [
      GoRoute(
        path: '/',
        builder: (context, state) =>
            const RoleHomeScreen(role: UserRole.dairy),
      ),
      GoRoute(
        path: '/dairy/branch/:branchId/dashboard',
        builder: (context, state) => DairyDashboardScreen(
          branchId: int.parse(state.pathParameters['branchId']!),
        ),
      ),
      GoRoute(
        path: '/dairy/branch/:branchId/batches',
        builder: (context, state) => const _StubPage(title: 'batches'),
      ),
      GoRoute(
        path: '/dairy/branch/:branchId/production',
        builder: (context, state) => const _StubPage(title: 'production'),
      ),
      GoRoute(
        path: '/dairy/branch/:branchId/availability',
        builder: (context, state) => const _StubPage(title: 'availability'),
      ),
    ],
  );
  addTearDown(router.dispose);
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(session),
        ),
        dairyRepositoryProvider.overrideWithValue(dairy),
      ],
      child: MaterialApp.router(routerConfig: router),
    ),
  );
  await tester.pumpAndSettle();
  return _Pumped(router: router, dairy: dairy);
}

/// Pumps [DairyDashboardScreen] (or the generic entry when [branchId] is
/// null) with a stub dairy backend and stub destination routes.
Future<_Pumped> _pumpDashboard(
  WidgetTester tester, {
  required int? branchId,
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  final dairy = _FakeDairyRepository();
  final router = GoRouter(
    initialLocation: branchId == null
        ? '/dairy/dashboard'
        : '/dairy/branch/$branchId/dashboard',
    routes: [
      GoRoute(
        path: '/dairy/dashboard',
        builder: (context, state) => const DairyDashboardScreen(),
      ),
      GoRoute(
        path: '/dairy/branch/:branchId/dashboard',
        builder: (context, state) => DairyDashboardScreen(
          branchId: int.parse(state.pathParameters['branchId']!),
        ),
      ),
      GoRoute(
        path: '/dairy/branch/:branchId/batches',
        builder: (context, state) => const _StubPage(title: 'batches'),
      ),
      GoRoute(
        path: '/dairy/branch/:branchId/production',
        builder: (context, state) => const _StubPage(title: 'production'),
      ),
      GoRoute(
        path: '/dairy/branch/:branchId/availability',
        builder: (context, state) => const _StubPage(title: 'availability'),
      ),
    ],
  );
  addTearDown(router.dispose);
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(_twoDairySession),
        ),
        dairyRepositoryProvider.overrideWithValue(dairy),
      ],
      child: MaterialApp.router(routerConfig: router),
    ),
  );
  await tester.pumpAndSettle();
  return _Pumped(router: router, dairy: dairy);
}

class _StubPage extends StatelessWidget {
  const _StubPage({required this.title});

  final String title;

  @override
  Widget build(BuildContext context) =>
      Scaffold(appBar: AppBar(title: Text(title)), body: const SizedBox());
}

class _SeededSessionController extends SessionController {
  _SeededSessionController(this.session);

  final AuthSession session;

  @override
  SessionState build() => SessionState.authenticated(session);
}

class _FakeDairyRepository extends DairyRepository {
  _FakeDairyRepository()
      : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final List<int> dashboardBranchIds = [];

  @override
  Future<DairyDashboard> getDashboard(
    String token,
    int branchId, {
    DateTime? productionDate,
  }) async {
    dashboardBranchIds.add(branchId);
    return DairyDashboard.fromJson({
      'branchId': branchId,
      'productionDate': '2026-08-17',
      'quantityProduced': 24.5,
      'availableQuantity': 18.75,
      'unit': 'L',
      'productionEntryCount': 2,
      'availableBatchCount': 1,
      'calculatedAt': '2026-08-17T05:00:00',
    });
  }

  @override
  Future<MilkAvailability> getAvailability(String token, int branchId) async =>
      MilkAvailability.fromJson({
        'branchId': branchId,
        'quantityProduced': 24.5,
        'quantityUsed': 5.75,
        'availableQuantity': 18.75,
        'unit': 'L',
        'availableBatchCount': 1,
        'calculatedAt': '2026-08-17T05:00:00',
      });
}

final _twoDairySession = AuthSession(
  user: const AuthUser(
    publicUserId: '00000000-0000-0000-0000-000000000007',
    displayName: 'Dairy Manager User',
    email: 'dairy-manager@example.test',
    mobile: null,
    roles: ['DAIRY_MANAGER'],
    permissions: [
      'IDENTITY.BRANCH.ACCESS',
      'DELIVERIES.READ_BRANCH',
      'DELIVERIES.ASSIGN_BRANCH',
    ],
    branchIds: [7, 9],
    branchDetails: [
      AuthUserBranchInfo(id: 7, code: 'DB0001', name: 'Dabua'),
      AuthUserBranchInfo(id: 9, code: 'NIT3', name: 'NIT3'),
    ],
  ),
  accessToken: 'dairy-manager-access-token',
  refreshToken: 'dairy-manager-refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
