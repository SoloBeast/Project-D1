import 'dart:async';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
import 'package:doodh_direct_mobile/features/customer/customer_repository.dart';
import 'package:doodh_direct_mobile/features/orders/guest_cart_storage.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_repository.dart';
import 'package:doodh_direct_mobile/features/orders/order_screens.dart';
import 'package:doodh_direct_mobile/features/payments/payment_controller.dart';
import 'package:doodh_direct_mobile/features/payments/payment_models.dart';
import 'package:doodh_direct_mobile/features/payments/payment_repository.dart';
import 'package:doodh_direct_mobile/features/payments/payment_screens.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_controller.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_models.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

void main() {
  group('checkout cart presentation', () {
    testWidgets('renders cart lines with unit price, quantity, and line total',
        (tester) async {
      final semantics = tester.ensureSemantics();
      try {
        await _pumpCheckout(tester, quantity: 2);

        expect(find.text('Whole Milk'), findsOneWidget);
        expect(find.text('₹60.00 per litre'), findsOneWidget);
        expect(find.text('2 litre · ₹60.00 each'), findsOneWidget);
        expect(find.text('Estimated line total'), findsOneWidget);
        expect(find.text('₹120.00'), findsWidgets);
        expect(find.text('Quantity'), findsOneWidget);
        expect(
          find.bySemanticsLabel(
            RegExp(
              r'Whole Milk, 2 litre, ₹60\.00 each, estimated line total ₹120\.00',
            ),
          ),
          findsOneWidget,
        );
      } finally {
        semantics.dispose();
      }
    });

    testWidgets('quantity controls adjust the line and clear it at zero',
        (tester) async {
      await _pumpCheckout(tester, quantity: 2);

      await tester.tap(find.byTooltip('Decrease quantity'));
      await tester.pumpAndSettle();
      expect(find.text('1 litre · ₹60.00 each'), findsOneWidget);
      expect(find.text('₹60.00'), findsOneWidget);

      await tester.tap(find.byTooltip('Increase quantity'));
      await tester.pumpAndSettle();
      expect(find.text('2 litre · ₹60.00 each'), findsOneWidget);
      expect(find.text('₹120.00'), findsOneWidget);

      await tester.tap(find.byTooltip('Decrease quantity'));
      await tester.pumpAndSettle();
      await tester.tap(find.byTooltip('Decrease quantity'));
      await tester.pumpAndSettle();

      expect(find.text('Your cart is empty'), findsOneWidget);
      expect(find.text('Continue Shopping'), findsWidgets);
    });

    testWidgets('remove action drops the line from the cart', (tester) async {
      await _pumpCheckout(tester, quantity: 2);

      await tester.tap(find.byTooltip('Remove Whole Milk'));
      await tester.pumpAndSettle();

      expect(find.text('Your cart is empty'), findsOneWidget);
      expect(find.text('Whole Milk'), findsNothing);
    });
  });

  group('checkout address selection', () {
    testWidgets('presents saved addresses with the default chip',
        (tester) async {
      await _pumpCheckout(
        tester,
        quantity: 2,
        customerRepository: _FakeCustomerRepository(
          addresses: [_addressHome, _addressOffice],
        ),
      );

      expect(find.text('Saved address'), findsOneWidget);
      expect(find.text('Choose where this order should arrive'), findsOneWidget);
      expect(find.text('Default'), findsOneWidget);
      // The selected address is shown in the summary block and as the closed
      // dropdown's current value.
      expect(find.text('Home'), findsOneWidget);
      expect(find.text('Home — Bengaluru'), findsOneWidget);
      expect(
        find.textContaining('12 Market Road, Indiranagar, Bengaluru'),
        findsOneWidget,
      );
      expect(
        find.text('Contact: Asha Sharma · 1234567890'),
        findsOneWidget,
      );

      // Opening the picker lists every saved address.
      await tester.tap(find.byType(DropdownButtonFormField<String>));
      await tester.pumpAndSettle();
      expect(find.text('Home — Bengaluru'), findsWidgets);
      expect(find.text('Office — Mysuru'), findsWidgets);
    });

    testWidgets('selecting another saved address refreshes the summary block',
        (tester) async {
      await _pumpCheckout(
        tester,
        quantity: 2,
        customerRepository: _FakeCustomerRepository(
          addresses: [_addressHome, _addressOffice],
        ),
      );

      await tester.tap(find.byType(DropdownButtonFormField<String>));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Office — Mysuru').last);
      await tester.pumpAndSettle();

      expect(find.text('Office'), findsOneWidget);
      expect(
        find.textContaining('5 Lake Road, Vijayanagar, Mysuru'),
        findsOneWidget,
      );
    });

    testWidgets('renders a one-time manual address with an edit action',
        (tester) async {
      final semantics = tester.ensureSemantics();
      try {
        await _pumpCheckout(
          tester,
          quantity: 2,
          customerRepository: _FakeCustomerRepository(addresses: const []),
          manualAddress: _checkoutManualDraft,
        );

        expect(
          find.bySemanticsLabel(
            RegExp(r'One-time delivery address for Asha Sharma'),
          ),
          findsOneWidget,
        );
        expect(find.text('One-time address'), findsOneWidget);
        expect(find.byTooltip('Edit address'), findsOneWidget);
        expect(find.text('Contact: Asha Sharma · 1234567890'), findsOneWidget);
        expect(
          find.textContaining('9 Farm Lane, Whitefield, Bengaluru'),
          findsOneWidget,
        );
        expect(find.text('Saved address'), findsNothing);
      } finally {
        semantics.dispose();
      }
    });

    testWidgets('saving a new address selects it for the order',
        (tester) async {
      await _pumpCheckout(
        tester,
        quantity: 2,
        customerRepository: _FakeCustomerRepository(addresses: [_addressHome]),
      );

      await _startNewAddressFlow(tester);

      // The freshly saved profile address becomes the selected delivery
      // address: it is shown in the summary block and in the closed dropdown.
      expect(find.text('Farm'), findsOneWidget);
      expect(find.text('Farm — Bengaluru'), findsWidgets);
      expect(
        find.textContaining('9 Farm Lane, Whitefield, Bengaluru'),
        findsOneWidget,
      );
      expect(find.text('Default'), findsNothing);
    });

    testWidgets('falls back when the saved address cannot be selected',
        (tester) async {
      await _pumpCheckout(
        tester,
        quantity: 2,
        customerRepository: _FakeCustomerRepository(
          addresses: [_addressHome],
          persistCreatedAddress: false,
        ),
      );

      await _startNewAddressFlow(tester);

      expect(
        find.text('The saved address could not be selected. Try again.'),
        findsOneWidget,
      );

      // Drain the SnackBar dismissal timer so no pending timer is left behind.
      await tester.pumpAndSettle(const Duration(seconds: 5));
    });
  });

  group('checkout quote and totals', () {
    testWidgets('preview renders the server-authoritative quote',
        (tester) async {
      final semantics = tester.ensureSemantics();
      try {
        await _pumpCheckout(
          tester,
          quantity: 2,
          customerRepository:
              _FakeCustomerRepository(addresses: [_addressHome]),
        );

        expect(find.text('Awaiting preview'), findsOneWidget);

        await tester.tap(find.text('Preview order'));
        await tester.pumpAndSettle();

        expect(find.text('Server quote'), findsOneWidget);
        expect(find.text('Server verified'), findsOneWidget);
        expect(find.text('Whole Milk × 2'), findsOneWidget);
        expect(find.text('Subtotal'), findsOneWidget);
        // The subtotal matches the cart line total, so it appears in both the
        // cart card and the server quote.
        expect(find.text('₹120.00'), findsWidgets);
        expect(find.text('Discount'), findsOneWidget);
        expect(find.text('₹-10.00'), findsOneWidget);
        expect(find.text('Total payable'), findsWidgets); // summary row + sticky total
        expect(
          find.bySemanticsLabel(
            RegExp(
              r'Authoritative checkout quote for Home\. '
              r'Final amount payable ₹110\.00',
            ),
          ),
          findsOneWidget,
        );
        expect(
          find.bySemanticsLabel(
            RegExp(r'Success: Server verified'),
          ),
          findsOneWidget,
        );
      } finally {
        semantics.dispose();
      }
    });

    testWidgets('backend delivery information is shown once confirmed',
        (tester) async {
      await _pumpCheckout(
        tester,
        quantity: 2,
        customerRepository: _FakeCustomerRepository(addresses: [_addressHome]),
      );

      expect(find.text('Delivery information'), findsNothing);

      await tester.tap(find.text('Preview order'));
      await tester.pumpAndSettle();

      expect(find.text('Delivery information'), findsOneWidget);
      expect(find.text('Confirmed for this order'), findsOneWidget);
      expect(find.text('Delivering to'), findsOneWidget);
      expect(find.text('Fulfilling branch'), findsOneWidget);
      expect(find.text('Central Dairy'), findsOneWidget);
      expect(find.text('Distance'), findsOneWidget);
      expect(find.text('2.5 km'), findsOneWidget);
    });

    testWidgets('sticky action previews then places the order and navigates',
        (tester) async {
      final orderRepository = _FakeOrderRepository();
      final (_, router) = await _pumpCheckout(
        tester,
        quantity: 2,
        orderRepository: orderRepository,
        customerRepository: _FakeCustomerRepository(addresses: [_addressHome]),
      );

      expect(find.text('Preview order'), findsWidgets);
      expect(find.text('Total payable'), findsWidgets);

      await tester.tap(find.text('Preview order'));
      await tester.pumpAndSettle();

      expect(orderRepository.previewCalls, 1);
      expect(orderRepository.lastToken, 'customer-token');
      expect(orderRepository.lastRequest!.addressId, _addressHome.publicId);
      expect(orderRepository.lastRequest!.manualAddress, isNull);
      expect(find.text('Place order · ₹110.00'), findsOneWidget);

      await tester.tap(find.text('Place order · ₹110.00'));
      await tester.pumpAndSettle();

      expect(orderRepository.createCalls, 1);
      expect(orderRepository.lastIdempotencyKey, startsWith('mobile-'));
      expect(router.routerDelegate.currentConfiguration.uri.path,
          '/orders/order-1/payment');
      expect(find.text('Payment target order-1'), findsOneWidget);
    });

    testWidgets('uses a two-column layout on wide authenticated viewports',
        (tester) async {
      await _pumpCheckout(
        tester,
        quantity: 2,
        surfaceSize: const Size(1100, 2200),
      );

      final totalOffset = tester.getTopLeft(find.text('Total payable').first);
      final actionOffset = tester.getTopLeft(find.text('Preview order').first);

      // The wide sticky bar keeps the total and the primary action on the same
      // visual row - their vertical bands overlap - with the action to the
      // right of the total.
      const totalHeight = 20.0;
      expect(
        (actionOffset.dy - totalOffset.dy).abs(),
        lessThan(totalHeight * 2),
      );
      expect(actionOffset.dx, greaterThan(totalOffset.dx));
    });

    testWidgets('stacks the sticky action on a compact viewport',
        (tester) async {
      await _pumpCheckout(
        tester,
        quantity: 2,
        surfaceSize: const Size(400, 2200),
      );

      final totalOffset = tester.getTopLeft(find.text('Total payable').first);
      final actionOffset = tester.getTopLeft(find.text('Preview order').first);

      expect(actionOffset.dy, greaterThan(totalOffset.dy + 10));
    });
  });

  group('order controller checkout request', () {
    test('requestFor keeps the saved address and decimal quantities', () async {
      final orderRepository = _FakeOrderRepository();
      final container = await _authContainer(orderRepository: orderRepository);
      addTearDown(container.dispose);

      final controller = container.read(orderControllerProvider.notifier);
      controller.setCartItem(_product, 2.5);
      controller.selectSavedAddress(_addressHome.publicId);
      await pumpEventQueue();

      final request = controller.requestFor(_addressHome.publicId);
      expect(request, isNotNull);
      expect(request!.addressId, _addressHome.publicId);
      expect(request.manualAddress, isNull);
      expect(request.items, hasLength(1));
      expect(request.items.single.productId, _product.publicId);
      expect(request.items.single.quantity, 2.5);

      final previewed = await controller.previewFor(_addressHome.publicId);
      expect(previewed, isTrue);
      expect(orderRepository.previewCalls, 1);
      expect(orderRepository.lastToken, 'customer-token');
      expect(orderRepository.lastRequest!.addressId, _addressHome.publicId);
    });

    test('requestFor keeps a manual unsaved address without an id', () async {
      final container = await _authContainer();
      addTearDown(container.dispose);

      final controller = container.read(orderControllerProvider.notifier);
      controller.setCartItem(_product, 2);
      controller.selectManualAddress(_checkoutManualDraft);
      await pumpEventQueue();

      final request = controller.requestFor(_checkoutManualDraft);
      expect(request, isNotNull);
      expect(request!.addressId, isNull);
      expect(request.manualAddress, isNotNull);
      expect(request.manualAddress!.addressLine1, '9 Farm Lane');
      expect(request.items.single.quantity, 2);
    });

    test('each place-order attempt sends a fresh idempotency key', () async {
      final orderRepository = _FakeOrderRepository();
      final container = await _authContainer(orderRepository: orderRepository);
      addTearDown(container.dispose);

      final controller = container.read(orderControllerProvider.notifier);
      controller.setCartItem(_product, 2);
      controller.selectSavedAddress(_addressHome.publicId);
      await pumpEventQueue();

      final first = await controller.create(_addressHome.publicId);
      final second = await controller.create(_addressHome.publicId);

      expect(first, isNotNull);
      expect(second, isNotNull);
      expect(orderRepository.createCalls, 2);
      expect(orderRepository.idempotencyKeys, hasLength(2));
      expect(
        orderRepository.idempotencyKeys.every((key) => key.startsWith('mobile-')),
        isTrue,
      );
    });
  });

  group('payment method selection', () {
    testWidgets('wallet is selected by default and shows its balance',
        (tester) async {
      final semantics = tester.ensureSemantics();
      try {
        await _pumpPaymentMethod(tester);

        expect(find.text('Ready to pay'), findsOneWidget);
        expect(find.text('Amount due: ₹60.00'), findsOneWidget);
        expect(find.text('Choose how to pay'), findsOneWidget);
        expect(find.text('DoodhDirect Wallet balance'), findsOneWidget);
        expect(find.text('₹410.50'), findsOneWidget);
        expect(find.text('Available balance: ₹410.50'), findsOneWidget);
        expect(
          find.text('UPI, cards, netbanking, and supported wallets'),
          findsOneWidget,
        );
        expect(
          find.bySemanticsLabel(
            RegExp(
              r'DoodhDirect Wallet\. Available balance: ₹410\.50\. Selected',
            ),
          ),
          findsOneWidget,
        );
        expect(find.text('Pay ₹60.00'), findsOneWidget);
      } finally {
        semantics.dispose();
      }
    });

    testWidgets('selecting Razorpay updates the chosen method', (tester) async {
      final semantics = tester.ensureSemantics();
      try {
        await _pumpPaymentMethod(tester);

        await tester.tap(find.text('Razorpay'));
        await tester.pumpAndSettle();

        expect(find.text('DoodhDirect Wallet balance'), findsNothing);
        expect(
          find.bySemanticsLabel(
            RegExp(
              r'Razorpay\. UPI, cards, netbanking, and supported wallets\. '
              r'Selected',
            ),
          ),
          findsOneWidget,
        );
      } finally {
        semantics.dispose();
      }
    });

    testWidgets('insufficient wallet balance surfaces the server message',
        (tester) async {
      final paymentRepository = _FakePaymentRepository(
        createError: ApiException(
          422,
          'INSUFFICIENT_WALLET_BALANCE',
          'Your wallet balance is too low for this order.',
        ),
      );
      await _pumpPaymentMethod(tester, paymentRepository: paymentRepository);

      await tester.tap(find.text('Pay ₹60.00'));
      await tester.pumpAndSettle();

      expect(
        find.text('Your wallet balance is too low for this order.'),
        findsOneWidget,
      );
      expect(paymentRepository.lastMethod, PaymentMethod.wallet);
      expect(paymentRepository.lastOrderId, 'order-1');
    });

    testWidgets('shows processing state while the payment is created',
        (tester) async {
      final paymentRepository = _FakePaymentRepository(neverCompletes: true);
      await _pumpPaymentMethod(tester, paymentRepository: paymentRepository);

      await tester.tap(find.text('Pay ₹60.00'));
      await tester.pump();

      expect(find.text('Processing...'), findsOneWidget);
      expect(find.text('Pay ₹60.00'), findsNothing);
    });

    testWidgets('navigates to the payment result after creation',
        (tester) async {
      final paymentRepository = _FakePaymentRepository(
        createResult: PaymentDetails.fromJson(_walletPaymentJson()),
      );
      final router = await _pumpPaymentMethod(
        tester,
        paymentRepository: paymentRepository,
      );

      await tester.tap(find.text('Pay ₹60.00'));
      await tester.pumpAndSettle();

      expect(paymentRepository.createCalls, 1);
      expect(
        router.routerDelegate.currentConfiguration.uri.path,
        '/payments/payment-1/result',
      );
      expect(find.text('Result target payment-1'), findsOneWidget);
    });
  });
}

