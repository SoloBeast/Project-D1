import 'package:doodh_direct_mobile/app/app.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/customer/client_configuration_repository.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_repository.dart';
import 'package:doodh_direct_mobile/features/setup/charge_controller.dart';
import 'package:doodh_direct_mobile/features/setup/charge_models.dart';
import 'package:doodh_direct_mobile/features/setup/charge_repository.dart';
import 'package:doodh_direct_mobile/features/setup/charge_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

/// Production-composition tests for the Tax & Charges admin routes.
///
/// These pump the ACTUAL production app (`DoodhDirectApp` with the real
/// `routerProvider`) and navigate straight to `/admin/setup/tax-charges` —
/// the deep-link path an Owner or System Administrator may take. They prove:
///
///   * the route is registered and reachable for an authenticated admin
///     session (no redirect away, no unauthorized panel for a READ holder),
///   * the list performs its load and renders the proper empty state when no
///     charges are configured,
///   * a MANAGE holder sees the create affordance on the same route.
void main() {
  group('Tax & Charges direct route (production app)', () {
    testWidgets(
      'direct navigation opens the charge list with the empty state for a '
      'READ-only admin',
      (tester) async {
        final container = await _pumpProductionApp(
          tester,
          permissions: const ['SETUP.TAX_CHARGES.READ'],
          charges: const [],
        );

        container.read(routerProvider).go('/admin/setup/tax-charges');
        await tester.pumpAndSettle();

        expect(find.byType(ChargeListScreen), findsOneWidget);
        // The list loaded and rendered the proper empty state — the route is
        // fully functional for a read-only holder.
        expect(find.text('Tax & Charges'), findsWidgets);
        expect(find.text('No charges configured'), findsOneWidget);
        // READ without MANAGE: no create affordance anywhere.
        expect(find.text('New charge'), findsNothing);
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'direct navigation shows the create affordance for a MANAGE holder',
      (tester) async {
        final container = await _pumpProductionApp(
          tester,
          permissions: const ['SETUP.TAX_CHARGES.READ', 'SETUP.TAX_CHARGES.MANAGE'],
          charges: const [],
        );

        container.read(routerProvider).go('/admin/setup/tax-charges');
        await tester.pumpAndSettle();

        expect(find.byType(ChargeListScreen), findsOneWidget);
        expect(find.text('No charges configured'), findsOneWidget);
        expect(find.text('New charge'), findsWidgets);
        expect(tester.takeException(), isNull);
      },
    );
  });
}

/// Pumps the ACTUAL production app with deterministic fakes, mirroring the
/// proven app-level harness in `milk_test_route_composition_test.dart`.
Future<ProviderContainer> _pumpProductionApp(
  WidgetTester tester, {
  required List<String> permissions,
  required List<Charge> charges,
}) async {
  final session = AuthSession(
    user: AuthUser(
      publicUserId: 'admin-route-user',
      displayName: 'Owner',
      email: 'owner@example.test',
      mobile: null,
      roles: const ['OWNER'],
      permissions: permissions,
      branchIds: const [],
    ),
    accessToken: 'tax-route-token',
    refreshToken: 'refresh-token',
    accessTokenExpiresAtUtc: DateTime.utc(2099),
    refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
  );

  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_SeededAuthRepository(session)),
      orderRepositoryProvider.overrideWithValue(_FakeOrderRepository()),
      clientConfigurationRepositoryProvider.overrideWithValue(
        _FakeClientConfigurationRepository(),
      ),
      chargeRepositoryProvider.overrideWithValue(
        _StaticChargeRepository(charges),
      ),
      notificationControllerProvider.overrideWith(
        _SeededNotificationController.new,
      ),
    ],
  );
  addTearDown(container.dispose);
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));

  await tester.pumpWidget(
    UncontrolledProviderScope(container: container, child: const DoodhDirectApp()),
  );
  await tester.pumpAndSettle();

  return container;
}

class _SeededAuthRepository extends AuthRepository {
  _SeededAuthRepository(this.session);

  final AuthSession session;

  @override
  Future<AuthSession?> restore() async => session;
}

class _SeededNotificationController extends NotificationController {
  @override
  NotificationState build() => const NotificationState();
}

/// Charge repository serving a fixed list without any HTTP.
class _StaticChargeRepository extends ChargeRepository {
  _StaticChargeRepository(this._charges)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final List<Charge> _charges;

  @override
  Future<List<Charge>> list(String accessToken) async => _charges;
}

class _FakeOrderRepository extends OrderRepository {
  _FakeOrderRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<List<OrderSummary>> getMine(String token) async => const [];
}

class _FakeClientConfigurationRepository extends ClientConfigurationRepository {
  _FakeClientConfigurationRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<ClientConfiguration> get(String token) async =>
      const ClientConfiguration();
}
