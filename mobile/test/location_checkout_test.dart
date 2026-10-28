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
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

/// Outside-radius eligibility at checkout: the backend rejection surfaces
/// verbatim in the error banner (naming the offending SKU), nothing is
/// ordered, the cart is preserved, and a new address/cart selection refreshes
/// eligibility. The client flag is display-only — the server decides.
void main() {
  group('outside-radius checkout', () {    testWidgets('backend ineligibility surfaces with the offending SKU', (
      tester,
    ) async {
      final orderRepository = _FailingPreviewOrderRepository();
      final container = await _pumpCheckout(
        tester,
        orderRepository: orderRepository,
        quantity: 2,
      );

      // Drive the authoritative preview through the controller (the action
      // button flow is covered by the checkout screen suite); the display
      // assertion below is what this test owns.
      final controller = container.read(orderControllerProvider.notifier);
      controller.selectSavedAddress(_addressHome.publicId);
      await tester.pump(const Duration(milliseconds: 1));
      expect(await controller.previewFor(_addressHome.publicId), isFalse);
      await tester.pumpAndSettle();

      expect(orderRepository.previewCalls, 1);
      expect(orderRepository.createCalls, 0);
      expect(
        find.textContaining(
          'outside every branch service radius',
          findRichText: true,
        ),
        findsOneWidget,
      );
      expect(find.textContaining('MILK-1L', findRichText: true), findsOneWidget);
      // The cart is preserved: nothing was silently removed.
      expect(find.text('Whole Milk'), findsOneWidget);
      expect(find.text('Place order', skipOffstage: false), findsNothing);
    });

    test('selecting another address clears the error and re-checks', () async {
      final orderRepository = _FailingPreviewOrderRepository();
      final container = await _authContainer(
        orderRepository: orderRepository,
      );
      addTearDown(container.dispose);

      final controller = container.read(orderControllerProvider.notifier);
      controller.setCartItem(_product, 2);
      controller.selectSavedAddress(_addressHome.publicId);
      await _flushAsync(null);
      expect(await controller.previewFor(_addressHome.publicId), isFalse);
      expect(
        container.read(orderControllerProvider).errorMessage,
        contains('outside every branch service radius'),
      );

      controller.selectSavedAddress(_addressOffice.publicId);
      await _flushAsync(null);
      expect(
        container.read(orderControllerProvider).errorMessage,
        isNull,
      );
      expect(container.read(orderControllerProvider).preview, isNull);
    });
  });
}

/// Yields to pending provider microtasks. Widget tests run inside a
/// fake-async clock where zero-duration timers never fire unless the tester
/// pumps, so a real [WidgetTester] frame is used there; pure `test` bodies
/// keep using the event queue.
Future<void> _flushAsync(WidgetTester? tester) async {
  if (tester == null) {
    await Future<void>.delayed(Duration.zero);
  } else {
    await tester.pump(const Duration(milliseconds: 1));
  }
}

const _outsideRadiusMessage =
    'The delivery address is outside every branch service radius, so the '
    'following products cannot be ordered: MILK-1L. Remove them from the '
    'cart or choose a different delivery address.';

class _FailingPreviewOrderRepository extends OrderRepository {
  _FailingPreviewOrderRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  int previewCalls = 0;
  int createCalls = 0;

  @override
  Future<CheckoutPreview> preview(
    String token,
    CheckoutRequest request,
  ) async {
    previewCalls += 1;
    throw const ApiException(
      422,
      'OUTSIDE_SERVICE_RADIUS',
      _outsideRadiusMessage,
      field: 'items',
    );
  }

  @override
  Future<OrderSummary> create(
    String token,
    CheckoutRequest request,
    String idempotencyKey,
  ) async {
    createCalls += 1;
    throw const ApiException(409, 'UNEXPECTED', 'must not be called');
  }

  @override
  Future<List<OrderSummary>> getMine(String token) async => const [];

  @override
  Future<OrderSummary> get(String token, String orderId) async =>
      throw UnimplementedError();
}

Future<ProviderContainer> _authContainer({
  _FailingPreviewOrderRepository? orderRepository,
  WidgetTester? tester,
}) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedAuthRepository()),
      guestCartStorageProvider.overrideWithValue(
        GuestCartStorage(storage: _FakeFlutterSecureStorage()),
      ),
      orderRepositoryProvider.overrideWithValue(
        orderRepository ?? _FailingPreviewOrderRepository(),
      ),
      customerRepositoryProvider.overrideWithValue(
        _FakeCustomerRepository(),
      ),
    ],
  );
  container.read(sessionControllerProvider);
  await _flushAsync(tester);
  container.read(orderControllerProvider);
  await _flushAsync(tester);
  return container;
}

Future<ProviderContainer> _pumpCheckout(
  WidgetTester tester, {
  _FailingPreviewOrderRepository? orderRepository,
  double? quantity,
}) async {
  tester.view.physicalSize = const Size(400, 2200);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  final container = await _authContainer(
    orderRepository: orderRepository,
    tester: tester,
  );
  addTearDown(container.dispose);

  if (quantity != null) {
    container
        .read(orderControllerProvider.notifier)
        .setCartItem(_product, quantity);
    await tester.pump(const Duration(milliseconds: 1));
  }

  final router = GoRouter(
    initialLocation: '/checkout',
    routes: [
      GoRoute(
        path: '/checkout',
        builder: (context, state) => const CheckoutScreen(),
      ),
    ],
  );
  addTearDown(router.dispose);

  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp.router(routerConfig: router),
    ),
  );
  await tester.pumpAndSettle();

  return container;
}

class _AuthenticatedAuthRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _session;

  @override
  Future<void> saveSession(AuthSession session) async {}

  @override
  Future<void> clear() async {}
}

class _FakeCustomerRepository extends CustomerRepository {
  _FakeCustomerRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<CustomerProfile> getProfile(String token) async => _profile;

  @override
  Future<List<CustomerAddress>> getAddresses(String token) async =>
      [_addressHome, _addressOffice];
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
  }) async => values[key];

  @override
  Future<bool> containsKey({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async => values.containsKey(key);

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
  allowOrderOutsideBranchRadius: false,
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