// ---------------------------------------------------------------------------
// Harness
// ---------------------------------------------------------------------------

Future<void> _startNewAddressFlow(WidgetTester tester) async {
  await tester.tap(find.text('Enter New Address'));
  await tester.pumpAndSettle();

  await tester.tap(find.text('Provide draft'));
  await tester.pumpAndSettle();

  expect(find.text('Save this address to your profile?'), findsOneWidget);
  await tester.tap(find.text('Save Address'));
  await tester.pumpAndSettle();

  expect(find.text('Name this address'), findsOneWidget);
  await tester.enterText(find.byType(TextFormField), 'Farm');
  await tester.tap(find.text('Continue'));
  await tester.pumpAndSettle();
}

/// Drains pending provider microtasks.
///
/// Widget tests run inside a fake-async clock where [pumpEventQueue]'s
/// zero-duration timers never fire unless the tester pumps, so a real
/// [WidgetTester] is used to advance a frame instead. Pure `test` bodies keep
/// using [pumpEventQueue].
Future<void> _flushAsync(WidgetTester? tester) async {
  if (tester == null) {
    await pumpEventQueue();
  } else {
    await tester.pump(const Duration(milliseconds: 1));
  }
}

Future<ProviderContainer> _authContainer({
  _FakeOrderRepository? orderRepository,
  _FakeCustomerRepository? customerRepository,
  WidgetTester? tester,
}) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedAuthRepository()),
      guestCartStorageProvider.overrideWithValue(
        GuestCartStorage(storage: _FakeFlutterSecureStorage()),
      ),
      orderRepositoryProvider.overrideWithValue(
        orderRepository ?? _FakeOrderRepository(),
      ),
      customerRepositoryProvider.overrideWithValue(
        customerRepository ?? _FakeCustomerRepository(),
      ),
    ],
  );
  container.read(sessionControllerProvider);
  await _flushAsync(tester);
  // Instantiate the order controller so the guest-cart restore microtask runs
  // before any test seeds the cart.
  container.read(orderControllerProvider);
  await _flushAsync(tester);
  return container;
}

