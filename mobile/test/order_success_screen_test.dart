import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/payments/payment_controller.dart';
import 'package:doodh_direct_mobile/features/payments/payment_models.dart';
import 'package:doodh_direct_mobile/features/payments/payment_repository.dart';
import 'package:doodh_direct_mobile/features/payments/payment_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

Map<String, dynamic> successPaymentJson({
  String? orderId = 'order-1',
  String? orderNumber = 'DD-000001',
  String? subscriptionId,
}) => {
  'publicId': 'payment-1',
  'orderId': orderId,
  'subscriptionId': subscriptionId,
  'orderNumber': orderNumber,
  'method': 'Razorpay',
  'provider': 'Razorpay',
  'status': 'Success',
  'amount': 90,
  'refundedAmount': 0,
  'currency': 'INR',
  'gatewayOrderId': 'order_mock_payment_1',
  'gatewayPaymentId': null,
  'gatewayKeyId': null,
  'failureCode': null,
  'failureMessage': null,
  'expiresAtUtc': '2026-08-16T01:00:00Z',
  'verifiedAtUtc': '2026-08-16T00:30:00Z',
  'createdAtUtc': '2026-08-16T00:00:00Z',
};

Map<String, dynamic> confirmedOrderJson() => {
  'publicId': 'order-1',
  'orderNumber': 'DD-000001',
  'type': 'OneTime',
  'status': 'Confirmed',
  'createdAt': '2026-08-16T00:00:00Z',
  'addressLabel': 'Home',
  'city': 'Pune',
  'branchName': 'Kothrud Dairy',
  'items': [
    {
      'productId': 'product-1',
      'productName': 'Toned Milk',
      'sku': 'MILK-1L',
      'unitOfMeasure': 'litre',
      'quantity': 2,
      'unitPrice': 45,
      'lineTotal': 90,
    },
  ],
  'subtotal': 90,
  'discountAmount': 0,
  'payableAmount': 90,
  'cancelledAt': null,
  'paymentPublicId': 'payment-1',
  'paymentStatus': 'Success',
  'gatewayPaymentId': null,
  'deliveryPublicId': 'delivery-1',
  'deliveryReferenceNumber': 'DLV-1',
  'deliveryStatus': 'Scheduled',
};

http.Response successResponse(Object data, {int statusCode = 200}) =>
    http.Response(
      jsonEncode({'success': true, 'data': data, 'errors': []}),
      statusCode,
      headers: {'content-type': 'application/json'},
    );

final _authenticatedSession = AuthSession(
  user: const AuthUser(
    publicUserId: 'customer-1',
    displayName: 'Test Customer',
    email: 'customer@example.test',
    mobile: null,
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
  ),
  accessToken: 'customer-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2026, 8, 16, 6),
  refreshTokenExpiresAtUtc: DateTime.utc(2026, 9, 16),
);

class _AuthenticatedAuthRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _authenticatedSession;
}

class _SeededPaymentController extends PaymentController {
  _SeededPaymentController(this.payment);

  final PaymentDetails payment;

  @override
  PaymentState build() =>
      PaymentState(payment: payment, selectedMethod: payment.method);

  @override
  Future<bool> refresh() async => true;
}

class _LoadingPaymentController extends PaymentController {
  _LoadingPaymentController(this.payment);

  final PaymentDetails payment;

  @override
  PaymentState build() => PaymentState(
    payment: payment,
    selectedMethod: payment.method,
    isLoading: true,
  );

  @override
  Future<bool> refresh() async => true;
}

/// Controller with NO payment loaded, for the failure/retry path.
class _EmptyPaymentController extends PaymentController {
  @override
  PaymentState build() => const PaymentState();

  @override
  Future<bool> refresh() async => false;
}

/// Known successful payment whose periodic refresh fails: the confirmed state
/// stays authoritative and the failure is surfaced as a banner.
class _FailingRefreshPaymentController extends PaymentController {
  _FailingRefreshPaymentController(this.payment);

  final PaymentDetails payment;

  @override
  PaymentState build() =>
      PaymentState(payment: payment, selectedMethod: payment.method);

  @override
  Future<bool> refresh() async {
    state = state.copyWith(isLoading: false, errorMessage: 'boom');
    return false;
  }
}

