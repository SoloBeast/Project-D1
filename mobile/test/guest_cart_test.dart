import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/orders/guest_cart_storage.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('guest cart persistence', () {
    test('guest cart mutations persist under the guest storage key', () async {
      final storage = _FakeFlutterSecureStorage();
      final container = await _containerWith(storage: storage);
      addTearDown(container.dispose);
      container.read(sessionControllerProvider.notifier).enterAsGuest();
      await pumpEventQueue();

      final controller = container.read(orderControllerProvider.notifier);
      controller.setCartItem(_product, 2);
      await pumpEventQueue();

      final snapshot = await GuestCartStorage(storage: storage).read();
      expect(snapshot.userId, isNull);
      expect(snapshot.items, hasLength(1));
      expect(snapshot.items.single.product.publicId, _product.publicId);
      expect(snapshot.items.single.quantity, 2);

      controller.updateCartQuantity(_product.publicId, 3);
      await pumpEventQueue();
      final updated = await GuestCartStorage(storage: storage).read();
      expect(updated.items.single.quantity, 3);

      controller.removeCartItem(_product.publicId);
      await pumpEventQueue();
      final removed = await GuestCartStorage(storage: storage).read();
      expect(removed.items, isEmpty);
    });

    test('guest to sign-in re-scopes the snapshot and preserves the cart',
        () async {
      final storage = _FakeFlutterSecureStorage();
      final container = await _containerWith(storage: storage);
      addTearDown(container.dispose);
      container.read(sessionControllerProvider.notifier).enterAsGuest();
      await pumpEventQueue();

      final controller = container.read(orderControllerProvider.notifier);
      controller.setCartItem(_product, 2);
      await pumpEventQueue();

      await container
          .read(sessionControllerProvider.notifier)
          .establishSession(_session);
      await pumpEventQueue();

      final orderState = container.read(orderControllerProvider);
      expect(orderState.cart, hasLength(1));
      expect(orderState.cart.single.quantity, 2);

      final snapshot = await GuestCartStorage(storage: storage).read();
      expect(snapshot.userId, _session.user.publicUserId);
      expect(snapshot.items, hasLength(1));
      expect(snapshot.items.single.quantity, 2);
    });

    test('restarting as a guest restores the same cart', () async {
      final storage = _FakeFlutterSecureStorage();
      final first = await _containerWith(storage: storage);
      first.read(sessionControllerProvider.notifier).enterAsGuest();
      await pumpEventQueue();
      first.read(orderControllerProvider.notifier).setCartItem(_product, 2);
      await pumpEventQueue();
      first.dispose();

      final second = await _containerWith(storage: storage);
      addTearDown(second.dispose);
      second.read(sessionControllerProvider.notifier).enterAsGuest();
      await pumpEventQueue();

      // Instantiating the order controller registers the fire-immediately
      // session listener, which asynchronously adopts the persisted guest
      // snapshot. Pump so the restore completes before the immutable state
      // snapshot is captured.
      second.read(orderControllerProvider);
      await pumpEventQueue();

      final orderState = second.read(orderControllerProvider);
      expect(orderState.cart, hasLength(1));
      expect(orderState.cart.single.product.publicId, _product.publicId);
      expect(orderState.cart.single.quantity, 2);
    });

    test('restarting as a signed-in customer restores the user cart', () async {
      final storage = _FakeFlutterSecureStorage();
      await GuestCartStorage(storage: storage).write(
        userId: _session.user.publicUserId,
        items: const [OrderCartItem(product: _product, quantity: 2)],
      );

      final container = await _containerWith(
        storage: storage,
        restoreResult: _session,
      );
      addTearDown(container.dispose);
      container.read(orderControllerProvider);
      await pumpEventQueue();

      final orderState = container.read(orderControllerProvider);
      expect(orderState.cart, hasLength(1));
      expect(orderState.cart.single.quantity, 2);
    });

    test('a different user’s cart never leaks into the current identity',
        () async {
      final storage = _FakeFlutterSecureStorage();
      await GuestCartStorage(storage: storage).write(
        userId: 'customer-999',
        items: const [OrderCartItem(product: _product, quantity: 5)],
      );

      final container = await _containerWith(
        storage: storage,
        restoreResult: _session,
      );
      addTearDown(container.dispose);
      container.read(orderControllerProvider);
      await pumpEventQueue();

      final orderState = container.read(orderControllerProvider);
      expect(orderState.cart, isEmpty);
      final snapshot = await GuestCartStorage(storage: storage).read();
      expect(snapshot.items, isEmpty);
      expect(snapshot.userId, isNull);
    });

    test('a signed-in cart is never surfaced to a guest', () async {
      final storage = _FakeFlutterSecureStorage();
      await GuestCartStorage(storage: storage).write(
        userId: 'customer-1',
        items: const [OrderCartItem(product: _product, quantity: 5)],
      );

      final container = await _containerWith(storage: storage);
      addTearDown(container.dispose);
      container.read(sessionControllerProvider.notifier).enterAsGuest();
      await pumpEventQueue();
      container.read(orderControllerProvider);
      await pumpEventQueue();

      final orderState = container.read(orderControllerProvider);
      expect(orderState.cart, isEmpty);
      final snapshot = await GuestCartStorage(storage: storage).read();
      expect(snapshot.items, isEmpty);
    });

    test('a successful payment clears the in-memory and device cart', () async {
      final storage = _FakeFlutterSecureStorage();
      final container = await _containerWith(storage: storage);
      addTearDown(container.dispose);
      container.read(sessionControllerProvider.notifier).enterAsGuest();
      await pumpEventQueue();
      container.read(orderControllerProvider.notifier).setCartItem(_product, 2);
      await pumpEventQueue();

      container
          .read(orderControllerProvider.notifier)
          .clearCartAfterSuccessfulPayment();
      await pumpEventQueue();

      final orderState = container.read(orderControllerProvider);
      expect(orderState.cart, isEmpty);
      final snapshot = await GuestCartStorage(storage: storage).read();
      expect(snapshot.items, isEmpty);
    });

    test('guest checkout and order actions never call protected endpoints',
        () async {
      final storage = _FakeFlutterSecureStorage();
      final orderRepository = _TrackingOrderRepository();
      final container = await _containerWith(
        storage: storage,
        orderRepository: orderRepository,
      );
      addTearDown(container.dispose);
      container.read(sessionControllerProvider.notifier).enterAsGuest();
      await pumpEventQueue();

      final controller = container.read(orderControllerProvider.notifier);
      controller.setCartItem(_product, 2);
      await pumpEventQueue();

      expect(await controller.previewFor('address-1'), isFalse);
      expect(await controller.create('address-1'), isNull);
      await controller.loadOrders();
      await controller.loadOrder('order-1');
      expect(await controller.cancel('order-1'), isFalse);

      expect(orderRepository.previewCalls, 0);
      expect(orderRepository.createCalls, 0);
      expect(orderRepository.getMineCalls, 0);
      expect(orderRepository.getCalls, 0);
      expect(orderRepository.cancelCalls, 0);

      final orderState = container.read(orderControllerProvider);
      expect(orderState.errorMessage, isNull);
    });

    test('session expiry keeps the persisted cart for a later sign-in', () async {
      final storage = _FakeFlutterSecureStorage();
      final container = await _containerWith(
        storage: storage,
        restoreResult: _session,
      );
      addTearDown(container.dispose);
      container.read(orderControllerProvider);
      await pumpEventQueue();

      // Restore the user-scoped cart first so there is something to preserve.
      final controller = container.read(orderControllerProvider.notifier);
      controller.setCartItem(_product, 2);
      await pumpEventQueue();

      await container.read(sessionControllerProvider.notifier).expireSession();
      await pumpEventQueue();

      final snapshot = await GuestCartStorage(storage: storage).read();
      expect(snapshot.userId, _session.user.publicUserId);
      expect(snapshot.items, hasLength(1));
      expect(container.read(sessionControllerProvider).status,
          SessionStatus.unauthenticated);
    });
  });
}