Future<(ProviderContainer, GoRouter)> _pumpCheckout(
  WidgetTester tester, {
  _FakeOrderRepository? orderRepository,
  _FakeCustomerRepository? customerRepository,
  double? quantity,
  CheckoutAddressDraft? manualAddress,
  Size surfaceSize = const Size(400, 2200),
}) async {
  // The view size drives both the render surface and MediaQuery, so the
  // responsive branches - which read MediaQuery.sizeOf(context).width - see the
  // requested logical width. `setSurfaceSize` only resizes the render view and
  // leaves MediaQuery at the 800x600 test default.
  tester.view.physicalSize = surfaceSize;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  final container = await _authContainer(
    orderRepository: orderRepository,
    customerRepository: customerRepository,
    tester: tester,
  );
  addTearDown(container.dispose);

  if (quantity != null || manualAddress != null) {
    final controller = container.read(orderControllerProvider.notifier);
    if (quantity != null) {
      controller.setCartItem(_product, quantity);
    }
    if (manualAddress != null) {
      controller.selectManualAddress(manualAddress);
    }
    await tester.pump(const Duration(milliseconds: 1));
  }

  final router = _checkoutRouter();
  addTearDown(router.dispose);

  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp.router(routerConfig: router),
    ),
  );
  await tester.pumpAndSettle();

  return (container, router);
}