/// Seeds the controller with the already-confirmed order snapshot so the
/// success screen renders full order/delivery data without network access.
class _SeededOrderController extends OrderController {
  _SeededOrderController(this.initialState);

  final OrderState initialState;

  @override
  OrderState build() => initialState;

  @override
  Future<void> loadOrder(String orderId) async {
    // Keep the seeded snapshot for the same order; no network access.
    if (state.selectedOrder?.publicId == orderId) return;
    state = state.copyWith(selectedOrder: null, isLoading: false);
  }
}

/// Order controller for failure-path tests: the detail fetch reports an error.
class _FailingOrderController extends OrderController {
  @override
  OrderState build() => const OrderState();

  @override
  Future<void> loadOrder(String orderId) async {
    state = state.copyWith(isLoading: false, errorMessage: 'Order fetch failed');
  }
}

class _FailingPaymentRepository extends PaymentRepository {
  _FailingPaymentRepository()
    : super(
        api: ApiClient(
          client: MockClient((_) async => http.Response('', 500)),
          baseUrl: 'https://api.example.test',
        ),
      );

  @override
  Future<PaymentDetails> get(String token, String paymentId) async {
    throw const ApiException(500, 'LOAD_FAILED', 'boom');
  }
}

class _OrderDetailTarget extends ConsumerWidget {
  const _OrderDetailTarget();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final order = ref.watch(
      orderControllerProvider.select((state) => state.selectedOrder),
    );
    // The cart must already be cleared by the Order Success screen by the
    // time the customer reaches the order detail destination.
    final cartCount = ref.watch(
      orderControllerProvider.select((state) => state.cart.length),
    );
    return Scaffold(
      body: Column(
        children: [
          Text('Order target ${order?.orderNumber ?? 'none'}'),
          Text('cart lines $cartCount'),
        ],
      ),
    );
  }
}

GoRouter _resultRouter() => GoRouter(
  initialLocation: '/payments/payment-1/result',
  routes: [
    GoRoute(
      path: '/payments/:paymentId/result',
      builder: (context, state) =>
          PaymentResultScreen(paymentId: state.pathParameters['paymentId']!),
    ),
    GoRoute(
      path: '/orders/:orderId',
      builder: (context, state) => const _OrderDetailTarget(),
    ),
    GoRoute(
      path: '/deliveries/:deliveryId',
      builder: (context, state) => Scaffold(
        body: Text('Delivery target ${state.pathParameters['deliveryId']}'),
      ),
    ),
    GoRoute(
      path: '/subscriptions/:subscriptionId',
      builder: (context, state) =>
          const Scaffold(body: Text('Subscription target')),
    ),
    GoRoute(path: '/home', builder: (context, state) => const Scaffold(body: Text('Home target'))),
    GoRoute(
      path: '/catalogue',
      builder: (context, state) => const Scaffold(body: Text('Catalogue target')),
    ),
  ],
);

/// Tall surface for navigation tests so every CTA is built and hittable
/// without scrolling (the [WidgetTester.view] API drives hit testing here).
void _useSurface(WidgetTester tester, Size size) {
  tester.view.devicePixelRatio = 1.0;
  tester.view.physicalSize = size;
  addTearDown(tester.view.reset);
}

Future<ProviderContainer> _pumpSuccessScreen(
  WidgetTester tester, {
  required PaymentDetails payment,
  OrderState? orderState,
  PaymentRepository? paymentRepository,
  bool useEmptyPayment = false,
  PaymentController Function()? paymentControllerBuilder,
}) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedAuthRepository()),
      paymentControllerProvider.overrideWith(() {
        final built = paymentControllerBuilder?.call();
        if (built != null) return built;
        if (useEmptyPayment) return _EmptyPaymentController();
        if (paymentRepository != null) {
          return _LoadingPaymentController(payment);
        }
        return _SeededPaymentController(payment);
      }),
      if (paymentRepository != null)
        paymentRepositoryProvider.overrideWithValue(paymentRepository),
      orderControllerProvider.overrideWith(
        () => orderState == null
            ? _FailingOrderController()
            : _SeededOrderController(orderState),
      ),
    ],
  );
  addTearDown(container.dispose);
  final router = _resultRouter();
  addTearDown(router.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp.router(routerConfig: router),
    ),
  );
  await tester.pump();
  return container;
}