/// Builds a container whose session restore yields [restoreResult] (null = no
/// session) and whose guest cart store is backed by [storage]. The order
/// repository is only read lazily by the order controller.
Future<ProviderContainer> _containerWith({
  required _FakeFlutterSecureStorage storage,
  AuthSession? restoreResult,
  OrderRepository? orderRepository,
}) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(
        _TrackingAuthRepository(restoreResult: restoreResult),
      ),
      guestCartStorageProvider.overrideWithValue(
        GuestCartStorage(storage: storage),
      ),
      orderRepositoryProvider.overrideWithValue(
        orderRepository ?? _TrackingOrderRepository(),
      ),
    ],
  );
  container.read(sessionControllerProvider);
  await Future<void>.delayed(Duration.zero);
  return container;
}

/// Auth repository that never touches storage and records calls.
class _TrackingAuthRepository extends AuthRepository {
  _TrackingAuthRepository({this.restoreResult})
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final AuthSession? restoreResult;

  @override
  Future<AuthSession?> restore() async => restoreResult;

  @override
  Future<void> saveSession(AuthSession session) async {}

  @override
  Future<void> clear() async {}
}

/// Order repository that records every protected call. Any call from a guest
/// would indicate a regression, so each method fails loudly if invoked.
class _TrackingOrderRepository extends OrderRepository {
  _TrackingOrderRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  int previewCalls = 0;
  int createCalls = 0;
  int getMineCalls = 0;
  int getCalls = 0;
  int cancelCalls = 0;

  @override
  Future<CheckoutPreview> preview(String token, CheckoutRequest request) {
    previewCalls += 1;
    throw UnimplementedError('guest must never preview an order');
  }

  @override
  Future<OrderSummary> create(
    String token,
    CheckoutRequest request,
    String idempotencyKey,
  ) {
    createCalls += 1;
    throw UnimplementedError('guest must never create an order');
  }

  @override
  Future<List<OrderSummary>> getMine(String token) {
    getMineCalls += 1;
    throw UnimplementedError('guest must never load orders');
  }

  @override
  Future<OrderSummary> get(String token, String orderId) {
    getCalls += 1;
    throw UnimplementedError('guest must never load an order');
  }

  @override
  Future<OrderSummary> cancel(String token, String orderId) {
    cancelCalls += 1;
    throw UnimplementedError('guest must never cancel an order');
  }
}

/// In-memory secure storage sharing v9.2.4 write/read/delete semantics
/// (writing null deletes the key).
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