GoRouter _checkoutRouter() => GoRouter(
      initialLocation: '/checkout',
      routes: [
        GoRoute(
          path: '/checkout',
          builder: (context, state) => const CheckoutScreen(),
        ),
        GoRoute(
          path: '/catalogue',
          builder: (context, state) =>
              const Scaffold(body: Text('Catalogue target')),
        ),
        GoRoute(
          path: '/login',
          builder: (context, state) => const Scaffold(body: Text('Login target')),
        ),
        GoRoute(
          path: '/checkout/address/new',
          builder: (context, state) => Scaffold(
            body: Center(
              child: TextButton(
                onPressed: () => context.pop<AddressDraft>(_manualDraft),
                child: const Text('Provide draft'),
              ),
            ),
          ),
        ),
        GoRoute(
          path: '/orders/:orderId/payment',
          builder: (context, state) => Scaffold(
            body: Text('Payment target ${state.pathParameters['orderId']}'),
          ),
        ),
        GoRoute(
          path: '/payments/:paymentId/result',
          builder: (context, state) => Scaffold(
            body: Text('Result target ${state.pathParameters['paymentId']}'),
          ),
        ),
      ],
    );

Future<GoRouter> _pumpPaymentMethod(
  WidgetTester tester, {
  _FakePaymentRepository? paymentRepository,
}) async {
  await tester.binding.setSurfaceSize(const Size(420, 1400));
  addTearDown(() => tester.binding.setSurfaceSize(null));

  final router = GoRouter(
    initialLocation: '/orders/order-1/payment',
    routes: [
      GoRoute(
        path: '/orders/:orderId/payment',
        builder: (context, state) => PaymentMethodScreen(
          orderId: state.pathParameters['orderId']!,
          initialOrder: _orderSummary('order-1'),
        ),
      ),
      GoRoute(
        path: '/payments/:paymentId/result',
        builder: (context, state) => Scaffold(
          body: Text('Result target ${state.pathParameters['paymentId']}'),
        ),
      ),
    ],
  );
  addTearDown(router.dispose);

  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedAuthRepository()),
      guestCartStorageProvider.overrideWithValue(
        GuestCartStorage(storage: _FakeFlutterSecureStorage()),
      ),
      paymentRepositoryProvider.overrideWithValue(
        paymentRepository ?? _FakePaymentRepository(),
      ),
      walletControllerProvider.overrideWith(
        () => _SeededWalletController(_walletState),
      ),
    ],
  );
  addTearDown(container.dispose);

  // Restore the session before the screen mounts: the payment controller only
  // loads capabilities with an access token, and an empty capability list keeps
  // an indeterminate progress bar on screen that pumpAndSettle can never settle.
  container.read(sessionControllerProvider);
  await tester.pump(const Duration(milliseconds: 1));
  container.read(paymentControllerProvider);
  await tester.pump(const Duration(milliseconds: 1));

  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp.router(routerConfig: router),
    ),
  );
  await tester.pumpAndSettle();

  return router;
}

