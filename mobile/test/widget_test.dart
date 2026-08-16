import 'package:doodh_direct_mobile/app/app.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_repository.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

void main() {
  test('unauthenticated session has no role', () {
    const session = SessionState.unauthenticated();

    expect(session.isAuthenticated, isFalse);
    expect(session.role, isNull);
  });

  test('canonical role codes map to role-aware navigation', () {
    expect(roleFromCodes(['CUSTOMER']).label, 'Customer');
    expect(roleFromCodes(['DELIVERY_STAFF']).label, 'Delivery');
    expect(roleFromCodes(['SYSTEM_ADMIN']).label, 'Admin');
    expect(roleFromCodes(['OWNER', 'CUSTOMER']).label, 'Owner');
  });

  testWidgets(
    'password login routes to server-derived workspace and logs out',
    (tester) async {
      final repository = _FakeAuthRepository();
      await tester.pumpWidget(
        ProviderScope(
          overrides: [authRepositoryProvider.overrideWithValue(repository)],
          child: const DoodhDirectApp(),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Sign in to your account'), findsOneWidget);
      await tester.enterText(
        find.byType(EditableText).at(0),
        'delivery@example.test',
      );
      await tester.enterText(
        find.byType(EditableText).at(1),
        'correct-password',
      );
      await tester.tap(find.text('Sign in'));
      await tester.pumpAndSettle();

      expect(repository.lastLogin, 'delivery@example.test');
      expect(find.text('Delivery workspace'), findsOneWidget);
      expect(find.text('Delivery route'), findsOneWidget);
      expect(find.text("Today's deliveries"), findsOneWidget);

      await tester.tap(find.byTooltip('Sign out'));
      await tester.pumpAndSettle();

      expect(repository.loggedOut, isTrue);
      expect(find.text('Sign in to your account'), findsOneWidget);
    },
  );

  group('authenticated customer routing', () {
    testWidgets('forward payment navigation uses the supplied order', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(tester);

      harness.router.go('/orders/${_order.publicId}/payment', extra: _order);
      await tester.pumpAndSettle();

      expect(find.text(_order.orderNumber), findsOneWidget);
      expect(find.text('Amount due: ${_order.formattedTotal}'), findsOneWidget);
      expect(harness.orders.requestedOrderIds, isEmpty);
    });

    testWidgets('restored payment URL loads order when extra is absent', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(tester);

      harness.router.go('/orders/${_order.publicId}/payment');
      await tester.pumpAndSettle();

      expect(find.text(_order.orderNumber), findsOneWidget);
      expect(harness.orders.requestedOrderIds, [_order.publicId]);
      expect(tester.takeException(), isNull);
    });

    testWidgets('back to a payment URL without extra does not crash', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(tester);

      harness.router.go('/orders/${_order.publicId}/payment');
      await tester.pumpAndSettle();
      harness.router.go('/home');
      await tester.pumpAndSettle();
      harness.router.go('/orders/${_order.publicId}/payment');
      await tester.pumpAndSettle();

      expect(find.text(_order.orderNumber), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('blank required route parameter shows a safe error state', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(tester);

      harness.router.go('/orders/%20/payment');
      await tester.pumpAndSettle();

      expect(find.text('Invalid Order link'), findsOneWidget);
      expect(
        find.text('The required order identifier is missing.'),
        findsOneWidget,
      );
      expect(tester.takeException(), isNull);
    });

    testWidgets('customer detail routes pop back to their parent screens', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(tester);

      harness.router.go('/orders');
      await tester.pumpAndSettle();
      harness.router.push('/orders/${_order.publicId}');
      await tester.pumpAndSettle();
      expect(find.text('Order details'), findsOneWidget);
      harness.router.pop();
      await tester.pumpAndSettle();
      expect(find.text('My orders'), findsOneWidget);

      harness.router.go('/catalogue');
      await tester.pumpAndSettle();
      harness.router.push('/catalogue/products/%20');
      await tester.pumpAndSettle();
      expect(find.text('Invalid Product link'), findsOneWidget);
      harness.router.pop();
      await tester.pumpAndSettle();
      expect(find.text('Catalogue'), findsOneWidget);

      harness.router.go('/customer/account');
      await tester.pumpAndSettle();
      harness.router.push('/customer/profile/edit');
      await tester.pumpAndSettle();
      expect(find.text('Edit profile'), findsOneWidget);
      harness.router.pop();
      await tester.pumpAndSettle();
      expect(find.text('My account'), findsOneWidget);

      harness.router.push('/customer/addresses/new');
      await tester.pumpAndSettle();
      expect(find.text('Add address'), findsOneWidget);
      harness.router.pop();
      await tester.pumpAndSettle();
      expect(find.text('My account'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  });
}

Future<_RouterHarness> _pumpAuthenticatedApp(WidgetTester tester) async {
  final auth = _AuthenticatedCustomerRepository();
  final orders = _FakeOrderRepository();
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(auth),
      orderRepositoryProvider.overrideWithValue(orders),
    ],
  );
  addTearDown(container.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const DoodhDirectApp(),
    ),
  );
  await tester.pumpAndSettle();
  return _RouterHarness(container.read(routerProvider), orders);
}

class _RouterHarness {
  const _RouterHarness(this.router, this.orders);

  final GoRouter router;
  final _FakeOrderRepository orders;
}

class _AuthenticatedCustomerRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _customerSession;
}