OrderState get _confirmedOrderState => OrderState(selectedOrder: _confirmedOrder);

final _confirmedOrder = OrderSummary(
  publicId: 'order-1',
  orderNumber: 'DD-000001',
  type: 'OneTime',
  status: 'Confirmed',
  createdAt: DateTime.utc(2026, 8, 16),
  addressLabel: 'Home',
  city: 'Pune',
  branchName: 'Kothrud Dairy',
  items: [
    OrderItem(
      productId: 'product-1',
      productName: 'Toned Milk',
      sku: 'MILK-1L',
      unitOfMeasure: 'litre',
      quantity: 2,
      unitPrice: 45,
      lineTotal: 90,
    ),
  ],
  subtotal: 90,
  discountAmount: 0,
  payableAmount: 90,
  cancelledAt: null,
  paymentPublicId: 'payment-1',
  paymentStatus: 'Success',
  gatewayPaymentId: null,
  deliveryPublicId: 'delivery-1',
  deliveryReferenceNumber: 'DLV-1',
  deliveryStatus: 'Scheduled',
);

void main() {
  PaymentDetails paymentFrom(Map<String, dynamic> json) =>
      PaymentDetails.fromJson(json);

  testWidgets('confirmed success renders the hero, semantics and status pill', (
    tester,
  ) async {
    await _pumpSuccessScreen(
      tester,
      payment: paymentFrom(successPaymentJson()),
      orderState: _confirmedOrderState,
    );
    await tester.pumpAndSettle();

    expect(find.text('Order Success'), findsOneWidget);
    expect(find.text('Payment successful'), findsOneWidget);
    expect(
      find.text('₹90.00 was verified for order DD-000001.'),
      findsOneWidget,
    );
    expect(
      find.text('Payment confirmed by DoodhDirect'),
      findsOneWidget,
    );
    expect(
      find.bySemanticsLabel(
        'Payment confirmed by DoodhDirect. Payment successful. '
        '₹90.00 was verified for order DD-000001.',
      ),
      findsOneWidget,
    );
  });

  testWidgets('order summary shows reference, method, items and amounts', (
    tester,
  ) async {
    await _pumpSuccessScreen(
      tester,
      payment: paymentFrom(successPaymentJson()),
      orderState: _confirmedOrderState,
    );
    await tester.pumpAndSettle();

    expect(find.text('Order summary'), findsOneWidget);
    expect(find.text('DD-000001'), findsOneWidget);
    expect(find.text('Razorpay'), findsOneWidget);
    expect(find.text('Verified'), findsOneWidget);
    // Item lines come from the confirmed order snapshot.
    expect(find.text('Toned Milk'), findsOneWidget);
    expect(find.text('2 litre'), findsOneWidget);
    // Item lines come from the confirmed order snapshot. The amount appears
    // three times from server data: item line total, subtotal, and the
    // verified payment amount (all ₹90.00 in this fixture).
    expect(find.text('₹90.00'), findsNWidgets(3));
    expect(find.text('Subtotal'), findsOneWidget);
    expect(find.text('₹90.00 / litre'), findsNothing);
  });

  testWidgets('delivery card shows destination and status without inventing ETA', (
    tester,
  ) async {
    await _pumpSuccessScreen(
      tester,
      payment: paymentFrom(successPaymentJson()),
      orderState: _confirmedOrderState,
    );
    await tester.pumpAndSettle();

    expect(find.text('Delivery'), findsOneWidget);
    expect(find.text('Home · Pune'), findsOneWidget);
    expect(find.text('Kothrud Dairy'), findsOneWidget);
    expect(find.text('Scheduled'), findsOneWidget);
    expect(find.text('ETA'), findsNothing);
  });

  testWidgets('View Order navigates through the existing order route', (
    tester,
  ) async {
    _useSurface(tester, const Size(1024, 1600));
    final router = _resultRouter();
    addTearDown(router.dispose);
    final container = ProviderContainer(
      overrides: [
        authRepositoryProvider.overrideWithValue(
          _AuthenticatedAuthRepository(),
        ),
        paymentControllerProvider.overrideWith(
          () => _SeededPaymentController(
            paymentFrom(successPaymentJson()),
          ),
        ),
        orderControllerProvider.overrideWith(
          () => _SeededOrderController(
            _confirmedOrderState,
          ),
        ),
      ],
    );
    addTearDown(container.dispose);
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: MaterialApp.router(routerConfig: router),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('View Order'));
    await tester.pumpAndSettle();

    expect(
      router.routerDelegate.currentConfiguration.uri.path,
      '/orders/order-1',
    );
    // The order detail target reads the SAME shared controller state, so the
    // confirmed snapshot round-trips without a second source of truth.
    expect(find.text('Order target DD-000001'), findsOneWidget);
    // The Order Success screen cleared the cart through the existing
    // controller method before navigation.
    expect(find.text('cart lines 0'), findsOneWidget);
  });

  testWidgets('Track delivery navigates only when a delivery exists', (
    tester,
  ) async {
    _useSurface(tester, const Size(1024, 1600));
    final router = _resultRouter();
    addTearDown(router.dispose);
    final container = ProviderContainer(
      overrides: [
        authRepositoryProvider.overrideWithValue(
          _AuthenticatedAuthRepository(),
        ),
        paymentControllerProvider.overrideWith(
          () => _SeededPaymentController(
            paymentFrom(successPaymentJson()),
          ),
        ),
        orderControllerProvider.overrideWith(
          () => _SeededOrderController(
            _confirmedOrderState,
          ),
        ),
      ],
    );
    addTearDown(container.dispose);
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: MaterialApp.router(routerConfig: router),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Track delivery'), findsOneWidget);
    final trackButton = find.byWidgetPredicate(
      (widget) => widget is DoodhButton && widget.label == 'Track delivery',
    );
    expect(trackButton, findsOneWidget);
    // A full press gesture (down, settle, up) is used so the tap cannot be
    // swallowed as an ambiguous short tap in the hit-test chain.
    final gesture = await tester.startGesture(tester.getCenter(trackButton));
    await tester.pump(const Duration(milliseconds: 150));
    await gesture.up();
    await tester.pumpAndSettle();
    expect(
      router.routerDelegate.currentConfiguration.uri.path,
      '/deliveries/delivery-1',
    );
  });

  testWidgets('order without a delivery id offers no Track delivery action', (
    tester,
  ) async {
    // Same confirmed order but with no server-provided delivery reference.
    final OrderSummary deliveryLessOrder = OrderSummary(
      publicId: _confirmedOrder.publicId,
      orderNumber: _confirmedOrder.orderNumber,
      type: _confirmedOrder.type,
      status: _confirmedOrder.status,
      createdAt: _confirmedOrder.createdAt,
      addressLabel: _confirmedOrder.addressLabel,
      city: _confirmedOrder.city,
      branchName: _confirmedOrder.branchName,
      items: _confirmedOrder.items,
      subtotal: _confirmedOrder.subtotal,
      discountAmount: _confirmedOrder.discountAmount,
      payableAmount: _confirmedOrder.payableAmount,
      cancelledAt: null,
      paymentPublicId: _confirmedOrder.paymentPublicId,
      paymentStatus: _confirmedOrder.paymentStatus,
      gatewayPaymentId: null,
      deliveryPublicId: null,
      deliveryReferenceNumber: null,
      deliveryStatus: null,
    );
    await _pumpSuccessScreen(
      tester,
      payment: paymentFrom(successPaymentJson()),
      orderState: OrderState(selectedOrder: deliveryLessOrder),
    );
    await tester.pumpAndSettle();
    expect(find.text('Track delivery'), findsNothing);
    expect(find.text('Delivery'), findsOneWidget);
  });

  testWidgets('Continue Shopping goes to the catalogue', (tester) async {
    _useSurface(tester, const Size(1024, 1600));
    final router = _resultRouter();
    addTearDown(router.dispose);
    final container = ProviderContainer(
      overrides: [
        authRepositoryProvider.overrideWithValue(
          _AuthenticatedAuthRepository(),
        ),
        paymentControllerProvider.overrideWith(
          () => _SeededPaymentController(
            paymentFrom(successPaymentJson()),
          ),
        ),
        orderControllerProvider.overrideWith(
          () => _SeededOrderController(
            _confirmedOrderState,
          ),
        ),
      ],
    );
    addTearDown(container.dispose);
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: MaterialApp.router(routerConfig: router),
      ),
    );
    await tester.pumpAndSettle();

    await tester.ensureVisible(find.text('Continue Shopping'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Continue Shopping'));
    await tester.pumpAndSettle();
    expect(
      router.routerDelegate.currentConfiguration.uri.path,
      '/catalogue',
    );
  });

  testWidgets('Go to Home goes home', (tester) async {
    _useSurface(tester, const Size(1024, 1600));
    final router = _resultRouter();
    addTearDown(router.dispose);
    final container = ProviderContainer(
      overrides: [
        authRepositoryProvider.overrideWithValue(
          _AuthenticatedAuthRepository(),
        ),
        paymentControllerProvider.overrideWith(
          () => _SeededPaymentController(
            paymentFrom(successPaymentJson()),
          ),
        ),
        orderControllerProvider.overrideWith(
          () => _SeededOrderController(
            _confirmedOrderState,
          ),
        ),
      ],
    );
    addTearDown(container.dispose);
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: MaterialApp.router(routerConfig: router),
      ),
    );
    await tester.pumpAndSettle();

    await tester.ensureVisible(find.text('Go to Home'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Go to Home'));
    await tester.pumpAndSettle();
    expect(router.routerDelegate.currentConfiguration.uri.path, '/home');
  });

  testWidgets('successful subscription payment never shows order success UI', (
    tester,
  ) async {
    await _pumpSuccessScreen(
      tester,
      payment: paymentFrom(
        successPaymentJson(orderId: null, orderNumber: null, subscriptionId: 'subscription-1'),
      ),
      orderState: const OrderState(),
    );
    await tester.pumpAndSettle();

    expect(find.text('Order Success'), findsNothing);
    expect(find.byType(DoodhSectionCard), findsWidgets);
    expect(find.text('View Subscription'), findsOneWidget);
    expect(find.text('Continue Shopping'), findsOneWidget);
  });

  testWidgets('pending payment keeps the pending flow and is not styled as success', (
    tester,
  ) async {
    await _pumpSuccessScreen(
      tester,
      payment: paymentFrom({
        ...successPaymentJson(),
        'status': 'Pending',
        'verifiedAtUtc': null,
      }),
      orderState: _confirmedOrderState,
    );
    await tester.pumpAndSettle();

    expect(find.text('Verification pending'), findsNWidgets(2));
    expect(find.text('Order Success'), findsNothing);
    expect(find.text('Payment successful'), findsNothing);
    expect(
      find.bySemanticsLabel(
        'Payment confirmed by DoodhDirect. Payment successful. '
        '₹90.00 was verified for order DD-000001.',
      ),
      findsNothing,
    );
    expect(find.text('Check status'), findsOneWidget);
    expect(find.text('Continue Razorpay payment'), findsOneWidget);
  });

  testWidgets('failed payment keeps the terminal failure flow', (tester) async {
    await _pumpSuccessScreen(
      tester,
      payment: paymentFrom({
        ...successPaymentJson(),
        'status': 'Failed',
        'failureMessage': 'Gateway declined the payment.',
      }),
      orderState: _confirmedOrderState,
    );
    await tester.pumpAndSettle();

    expect(find.text('Payment failed'), findsNWidgets(2));
    expect(find.text('Gateway declined the payment.'), findsOneWidget);
    expect(find.text('Order Success'), findsNothing);
    expect(find.text('Payment successful'), findsNothing);
  });

  testWidgets('expired payment keeps the expired flow', (tester) async {
    await _pumpSuccessScreen(
      tester,
      payment: paymentFrom({...successPaymentJson(), 'status': 'Expired'}),
      orderState: _confirmedOrderState,
    );
    await tester.pumpAndSettle();

    expect(find.text('Payment expired'), findsNWidgets(2));
    expect(find.text('Order Success'), findsNothing);
    expect(find.text('Continue Shopping'), findsNothing);
  });

  testWidgets('unloaded payment shows the existing error state with retry', (
    tester,
  ) async {
    await _pumpSuccessScreen(
      tester,
      payment: paymentFrom(successPaymentJson()),
      paymentRepository: _FailingPaymentRepository(),
      useEmptyPayment: true,
    );
    await tester.pumpAndSettle();

    expect(find.byType(ErrorStatePanel), findsOneWidget);
    expect(find.text('Payment could not be loaded.'), findsOneWidget);
    expect(find.text('Payment successful'), findsNothing);
    // Once the session settles, Retry reaches the repository and surfaces the
    // server error message through the same state panel.
    await tester.tap(find.text('Retry'));
    await tester.pumpAndSettle();
    expect(find.text('boom'), findsOneWidget);
  });

  testWidgets('a failed refresh on a known payment surfaces the error banner', (
    tester,
  ) async {
    await _pumpSuccessScreen(
      tester,
      payment: paymentFrom(successPaymentJson()),
      orderState: _confirmedOrderState,
      paymentControllerBuilder: () =>
          _FailingRefreshPaymentController(paymentFrom(successPaymentJson())),
    );
    await tester.pumpAndSettle();

    // The backend-verified payment stays authoritative; the refresh failure
    // is surfaced as a banner without questioning the confirmed state.
    expect(find.text('Payment successful'), findsOneWidget);
    expect(find.text('boom'), findsOneWidget);
  });

  testWidgets('compact layout renders without overflow', (tester) async {
    await tester.binding.setSurfaceSize(const Size(320, 640));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await _pumpSuccessScreen(
      tester,
      payment: paymentFrom(successPaymentJson()),
      orderState: _confirmedOrderState,
    );
    await tester.pumpAndSettle();

    expect(tester.takeException(), isNull);
    expect(find.text('Payment successful'), findsOneWidget);
    expect(find.text('View Order'), findsOneWidget);
  });

  testWidgets('wide layout constrains the composition to the reading width', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1440, 900));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await _pumpSuccessScreen(
      tester,
      payment: paymentFrom(successPaymentJson()),
      orderState: _confirmedOrderState,
    );
    await tester.pumpAndSettle();

    expect(tester.takeException(), isNull);
    final heroWidth = tester.getSize(find.byType(DoodhCard).first).width;
    expect(heroWidth, lessThanOrEqualTo(DoodhContentMax.form + 1));
    expect(find.text('Payment successful'), findsOneWidget);
  });

  testWidgets('primary action keeps the 48dp minimum touch target', (
    tester,
  ) async {
    await _pumpSuccessScreen(
      tester,
      payment: paymentFrom(successPaymentJson()),
      orderState: _confirmedOrderState,
    );
    await tester.pumpAndSettle();

    final primary = find.byWidgetPredicate(
      (widget) => widget is DoodhButton && widget.label == 'View Order',
    );
    expect(primary, findsOneWidget);
    expect(tester.getSize(primary).height, greaterThanOrEqualTo(48));
  });

  testWidgets(
    'quick-action chips keep the existing labels and route behavior',
    (tester) async {
      _useSurface(tester, const Size(1024, 1600));
      final router = _resultRouter();
      addTearDown(router.dispose);
      final container = ProviderContainer(
        overrides: [
          authRepositoryProvider.overrideWithValue(
            _AuthenticatedAuthRepository(),
          ),
          paymentControllerProvider.overrideWith(
            () => _SeededPaymentController(
              paymentFrom(successPaymentJson()),
            ),
          ),
          orderControllerProvider.overrideWith(
            () => _SeededOrderController(
              _confirmedOrderState,
            ),
          ),
        ],
      );
      addTearDown(container.dispose);
      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: container,
          child: MaterialApp.router(routerConfig: router),
        ),
      );
      await tester.pumpAndSettle();

      // The compact chip row carries the same existing actions; the tertiary
      // Go to Home stays a text action below it.
      final quickActions = find.byKey(
        const ValueKey('order-success-quick-actions'),
      );
      expect(quickActions, findsOneWidget);
      expect(
        find.descendant(
          of: quickActions,
          matching: find.text('Track delivery'),
        ),
        findsOneWidget,
      );
      expect(
        find.descendant(
          of: quickActions,
          matching: find.text('Continue Shopping'),
        ),
        findsOneWidget,
      );
      expect(
        find.descendant(of: quickActions, matching: find.text('Go to Home')),
        findsNothing,
      );
      expect(find.text('Go to Home'), findsOneWidget);

      // Tapping a chip keeps its existing route.
      await tester.tap(find.text('Continue Shopping'));
      await tester.pumpAndSettle();
      expect(
        router.routerDelegate.currentConfiguration.uri.path,
        '/catalogue',
      );
    },
  );
}