// ---------------------------------------------------------------------------
// Fakes
// ---------------------------------------------------------------------------

class _AuthenticatedAuthRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _session;

  @override
  Future<void> saveSession(AuthSession session) async {}

  @override
  Future<void> clear() async {}
}

class _FakeOrderRepository extends OrderRepository {
  _FakeOrderRepository()
      : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  int previewCalls = 0;
  int createCalls = 0;
  String? lastToken;
  CheckoutRequest? lastRequest;
  String? lastIdempotencyKey;
  final List<String> idempotencyKeys = [];

  @override
  Future<CheckoutPreview> preview(String token, CheckoutRequest request) async {
    previewCalls += 1;
    lastToken = token;
    lastRequest = request;
    return _previewFrom(request);
  }

  @override
  Future<OrderSummary> create(
    String token,
    CheckoutRequest request,
    String idempotencyKey,
  ) async {
    createCalls += 1;
    lastToken = token;
    lastRequest = request;
    lastIdempotencyKey = idempotencyKey;
    idempotencyKeys.add(idempotencyKey);
    return _orderSummary('order-1');
  }

  @override
  Future<List<OrderSummary>> getMine(String token) async => const [];

  @override
  Future<OrderSummary> get(String token, String orderId) async =>
      _orderSummary(orderId);
}