class _FakeOrderRepository extends OrderRepository {
  _FakeOrderRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final List<String> requestedOrderIds = [];

  @override
  Future<List<OrderSummary>> getMine(String token) async => [_order];

  @override
  Future<OrderSummary> get(String token, String orderId) async {
    requestedOrderIds.add(orderId);
    return _order;
  }
}

final _order = OrderSummary(
  publicId: '00000000-0000-0000-0000-000000000010',
  orderNumber: 'ORD-TEST-10',
  type: 'OneTime',
  status: 'PendingPayment',
  createdAtUtc: DateTime.utc(2026, 8, 16),
  addressLabel: 'Home',
  city: 'Pune',
  branchName: 'Central Dairy',
  items: const [
    OrderItem(
      productId: '00000000-0000-0000-0000-000000000020',
      productName: 'Whole Milk',
      sku: 'MILK-1L',
      unitOfMeasure: 'litre',
      quantity: 1,
      unitPrice: 60,
      lineTotal: 60,
    ),
  ],
  subtotal: 60,
  discountAmount: 0,
  payableAmount: 60,
  cancelledAtUtc: null,
);

final _customerSession = AuthSession(
  user: const AuthUser(
    publicUserId: '00000000-0000-0000-0000-000000000001',
    displayName: 'Customer User',
    email: 'customer@example.test',
    mobile: null,
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
  ),
  accessToken: 'customer-access-token',
  refreshToken: 'customer-refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

class _FakeAuthRepository extends AuthRepository {
  String? lastLogin;
  bool loggedOut = false;

  @override
  Future<AuthSession?> restore() async => null;

  @override
  Future<AuthSession> login(String login, String password) async {
    lastLogin = login;
    return _session;
  }

  @override
  Future<void> logout(AuthSession session) async {
    loggedOut = true;
  }

  static final _session = AuthSession(
    user: const AuthUser(
      publicUserId: '00000000-0000-0000-0000-000000000001',
      displayName: 'Delivery User',
      email: 'delivery@example.test',
      mobile: null,
      roles: ['DELIVERY_STAFF'],
      permissions: ['IDENTITY.BRANCH.ACCESS'],
      branchIds: [1],
    ),
    accessToken: 'access-token',
    refreshToken: 'refresh-token',
    accessTokenExpiresAtUtc: DateTime.utc(2099),
    refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
  );
}