class _FakeCustomerRepository extends CustomerRepository {
  _FakeCustomerRepository({
    List<CustomerAddress>? addresses,
    this.persistCreatedAddress = true,
  })  : addresses = [...?addresses],
        super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final List<CustomerAddress> addresses;
  final bool persistCreatedAddress;
  int createCalls = 0;
  AddressDraft? lastCreatedDraft;

  @override
  Future<CustomerProfile> getProfile(String token) async => _profile;

  @override
  Future<List<CustomerAddress>> getAddresses(String token) async =>
      List<CustomerAddress>.unmodifiable(addresses);

  @override
  Future<CustomerAddress> createAddress(String token, AddressDraft request) async {
    createCalls += 1;
    lastCreatedDraft = request;
    final created = CustomerAddress(
      publicId: 'address-new',
      label: request.label,
      addressLine1: request.addressLine1,
      addressLine2: request.addressLine2,
      locality: request.locality,
      city: request.city,
      state: request.state,
      pinCode: request.pinCode,
      landmark: request.landmark,
      deliveryInstructions: request.deliveryInstructions,
      contactName: request.contactName,
      contactMobile: request.contactMobile,
      latitude: request.latitude,
      longitude: request.longitude,
      isDefault: request.isDefault,
      isActive: true,
    );
    if (persistCreatedAddress) {
      addresses.add(created);
    }
    return created;
  }
}

class _FakePaymentRepository extends PaymentRepository {
  _FakePaymentRepository({
    this.createResult,
    this.createError,
    this.neverCompletes = false,
  }) : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final PaymentDetails? createResult;
  final Object? createError;
  final bool neverCompletes;
  int createCalls = 0;
  String? lastOrderId;
  PaymentMethod? lastMethod;

  @override
  Future<List<PaymentCapability>> getCapabilities(String token) async => const [
        PaymentCapability(
          method: PaymentMethod.wallet,
          provider: 'Wallet',
          label: 'DoodhDirect Wallet',
          isAvailable: true,
          unavailableReason: null,
        ),
        PaymentCapability(
          method: PaymentMethod.razorpay,
          provider: 'Razorpay',
          label: 'Razorpay',
          isAvailable: true,
          unavailableReason: null,
        ),
      ];

  @override
  Future<PaymentDetails> create({
    required String token,
    required String orderId,
    required PaymentMethod method,
    required String idempotencyKey,
  }) {
    createCalls += 1;
    lastOrderId = orderId;
    lastMethod = method;
    if (neverCompletes) {
      return Completer<PaymentDetails>().future;
    }
    final error = createError;
    if (error != null) {
      return Future<PaymentDetails>.error(error);
    }
    return Future<PaymentDetails>.value(createResult!);
  }
}

class _SeededWalletController extends WalletController {
  _SeededWalletController(this.initialState);

  final WalletState initialState;

  @override
  WalletState build() => initialState;

  @override
  Future<void> load() async {}
}

class _FakeFlutterSecureStorage extends FlutterSecureStorage {
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
  }) async =>
      values[key];

  @override
  Future<bool> containsKey({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async =>
      values.containsKey(key);

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

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

const _product = CatalogueProduct(
  publicId: '00000000-0000-0000-0000-000000000030',
  sku: 'MILK-1L',
  name: 'Whole Milk',
  description: 'Fresh whole milk.',
  category: ProductCategory(
    publicId: '00000000-0000-0000-0000-000000000040',
    code: 'MILK',
    name: 'Milk',
    description: 'Fresh dairy products.',
    isActive: true,
  ),
  unitOfMeasure: 'litre',
  price: 60,
  isActive: true,
  branchAvailability: [
    BranchAvailability(
      branchId: '00000000-0000-0000-0000-000000000050',
      branchCode: 'CENTRAL',
      branchName: 'Central Dairy',
      isAvailable: true,
      maxDailyQuantity: 10,
    ),
  ],
);

const _profile = CustomerProfile(
  publicId: 'customer-1',
  firstName: 'Asha',
  lastName: 'Sharma',
  dateOfBirth: null,
  gender: null,
  alternateMobile: null,
  customerNumber: 'DD-C-0001',
);

const _addressHome = CustomerAddress(
  publicId: 'address-home',
  label: 'Home',
  addressLine1: '12 Market Road',
  addressLine2: 'Near Market',
  locality: 'Indiranagar',
  city: 'Bengaluru',
  state: 'Karnataka',
  pinCode: '560038',
  landmark: null,
  deliveryInstructions: null,
  contactName: 'Asha Sharma',
  contactMobile: '1234567890',
  latitude: 12.9716,
  longitude: 77.5946,
  isDefault: true,
  isActive: true,
);

const _addressOffice = CustomerAddress(
  publicId: 'address-office',
  label: 'Office',
  addressLine1: '5 Lake Road',
  addressLine2: null,
  locality: 'Vijayanagar',
  city: 'Mysuru',
  state: 'Karnataka',
  pinCode: '570001',
  landmark: null,
  deliveryInstructions: null,
  contactName: 'Asha Sharma',
  contactMobile: '1234567890',
  latitude: 12.2958,
  longitude: 76.6394,
  isDefault: false,
  isActive: true,
);

const _manualDraft = AddressDraft(
  label: '',
  addressLine1: '9 Farm Lane',
  locality: 'Whitefield',
  city: 'Bengaluru',
  state: 'Karnataka',
  pinCode: '560066',
  contactName: 'Asha Sharma',
  contactMobile: '1234567890',
  latitude: 12.9698,
  longitude: 77.7500,
  isDefault: false,
);

const _checkoutManualDraft = CheckoutAddressDraft(
  label: '',
  addressLine1: '9 Farm Lane',
  locality: 'Whitefield',
  city: 'Bengaluru',
  state: 'Karnataka',
  pinCode: '560066',
  contactName: 'Asha Sharma',
  contactMobile: '1234567890',
  latitude: 12.9698,
  longitude: 77.7500,
);

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'customer-1',
    displayName: 'Asha Sharma',
    email: 'asha@example.test',
    mobile: '9876543210',
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
  ),
  accessToken: 'customer-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

final _walletState = WalletState(
  wallet: WalletDetails(
    publicId: 'wallet-1',
    balance: 410.5,
    currency: 'INR',
    createdAt: DateTime.utc(2026, 8, 16, 5, 30),
    updatedAt: DateTime.utc(2026, 8, 16, 5, 35),
  ),
);

Map<String, dynamic> _walletPaymentJson({String status = 'Success'}) => {
      'publicId': 'payment-1',
      'orderId': 'order-1',
      'subscriptionId': null,
      'orderNumber': 'DD-000001',
      'method': 'Wallet',
      'provider': 'Wallet',
      'status': status,
      'amount': 60,
      'refundedAmount': 0,
      'currency': 'INR',
      'gatewayOrderId': null,
      'gatewayPaymentId': null,
      'gatewayKeyId': null,
      'failureCode': null,
      'failureMessage': null,
      'expiresAtUtc': '2026-08-16T01:00:00Z',
      'verifiedAtUtc': null,
      'createdAtUtc': '2026-08-16T00:00:00Z',
    };

CheckoutPreview _previewFrom(CheckoutRequest request) => CheckoutPreview(
      addressId: request.addressId ?? 'manual-address',
      addressLabel: 'Home',
      addressLine1: '12 Market Road',
      addressLine2: 'Near Market',
      locality: 'Indiranagar',
      city: 'Bengaluru',
      state: 'Karnataka',
      pinCode: '560038',
      contactName: 'Asha Sharma',
      contactMobile: '1234567890',
      branchId: 'branch-1',
      branchCode: 'CENTRAL',
      branchName: 'Central Dairy',
      distanceKm: 2.5,
      items: [
        for (final item in request.items)
          CheckoutLine(
            productId: item.productId,
            productName: 'Whole Milk',
            sku: 'MILK-1L',
            unitOfMeasure: 'litre',
            quantity: item.quantity,
            unitPrice: 60,
            lineTotal: 60 * item.quantity,
          ),
      ],
      subtotal: 120,
      discountAmount: 10,
      payableAmount: 110,
    );

OrderSummary _orderSummary(String orderId) => OrderSummary(
      publicId: orderId,
      orderNumber: 'DD-000001',
      type: 'OneTime',
      status: 'Confirmed',
      createdAt: DateTime.utc(2026, 8, 16, 9),
      addressLabel: 'Home',
      city: 'Bengaluru',
      branchName: 'Main Branch',
      items: const [
        OrderItem(
          productId: '00000000-0000-0000-0000-000000000030',
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
      cancelledAt: null,
      paymentPublicId: 'payment-1',
      paymentStatus: 'Captured',
      gatewayPaymentId: null,
      deliveryPublicId: null,
      deliveryReferenceNumber: null,
      deliveryStatus: null,
    );
